using System.Diagnostics;
using StackExchange.Redis;
using StackExchange.Redis.Caching;

namespace CachingDemo;

/// <summary>A cache configuration the demo can switch to live (each switch is a reconnect).</summary>
sealed record CacheProfile(string Name, string Summary, CacheOptions? Options);

/// <summary>One 250ms window of traffic, for the strip.</summary>
readonly record struct Window(int Reads, int ServerReads, string? Marker);

readonly record struct LogLine(DateTime At, string Text);

sealed record KeyView(string Key, string? Cached, string? Server, double LastMicros, bool Selected);

/// <summary>Counters copied from <see cref="ClientCache"/>, plus the demo's own.</summary>
sealed record CacheView(
    bool Enabled,
    string Mode,
    string Admission,
    long Count, long Bytes, long Stored, long Evicted, long Promoted, long Expired, long Refreshes, long ServedStale, long TrackedKeys,
    long Reads, long ServerReads);

sealed record Snapshot(Window[] Windows, KeyView[] Keys, CacheView Cache, LogLine[] Log, CacheProfile Profile, bool Bypass, bool Paused, int LastWindowReads, int LastWindowServerReads);

/// <summary>
/// Drives two connections to one Redis server:
///  - the "app" connection, with client-side caching configured, which reads the catalogue continuously;
///  - the "back door" connection, with no cache, which writes, expires and flushes behind the app's back,
///    and reads the truth for the display.
/// Hit rate is measured on the server (INFO commandstats, cmdstat_get): a read answered from the cache
/// is one the server never sees. That is the measurement the library's own tests use, and the only one
/// the client cannot fool.
/// </summary>
sealed class CacheEngine : IAsyncDisposable
{
    private const string Endpoint = "localhost:16411";
    private const string Prefix = "demo:";
    public const int KeyCount = 8;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    // 4.0.146-alpha (StackExchange.Redis issue #3318): an entry that reaches TimeToLive is not replaced by the next fill until the sweep
    // reclaims it (SweepInterval, 10s by default), so with a 5s TTL the cache would spend half its life
    // missing. A 1s sweep bounds that to a second. Invalidated entries are replaced at once; only
    // expiry is affected. Not needed once the fill path treats an expired entry as dead.
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

    private readonly object _sync = new();
    private readonly Queue<LogLine> _log = new();
    private readonly Window[] _windows = new Window[400];
    private int _windowCount, _windowHead;
    private readonly CancellationTokenSource _stop = new();

    private ConnectionMultiplexer? _app;        // cached
    private ConnectionMultiplexer _backDoor = null!; // uncached, separate client
    private IServer _server = null!;
    private volatile IDatabase? _db;
    private Task _reader = Task.CompletedTask, _probe = Task.CompletedTask;

    private readonly string[] _keys = Enumerable.Range(1, KeyCount).Select(i => $"{Prefix}item:{i}").ToArray();
    private readonly string?[] _cached = new string?[KeyCount];
    private readonly string?[] _serverValues = new string?[KeyCount];
    private readonly double[] _lastMicros = new double[KeyCount];
    private readonly Random _random = new();

    private long _reads;              // reads the app issued (through the cache, or bypassing it)
    private long _serverGets;         // cmdstat_get calls on the server, from INFO
    private long _serverGetsAtStart;
    private long _readsWindowBase, _serverGetsWindowBase;
    private string? _pendingMarker;

    public int ProfileIndex { get; private set; }
    public CacheProfile Profile => Profiles[ProfileIndex];
    public bool Bypass { get; private set; }
    public bool Paused { get; private set; }
    public int Selected { get; private set; }

    public async Task StartAsync()
    {
        _backDoor = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true"); // INFO and FLUSHDB are admin commands
        _server = _backDoor.GetServers()[0];
        var bd = _backDoor.GetDatabase();
        for (int i = 0; i < KeyCount; i++)
        {
            await bd.StringSetAsync(_keys[i], Price());
        }
        _serverGetsAtStart = await ReadServerGetsAsync();
        _serverGetsWindowBase = _serverGetsAtStart;

        await ConnectAppAsync();
        _reader = Task.Run(ReaderLoopAsync);
        _probe = Task.Run(ProbeLoopAsync);
    }

    private async Task ConnectAppAsync()
    {
        var options = ConfigurationOptions.Parse(Endpoint + ",abortConnect=false");
        options.Protocol = RedisProtocol.Resp3; // invalidations arrive as RESP3 push messages
        options.ClientCache = Profile.Options;
        var muxer = await ConnectionMultiplexer.ConnectAsync(options);
        var old = _app;
        _app = muxer;
        _db = muxer.GetDatabase();
        var cache = muxer.GetClientCache();
        Log($"connected [{Profile.Name}]: {(cache is null ? "no client cache" : $"cache on, tracking mode {cache.EffectiveTrackingMode}")}");
        if (old is not null) await old.DisposeAsync();
    }

    // ---- keyboard ----

    public void SelectNext(int delta) => Selected = (Selected + delta + KeyCount) % KeyCount;

    public void TogglePause() { Paused = !Paused; Log(Paused ? "reads paused" : "reads resumed"); }

    public void ToggleBypass()
    {
        Bypass = !Bypass;
        Mark("B");
        Log(Bypass ? "reads now pass CommandFlags.CacheOptOut: every read is a round trip" : "reads use the cache again");
    }

    public void CycleProfile()
    {
        ProfileIndex = (ProfileIndex + 1) % Profiles.Length;
        Mark("C");
        Log($"switching to [{Profile.Name}]: {Profile.Summary}");
        _ = ConnectAppAsync();
    }

    /// <summary>Write the selected key from the other connection: the server pushes an invalidation.</summary>
    public void WriteSelected()
    {
        var key = _keys[Selected];
        var value = Price();
        Mark("W");
        Log($"back door: SET {key} {value}  (server will invalidate the app's copy)");
        _ = _backDoor.GetDatabase().StringSetAsync(key, value);
    }

    /// <summary>Expire the selected key from the other connection: expiry is NOT announced.</summary>
    public void ExpireSelected()
    {
        var key = _keys[Selected];
        Mark("X");
        Log($"back door: EXPIRE {key} 1  (expiry is not announced; the cached copy lives until TimeToLive)");
        _ = _backDoor.GetDatabase().KeyExpireAsync(key, TimeSpan.FromSeconds(1));
    }

    /// <summary>Restore every key that has gone missing (after EXPIRE or FLUSHDB).</summary>
    public void RestoreMissing()
    {
        Mark("S");
        Log("back door: re-seeding any missing keys");
        _ = Task.Run(async () =>
        {
            var bd = _backDoor.GetDatabase();
            for (int i = 0; i < KeyCount; i++)
            {
                if (!await bd.KeyExistsAsync(_keys[i])) await bd.StringSetAsync(_keys[i], Price());
            }
        });
    }

    /// <summary>Read a burst of never-seen keys once each: admission decides whether they pollute the cache.</summary>
    public void OneOffBurst()
    {
        var db = _db;
        if (db is null) return;
        Mark("N");
        Log("25 one-off reads of fresh keys: watch Count/Stored - first-miss admission stores them all, the doorkeeper stores none");
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    await db.StringGetAsync($"{Prefix}oneoff:{Guid.NewGuid():N}");
                    Interlocked.Increment(ref _reads);
                }
                catch (Exception ex) { Log($"one-off read failed: {ex.Message}"); return; }
            }
        });
    }

    public void InvalidateSelected()
    {
        var cache = _app?.GetClientCache();
        var key = _keys[Selected];
        Mark("I");
        if (cache is null) { Log("no client cache to invalidate"); return; }
        bool had = cache.Invalidate(key);
        Log($"cache.Invalidate({key}) -> {(had ? "an entry depended on it" : "nothing cached for it")}; nothing sent to the server");
    }

    public void InvalidateAll()
    {
        var cache = _app?.GetClientCache();
        Mark("A");
        if (cache is null) { Log("no client cache to invalidate"); return; }
        cache.InvalidateAll();
        Log("cache.InvalidateAll(): every entry dropped locally");
    }

    public void FlushDb()
    {
        Mark("F");
        Log("back door: FLUSHDB  (the app's whole cache is discarded; keys are gone until re-seeded)");
        _ = _server.FlushDatabaseAsync();
    }

    // ---- state for the UI ----

    public Snapshot Snapshot()
    {
        lock (_sync)
        {
            var windows = new Window[_windowCount];
            for (int i = 0; i < _windowCount; i++)
            {
                windows[i] = _windows[(_windowHead - _windowCount + i + _windows.Length) % _windows.Length];
            }
            var keys = new KeyView[KeyCount];
            for (int i = 0; i < KeyCount; i++)
            {
                keys[i] = new KeyView(_keys[i], _cached[i], _serverValues[i], _lastMicros[i], i == Selected);
            }
            var cache = _app?.GetClientCache();
            var view = cache is null
                ? new CacheView(false, "none", "n/a", 0, 0, 0, 0, 0, 0, 0, 0, 0, _reads, _serverGets - _serverGetsAtStart)
                : new CacheView(true, cache.EffectiveTrackingMode.ToString(), (cache.Options.Admission == CacheAdmission.Default ? "OnRepeatedMiss (Default)" : cache.Options.Admission.ToString()), cache.Count, cache.Bytes, cache.Stored, cache.Evicted, cache.Promoted,
                    cache.Expired, cache.Refreshes, cache.ServedStale, cache.TrackedKeys, _reads, _serverGets - _serverGetsAtStart);
            var last = _windowCount > 0 ? windows[^1] : default;
            return new Snapshot(windows, keys, view, _log.ToArray(), Profile, Bypass, Paused, last.Reads, last.ServerReads);
        }
    }

    /// <summary>
    /// Close the current window: reads issued vs reads that reached the server. Called by the probe loop
    /// right after it has read the server's counters, so both sides are sampled at the same instant.
    /// </summary>
    private void CloseWindow()
    {
        lock (_sync)
        {
            long reads = Volatile.Read(ref _reads);
            int r = (int)(reads - _readsWindowBase);
            int s = (int)Math.Max(0, _serverGets - _serverGetsWindowBase);
            _readsWindowBase = reads;
            _serverGetsWindowBase = _serverGets;
            _windows[_windowHead] = new Window(r, Math.Min(s, r), _pendingMarker);
            _pendingMarker = null;
            _windowHead = (_windowHead + 1) % _windows.Length;
            if (_windowCount < _windows.Length) _windowCount++;
        }
    }

    // ---- internals ----

    private async Task ReaderLoopAsync()
    {
        var token = _stop.Token;
        int i = 0;
        while (!token.IsCancellationRequested)
        {
            var db = _db;
            if (!Paused && db is not null)
            {
                int k = i++ % KeyCount;
                var flags = Bypass ? CommandFlags.CacheOptOut : CommandFlags.None;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var value = await db.StringGetAsync(_keys[k], flags);
                    var micros = Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000;
                    lock (_sync)
                    {
                        _cached[k] = value.IsNull ? null : value.ToString();
                        _lastMicros[k] = micros;
                    }
                    Interlocked.Increment(ref _reads);
                }
                catch (Exception ex)
                {
                    Log($"read failed: {ex.GetType().Name}: {ex.Message}");
                    try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
                }
            }
            try { await Task.Delay(5, token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Read the truth: the server's values (uncached, via MGET so it does not disturb cmdstat_get) and its GET count.</summary>
    private async Task ProbeLoopAsync()
    {
        var token = _stop.Token;
        var keys = _keys.Select(k => (RedisKey)k).ToArray();
        while (!token.IsCancellationRequested)
        {
            try
            {
                var values = await _backDoor.GetDatabase().StringGetAsync(keys);
                long gets = await ReadServerGetsAsync();
                lock (_sync)
                {
                    for (int i = 0; i < KeyCount; i++) _serverValues[i] = values[i].IsNull ? null : values[i].ToString();
                    _serverGets = gets;
                }
                CloseWindow();
            }
            catch (Exception ex)
            {
                Log($"probe failed: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(250, token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<long> ReadServerGetsAsync()
    {
        var info = await _server.InfoAsync("commandstats");
        foreach (var group in info)
        {
            foreach (var pair in group)
            {
                if (pair.Key == "cmdstat_get")
                {
                    // calls=123,usec=456,usec_per_call=3.71,rejected_calls=0,failed_calls=0
                    foreach (var part in pair.Value.Split(','))
                    {
                        if (part.StartsWith("calls=", StringComparison.Ordinal) && long.TryParse(part.AsSpan(6), out var calls)) return calls;
                    }
                }
            }
        }
        return 0; // no GET has ever been issued
    }

    private string Price() => $"£{_random.Next(1, 200)}.{_random.Next(0, 100):00}";

    private void Mark(string marker) { lock (_sync) { _pendingMarker = marker; } }

    private void Log(string text)
    {
        lock (_sync)
        {
            _log.Enqueue(new LogLine(DateTime.Now, text));
            while (_log.Count > 14) _log.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_reader, _probe).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        if (_app is not null) await _app.DisposeAsync();
        await _backDoor.DisposeAsync();
    }
}
