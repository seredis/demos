using System.Diagnostics;
using System.Text.Json;
using StackExchange.Redis;

namespace VectorSetsDemo;

/// <summary>
/// Nearest-neighbour image matching over MNIST digits. The 784 raw pixels of each 28×28 image are
/// the vector - no neural net, no embedding service - and the corpus is loaded three times with
/// the same data but different quantization (<c>NOQUANT</c>/<c>Q8</c>/<c>BIN</c>), so the
/// accuracy/memory trade-off is directly observable on the same queries.
///
/// Every query also runs <c>VSIM ... EF ... TRUTH</c>-style ground truth: an exact (linear scan)
/// search on the float32 key, so each displayed result set carries its recall against the real
/// answer, not just plausible-looking neighbours.
/// </summary>
sealed class Engine : IAsyncDisposable
{
    private const string Endpoint = "localhost:16461";
    public const int CorpusSize = 5000;
    public const int MatchCount = 5;

    public static readonly (string Key, VectorSetQuantization Quant, string Label)[] Keys =
    [
        ("digits:f32", VectorSetQuantization.None, "NOQUANT (float32)"),
        ("digits:q8", VectorSetQuantization.Int8, "Q8 (int8, the default)"),
        ("digits:bin", VectorSetQuantization.Binary, "BIN (1 bit/component)"),
    ];

    private static readonly (string? Expression, string Label)[] Filters =
    [
        (null, "none"),
        (".label == 7", ".label == 7 (only sevens)"),
        (".label >= 5", ".label >= 5"),
    ];

    private ConnectionMultiplexer? _muxer;
    private IDatabase _db = null!;
    private Mnist.Dataset _data = null!;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];
    private readonly Dictionary<string, VectorSetInfoSnapshot> _info = [];

    private string _phase = "starting...";
    private bool _ready;
    private int _keyIndex = 1; // start on Q8: the server default
    private int _filterIndex;
    private bool _exact;
    private int _queryIndex = -1;
    private MatchRow[] _matches = [];
    private double _lastQueryMs, _recall = -1;
    private bool _errors;

    public List<string> Transcript { get; } = [];

    public bool HadErrors { get { lock (_lock) { return _errors; } } }

    public bool Ready { get { lock (_lock) { return _ready; } } }

    public async Task StartAsync()
    {
        _muxer = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
        _db = _muxer.GetDatabase();
        _ = Task.Run(LoadAsync); // the TUI comes up immediately and shows load progress
    }

    private async Task LoadAsync()
    {
        try
        {
            _data = await Mnist.LoadAsync(
                Path.Combine(AppContext.BaseDirectory, "../../../data"),
                msg => { lock (_lock) { _phase = msg; } Log(msg); });

            bool loaded = true;
            foreach (var (key, _, _) in Keys)
            {
                try
                {
                    if (await _db.VectorSetLengthAsync(key) != CorpusSize) { loaded = false; break; }
                }
                catch (RedisServerException) { loaded = false; break; }
            }

            if (loaded)
            {
                Log("vector sets already loaded (VCARD matches); skipping the import");
            }
            else
            {
                await _muxer!.GetServer(_muxer.GetEndPoints()[0]).FlushDatabaseAsync();
                foreach (var (key, quant, label) in Keys)
                {
                    var sw = Stopwatch.StartNew();
                    for (int start = 0; start < CorpusSize; start += 400)
                    {
                        int end = Math.Min(start + 400, CorpusSize);
                        var batch = new Task[end - start];
                        for (int i = start; i < end; i++)
                        {
                            var req = VectorSetAddRequest.Member(Member(i), _data.Vector(i), $"{{\"label\":{_data.Labels[i]}}}");
                            req.Quantization = quant;
                            batch[i - start] = _db.VectorSetAddAsync(key, req);
                        }
                        await Task.WhenAll(batch);
                        lock (_lock) { _phase = $"VADD → {key} ({label}): {end}/{CorpusSize}"; }
                    }
                    Log($"loaded {key}: {CorpusSize} × 784-dim vectors as {label} in {sw.Elapsed.TotalSeconds:0.0}s");
                }
            }

            foreach (var (key, _, _) in Keys)
            {
                if (await _db.VectorSetInfoAsync(key) is { } info)
                {
                    long bytes = (long)await _db.ExecuteAsync("MEMORY", "USAGE", key);
                    lock (_lock) { _info[key] = new(info.QuantizationRaw ?? info.Quantization.ToString(), info.Dimension, info.Length, info.MaxLevel, bytes); }
                }
            }
            lock (_lock)
            {
                _ready = true;
                _phase = "ready";
            }
            Log($"three copies of the same corpus; MEMORY USAGE: " + string.Join(", ",
                Keys.Select(k => $"{k.Key}={_info[k.Key].MemoryBytes / 1024.0 / 1024.0:0.0}MB")));
            NewQuery();
        }
        catch (Exception ex)
        {
            Log($"ERR load: {ex.Message}");
            lock (_lock) { _errors = true; _phase = $"load failed: {ex.Message}"; }
        }
    }

    private static string Member(int index) => $"d:{index:0000}";

    private static int IndexOf(RedisValue member) => int.Parse(((string)member!).AsSpan(2));

    // ---- queries ----

    public void NewQuery()
    {
        if (!Ready) return;
        // queries are held out: the corpus is images 0..4999, queries come from 5000..9999
        int idx = Random.Shared.Next(CorpusSize, _data.Count);
        lock (_lock) { _queryIndex = idx; }
        RunQuery();
    }

    public void CycleKey()
    {
        lock (_lock) { _keyIndex = (_keyIndex + 1) % Keys.Length; }
        RunQuery();
    }

    public void CycleFilter()
    {
        lock (_lock) { _filterIndex = (_filterIndex + 1) % Filters.Length; }
        RunQuery();
    }

    public void ToggleExact()
    {
        lock (_lock) { _exact = !_exact; }
        RunQuery();
    }

    /// <summary>Scripted mode: pin the state instead of cycling blindly.</summary>
    public void Set(int? keyIndex = null, int? filterIndex = null, bool? exact = null)
    {
        lock (_lock)
        {
            if (keyIndex is { } k) _keyIndex = k;
            if (filterIndex is { } f) _filterIndex = f;
            if (exact is { } e) _exact = e;
        }
        RunQuery();
    }

    private void RunQuery() => Fire(async () =>
    {
        int queryIdx, keyIdx, filterIdx;
        bool exact;
        lock (_lock)
        {
            if (!_ready || _queryIndex < 0) return;
            (queryIdx, keyIdx, filterIdx, exact) = (_queryIndex, _keyIndex, _filterIndex, _exact);
        }
        var result = await SearchAsync(Keys[keyIdx].Key, queryIdx, filterIdx, exact);

        // ground truth: an exact scan of the float32 copy, same filter, same count
        var truth = keyIdx == 0 && exact
            ? result
            : await SearchAsync(Keys[0].Key, queryIdx, filterIdx, exact: true);
        var truthSet = truth.Rows.Select(static r => r.CorpusIndex).ToHashSet();
        double recall = truth.Rows.Length == 0 ? 1 : result.Rows.Count(r => truthSet.Contains(r.CorpusIndex)) / (double)truth.Rows.Length;

        lock (_lock)
        {
            _matches = result.Rows;
            _lastQueryMs = result.Milliseconds;
            _recall = recall;
        }
        string filter = Filters[filterIdx].Expression is { } f ? $" FILTER '{f}'" : "";
        Log($"VSIM {Keys[keyIdx].Key}{filter}{(exact ? " TRUTH" : "")} COUNT {MatchCount} -> " +
            $"[{string.Join(", ", result.Rows.Select(static r => r.Label))}] in {result.Milliseconds:0.00}ms; " +
            $"recall vs exact-f32: {recall:P0}");
    });

    private async Task<(MatchRow[] Rows, double Milliseconds)> SearchAsync(string key, int queryIdx, int filterIdx, bool exact)
    {
        var request = VectorSetSimilaritySearchRequest.ByVector(_data.Vector(queryIdx));
        request.Count = MatchCount;
        request.WithScores = true;
        request.WithAttributes = true;
        request.UseExactSearch = exact;
        request.FilterExpression = Filters[filterIdx].Expression;

        var sw = Stopwatch.StartNew();
        using var lease = await _db.VectorSetSimilaritySearchAsync(key, request);
        sw.Stop();

        if (lease is null) return ([], sw.Elapsed.TotalMilliseconds);
        var rows = new MatchRow[lease.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var r = lease.Span[i];
            int corpusIdx = IndexOf(r.Member);
            int label = r.AttributesJson is { } json
                ? JsonDocument.Parse(json).RootElement.GetProperty("label").GetInt32()
                : _data.Labels[corpusIdx];
            rows[i] = new MatchRow(corpusIdx, label, r.Score);
        }
        return (rows, sw.Elapsed.TotalMilliseconds);
    }

    // ---- state out ----

    public Snapshot Snapshot()
    {
        lock (_lock)
        {
            var (key, _, quantLabel) = Keys[_keyIndex];
            return new Snapshot(
                _ready,
                _phase,
                key,
                quantLabel,
                _info.TryGetValue(key, out var info) ? info : null,
                Filters[_filterIndex].Label,
                _exact,
                _queryIndex,
                _queryIndex >= 0 ? _data.Labels[_queryIndex] : -1,
                _queryIndex >= 0 ? _data.Images[_queryIndex] : null,
                [.. _matches.Select(m => (m, _data.Images[m.CorpusIndex]))],
                _lastQueryMs,
                _recall,
                [.. _log.TakeLast(10)]);
        }
    }

    public MatchRow[] Matches { get { lock (_lock) { return _matches; } } }

    public int QueryLabel { get { lock (_lock) { return _queryIndex >= 0 ? _data.Labels[_queryIndex] : -1; } } }

    public double Recall { get { lock (_lock) { return _recall; } } }

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

/// <summary>One similarity match: which corpus image, its label, and the VSIM score (1 = identical).</summary>
readonly record struct MatchRow(int CorpusIndex, int Label, double Score);

/// <summary>The bits of <c>VINFO</c> + <c>MEMORY USAGE</c> the header shows.</summary>
sealed record VectorSetInfoSnapshot(string Quantization, int Dimension, long Length, int MaxLevel, long MemoryBytes);

/// <summary>Everything the UI renders, captured atomically.</summary>
sealed record Snapshot(
    bool Ready,
    string Phase,
    string Key,
    string QuantLabel,
    VectorSetInfoSnapshot? Info,
    string FilterLabel,
    bool Exact,
    int QueryIndex,
    int QueryLabel,
    byte[]? QueryImage,
    (MatchRow Match, byte[] Image)[] Matches,
    double LastQueryMs,
    double Recall,
    string[] Log);
