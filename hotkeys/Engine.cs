using StackExchange.Redis;

namespace HotKeysDemo;

/// <summary>
/// A workload plus a profiler. The workload is deliberately lopsided: GETs over the user keys
/// follow a Zipf distribution (a few keys get most of the traffic - like real caches do), one
/// pair of big sets is hammered with SINTERCARD (lots of CPU, a one-integer reply), and one list
/// is drained with LRANGE (cheap to compute, heavy on the wire). HOTKEYS tells those three
/// stories apart: the Zipf head ranks by volume, SINTERCARD ranks by CPU but not bytes, and the
/// LRANGE key ranks by bytes but not CPU.
///
/// Everything displayed comes from the server: <c>HOTKEYS GET</c> is polled while a capture runs,
/// and the leaderboard is <see cref="HotKeysResult.CpuByKey"/> / <see cref="HotKeysResult.NetworkBytesByKey"/>
/// as the server reported them.
/// </summary>
sealed class Engine : IAsyncDisposable
{
    private const string Endpoint = "localhost:16451";
    private const int UserKeys = 400;
    private const int ReportCount = 12;          // COUNT: keys retained per metric in the report
    public static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SpikeDuration = TimeSpan.FromSeconds(10);
    private static readonly long[] SampleRatios = [1, 8, 64];

    private ConnectionMultiplexer? _muxer;
    private IDatabase _db = null!;
    private IServer _server = null!;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];
    private readonly double[] _zipfCdf = BuildZipfCdf(UserKeys, 1.0);

    private HotKeysResultSnapshot? _result;
    private DateTime _captureStartedLocal;
    private bool _captureRequested;
    private int _sampleRatioIndex;
    private bool _sortByNetwork;
    private long _opsSent;
    private double _opsPerSecond;
    private string? _spikeKey;
    private DateTime _spikeUntil;
    private bool _workloadPaused;
    private bool _errors;

    public List<string> Transcript { get; } = [];

    public bool HadErrors { get { lock (_lock) { return _errors; } } }

    public async Task StartAsync()
    {
        _muxer = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
        _db = _muxer.GetDatabase();
        _server = _muxer.GetServer(_muxer.GetEndPoints()[0]);
        await _server.FlushDatabaseAsync();

        // seed the dataset: 400 small user keys, two 5000-member sets, one chunky list
        var seed = new List<Task>();
        for (int i = 0; i < UserKeys; i++)
        {
            seed.Add(_db.StringSetAsync(UserKey(i), $"profile-data-for-user-{i:000}-{Guid.NewGuid():N}"));
        }
        RedisValue[] a = new RedisValue[5000], b = new RedisValue[5000];
        for (int i = 0; i < 5000; i++)
        {
            a[i] = i;          // 0..4999
            b[i] = i + 2500;   // 2500..7499: half overlap, so the intersection is real work
        }
        seed.Add(_db.SetAddAsync("tags:a", a));
        seed.Add(_db.SetAddAsync("tags:b", b));
        RedisValue[] items = new RedisValue[300];
        for (int i = 0; i < 300; i++)
        {
            items[i] = $"catalog-item-{i:000}-{new string('x', 48)}";
        }
        seed.Add(_db.ListRightPushAsync("catalog", items));
        await Task.WhenAll(seed);

        Log($"seeded {UserKeys} user keys (Zipf traffic), tags:a/tags:b (SINTERCARD = CPU), catalog (LRANGE = bytes)");
        Log("press C to start a HOTKEYS capture; the workload is already running");

        _ = Task.Run(() => WorkloadLoopAsync(_stop.Token));
        _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    private static string UserKey(int rank) => $"user:{rank:000}";

    // ---- the workload ----

    private static double[] BuildZipfCdf(int n, double skew)
    {
        var cdf = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++) { sum += 1.0 / Math.Pow(i + 1, skew); cdf[i] = sum; }
        for (int i = 0; i < n; i++) { cdf[i] /= sum; }
        return cdf;
    }

    private string PickZipfKey()
    {
        double p = Random.Shared.NextDouble();
        int idx = Array.BinarySearch(_zipfCdf, p);
        if (idx < 0) idx = ~idx;
        return UserKey(Math.Min(idx, UserKeys - 1));
    }

    private async Task WorkloadLoopAsync(CancellationToken token)
    {
        int tick = 0;
        long lastOps = 0;
        var lastRate = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            if (!_workloadPaused)
            {
                string? spike;
                lock (_lock)
                {
                    spike = _spikeUntil > DateTime.UtcNow ? _spikeKey : null;
                    if (spike is null) _spikeKey = null;
                }

                var batch = new List<Task>(40);
                for (int i = 0; i < 30; i++)
                {
                    // during a spike, 60% of reads pile onto one key
                    string key = spike is not null && Random.Shared.NextDouble() < 0.6 ? spike : PickZipfKey();
                    batch.Add(_db.StringGetAsync(key));
                }
                for (int i = 0; i < 4; i++)
                {
                    batch.Add(_db.StringSetAsync(PickZipfKey(), $"updated-{Guid.NewGuid():N}"));
                }
                if (tick % 10 == 0)
                {
                    // CPU story: intersect two 5000-member sets, reply is a single integer
                    batch.Add(_db.SetIntersectionLengthAsync(["tags:a", "tags:b"]));
                    // bytes story: ~20KB reply, trivial to compute
                    batch.Add(_db.ListRangeAsync("catalog"));
                }
                try
                {
                    await Task.WhenAll(batch);
                    Interlocked.Add(ref _opsSent, batch.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"ERR workload: {ex.Message}");
                    lock (_lock) { _errors = true; }
                }
            }

            var now = DateTime.UtcNow;
            if ((now - lastRate).TotalSeconds >= 1)
            {
                long ops = Interlocked.Read(ref _opsSent);
                lock (_lock) { _opsPerSecond = (ops - lastOps) / (now - lastRate).TotalSeconds; }
                lastOps = ops;
                lastRate = now;
            }

            tick++;
            try { await Task.Delay(50, token); } catch (OperationCanceledException) { }
        }
    }

    // ---- the profiler ----

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool wanted;
            lock (_lock) { wanted = _captureRequested; }
            if (wanted)
            {
                try
                {
                    var result = await _server.HotKeysGetAsync();
                    if (result is not null)
                    {
                        var snap = HotKeysResultSnapshot.From(result);
                        lock (_lock)
                        {
                            bool wasActive = _result?.TrackingActive ?? false;
                            _result = snap;
                            if (wasActive && !snap.TrackingActive)
                            {
                                Log($"capture finished: {snap.CollectionDuration.TotalSeconds:0.0}s measured, " +
                                    $"CPU {snap.TotalCpuMs:0}ms, {snap.TotalNetworkBytes:N0} net bytes - the report stays until RESET");
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"ERR HOTKEYS GET: {ex.Message}");
                    lock (_lock) { _errors = true; }
                }
            }
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { }
        }
    }

    public void StartCapture()
    {
        long ratio = SampleRatios[_sampleRatioIndex];
        Fire(async () =>
        {
            try
            {
                await _server.HotKeysStartAsync(
                    metrics: HotKeysMetrics.Cpu | HotKeysMetrics.Network,
                    count: ReportCount,
                    duration: CaptureDuration,
                    sampleRatio: ratio);
                lock (_lock)
                {
                    _captureRequested = true;
                    _captureStartedLocal = DateTime.UtcNow;
                }
                Log($"HOTKEYS START METRICS CPU NET COUNT {ReportCount} DURATION {CaptureDuration.TotalSeconds:0} SAMPLE {ratio}" +
                    (ratio > 1 ? $" - measuring every {ratio}th command" : ""));
            }
            catch (RedisServerException ex) when (ex.Message.Contains("already in progress"))
            {
                Log("a capture is already running (HOTKEYS START refuses to stack; X stops it)");
            }
        });
    }

    public void StopCapture() => Fire(async () =>
    {
        await _server.HotKeysStopAsync();
        Log("HOTKEYS STOP: capture ended early; the data collected so far is still readable");
    });

    public void ResetCapture() => Fire(async () =>
    {
        await _server.HotKeysResetAsync();
        lock (_lock)
        {
            _captureRequested = false;
            _result = null;
        }
        Log("HOTKEYS RESET: report discarded");
    });

    public void CycleSampleRatio()
    {
        long ratio;
        lock (_lock)
        {
            _sampleRatioIndex = (_sampleRatioIndex + 1) % SampleRatios.Length;
            ratio = SampleRatios[_sampleRatioIndex];
        }
        Log($"sample ratio for the NEXT capture: {ratio} (SAMPLE trades accuracy for lower profiling overhead)");
    }

    public void ToggleSort()
    {
        bool byNet;
        lock (_lock) { byNet = _sortByNetwork = !_sortByNetwork; }
        Log($"leaderboard now ranked by {(byNet ? "network bytes (by-net-bytes)" : "CPU time (by-cpu-time-us)")}");
    }

    public void Spike()
    {
        string key = UserKey(Random.Shared.Next(UserKeys / 2, UserKeys)); // from the cold tail, so the climb is visible
        lock (_lock)
        {
            _spikeKey = key;
            _spikeUntil = DateTime.UtcNow + SpikeDuration;
        }
        Log($"SPIKE: {key} now takes 60% of reads for {SpikeDuration.TotalSeconds:0}s - watch it climb the board");
    }

    /// <summary>Scripted mode: the current spike key, if any.</summary>
    public string? SpikeKey { get { lock (_lock) { return _spikeKey; } } }

    public void ToggleWorkload()
    {
        bool paused;
        lock (_lock) { paused = _workloadPaused = !_workloadPaused; }
        Log(paused ? "workload paused" : "workload resumed");
    }

    // ---- state out ----

    public Snapshot Snapshot()
    {
        lock (_lock)
        {
            var r = _result;
            double secondsLeft = 0;
            if (r is { TrackingActive: true })
            {
                secondsLeft = Math.Max(0, (CaptureDuration - (DateTime.UtcNow - _captureStartedLocal)).TotalSeconds);
            }
            return new Snapshot(
                r,
                secondsLeft,
                SampleRatios[_sampleRatioIndex],
                _sortByNetwork,
                _opsPerSecond,
                Interlocked.Read(ref _opsSent),
                _spikeUntil > DateTime.UtcNow ? _spikeKey : null,
                _workloadPaused,
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
        if (_muxer is not null)
        {
            try { await _server.HotKeysResetAsync(); } catch { /* best effort: don't leave a profiler running */ }
            await _muxer.DisposeAsync();
        }
    }
}

/// <summary>
/// <see cref="HotKeysResult"/> exposes its per-key reports as <see cref="ReadOnlySpan{T}"/>, which a
/// snapshot type can't hold; this copies the bits the UI renders into plain arrays.
/// </summary>
sealed record HotKeysResultSnapshot(
    bool TrackingActive,
    long SampleRatio,
    TimeSpan CollectionDuration,
    double TotalCpuMs,
    long TotalNetworkBytes,
    (string Key, long CpuMicroseconds)[] CpuByKey,
    (string Key, long Bytes)[] NetworkByKey)
{
    public static HotKeysResultSnapshot From(HotKeysResult r)
    {
        var cpu = new (string, long)[r.CpuByKey.Length];
        for (int i = 0; i < cpu.Length; i++)
        {
            cpu[i] = (r.CpuByKey[i].Key.ToString()!, (long)r.CpuByKey[i].Duration.TotalMicroseconds);
        }
        var net = new (string, long)[r.NetworkBytesByKey.Length];
        for (int i = 0; i < net.Length; i++)
        {
            net[i] = (r.NetworkBytesByKey[i].Key.ToString()!, r.NetworkBytesByKey[i].Bytes);
        }
        return new(
            r.TrackingActive,
            r.SampleRatio,
            r.CollectionDuration,
            r.TotalCpuTime?.TotalMilliseconds ?? 0,
            r.TotalNetworkBytes ?? 0,
            cpu,
            net);
    }
}

/// <summary>Everything the UI renders, captured atomically.</summary>
sealed record Snapshot(
    HotKeysResultSnapshot? Result,
    double CaptureSecondsLeft,
    long NextSampleRatio,
    bool SortByNetwork,
    double OpsPerSecond,
    long OpsSent,
    string? SpikeKey,
    bool WorkloadPaused,
    string[] Log);
