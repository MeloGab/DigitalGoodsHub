using DigitalGoodsHub.Api;
using DigitalGoodsHub.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHubDb(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ProductCache>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddSingleton<Fulfillment>();
builder.Services.AddHttpClient("provider", c => { c.BaseAddress = new Uri(builder.Configuration["ProviderUrl"] ?? "http://localhost:5081/"); c.Timeout = TimeSpan.FromSeconds(1); });
if (builder.Configuration["DisableWorker"] != "true") builder.Services.AddHostedService<MessagingWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<HubDb>().Database.EnsureCreatedAsync();
app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Trace-Id"] = ctx.TraceIdentifier;
    await next();
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", async (HubDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "healthy", database = db.Database.ProviderName }) : Results.StatusCode(503));
app.MapGet("/api/products", async (HubDb db, string? type, string? provider) => await db.Products.AsNoTracking()
    .Where(x => x.Active && (type == null || x.Type == type) && (provider == null || x.Provider == provider)).ToListAsync());
app.MapGet("/api/products/{id}", async (string id, ProductCache cache, CancellationToken ct) => {
    try { var product = await cache.Get(id, ct); return product is null ? Results.NotFound() : Results.Ok(product); }
    catch (TimeoutException) { return Results.StatusCode(503); }
});
app.MapPost("/api/payments", async (HttpContext ctx, PaymentRequest request, PaymentService service, CancellationToken ct) => {
    var key = ctx.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 100) return Results.BadRequest(new { error = "Envie Idempotency-Key com 1 a 100 caracteres." });
    if (string.IsNullOrWhiteSpace(request.ProductId) || request.ProductId.Length > 100 ||
        !Regex.IsMatch(request.PhoneNumber ?? "", @"^\d{10,11}$") || !Scenarios.All.Contains(request.Scenario))
        return Results.BadRequest(new { error = "Produto, telefone (10–11 dígitos) e cenário válidos são obrigatórios." });
    try
    {
        var result = await service.Create(key, request, ct);
        ctx.Response.Headers["Idempotency-Replayed"] = result.Replayed.ToString().ToLowerInvariant();
        return result.Replayed ? Results.Ok(View(result.Payment)) : Results.Created($"/api/payments/{result.Payment.Id}", View(result.Payment));
    }
    catch (IdempotencyConflictException) { return Results.Conflict(new { error = "Chave já utilizada com outro conteúdo." }); }
    catch (KeyNotFoundException e) { return Results.NotFound(new { error = e.Message }); }
});
app.MapGet("/api/payments/{id}", async (string id, HubDb db) => {
    var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    return p is null ? Results.NotFound() : Results.Ok(View(p));
});
app.MapGet("/api/payments", async (HubDb db) => (await db.Payments.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync()).Select(View));
app.MapGet("/api/diagnostics", async (HubDb db, ProductCache cache, IConfiguration c) => new {
    cache = cache.Stats,
    payments = await db.Payments.CountAsync(),
    pendingOutbox = await db.Outbox.CountAsync(x => !x.Published),
    inbox = await db.Inbox.CountAsync(),
    reconciliation = await db.Payments.CountAsync(x => x.Status == "PendingReconciliation"),
    failed = await db.Payments.CountAsync(x => x.Status == "Failed"),
    messaging = string.IsNullOrWhiteSpace(c["RabbitMQ"]) ? "Durable database polling (local demo)" : "RabbitMQ with publisher confirms and manual ACK",
    note = "Pagamento e integrador são simulados. Diagnósticos de demonstração; sem autenticação."
});
app.Run();
static object View(Payment p) => new { p.Id, p.ProductId, phoneNumber = "*******" + p.PhoneNumber[^4..], p.AmountCents, p.Scenario, p.Status, p.Attempts, p.ProviderTransactionId, p.LastError, p.CreatedAt };
public partial class Program { }
