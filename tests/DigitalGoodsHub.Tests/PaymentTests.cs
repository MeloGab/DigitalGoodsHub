using System.Net;
using System.Net.Http.Json;
using DigitalGoodsHub.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace DigitalGoodsHub.Tests;
public sealed class DemoFactory : WebApplicationFactory<Program>
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "digitalgoods-test-" + Guid.NewGuid().ToString("N") + ".db");
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:Sqlite"] = $"Data Source={path};Default Timeout=30", ["DisableWorker"] = "true"
    }));
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); } }
}
public sealed class PaymentTests
{
    [Fact]
    public async Task Concurrent_same_key_creates_one_payment_and_one_atomic_outbox()
    {
        using var app = new DemoFactory(); using var client = app.CreateClient();
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Pay(client, key, "11999999999")));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, x => Assert.True(x.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        Assert.Equal(1, await db.Payments.CountAsync()); Assert.Equal(1, await db.Outbox.CountAsync());
        Assert.Equal((await db.Payments.SingleAsync()).Id, (await db.Outbox.SingleAsync()).PaymentId);
        foreach (var r in responses) r.Dispose();
    }
    [Fact]
    public async Task Reusing_key_with_different_body_is_conflict()
    {
        using var app = new DemoFactory(); using var client = app.CreateClient();
        using var first = await Pay(client, "same-key", "11999999999");
        using var second = await Pay(client, "same-key", "11888888888");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
    [Fact]
    public async Task Missing_key_is_rejected_without_database_effect()
    {
        using var app = new DemoFactory(); using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/payments", new PaymentRequest("recarga-vivo-30", "11999999999"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        Assert.Equal(0, await db.Payments.CountAsync()); Assert.Equal(0, await db.Outbox.CountAsync());
    }
    [Fact]
    public async Task Unknown_product_has_no_payment_or_outbox()
    {
        using var app = new DemoFactory(); using var client = app.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/payments") { Content = JsonContent.Create(new PaymentRequest("missing", "11999999999")) };
        message.Headers.Add("Idempotency-Key", "unknown-product");
        using var response = await client.SendAsync(message); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        Assert.Equal(0, await db.Outbox.CountAsync());
    }
    [Fact]
    public async Task Concurrent_cache_misses_query_database_once()
    {
        using var app = new DemoFactory(); using var client = app.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => client.GetAsync("/api/products/recarga-vivo-30")));
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var cache = app.Services.GetRequiredService<DigitalGoodsHub.Api.ProductCache>();
        var stats = System.Text.Json.JsonSerializer.SerializeToElement(cache.Stats);
        Assert.Equal(1, stats.GetProperty("databaseQueries").GetInt64());
        foreach (var r in responses) r.Dispose();
    }
    private static Task<HttpResponseMessage> Pay(HttpClient client, string key, string phone)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments") { Content = JsonContent.Create(new PaymentRequest("recarga-vivo-30", phone)) };
        request.Headers.Add("Idempotency-Key", key); return client.SendAsync(request);
    }
}
