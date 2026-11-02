using System.Diagnostics;
using StackExchange.Redis;
using StackExchange.Redis.Caching;

namespace CachingDemo;

/// <summary>A cache policy the demo can switch to live (each switch is a reconnect); null means no cache.</summary>
sealed record CacheProfile(string Name, string Summary, CachePolicy? Policy);

/// <summary>A tracking mode the demo can switch to live, and whether Prefixes are given with it.</summary>
sealed record TrackingChoice(string Name, CacheTrackingMode Mode, bool Prefixed, string Summary);

/// <summary>One 250ms window of traffic, for the strip.</summary>
readonly record struct Window(int Reads, int ServerReads, string? Marker);

readonly record struct LogLine(DateTime At, string Text);

sealed record KeyView(string Key, string? Cached, string? Server, double LastMicros, long Reads, bool Selected);

/// <summary>Counters copied from <see cref="ClientCache"/>, plus the demo's own.</summary>
sealed record CacheView(
    bool Enabled,
    string Mode,
    string Admission,
    long Count, long Bytes, long Stored, long Evicted, long Promoted, long Expired, long Refreshes, long ServedStale, long TrackedKeys,
    long Reads, long ServerReads);

sealed record Snapshot(Window[] Windows, KeyView[] Keys, CacheView Cache, LogLine[] Log, CacheProfile Profile, TrackingChoice Tracking, string KeyspaceEvents,
    bool Bypass, bool Paused, int LastWindowReads, int LastWindowServerReads);

/// <summary>
/// Drives two connections to one Redis server:
///  - the "app" connection, with client-side caching configured, which reads the catalogue continuously;
///  - the "back door" connection, with no cache, which writes, expires and flushes behind the app's back,
///    and reads the truth for the display.
/// Hit rate is measured on the server (INFO commandstats, cmdstat_get): a read answered from the cache
/// is one the server never sees. That is the measurement the library's own tests use, and the only one
/// the client cannot fool.
/// </summary>
sealed partial class CacheEngine : IAsyncDisposable
{
    public const int KeyCount = 8;

    private readonly object _sync = new();
    private readonly Queue<LogLine> _log = new();
    private readonly List<string> _transcript = new(); // every event, with the counters at that moment; printed after a scripted run
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
    private readonly long[] _keyReads = new long[KeyCount];
    private readonly long[] _staleSince = new long[KeyCount]; // Stopwatch timestamp of the first probe that saw the row out of sync, or 0
    private readonly SemaphoreSlim _reconnect = new(1, 1);

    // The read mix is zipf-adjacent rather than zipf proper: weight 1/(rank+1), i.e. a Zipf-Mandelbrot
    // with a +1 shift, which keeps a hot head (item:1 ~27% of reads) without the tail going silent
    // (item:8 ~6%) - a genuinely zipfian tail would be too quiet to watch in a demo this small.
    private static readonly double[] CumulativeWeight = BuildCumulativeWeights();

    private static double[] BuildCumulativeWeights()
    {
        var cumulative = new double[KeyCount];
        double sum = 0;
        for (int i = 0; i < KeyCount; i++) cumulative[i] = sum += 1.0 / (i + 2);
        return cumulative;
    }

    private static int NextKey()
    {
        double r = Random.Shared.NextDouble() * CumulativeWeight[^1];
        for (int i = 0; i < KeyCount - 1; i++)
        {
            if (r < CumulativeWeight[i]) return i;
        }
        return KeyCount - 1;
    }

    private long _reads;              // reads the app issued (through the cache, or bypassing it)
    private long _serverGets;         // cmdstat_get calls on the server, from INFO
    private long _serverGetsAtStart;
    private long _readsWindowBase, _serverGetsWindowBase;
    private string? _pendingMarker;
    private string _keyspaceEvents = "", _keyspaceEventsAtStart = "";

    public int ProfileIndex { get; private set; }
    public CacheProfile Profile => Profiles[ProfileIndex];
    public int TrackingIndex { get; private set; }
    public TrackingChoice Tracking => TrackingModes[TrackingIndex];
    public bool Bypass { get; private set; }
    public bool Paused { get; private set; }
    public int Selected { get; private set; }

    public async Task StartAsync()
    {
        _backDoor = await ConnectBackDoorAsync();
        _server = _backDoor.GetServers()[0];
        var bd = _backDoor.GetDatabase();
        for (int i = 0; i < KeyCount; i++)
        {
            await bd.StringSetAsync(_keys[i], Price());
        }
        _serverGetsAtStart = await ReadServerGetsAsync();
        _serverGetsWindowBase = _serverGetsAtStart;
        _keyspaceEvents = _keyspaceEventsAtStart = await ReadKeyspaceEventsAsync();

        await ApplyProfileAsync();
        _reader = Task.Run(ReaderLoopAsync);
        _probe = Task.Run(ProbeLoopAsync);
    }

    /// <summary>Connect with the current profile (see Setup.cs) and swap the live connection.</summary>
    private async Task ApplyProfileAsync()
    {
        await _reconnect.WaitAsync(); // two quick key presses must not race over _app
        try
        {
            var muxer = await ConnectAppAsync();
            var old = _app;
            _app = muxer;
            _db = muxer.GetDatabase();
            var cache = muxer.GetClientCache();
            Log($"connected [{Profile.Name} / {Tracking.Name} / {Admission}]: {(cache is null ? "no client cache" : $"cache on, EffectiveTrackingMode {cache.EffectiveTrackingMode}, Admission {cache.Options.Admission}")}");
            if (old is not null) await old.DisposeAsync();
        }
        catch (Exception ex) { Log($"reconnect failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _reconnect.Release(); }
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
        Log($"policy [{Profile.Name}]: {Profile.Summary}");
        _ = ApplyProfileAsync();
    }

    public void CycleTracking()
    {
        TrackingIndex = (TrackingIndex + 1) % TrackingModes.Length;
        Mark("T");
        Log($"tracking [{Tracking.Name}]: {Tracking.Summary}");
        _ = ApplyProfileAsync();
    }

    public void ToggleAdmission()
    {
        Admission = Admission == CacheAdmission.OnFirstMiss ? CacheAdmission.OnRepeatedMiss : CacheAdmission.OnFirstMiss;
        Mark("D");
        Log(Admission == CacheAdmission.OnFirstMiss
            ? "Admission OnFirstMiss: every cacheable reply is stored the first time it misses"
            : "Admission OnRepeatedMiss (the default): a first miss is only remembered; the reply is stored on the next miss");
        if (Tracking.Mode == CacheTrackingMode.Default && Tracking.Prefixed)
        {
            Log("  (not consulted under by-prefix: an in-prefix read is an implicit CacheOptIn, which stores on the first miss)");
        }
        _ = ApplyProfileAsync();
    }

    /// <summary>Flip the server's notify-keyspace-events between off and KA, from the back door. No reconnect:
    /// the keyspace mode's subscription is already in place; this decides whether anything is published to it.</summary>
    public void ToggleKeyspaceEvents()
    {
        Mark("K");
        string value = KeyspaceEventsInvalidate(_keyspaceEvents) ? "" : "KA";
        Log(value.Length == 0
            ? "back door: CONFIG SET notify-keyspace-events \"\"  (keyspace mode now hears nothing: it behaves as None)"
            : "back door: CONFIG SET notify-keyspace-events KA  (keyspace mode now hears every write, expiry and eviction under the prefix)");
        _ = Task.Run(async () =>
        {
            try
            {
                await _server.ConfigSetAsync("notify-keyspace-events", value);
                lock (_sync) { _keyspaceEvents = value; }
            }
            catch (Exception ex) { Log($"CONFIG SET failed: {ex.Message}"); }
        });
    }

    /// <summary>Whether a notify-keyspace-events value publishes anything that could invalidate a cached read:
    /// K (keyspace channel) plus at least one event class that changes a key.</summary>
    private static bool KeyspaceEventsInvalidate(string value)
        => value.Contains('K') && value.Any(c => c is 'A' or 'g' or 's' or 'h' or 'z' or 'x' or 'e' or 'l' or 't' or 'd' or '$');

    /// <summary>What the current mode will do about a change made by another client: the text after each back-door action.</summary>
    private string InvalidationNote(bool expiry = false, bool flush = false)
    {
        var mode = _app?.GetClientCache()?.EffectiveTrackingMode;
        switch (mode)
        {
            case null:
                return "no cache: the next read fetches it";
            case CacheTrackingMode.None:
                return "nothing is announced in None mode: the cached copy lives until TimeToLive";
            case CacheTrackingMode.KeyspaceNotifications:
                if (flush) return "FLUSHDB emits no per-key keyspace event: the cache keeps everything, stale until TimeToLive";
                if (!KeyspaceEventsInvalidate(_keyspaceEvents)) return "notify-keyspace-events is off: nothing is published; stale until TimeToLive ('K' turns it on)";
                return expiry ? "the x event IS published: keyspace mode drops the copy as the key expires" : "a __keyspace@ event drops the app's copy";
            default:
                if (expiry) return "the server pushes an invalidation once it notices the expiry: near-instant on this tiny keyspace, lazy on a large one; TimeToLive is the backstop";
                if (flush) return "the server pushes a flush invalidation: the app's whole cache is discarded";
                return "the server pushes an invalidation; the app's copy is dropped";
        }
    }

    /// <summary>Write the selected key from the other connection: the server pushes an invalidation.</summary>
    public void WriteSelected()
    {
        var key = _keys[Selected];
        var value = Price();
        Mark("W");
        Log($"back door: SET {key} {value}  ({InvalidationNote()})");
        _ = _backDoor.GetDatabase().StringSetAsync(key, value);
    }

    /// <summary>Expire the selected key from the other connection: announced only once the server notices the expiry, and not at all in None mode.</summary>
    public void ExpireSelected()
    {
        var key = _keys[Selected];
        Mark("X");
        Log($"back door: EXPIRE {key} 1  (in 1s: {InvalidationNote(expiry: true)})");
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
        Log("25 one-off reads of fresh in-prefix keys: watch Count/Stored - OnFirstMiss (and by-prefix's implicit CacheOptIn) store all 25; OnRepeatedMiss stores none");
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
        Log($"back door: FLUSHDB  ({InvalidationNote(flush: true)}; keys are gone until re-seeded)");
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
                keys[i] = new KeyView(_keys[i], _cached[i], _serverValues[i], _lastMicros[i], _keyReads[i], i == Selected);
            }
            var cache = _app?.GetClientCache();
            var view = cache is null
                ? new CacheView(false, "none", "n/a", 0, 0, 0, 0, 0, 0, 0, 0, 0, _reads, _serverGets - _serverGetsAtStart)
                : new CacheView(true, cache.EffectiveTrackingMode.ToString(), cache.Options.Admission.ToString(), cache.Count, cache.Bytes, cache.Stored, cache.Evicted, cache.Promoted,
                    cache.Expired, cache.Refreshes, cache.ServedStale, cache.TrackedKeys, _reads, _serverGets - _serverGetsAtStart);
            var last = _windowCount > 0 ? windows[^1] : default;
            return new Snapshot(windows, keys, view, _log.ToArray(), Profile, Tracking, _keyspaceEvents, Bypass, Paused, last.Reads, last.ServerReads);
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
        try
        {
            // jitter the first touch of each key: filled all at once, the whole catalogue would reach
            // TimeToLive in the same instant, and every TTL boundary would be a synchronized miss
            // stampede (TimeToLive is fixed, so entries expire in the order - and spacing - they fill)
            foreach (var k in Enumerable.Range(0, KeyCount).OrderBy(_ => Random.Shared.Next()))
            {
                await ReadKeyAsync(k, token);
                await Task.Delay(Random.Shared.Next(50, 300), token);
            }
            while (!token.IsCancellationRequested)
            {
                if (!Paused) await ReadKeyAsync(NextKey(), token);
                await Task.Delay(5, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>What <see cref="ReadKeyAsync"/> (CoreUsage.cs) records about each read, for the UI.</summary>
    private void RecordRead(int k, RedisValue value, TimeSpan elapsed)
    {
        lock (_sync)
        {
            _cached[k] = value.IsNull ? null : value.ToString();
            _lastMicros[k] = elapsed.TotalMilliseconds * 1000;
            _keyReads[k]++;
        }
        Interlocked.Increment(ref _reads);
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
                List<(int, TimeSpan)>? recovered = null;
                lock (_sync)
                {
                    long now = Stopwatch.GetTimestamp();
                    for (int i = 0; i < KeyCount; i++)
                    {
                        _serverValues[i] = values[i].IsNull ? null : values[i].ToString();
                        // how long each row stays out of sync is the cost of the chosen tracking mode, so say it
                        if (_keyReads[i] == 0) continue; // not read yet: nothing to be stale
                        if (_cached[i] != _serverValues[i]) { if (_staleSince[i] == 0) _staleSince[i] = now; }
                        else if (_staleSince[i] != 0)
                        {
                            (recovered ??= []).Add((i, Stopwatch.GetElapsedTime(_staleSince[i], now)));
                            _staleSince[i] = 0;
                        }
                    }
                    _serverGets = gets;
                }
                CloseWindow();
                if (recovered is not null)
                {
                    foreach (var (i, span) in recovered) Log($"{_keys[i]} was stale for ~{span.TotalSeconds:0.0}s (probed every 250ms)");
                }
            }
            catch (Exception ex)
            {
                Log($"probe failed: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(250, token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<string> ReadKeyspaceEventsAsync()
    {
        var pairs = await _server.ConfigGetAsync("notify-keyspace-events");
        return pairs.Length == 0 ? "" : pairs[0].Value;
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

    // Random.Shared: prices are made on the UI thread and back-door tasks, the key mix on the reader
    // loop - a shared System.Random instance is not thread-safe
    private static string Price() => $"£{Random.Shared.Next(1, 200)}.{Random.Shared.Next(0, 100):00}";

    private void Mark(string marker) { lock (_sync) { _pendingMarker = marker; } }

    private void Log(string text)
    {
        var cache = _app?.GetClientCache();
        string counters = cache is null ? "" : $"  [Count={cache.Count} Stored={cache.Stored} Expired={cache.Expired} ServedStale={cache.ServedStale} TrackedKeys={cache.TrackedKeys}]";
        lock (_sync)
        {
            var now = DateTime.Now;
            _log.Enqueue(new LogLine(now, text));
            while (_log.Count > 14) _log.Dequeue();
            _transcript.Add($"{now:HH:mm:ss.fff} {text}{counters}");
        }
    }

    /// <summary>Every event so far, each with the cache counters as they stood: the scripted tour prints this on exit.</summary>
    public string[] Transcript { get { lock (_sync) { return _transcript.ToArray(); } } }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_reader, _probe).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        if (_app is not null) await _app.DisposeAsync();
        if (_keyspaceEvents != _keyspaceEventsAtStart)
        {
            // 'K' changes the container's live config; put it back the way we found it
            try { await _server.ConfigSetAsync("notify-keyspace-events", _keyspaceEventsAtStart); } catch { }
        }
        await _backDoor.DisposeAsync();
    }
}
