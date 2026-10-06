using DigitalGoodsHub.Core;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHubDb(builder.Configuration);
var app = builder.Build();
using (var scope = app.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<HubDb>().Database.EnsureCreatedAsync();
app.MapGet("/health", async (HubDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503));
app.MapGet("/provider/recharges/{id}", async (string id, HubDb db) => {
    var op = await db.ProviderOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    return op is null ? Results.NotFound() : Results.Ok(new RechargeResult(op.TransactionId, true));
});
app.MapGet("/provider/operations", async (HubDb db) => await db.ProviderOperations.AsNoTracking().ToListAsync());
app.MapPost("/provider/recharges", async (RechargeRequest request, HubDb db) => {
    if (string.IsNullOrWhiteSpace(request.ExternalOperationId) || request.AmountCents <= 0 || request.Attempt < 1 || !Scenarios.All.Contains(request.Scenario))
        return Results.BadRequest(new { error = "Invalid operation" });
    var hash = Persistence.Hash(new { request.PhoneNumber, request.AmountCents });
    var op = await db.ProviderOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ExternalOperationId);
    if (op is not null) return op.RequestHash == hash ? Results.Ok(new RechargeResult(op.TransactionId, true)) : Results.Conflict();
    if (request.Scenario == "PermanentFailure") return Results.BadRequest(new { error = "Número rejeitado pelo integrador (simulado)." });
    if (request.Scenario == "Unavailable" || (request.Scenario == "TransientFailure" && request.Attempt <= 2)) return Results.StatusCode(503);
    op = new ProviderOperation { Id = request.ExternalOperationId, RequestHash = hash };
    db.ProviderOperations.Add(op);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException)
    {
        db.ChangeTracker.Clear();
        var existing = await db.ProviderOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ExternalOperationId);
        if (existing is null) throw;
        return existing.RequestHash == hash ? Results.Ok(new RechargeResult(existing.TransactionId, true)) : Results.Conflict();
    }
    // Persist BEFORE delay: client timeout leaves a real durable external effect to reconcile.
    if (request.Scenario == "TimeoutAfterEffect" && request.Attempt == 1) await Task.Delay(3000);
    return Results.Ok(new RechargeResult(op.TransactionId, false));
});
app.Run();
