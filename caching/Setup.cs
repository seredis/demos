using StackExchange.Redis;
using StackExchange.Redis.Caching;

namespace CachingDemo;

// Setup.cs - the complete connection setup, and nothing else.
//
// Attaching the cache is the ONLY thing this app does differently from an uncached one: a
// ClientCache configuration on the ConfigurationOptions, once, at connect time. Every read after
// that is plain IDatabase code - see CoreUsage.cs.
sealed partial class CacheEngine
{
    private const string Endpoint = "localhost:16411";
    private const string Prefix = "demo:";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    // 1s (default 10s) so a TimeToLive-expired entry is replaced promptly in 4.0.146-alpha;
    // see README.md and StackExchange.Redis issue #3318
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(1);

    public static readonly CacheProfile[] Profiles =
    [
        new("plain", $"Prefixes [demo:], TimeToLive {Ttl.TotalSeconds:0}s: in-prefix reads are stored on their first miss; served until invalidated or aged out",
            new CacheOptions { Prefixes = [Prefix], SweepInterval = Sweep, DefaultPolicy = new CachePolicy { TimeToLive = Ttl } }),
        new("refresh-after", $"TimeToLive {Ttl.TotalSeconds:0}s, RefreshAfter 3s: steadily-read entries are refreshed in the background before they expire",
            new CacheOptions { Prefixes = [Prefix], SweepInterval = Sweep, DefaultPolicy = new CachePolicy { TimeToLive = Ttl, RefreshAfter = TimeSpan.FromSeconds(3) } }),
        new("grace", $"TimeToLive {Ttl.TotalSeconds:0}s, InvalidationGracePeriod 1.5s: after another client's write, the old value is served for up to 1.5s while one refresh runs",
            new CacheOptions { Prefixes = [Prefix], SweepInterval = Sweep, DefaultPolicy = new CachePolicy { TimeToLive = Ttl, InvalidationGracePeriod = TimeSpan.FromSeconds(1.5) } }),
        new("doorkeeper", $"no Prefixes (Tracking mode), TimeToLive {Ttl.TotalSeconds:0}s: default admission, a reply is stored on its second miss, so a key read once is never cached",
            new CacheOptions { SweepInterval = Sweep, DefaultPolicy = new CachePolicy { TimeToLive = Ttl } }),
        new("no cache", "ClientCache not configured: every read is a round trip", null),
    ];

    /// <summary>The app connection: ordinary options, plus the cache configuration.</summary>
    private async Task<ConnectionMultiplexer> ConnectAppAsync()
    {
        var options = ConfigurationOptions.Parse(Endpoint + ",abortConnect=false");
        options.Protocol = RedisProtocol.Resp3; // invalidations arrive as RESP3 push messages
        options.ClientCache = Profile.Options;  // null for the "no cache" profile
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    /// <summary>The back door: an ordinary uncached connection, used to interfere with the app
    /// behind its back and to measure the server (INFO and FLUSHDB are admin commands).</summary>
    private static Task<ConnectionMultiplexer> ConnectBackDoorAsync()
        => ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
}
