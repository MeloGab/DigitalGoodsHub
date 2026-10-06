using DigitalGoodsHub.Core;
using Microsoft.EntityFrameworkCore;
namespace DigitalGoodsHub.Api;
public sealed class PaymentService(HubDb db)
{
    public async Task<(Payment Payment, bool Replayed)> Create(string key, PaymentRequest request, CancellationToken ct)
    {
        var hash = Persistence.Hash(request);
        var previous = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (previous is not null) return Replay(previous, hash);
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ProductId && x.Active, ct)
            ?? throw new KeyNotFoundException("Produto indisponível.");
        var payment = new Payment { IdempotencyKey = key, RequestHash = hash, ProductId = product.Id,
            PhoneNumber = request.PhoneNumber, AmountCents = product.AmountCents, Scenario = request.Scenario };
        db.Payments.Add(payment);
        db.Outbox.Add(new OutboxMessage { PaymentId = payment.Id });
        // A single relational SaveChanges commits both inserts atomically.
        try { await db.SaveChangesAsync(ct); return (payment, false); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            previous = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (previous is null) throw;
            return Replay(previous, hash);
        }
    }
    private static (Payment, bool) Replay(Payment p, string hash) => p.RequestHash == hash
        ? (p, true) : throw new IdempotencyConflictException();
}
public sealed class IdempotencyConflictException : Exception;
