using StackExchange.Redis;

namespace ArraysDemo;

/// <summary>
/// A telemetry ring over one native array: <c>telemetry:cpu</c>, 48 slots, one CPU reading every
/// 200ms via <c>ARRING</c> - the server owns the ring: the cursor, the wrap-around, the overwrite
/// of the oldest slot. No client-side ring arithmetic, no LPUSH+LTRIM dance, no index key.
///
/// Everything observed comes from the server, through the first-class <c>IDatabase.Array*</c>
/// API: the strip is <c>ArrayGetRange</c> (holes and all), the cursor is <c>ArrayNext</c>, the
/// stats are <c>ArrayOperation</c> aggregates computed where the data lives, spike hunting is
/// <c>ArrayGrep</c> with a server-side regex, and the storage internals are <c>ArrayInfo</c>.
/// </summary>
sealed class Engine : IAsyncDisposable
{
    private const string Endpoint = "localhost:16481";
    public const int RingSize = 48;
    public static readonly RedisKey Key = "telemetry:cpu";
    private static readonly TimeSpan ProducerPeriod = TimeSpan.FromMilliseconds(200);
    private const string SpikeRegex = "^(9[0-9]|100)$"; // readings of 90+

    private ConnectionMultiplexer? _muxer;
    private IDatabase _db = null!;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];

    private long?[] _slots = new long?[RingSize];
    private long _next = -1, _used, _len, _sum, _min, _max;
    private long[] _recent = [];
    private ArrayInfo? _info;
    private readonly HashSet<long> _grepHits = [];
    private DateTime _grepHighlightUntil;
    private long _writes, _wraps, _grepMatches;
    private bool _spikeNext, _producerPaused;
    private bool _errors;

    public List<string> Transcript { get; } = [];

    public bool HadErrors { get { lock (_lock) { return _errors; } } }

    public long GrepMatches { get { lock (_lock) { return _grepMatches; } } }

    public async Task StartAsync()
    {
        _muxer = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
        _db = _muxer.GetDatabase();
        await _muxer.GetServer(_muxer.GetEndPoints()[0]).FlushDatabaseAsync();

        Log($"ARRING {Key} {RingSize} <reading> every {ProducerPeriod.TotalMilliseconds:0}ms: the server owns the ring");
        _ = Task.Run(() => ProducerLoopAsync(_stop.Token));
        _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    // ---- the producer ----

    private async Task ProducerLoopAsync(CancellationToken token)
    {
        double phase = 0;
        while (!token.IsCancellationRequested)
        {
            if (!_producerPaused)
            {
                bool spike;
                lock (_lock) { spike = _spikeNext; _spikeNext = false; }
                phase += 0.04;
                long reading = spike
                    ? Random.Shared.Next(94, 101)
                    : Math.Clamp((long)(50 + 20 * Math.Sin(phase) + Random.Shared.Next(-8, 9)), 0, 89);
                try
                {
                    RedisArrayIndex index = await _db.ArrayRingAsync(Key, RingSize, reading);
                    long writes = Interlocked.Increment(ref _writes);
                    if (index.Value == 0 && writes > 1)
                    {
                        Interlocked.Increment(ref _wraps);
                        Log($"the ring wrapped: write #{writes} landed back in slot 0, overwriting the oldest reading");
                    }
                    if (spike) Log($"injected a spike: ARRING ... {reading} -> slot {index}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"ERR producer: {ex.Message}");
                    lock (_lock) { _errors = true; }
                }
            }
            try { await Task.Delay(ProducerPeriod, token); } catch (OperationCanceledException) { }
        }
    }

    // ---- the observer ----

    private async Task PollLoopAsync(CancellationToken token)
    {
        int tick = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                RedisValue[] range = await _db.ArrayGetRangeAsync(Key, 0, RingSize - 1);
                var slots = new long?[RingSize];
                for (int i = 0; i < range.Length && i < RingSize; i++)
                {
                    slots[i] = range[i].IsNull ? null : (long)range[i];
                }
                RedisArrayIndex? next = await _db.ArrayNextAsync(Key);
                long used = Aggregate(await _db.ArrayOperationAsync(Key, 0, RingSize - 1, ArrayOperation.Used));
                long sum = Aggregate(await _db.ArrayOperationAsync(Key, 0, RingSize - 1, ArrayOperation.Sum));
                long min = Aggregate(await _db.ArrayOperationAsync(Key, 0, RingSize - 1, ArrayOperation.Min));
                long max = Aggregate(await _db.ArrayOperationAsync(Key, 0, RingSize - 1, ArrayOperation.Max));
                long len = (long)(await _db.ArrayLengthAsync(Key)).Value;
                RedisValue[] recent = await _db.ArrayLastItemsAsync(Key, 5, reverse: true);
                ArrayInfo? info = tick % 4 == 0 ? await _db.ArrayInfoAsync(Key) : null;

                lock (_lock)
                {
                    _slots = slots;
                    _next = next is { } n ? (long)n.Value : -1;
                    (_used, _sum, _min, _max, _len) = (used, sum, min, max, len);
                    _recent = [.. recent.Where(static v => !v.IsNull).Select(static v => (long)v)];
                    if (info is not null) _info = info;
                    if (_grepHighlightUntil < DateTime.UtcNow) _grepHits.Clear();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the very first poll can race the very first ARRING; only treat it as an error
                // once the key should exist
                if (Interlocked.Read(ref _writes) > 0)
                {
                    Log($"ERR poll: {ex.Message}");
                    lock (_lock) { _errors = true; }
                }
            }
            tick++;
            try { await Task.Delay(250, token); } catch (OperationCanceledException) { }
        }

        static long Aggregate(RedisValue value) => value.IsNull ? 0 : (long)value;
    }

    // ---- keyboard actions ----

    public void InjectSpike()
    {
        lock (_lock) { _spikeNext = true; }
    }

    public void Grep() => Fire(async () =>
    {
        var request = new ArrayGrepRequest { Start = 0, End = RingSize - 1 };
        request.AddPredicate(ArrayGrepRequest.Predicate.Regex(SpikeRegex));
        RedisArrayEntry[] hits = await _db.ArrayGrepAsync(Key, request);
        lock (_lock)
        {
            _grepHits.Clear();
            foreach (var hit in hits) { _grepHits.Add((long)hit.Index.Value); }
            _grepMatches += hits.Length;
            _grepHighlightUntil = DateTime.UtcNow.AddSeconds(3);
        }
        Log($"ARGREP 0 {RingSize - 1} RE '{SpikeRegex}' -> {hits.Length} slot(s) hold a 90+ reading" +
            (hits.Length > 0 ? $": [{string.Join(", ", hits.Select(static h => h.Index))}]" : ""));
    });

    public void SeekToStart() => Fire(async () =>
    {
        bool ok = await _db.ArraySeekAsync(Key, 0);
        Log($"ARSEEK {Key} 0 -> {(ok ? 1 : 0)}: the cursor is state too - the next write lands in slot 0, age be damned");
    });

    public void RetconLast() => Fire(async () =>
    {
        long next;
        lock (_lock) { next = _next; }
        if (next < 0) return;
        long slot = (next - 1 + RingSize) % RingSize;
        bool wasEmpty = await _db.ArraySetAsync(Key, slot, 0);
        Log($"ARSET {Key} {slot} 0 -> {(wasEmpty ? 1 : 0)} (slots newly occupied): the last reading retconned to 0; the cursor did not move");
    });

    public void ToggleProducer()
    {
        bool paused;
        lock (_lock) { paused = _producerPaused = !_producerPaused; }
        Log(paused ? "producer paused: the ring holds its breath (nothing expires - it is storage, not a stream)" : "producer resumed");
    }

    // ---- state out ----

    public Snapshot Snapshot()
    {
        lock (_lock)
        {
            return new Snapshot(
                [.. _slots],
                _next,
                _used,
                _len,
                _sum,
                _min,
                _max,
                _recent,
                _info,
                [.. _grepHits],
                Interlocked.Read(ref _writes),
                Interlocked.Read(ref _wraps),
                _producerPaused,
                [.. _log.TakeLast(12)]);
        }
    }

    private void Fire(Func<Task> action) => _ = Task.Run(async () =>
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"ERR {ex.GetType().Name}: {ex.Message}");
            lock (_lock) { _errors = true; }
        }
    });

    private void Log(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_lock)
        {
            _log.Add(line);
            if (_log.Count > 200) _log.RemoveAt(0);
            Transcript.Add(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_muxer is not null) await _muxer.DisposeAsync();
    }
}

/// <summary>Everything the UI renders, captured atomically.</summary>
sealed record Snapshot(
    long?[] Slots,
    long Next,
    long Used,
    long Length,
    long Sum,
    long Min,
    long Max,
    long[] Recent,
    ArrayInfo? Info,
    long[] GrepHits,
    long Writes,
    long Wraps,
    bool ProducerPaused,
    string[] Log);
