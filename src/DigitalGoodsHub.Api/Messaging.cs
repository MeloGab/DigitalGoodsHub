using System.Text;
using System.Text.Json;
using DigitalGoodsHub.Core;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
namespace DigitalGoodsHub.Api;
public sealed record FulfillmentMessage(string MessageId, string PaymentId);
public sealed class MessagingWorker(IServiceScopeFactory scopes, Fulfillment fulfillment, IConfiguration config, ILogger<MessagingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config["RabbitMQ"])) await Poll(ct);
                else await Broker(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Messaging loop failed; durable work will be retried");
                await Task.Delay(2000, ct);
            }
        }
    }
    private async Task Poll(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        var batch = await db.Outbox.AsNoTracking().Where(x => !x.Published).OrderBy(x => x.CreatedAt).Take(10).ToListAsync(ct);
        foreach (var msg in batch)
            if (await fulfillment.Process(msg.Id, msg.PaymentId, ct))
                await db.Outbox.Where(x => x.Id == msg.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Published, true), ct);
        await Task.Delay(300, ct);
    }
    private async Task Broker(CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(config["RabbitMQ"]!), AutomaticRecoveryEnabled = true };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.QueueDeclare("fulfillment.dlq", true, false, false, null);
        channel.QueueDeclare("fulfillment.queue", true, false, false,
            new Dictionary<string, object> { ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = "fulfillment.dlq" });
        channel.BasicQos(0, 1, false);
        channel.ConfirmSelect();
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            var batch = await db.Outbox.AsNoTracking().Where(x => !x.Published).OrderBy(x => x.CreatedAt).Take(10).ToListAsync(ct);
            foreach (var msg in batch)
            {
                var props = channel.CreateBasicProperties(); props.Persistent = true; props.MessageId = msg.Id;
                channel.BasicPublish("", "fulfillment.queue", true, props, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new FulfillmentMessage(msg.Id, msg.PaymentId))));
                channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
                await db.Outbox.Where(x => x.Id == msg.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Published, true), ct);
            }
            var delivery = channel.BasicGet("fulfillment.queue", false);
            if (delivery is not null)
            {
                FulfillmentMessage? msg;
                try { msg = JsonSerializer.Deserialize<FulfillmentMessage>(delivery.Body.Span); }
                catch (JsonException) { channel.BasicNack(delivery.DeliveryTag, false, false); continue; }
                if (msg is null || string.IsNullOrWhiteSpace(msg.MessageId) || string.IsNullOrWhiteSpace(msg.PaymentId)) channel.BasicNack(delivery.DeliveryTag, false, false);
                else if (await fulfillment.Process(msg.MessageId, msg.PaymentId, ct))
                {
                    var status = await db.Payments.Where(x => x.Id == msg.PaymentId).Select(x => x.Status).SingleOrDefaultAsync(ct);
                    if (status is null or "Failed" or "PendingReconciliation") channel.BasicNack(delivery.DeliveryTag, false, false);
                    else channel.BasicAck(delivery.DeliveryTag, false);
                }
                else channel.BasicNack(delivery.DeliveryTag, false, true);
            }
            await Task.Delay(300, ct);
        }
    }
}
