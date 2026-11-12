// Setup.cs - the ONLY file where the two implementations differ; everything else (CoreUsage.cs
// above all) is identical for both, and the entry options are identical too: the variable is the
// invalidation TRANSPORT, not the policy. The M key in the TUI tears both "web servers" down and
// rebuilds them through the other branch of this file - nothing else changes.
//
//   Stock - the in-box HybridCache (DefaultHybridCache) over IDistributedCache-on-redis,
//     with a MemoryCache near cache per server. There is NO channel from redis back to the
//     near caches: a foreign change is seen when the local copy EXPIRES, so
//     LocalCacheExpiration is a tradeoff you must choose (shorter = less stale, but more
//     round trips). Invalidation is PASSIVE - expiry.
//
//   Push - StackExchange.Redis.HybridCache, built directly on client-side caching. The
//     near cache is the multiplexer's client cache, and redis PUSHES invalidations into it
//     (CLIENT TRACKING over RESP3): another server's write or tag invalidation evicts the
//     local copy in milliseconds. Invalidation is ACTIVE - push.

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.Caching;

namespace HybridCacheTui;

enum CacheMode
{
    /// <summary>stock HybridCache over IDistributedCache-on-redis: passive (expiry) invalidation</summary>
    Stock,

    /// <summary>StackExchange.Redis.HybridCache on client-side caching: active (push) invalidation</summary>
    Push,
}

static class Setup
{
    private const string Endpoint = "localhost:16412";

    // identical for both modes
    public static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),            // lifetime in redis
        LocalCacheExpiration = TimeSpan.FromSeconds(10), // lifetime of a local copy
    };

    /// <summary>Builds the two "web servers" for one implementation. Each run gets a fresh
    /// keyspace (shared by its two servers), so a mode switch always starts clean.</summary>
    public static async Task<(ServiceProvider ServerA, ServiceProvider ServerB)> CreateServersAsync(CacheMode mode)
    {
        string keySpace = $"{mode.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}:";
        return mode == CacheMode.Stock
            ? (await CreateStockServerAsync(keySpace), await CreateStockServerAsync(keySpace))
            : (await CreatePushServerAsync(keySpace), await CreatePushServerAsync(keySpace));
    }

    private static async Task<ServiceProvider> CreateStockServerAsync(string keySpace)
    {
        var services = new ServiceCollection();

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = Endpoint;
            options.InstanceName = keySpace;
        });

        services.AddHybridCache(options => options.DefaultEntryOptions = EntryOptions);

        var provider = services.BuildServiceProvider();

        // fail fast if redis is down: this implementation treats an unreachable backend as
        // a cache miss, which would quietly turn the demo into a single-tier cache
        _ = await provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>()
            .GetAsync("connectivity-check");
        return provider;
    }

    private static async Task<ServiceProvider> CreatePushServerAsync(string keySpace)
    {
        var config = ConfigurationOptions.Parse(Endpoint);
        config.Protocol = RedisProtocol.Resp3;                     // invalidations arrive as RESP3 push messages
        config.ClientCache = new CacheOptions { Prefixes = [keySpace] };
        var muxer = await ConnectionMultiplexer.ConnectAsync(config);

        var services = new ServiceCollection();

        // a dedicated connection for the cache, keyed by the ecosystem convention
        services.AddKeyedSingleton<IConnectionMultiplexer>(typeof(HybridCache), muxer);

        services.AddRedisHybridCache(options =>
        {
            options.KeyPrefix = keySpace;
            options.DefaultEntryOptions = EntryOptions; // same bounds; LocalCacheExpiration is a safety net here, not the mechanism
        });

        return services.BuildServiceProvider();
    }
}
