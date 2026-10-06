using DigitalGoodsHub.Core;
using Microsoft.EntityFrameworkCore;
using System.Net;
namespace DigitalGoodsHub.Api;
public sealed class Fulfillment(IServiceScopeFactory scopes, IHttpClientFactory clients, ILogger<Fulfillment> logger)
{
    public async Task<bool> Process(string messageId, string paymentId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        if (await db.Inbox.AnyAsync(x => x.Id == messageId, ct)) return true;
        var owner = Guid.NewGuid().ToString("N");
        var now = Persistence.Now;
        var claimed = await db.Payments.Where(x => x.Id == paymentId && x.LeaseUntil < now &&
            (x.Status == "Approved" || x.Status == "Processing"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now + 60000).SetProperty(x => x.LeaseOwner, owner).SetProperty(x => x.Status, "Processing"), ct);
        if (claimed == 0)
        {
            var state = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == paymentId, ct);
            return state is null || state.Status is "Completed" or "Failed" or "PendingReconciliation";
        }
        var p = await db.Payments.SingleAsync(x => x.Id == paymentId, ct);
        try
        {
            var http = clients.CreateClient("provider");
            // Always query first: handles restart after external success and before local commit.
            while (p.Attempts < 3)
            {
                try
                {
                    p.Attempts++;
                    await db.SaveChangesAsync(ct); // Durable retry budget, including status lookup failures.
                    using var lookup = await http.GetAsync($"provider/recharges/{p.Id}", ct);
                    if (lookup.IsSuccessStatusCode)
                    {
                        var recovered = await lookup.Content.ReadFromJsonAsync<RechargeResult>(ct);
                        await Complete(db, p, messageId, recovered!.TransactionId, ct);
                        logger.LogInformation("Reconciled payment {PaymentId} using external operation ID", p.Id);
                        return true;
                    }
                    if (lookup.StatusCode != HttpStatusCode.NotFound) lookup.EnsureSuccessStatusCode();
                    using var response = await http.PostAsJsonAsync("provider/recharges", new RechargeRequest(p.Id, p.PhoneNumber, p.AmountCents, p.Scenario, p.Attempts), ct);
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        p.Status = "Failed"; p.LastError = "Integrador rejeitou a operação (HTTP 400).";
                        await Terminal(db, p, messageId, ct);
                        return true;
                    }
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<RechargeResult>(ct);
                    if (p.Scenario == "CrashAfterEffect" && p.Attempts == 1)
                        throw new SimulatedInterruptionException();
                    await Complete(db, p, messageId, result!.TransactionId, ct);
                    return true;
                }
                catch (SimulatedInterruptionException)
                {
                    // Simulates lost local result; a real process stop also leaves no Inbox/ACK.
                    p.LastError = "Interrupção simulada após efeito externo, antes de persistir resultado.";
                    logger.LogWarning("Simulated interruption after provider effect for {PaymentId}", p.Id);
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    p.LastError = ex is TaskCanceledException ? "Timeout: resultado externo desconhecido." : "Integrador indisponível.";
                }
                await db.SaveChangesAsync(ct);
                if (p.Attempts >= 3) break;
                await Task.Delay(TimeSpan.FromSeconds(p.Attempts == 1 ? 2 : 5), ct);
                // Bound lookup failures independently so an unavailable status endpoint cannot loop forever.
                var check = await db.Payments.AsNoTracking().SingleAsync(x => x.Id == p.Id, ct);
                if (check.LeaseOwner != owner) return false;
            }
            // Final reconciliation after the last ambiguous attempt.
            try
            {
                using var lookup = await clients.CreateClient("provider").GetAsync($"provider/recharges/{p.Id}", ct);
                if (lookup.IsSuccessStatusCode)
                {
                    var recovered = await lookup.Content.ReadFromJsonAsync<RechargeResult>(ct);
                    await Complete(db, p, messageId, recovered!.TransactionId, ct);
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) { }
            p.Status = "PendingReconciliation";
            p.LastError = "Tentativas esgotadas. Reconciliação necessária; sem retry financeiro cego.";
            await Terminal(db, p, messageId, ct);
            return true;
        }
        finally
        {
            // Only the lease owner may release; cancellation/crash recovery uses expiration.
            await db.Payments.Where(x => x.Id == paymentId && x.LeaseOwner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, 0).SetProperty(x => x.LeaseOwner, (string?)null), CancellationToken.None);
        }
    }
    private static async Task Complete(HubDb db, Payment p, string messageId, string transactionId, CancellationToken ct)
    {
        p.Status = "Completed"; p.ProviderTransactionId = transactionId; p.LastError = null;
        await Terminal(db, p, messageId, ct);
    }
    private static async Task Terminal(HubDb db, Payment p, string messageId, CancellationToken ct)
    {
        p.LeaseUntil = 0; p.LeaseOwner = null;
        db.Inbox.Add(new InboxMessage { Id = messageId });
        await db.SaveChangesAsync(ct); // Local outcome + Inbox are atomic.
    }
    private sealed class SimulatedInterruptionException : Exception;
}
