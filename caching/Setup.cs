using StackExchange.Redis;
using StackExchange.Redis.Caching;

namespace CachingDemo;

// Setup.cs - the complete connection setup, and nothing else.
//
// Attaching the cache is the ONLY thing this app does differently from an uncached one: a
// ClientCache configuration on the ConfigurationOptions, once, at connect time. Every read after
// that is plain IDatabase code - see CoreUsage.cs.
//
// The demo lets you vary three things independently, each a reconnect (CacheOptions are settled at
// connect time): the policy (how entries age), the tracking mode (how the server tells us about
// changes), and the admission rule (when a missed reply is stored at all).
sealed partial class CacheEngine
{
    private const string Endpoint = "localhost:16411";
    private const string Prefix = "demo:";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>How entries age: the <see cref="CachePolicy"/>. Cycled with 'C'.</summary>
    public static readonly CacheProfile[] Profiles =
    [
        new("plain", $"TimeToLive {Ttl.TotalSeconds:0}s: entries are served until invalidated or aged out",
            new CachePolicy { TimeToLive = Ttl }),
        new("refresh-after", $"TimeToLive {Ttl.TotalSeconds:0}s, RefreshAfter 3s: steadily-read entries are refreshed in the background before they expire",
            new CachePolicy { TimeToLive = Ttl, RefreshAfter = TimeSpan.FromSeconds(3) }),
        new("grace", $"TimeToLive {Ttl.TotalSeconds:0}s, InvalidationGracePeriod 1.5s: after another client's write, the old value is served for up to 1.5s while one refresh runs",
            new CachePolicy { TimeToLive = Ttl, InvalidationGracePeriod = TimeSpan.FromSeconds(1.5) }),
        new("no cache", "ClientCache not configured: every read is a round trip", null),
    ];

    /// <summary>How the server tells us about changes: the <see cref="CacheTrackingMode"/>, and whether
    /// Prefixes are given (Tracking rejects them; the others use them to bound what is cached). Cycled with 'T'.</summary>
    public static readonly TrackingChoice[] TrackingModes =
    [
        new("by-prefix", CacheTrackingMode.Default, Prefixed: true,
            "TrackingMode Default with Prefixes [demo:] resolves to TrackingByPrefix: CLIENT TRACKING ON OPTIN, and every in-prefix read is sent behind CLIENT CACHING YES and stored on its first miss (as if it had CacheOptIn, so Admission is not consulted); out-of-prefix reads are not cached"),
        new("tracking", CacheTrackingMode.Tracking, Prefixed: false,
            "CLIENT TRACKING ON OPTOUT, no Prefixes: the server remembers every key this client reads and announces only those; Admission decides what is stored"),
        new("broadcast", CacheTrackingMode.Broadcast, Prefixed: true,
            "CLIENT TRACKING BCAST PREFIX demo:: the server announces every change under the prefix by anyone, and keeps no per-client table; Admission decides what is stored"),
        new("keyspace", CacheTrackingMode.KeyspaceNotifications, Prefixed: true,
            "no CLIENT TRACKING: subscribe to __keyspace@*__:demo:* instead, for servers or proxies without tracking. Needs notify-keyspace-events on the server ('K' toggles it); with it off this is None. Writes and expiries are heard; FLUSHDB has no per-key event, so it is not"),
        new("none", CacheTrackingMode.None, Prefixed: true,
            "no server-assisted invalidation at all: another client's write is never heard about; TimeToLive is the only bound on staleness"),
    ];

    /// <summary>When a reply that missed is stored. Toggled with 'D'. OnRepeatedMiss is the library default.</summary>
    public CacheAdmission Admission { get; private set; } = CacheAdmission.OnRepeatedMiss;

    /// <summary>Compose the three choices into the <see cref="CacheOptions"/> for the next connection.</summary>
    private CacheOptions? BuildCacheOptions()
    {
        if (Profile.Policy is null) return null; // the "no cache" profile
        var tracking = Tracking;
        return new CacheOptions
        {
            TrackingMode = tracking.Mode,
            Prefixes = tracking.Prefixed ? [Prefix] : [],
            Admission = Admission,
            DefaultPolicy = Profile.Policy,
        };
    }

    /// <summary>The app connection: ordinary options, plus the cache configuration.</summary>
    private async Task<ConnectionMultiplexer> ConnectAppAsync()
    {
        var options = ConfigurationOptions.Parse(Endpoint + ",abortConnect=false");
        options.Protocol = RedisProtocol.Resp3;   // CLIENT TRACKING invalidations arrive as RESP3 push messages
        options.ClientCache = BuildCacheOptions(); // null for the "no cache" profile
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    /// <summary>The back door: an ordinary uncached connection, used to interfere with the app
    /// behind its back and to measure the server (INFO, CONFIG and FLUSHDB are admin commands).</summary>
    private static Task<ConnectionMultiplexer> ConnectBackDoorAsync()
        => ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
}
