namespace DigitalGoodsHub.Core;
public sealed class Product
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Provider { get; set; } = "";
    public int AmountCents { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Payment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public int AmountCents { get; set; }
    public string Scenario { get; set; } = "Success";
    public string Status { get; set; } = "Approved";
    public int Attempts { get; set; }
    public long LeaseUntil { get; set; }
    public string? LeaseOwner { get; set; }
    public string? ProviderTransactionId { get; set; }
    public string? LastError { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public sealed class OutboxMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PaymentId { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Published { get; set; }
}
public sealed class InboxMessage
{
    public string Id { get; set; } = "";
    public string Consumer { get; set; } = "Fulfillment";
    public long ProcessedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public sealed class ProviderOperation
{
    public string Id { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public sealed record PaymentRequest(string ProductId, string PhoneNumber, string Scenario = "Success");
public sealed record RechargeRequest(string ExternalOperationId, string PhoneNumber, int AmountCents, string Scenario, int Attempt);
public sealed record RechargeResult(string TransactionId, bool Replayed);
public static class Scenarios
{
    public static readonly string[] All = ["Success", "CrashAfterEffect", "TimeoutAfterEffect", "TransientFailure", "PermanentFailure", "Unavailable"];
}
