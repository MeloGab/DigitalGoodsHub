using System.Collections.Concurrent;
using System.Text.Json;
using DigitalGoodsHub.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
namespace DigitalGoodsHub.Api;
public sealed class ProductCache(IMemoryCache memory, IServiceScopeFactory scopes, IConfiguration config)
{
    private readonly SemaphoreSlim gate = new(1, 1); // Local MVP: one gate for bounded, seeded catalog.
    private readonly Lazy<ConnectionMultiplexer?> redis = new(() => string.IsNullOrWhiteSpace(config["Redis"])
        ? null : ConnectionMultiplexer.Connect(config["Redis"]!));
    private long hits, misses, queries;
    public object Stats => new { hits = Interlocked.Read(ref hits), misses = Interlocked.Read(ref misses), databaseQueries = Interlocked.Read(ref queries), mode = string.IsNullOrWhiteSpace(config["Redis"]) ? "Memory / single instance" : "Redis / distributed lease" };
    public async Task<Product?> Get(string id, CancellationToken ct)
    {
        var key = "product:" + id;
        var value = await Read(key);
        if (value is not null) { Interlocked.Increment(ref hits); return value; }
        Interlocked.Increment(ref misses);
        await gate.WaitAsync(ct);
        var token = Guid.NewGuid().ToString("N");
        var lockOwned = false;
        var cache = redis.Value?.GetDatabase();
        try
        {
            if (cache is not null)
            {
                // Fail closed after a bounded wait; do not fan misses out into the database.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!(lockOwned = await cache.StringSetAsync(key + ":lock", token, TimeSpan.FromSeconds(10), When.NotExists)))
                {
                    value = await Read(key);
                    if (value is not null) return value;
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Cache está sendo reconstruído; tente novamente.");
                    await Task.Delay(50, ct);
                }
            }
            value = await Read(key);
            if (value is not null) return value;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            Interlocked.Increment(ref queries);
            value = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Active, ct);
            if (value is not null)
            {
                if (cache is null) memory.Set(key, value, TimeSpan.FromMinutes(5));
                else await cache.StringSetAsync(key, JsonSerializer.Serialize(value), TimeSpan.FromMinutes(5));
            }
            return value;
        }
        finally
        {
            try
            {
                if (lockOwned && cache is not null)
                    await cache.ScriptEvaluateAsync("if redis.call('get',KEYS[1]) == ARGV[1] then return redis.call('del',KEYS[1]) else return 0 end", [new RedisKey(key + ":lock")], [new RedisValue(token)]);
            }
            finally { gate.Release(); }
        }
    }
    private async Task<Product?> Read(string key)
    {
        if (redis.Value is null) return memory.Get<Product>(key);
        var data = await redis.Value.GetDatabase().StringGetAsync(key);
        return data.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Product>(data.ToString());
    }
}
