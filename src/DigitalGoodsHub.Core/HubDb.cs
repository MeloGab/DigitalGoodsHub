using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DigitalGoodsHub.Core;
public sealed class HubDb(DbContextOptions<HubDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    public DbSet<ProviderOperation> ProviderOperations => Set<ProviderOperation>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Payment>().HasIndex(x => x.IdempotencyKey).IsUnique();
        model.Entity<Product>().HasIndex(x => new { x.Provider, x.Type, x.Active });
        model.Entity<OutboxMessage>().HasIndex(x => new { x.Published, x.CreatedAt });
        model.Entity<Payment>().HasIndex(x => new { x.Status, x.LeaseUntil });
        model.Entity<Product>().HasData(
            new Product { Id = "recarga-vivo-30", Name = "Recarga Vivo R$ 30", Type = "Recharge", Provider = "Vivo", AmountCents = 3000 },
            new Product { Id = "recarga-claro-50", Name = "Recarga Claro R$ 50", Type = "Recharge", Provider = "Claro", AmountCents = 5000 },
            new Product { Id = "giftcard-psn-100", Name = "Gift Card PlayStation R$ 100", Type = "GiftCard", Provider = "PSN", AmountCents = 10000 });
    }
}
public static class Persistence
{
    public static IServiceCollection AddHubDb(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<HubDb>(o => {
            var postgres = config.GetConnectionString("Postgres");
            if (!string.IsNullOrWhiteSpace(postgres)) o.UseNpgsql(postgres);
            else o.UseSqlite(config.GetConnectionString("Sqlite") ?? "Data Source=digitalgoods.db;Default Timeout=30");
        });
        return services;
    }
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
