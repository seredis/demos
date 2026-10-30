using System.Diagnostics;
using StackExchange.Redis;
using StackExchange.Redis.Availability;

namespace GeoFailoverTui;

/// <summary>Which member answered an operation, as proven by the data it returned.</summary>
enum Region { Unknown, East, West }

enum TrafficMode { Set, Incr }

/// <summary>One completed operation on the heartbeat strip.</summary>
readonly record struct Cell(Region Region, bool Retried, bool Failed);

/// <summary>A log line, coloured by the region it concerns (or none).</summary>
readonly record struct LogLine(DateTime At, Region Region, string Text);

/// <summary>What we know about one member, for the member cards.</summary>
sealed record MemberView(
    string Name,
    Region Region,
    string Endpoint,
    double Weight,
    bool IsActive,
    bool IsConnected,
    bool IsUnhealthy,
    bool ContainerStopped,
    string? Tick,
    string? Hits);

/// <summary>
/// Drives the connection group, the traffic and the docker containers, and exposes a snapshot
/// of state for the UI. All public state access goes through <see cref="Snapshot"/>.
/// </summary>
sealed class DemoEngine : IAsyncDisposable
{
    public const string EastService = "redis-east";
    public const string WestService = "redis-west";
    private const string EastEndpoint = "localhost:16401";
    private const string WestEndpoint = "localhost:16402";

    // an operation that took longer than this was almost certainly retried (RetryDelay is 200ms,
    // the failover wait can be up to 5s); there is no per-attempt callback in the library
    private static readonly TimeSpan RetryThreshold = TimeSpan.FromMilliseconds(350);

    private readonly object _sync = new();
    private readonly Cell[] _cells = new Cell[400];
    private int _cellCount, _cellHead;
    private readonly Queue<LogLine> _log = new();
    private readonly CancellationTokenSource _stop = new();

    private IConnectionGroup? _group;
    private ConnectionGroupMember _east = null!, _west = null!;
    private ConnectionMultiplexer? _eastDirect, _westDirect;
    private IDatabaseAsync _retrying = null!, _retryingOnce = null!;
    private Task _traffic = Task.CompletedTask, _probe = Task.CompletedTask;

    private bool _eastStopped, _westStopped, _dockerBusy;
    private string? _eastTick, _eastHits, _westTick, _westHits;
    private long _ops, _retries, _errors, _tick;

    public TrafficMode Mode { get; private set; } = TrafficMode.Set;
    public int MaxAttempts { get; private set; } = 5;
    public bool ManualFailover { get; private set; }
    public bool TrafficPaused { get; private set; }

    public async Task StartAsync()
    {
        _east = new ConnectionGroupMember(EastEndpoint, name: EastService) { Weight = 1.0 };
        _west = new ConnectionGroupMember(WestEndpoint, name: WestService) { Weight = 0.5 };

        MultiGroupOptions options = new MultiGroupOptions.Builder
        {
            CircuitBreaker = new CircuitBreaker.Builder { MinimumNumberOfFailures = 2, MetricsWindowSize = TimeSpan.FromSeconds(2) },
            HealthCheckInterval = TimeSpan.FromSeconds(1),
            FailbackDelay = TimeSpan.FromSeconds(5),
            RetryPolicy = new RetryPolicy.Builder { MaxAttempts = 5, RetryDelay = TimeSpan.FromMilliseconds(200) },
        };

        _group = await ConnectionMultiplexer.ConnectGroupAsync([_east, _west], options);
        _group.ConnectionChanged += OnConnectionChanged;

        _retrying = _group.GetDatabase().WithRetry();
        _retryingOnce = _group.GetDatabase().WithRetry(new RetryPolicy.Builder { MaxAttempts = 1 });

        // direct connections, bypassing the group: used to stamp each server with its region and
        // to read back what each one actually holds
        _eastDirect = await ConnectionMultiplexer.ConnectAsync(EastEndpoint + ",abortConnect=false");
        _westDirect = await ConnectionMultiplexer.ConnectAsync(WestEndpoint + ",abortConnect=false");
        await StampAsync();

        Log(Region.Unknown, $"connected; active = {_group.ActiveMember?.Name ?? "(none)"}");
        _traffic = Task.Run(TrafficLoopAsync);
        _probe = Task.Run(ProbeLoopAsync);
    }

    // ---- commands from the keyboard ----

    public void ToggleMode()
    {
        Mode = Mode == TrafficMode.Set ? TrafficMode.Incr : TrafficMode.Set;
        Log(Region.Unknown, Mode == TrafficMode.Set
            ? "traffic: SET tick (last-wins write: retried by default)"
            : "traffic: INCR hits (accumulating write: NOT retried by default)");
    }

    public void ToggleAttempts()
    {
        MaxAttempts = MaxAttempts == 5 ? 1 : 5;
        Log(Region.Unknown, $"retry policy: MaxAttempts = {MaxAttempts}" + (MaxAttempts == 1 ? " (no retries)" : ""));
    }

    public void TogglePause()
    {
        TrafficPaused = !TrafficPaused;
        Log(Region.Unknown, TrafficPaused ? "traffic paused" : "traffic resumed");
    }

    public void ToggleContainer(Region region)
    {
        var (service, stopped) = region == Region.East ? (EastService, _eastStopped) : (WestService, _westStopped);
        _ = DockerAsync(stopped ? "start" : "stop", service, region);
    }

    public void FailoverToOther()
    {
        var target = _group!.ActiveMember == _east ? _west : _east;
        bool ok = _group.TryFailoverTo(target);
        ManualFailover = ok;
        Log(RegionOf(target), ok ? $"manual failover to {target.Name}" : $"manual failover to {target.Name} refused (not usable)");
    }

    public void RestoreAutomatic()
    {
        _group!.TryFailoverTo(null);
        ManualFailover = false;
        Log(Region.Unknown, "manual failover cleared; weight-based selection restored");
    }

    // ---- state for the UI ----

    public (Region Active, ConnectionGroupMember? Member) ActiveRegion()
    {
        var m = _group?.ActiveMember;
        return (RegionOf(m), m);
    }

    public Snapshot Snapshot()
    {
        lock (_sync)
        {
            var cells = new Cell[_cellCount];
            for (int i = 0; i < _cellCount; i++)
            {
                cells[i] = _cells[(_cellHead - _cellCount + i + _cells.Length) % _cells.Length];
            }
            var active = _group?.ActiveMember;
            return new Snapshot(
                cells,
                _log.ToArray(),
                View(_east, Region.East, EastEndpoint, active, _eastStopped, _eastTick, _eastHits),
                View(_west, Region.West, WestEndpoint, active, _westStopped, _westTick, _westHits),
                _ops, _retries, _errors, _dockerBusy);
        }
    }

    private static MemberView View(ConnectionGroupMember m, Region region, string endpoint, ConnectionGroupMember? active, bool stopped, string? tick, string? hits)
        => new(m.Name, region, endpoint, m.Weight, ReferenceEquals(m, active), m.IsConnected, m.IsUnhealthy, stopped, tick, hits);

    private Region RegionOf(ConnectionGroupMember? m)
        => ReferenceEquals(m, _east) ? Region.East : ReferenceEquals(m, _west) ? Region.West : Region.Unknown;

    private static Region ParseRegion(RedisValue value)
        => value == "EAST" ? Region.East : value == "WEST" ? Region.West : Region.Unknown;

    // ---- internals ----

    private void OnConnectionChanged(object? sender, GroupConnectionChangedEventArgs e)
    {
        if (e.Type == GroupConnectionChangedEventArgs.ChangeType.ActiveChanged)
        {
            Log(RegionOf(e.Group), $"ACTIVE: {e.PreviousGroup?.Name ?? "(none)"} -> {e.Group?.Name ?? "(none)"}");
        }
        else
        {
            Log(RegionOf(e.Group), $"{e.Type}: {e.Group?.Name}");
            if (e.Type == GroupConnectionChangedEventArgs.ChangeType.Reconnected)
            {
                _ = StampAsync(); // a restarted container may have lost the region marker
            }
        }
    }

    /// <summary>Write the region marker to each server directly, so reads can prove their origin.</summary>
    private async Task StampAsync()
    {
        await Task.WhenAll(TryStamp(_eastDirect, "EAST"), TryStamp(_westDirect, "WEST"));

        static async Task TryStamp(ConnectionMultiplexer? muxer, string region)
        {
            if (muxer is null) return;
            try { await muxer.GetDatabase().StringSetAsync("region", region, flags: CommandFlags.FireAndForget); }
            catch { /* the server is down; the Reconnected event will trigger another attempt */ }
        }
    }

    private async Task TrafficLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            if (!TrafficPaused)
            {
                var db = MaxAttempts == 1 ? _retryingOnce : _retrying;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    // the payload write: what the mode toggle changes
                    if (Mode == TrafficMode.Set) await db.StringSetAsync("tick", Interlocked.Increment(ref _tick));
                    else await db.StringIncrementAsync("hits");

                    // the proof of origin: a read-only command, always within the default retry category
                    var region = ParseRegion(await db.StringGetAsync("region"));
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    Record(new Cell(region, elapsed > RetryThreshold, false));
                }
                catch (Exception ex)
                {
                    var fault = new FaultContext(ex);
                    Record(new Cell(ActiveRegion().Active, false, true));
                    Log(Region.Unknown, $"ERROR {ex.GetType().Name}: kind={fault.ErrorKind} notApplied={fault.NotApplied} category={fault.Flags & CommandFlags.CommandRetryNever}");
                }
            }
            try { await Task.Delay(150, token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Read what each server actually holds, bypassing the group.</summary>
    private async Task ProbeLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            var (et, eh) = await ReadAsync(_eastDirect);
            var (wt, wh) = await ReadAsync(_westDirect);
            lock (_sync) { (_eastTick, _eastHits, _westTick, _westHits) = (et, eh, wt, wh); }
            try { await Task.Delay(400, token); } catch (OperationCanceledException) { break; }
        }

        static async Task<(string? Tick, string? Hits)> ReadAsync(ConnectionMultiplexer? muxer)
        {
            if (muxer is null || !muxer.IsConnected) return (null, null);
            try
            {
                var db = muxer.GetDatabase();
                var values = await db.StringGetAsync([(RedisKey)"tick", (RedisKey)"hits"]);
                return (values[0].IsNull ? "-" : values[0].ToString(), values[1].IsNull ? "-" : values[1].ToString());
            }
            catch { return (null, null); }
        }
    }

    private void Record(Cell cell)
    {
        lock (_sync)
        {
            _cells[_cellHead] = cell;
            _cellHead = (_cellHead + 1) % _cells.Length;
            if (_cellCount < _cells.Length) _cellCount++;
            _ops++;
            if (cell.Retried) _retries++;
            if (cell.Failed) _errors++;
        }
    }

    private void Log(Region region, string text)
    {
        lock (_sync)
        {
            _log.Enqueue(new LogLine(DateTime.Now, region, text));
            while (_log.Count > 12) _log.Dequeue();
        }
    }

    private async Task DockerAsync(string verb, string service, Region region)
    {
        lock (_sync)
        {
            if (_dockerBusy) return;
            _dockerBusy = true;
        }
        Log(region, $"docker compose {verb} {service} ...");
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                WorkingDirectory = FindRepoRoot(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("compose");
            psi.ArgumentList.Add(verb);
            psi.ArgumentList.Add(service);
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                lock (_sync)
                {
                    if (service == EastService) _eastStopped = verb == "stop";
                    else _westStopped = verb == "stop";
                }
                Log(region, $"{service} {(verb == "stop" ? "stopped" : "started")}");
            }
            else
            {
                Log(region, $"docker failed: {(await stderr).Trim()}");
            }
        }
        catch (Exception ex)
        {
            Log(region, $"docker failed: {ex.Message}");
        }
        finally
        {
            lock (_sync) { _dockerBusy = false; }
        }
    }

    // docker-compose.yml sits in the repo root; walk up from the build output to find it
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Environment.CurrentDirectory;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_traffic, _probe).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        if (_group is not null) await _group.DisposeAsync();
        if (_eastDirect is not null) await _eastDirect.DisposeAsync();
        if (_westDirect is not null) await _westDirect.DisposeAsync();
    }
}

sealed record Snapshot(
    Cell[] Cells,
    LogLine[] Log,
    MemberView East,
    MemberView West,
    long Operations,
    long Retries,
    long Errors,
    bool DockerBusy);
