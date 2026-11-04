using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using StackExchange.Redis.Availability;
using StackExchange.Redis.Maintenance;

namespace HitlessDemo;

enum LogKind { Info, Notification, Handoff, Error, Fault, Library }

readonly record struct LogLine(DateTime At, LogKind Kind, string Text);

/// <summary>One 250ms window of traffic, for the strip.</summary>
readonly record struct Window(int Ok, int Errors, double MaxMs, string? Marker);

sealed record ServerView(string EndPoint, string Type, bool Connected, string? Node);

sealed record NotificationChoice(string Name, MaintenanceNotificationMode Mode, string Summary);

sealed record EndpointChoice(string Name, MaintenanceEndpointType Type, string Summary);

/// <summary>A fault the demo can trigger. Either a topology-change route (Effect + Trigger) or a generic action (ActionType).</summary>
sealed record FaultDef(ConsoleKey Key, string Name, string Produces, string? Effect, string? Trigger, string? Extra, string? ActionType, string Rladmin, string Note);

sealed record FaultJob(string Name, string Status, DateTime Started, DateTime? Ended, string? Detail);

/// <summary>One disruption, from the first sign of it (a fault key, a notification or an error) until things have been quiet for a while.</summary>
sealed class Episode
{
    public required string Trigger;
    public DateTime Started, LastActivity;
    public DateTime? FirstNotification, Handoff, Restored, SocketClosed;
    public string? FirstNotificationType;
    public int Errors;
    public double MaxMs;
    public bool Closed;

    public Episode Clone() => (Episode)MemberwiseClone();

    public string Summary()
    {
        var sb = new StringBuilder(Trigger).Append(" at ").Append(Started.ToString("HH:mm:ss"));
        if (FirstNotification is { } n && FirstNotificationType is not null) sb.Append($"; {FirstNotificationType} {Rel(n)}");
        if (Handoff is { } h) sb.Append($"; handoff {Rel(h)}");
        if (SocketClosed is { } c) sb.Append($"; socket closed by server {Rel(c)}");
        if (Restored is { } r) sb.Append($"; restored {Rel(r)}");
        sb.Append($"; {Errors} failed op{(Errors == 1 ? "" : "s")}; worst latency {(MaxMs >= 1000 ? $"{MaxMs / 1000:0.0}s" : $"{MaxMs:0}ms")}");
        return sb.ToString();

        string Rel(DateTime at) => $"+{(at - Started).TotalSeconds:0.0}s";
    }
}

sealed record Snapshot(
    Window[] Windows, LogLine[] Log,
    DatabaseTarget Target, int TargetCount, ServerView[] Servers, string[] Dns,
    NotificationChoice Mode, EndpointChoice EndpointType, bool Resp2, bool Retry, bool Paused,
    bool Connected, string? ConnectError, string OptIn, string RelaxedState, string RelaxedSettings,
    string InjectorStatus, FaultJob? Job, Episode? Current, Episode? Last,
    long TotalOk, long TotalErrors, KeyValuePair<string, int>[] ErrorKinds);

/// <summary>
/// Drives one connection to a Redis Enterprise database with continuous traffic, reports every maintenance
/// notification the client receives and everything the client does about it, and (optionally) triggers the
/// server-side operations that produce them through the fault injector.
/// </summary>
sealed partial class HandoffEngine : IAsyncDisposable
{
    private const int Workers = 8;
    private static readonly TimeSpan EpisodeQuiet = TimeSpan.FromSeconds(20);

    private readonly DemoSettings _settings;
    private readonly FaultInjector? _injector;
    private readonly object _sync = new();
    private readonly Queue<LogLine> _log = new();
    private readonly List<string> _transcript = new();
    private readonly List<Episode> _episodes = new();
    private readonly Window[] _windows = new Window[400];
    private int _windowCount, _windowHead;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _reconnect = new(1, 1);
    private readonly ILoggerFactory _loggerFactory;
    private readonly string[] _keys = Enumerable.Range(1, 16).Select(i => $"hitless:item:{i}").ToArray();
    private readonly Dictionary<string, int> _errorKinds = new();
    private readonly Dictionary<string, DateTime> _lastErrorLog = new();

    private ConnectionMultiplexer? _app;
    private ConfigurationOptions? _options;
    private volatile IDatabaseAsync? _db;
    private IDatabase? _plainDb;
    private Task[] _workers = [];
    private Task _ticker = Task.CompletedTask, _probe = Task.CompletedTask;

    private long _ok, _errors, _okWindowBase, _errorsWindowBase;
    private double _windowMaxMs;
    private string? _pendingMarker;
    private ServerView[] _servers = [];
    private string[] _dns = [];
    private string _optIn = "not requested";
    private string? _connectError;
    private string _injectorStatus;
    private FaultJob? _job;
    private Episode? _episode, _lastEpisode;
    private MaintenanceNotificationType _relaxedType;
    private DateTime? _relaxedUntil, _tailUntil, _movingUntil;
    private string? _lastFailureText;
    private DateTime _lastFailureAt;

    public HandoffEngine(DemoSettings settings)
    {
        _settings = settings;
        _loggerFactory = new DemoLoggerFactory(OnLibraryLog);
        if (settings.InitialMode is { } initial)
        {
            ModeIndex = Array.FindIndex(Modes, m => m.Name.Equals(initial, StringComparison.OrdinalIgnoreCase));
        }
        if (settings.FaultInjector is { } uri)
        {
            _injector = new FaultInjector(uri);
            _injectorStatus = $"{uri} (checking)";
        }
        else
        {
            _injectorStatus = "not configured: faults are run by hand (rladmin)";
        }
    }

    public int TargetIndex { get; private set; }
    public DatabaseTarget Target => _settings.Databases[TargetIndex];
    public int ModeIndex { get; private set; }
    public NotificationChoice Mode => Modes[ModeIndex];
    public int EndpointIndex { get; private set; }
    public EndpointChoice EndpointType => EndpointTypes[EndpointIndex];
    public bool Resp2 { get; private set; }
    public bool Retry { get; private set; }
    public bool Paused { get; private set; }

    public async Task StartAsync()
    {
        if (_injector is not null)
        {
            _ = Task.Run(async () =>
            {
                bool ok = await _injector.PingAsync();
                lock (_sync) { _injectorStatus = $"{_injector.BaseAddress} ({(ok ? "reachable" : "NOT reachable")})"; }
                if (!ok) Log(LogKind.Error, $"fault injector at {_injector.BaseAddress} is not answering: the fault keys will show the rladmin equivalents instead");
            });
        }
        await ApplyAsync();
        if (_app is null && _connectError is not null) throw new InvalidOperationException(_connectError);
        _workers = Enumerable.Range(0, Workers).Select(w => Task.Run(() => WorkerLoopAsync(w))).ToArray();
        _ticker = Task.Run(TickerLoopAsync);
        _probe = Task.Run(ProbeLoopAsync);
    }

    /// <summary>Connect with the current settings (see Setup.cs) and swap the live connection.</summary>
    private async Task ApplyAsync()
    {
        await _reconnect.WaitAsync(); // two quick key presses must not race over _app
        try
        {
            var target = Target;
            lock (_sync)
            {
                _optIn = Mode.Mode == MaintenanceNotificationMode.Disabled ? "not requested (Disabled)" : "requested, waiting for the handshake";
                _relaxedType = MaintenanceNotificationType.None;
                _relaxedUntil = _tailUntil = _movingUntil = null;
            }
            Log(LogKind.Info, $"connecting to {target.Name} ({target.Endpoint}) [{Mode.Name} / moving endpoint {EndpointType.Name} / {(Resp2 ? "RESP2" : "RESP3")}]");
            ConnectionMultiplexer muxer;
            try
            {
                muxer = await ConnectAppAsync(target);
            }
            catch (Exception ex)
            {
                // the state on screen must match what is running: drop the old connection rather than keep serving with the previous settings
                var failed = _app;
                _app = null; _db = null; _plainDb = null;
                lock (_sync) { _connectError = ex.Message; _servers = []; }
                Log(LogKind.Error, $"connect failed: {ex.Message}");
                if (failed is not null) await failed.DisposeAsync();
                return;
            }
            var old = _app;
            _app = muxer;
            _plainDb = muxer.GetDatabase();
            _db = Retry ? _plainDb.WithRetry() : _plainDb;
            lock (_sync) { _connectError = null; }
            var servers = muxer.GetServers();
            Log(LogKind.Info, $"connected: {servers.Length} server{(servers.Length == 1 ? "" : "s")} ({string.Join(", ", servers.Select(s => $"{s.EndPoint} {s.ServerType}"))})");
            if (old is not null) await old.DisposeAsync();
        }
        finally { _reconnect.Release(); }
    }

    // ---- keyboard ----

    public void CycleMode()
    {
        ModeIndex = (ModeIndex + 1) % Modes.Length;
        Mark("N");
        Log(LogKind.Info, $"maintNotifications={Mode.Name}: {Mode.Summary}");
        _ = ApplyAsync();
    }

    public void CycleEndpointType()
    {
        EndpointIndex = (EndpointIndex + 1) % EndpointTypes.Length;
        Mark("E");
        Log(LogKind.Info, $"maintMovingEndpointType={EndpointType.Name}: {EndpointType.Summary}");
        _ = ApplyAsync();
    }

    public void ToggleProtocol()
    {
        Resp2 = !Resp2;
        Mark("P");
        Log(LogKind.Info, Resp2
            ? "protocol=resp2: the notifications are RESP3 push frames, so this silently disables the feature under Auto, and is refused under Enabled"
            : "protocol=resp3 (the default): push frames can arrive");
        _ = ApplyAsync();
    }

    public void NextDatabase()
    {
        if (_settings.Databases.Count < 2) { Log(LogKind.Info, "only one database is configured"); return; }
        TargetIndex = (TargetIndex + 1) % _settings.Databases.Count;
        Mark("D");
        Log(LogKind.Info, $"database {Target.Name}: {Target.Endpoint}" + (Target.BdbId is { } id ? $" (bdb {id})" : ""));
        _ = ApplyAsync();
    }

    /// <summary>No reconnect: the retrying database is a wrapper over the same connection.</summary>
    public void ToggleRetry()
    {
        Retry = !Retry;
        Mark("R");
        var plain = _plainDb;
        if (plain is not null) _db = Retry ? plain.WithRetry() : plain;
        Log(LogKind.Info, Retry
            ? "GetDatabase().WithRetry(): an operation that fails on a dropped connection is replayed, so an unannounced close costs latency rather than an error"
            : "plain GetDatabase(): a failed operation is reported to the caller");
    }

    public void TogglePause() { Paused = !Paused; Log(LogKind.Info, Paused ? "traffic paused" : "traffic resumed"); }

    /// <summary>The operations that produce each notification. Through the fault injector when configured; otherwise shown for running by hand.</summary>
    public static readonly FaultDef[] Faults =
    [
        new(ConsoleKey.M, "endpoint rebind", "MOVING", "conn_drop", "endpoint_rebind", null, null,
            "rladmin bind endpoint <bdb>:1 exclude <serving node>",
            "the one serving proxy is replaced: MOVING names the successor (if asked), the client moves at once, the server closes the old socket about 17s later"),
        new(ConsoleKey.G, "migrate shards", "MIGRATING, MIGRATED", "data_movement_no_conn_drop", "migrate", null, null,
            "rladmin migrate shard <uid> target_node <node>",
            "the primary shards move to a free node: timeouts are relaxed between the two notifications and for a tail afterwards; nothing reconnects"),
        new(ConsoleKey.F, "failover", "FAILING_OVER, FAILED_OVER", "data_movement_no_conn_drop", "failover", null, null,
            "rladmin failover db db:<bdb> shard <uid>",
            "the replica is promoted: same shape as a migration (needs replication=true, which this database has)"),
        new(ConsoleKey.A, "maintenance mode", "MIGRATING, MIGRATED, MOVING", "data_movement_conn_drop", "maintenance_mode", "&automatically_clean_mm=true", null,
            "rladmin node <uid> maintenance_mode on",
            "a node is drained: shards migrate off it, then its proxy is retired - all three notifications in one run, one sequence counter"),
        new(ConsoleKey.X, "proxy restart (control)", "nothing", null, null, null, "dmc_restart",
            "kill the DMC proxy process on the node (SIGKILL; supervisorctl restarts it)",
            "the negative control: no notification is possible, so every mode sees the socket close under in-flight commands and reconnects blind"),
    ];

    public void RunFault(FaultDef def)
    {
        Mark(def.Key.ToString());
        var target = Target;
        lock (_sync)
        {
            if (_episode is { Closed: false } open) { CloseEpisode(open); }
            _episode = new Episode { Trigger = $"fault: {def.Name}", Started = DateTime.Now, LastActivity = DateTime.Now };
        }
        if (_injector is null || target.BdbId is null)
        {
            Log(LogKind.Fault, $"{def.Name} (expect {def.Produces}): {(_injector is null ? "no fault injector configured" : "no bdb id for this database")}; run it yourself now: {def.Rladmin}");
            return;
        }
        lock (_sync)
        {
            if (_job is { Ended: null } running) { Log(LogKind.Fault, $"{running.Name} is still {running.Status}; wait for it"); return; }
            _job = new FaultJob(def.Name, "posting", DateTime.Now, null, null);
        }
        Log(LogKind.Fault, $"fault: {def.Name}, expect {def.Produces}  (what rladmin would do: {def.Rladmin})");
        int bdb = target.BdbId.Value, clusterIndex = _settings.ClusterIndex;
        _ = Task.Run(async () =>
        {
            var started = DateTime.Now;
            try
            {
                string id = def.ActionType is null
                    ? await _injector.TopologyChangeAsync(def.Effect!, def.Trigger!, bdb, clusterIndex, def.Extra)
                    : await _injector.ActionAsync(def.ActionType, new { bdb_id = bdb, cluster_index = clusterIndex });
                string last = "posted";
                SetJob(j => j with { Status = last });
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(2000, _stop.Token);
                    var (status, detail) = await _injector.StatusAsync(id);
                    if (status != last)
                    {
                        last = status;
                        SetJob(j => j with { Status = status });
                    }
                    if (!FaultInjector.PendingStatuses.Contains(status))
                    {
                        SetJob(j => j with { Ended = DateTime.Now, Detail = detail });
                        Log(LogKind.Fault, $"{def.Name}: {status} after {(DateTime.Now - started).TotalSeconds:0}s{(detail is null ? "" : $": {detail}")}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetJob(j => j with { Status = "error", Ended = DateTime.Now, Detail = ex.Message });
                Log(LogKind.Error, $"{def.Name}: fault injector error: {ex.Message}");
            }
        });

        void SetJob(Func<FaultJob, FaultJob> update) { lock (_sync) { if (_job is not null) _job = update(_job); } }
    }

    // ---- what the library tells us ----

    private void OnMaintenanceEvent(object? sender, ServerMaintenanceEvent e)
    {
        if (e is not PushMaintenanceEvent p)
        {
            Log(LogKind.Notification, $"maintenance event: {e.RawMessage}");
            return;
        }
        var type = p.NotificationType;
        var sb = new StringBuilder(Wire(type)).Append("  seq ").Append(p.SequenceId);
        if (p.Time is { } time) sb.Append($"  grace {time.TotalSeconds:0}s");
        if (p.NewEndPoint is not null) sb.Append($"  successor {Format(p.NewEndPoint)}");
        else if (type == MaintenanceNotificationType.Moving) sb.Append("  successor not named ('_')");
        if (p.SlotMigrations.Count > 0) sb.Append("  ").Append(string.Join("; ", p.SlotMigrations.Select(m => $"slots {m.RawSlots}: {Format(m.Source)} -> {Format(m.Target)}")));
        else if (p.Payload is not null) sb.Append($"  shards {p.Payload}");
        sb.Append($"  (heard from {Format(p.EndPoint)})");
        Log(LogKind.Notification, sb.ToString());

        var now = DateTime.Now;
        lock (_sync)
        {
            var o = _options;
            var relaxed = o?.MaintenanceRelaxedTimeout ?? TimeSpan.FromSeconds(10);
            var windowMax = o?.MaintenanceRelaxedWindowMax ?? TimeSpan.FromSeconds(30);
            var tail = o?.MaintenancePostEventRelaxedDuration ?? TimeSpan.FromSeconds(20);
            switch (type)
            {
                case MaintenanceNotificationType.Migrating or MaintenanceNotificationType.FailingOver or MaintenanceNotificationType.SlotMigrating:
                    _relaxedType = type;
                    var announced = p.Time ?? TimeSpan.Zero;
                    _relaxedUntil = now + (announced < relaxed ? relaxed : announced > windowMax ? windowMax : announced);
                    break;
                case MaintenanceNotificationType.Migrated or MaintenanceNotificationType.FailedOver or MaintenanceNotificationType.SlotMigrated:
                    _relaxedType = MaintenanceNotificationType.None;
                    _relaxedUntil = null;
                    _tailUntil = now + tail;
                    break;
                case MaintenanceNotificationType.Moving:
                    _movingUntil = now + (p.Time ?? TimeSpan.FromSeconds(15));
                    break;
            }
            var ep = EnsureEpisode(Wire(type), now);
            if (ep.FirstNotification is null) { ep.FirstNotification = now; ep.FirstNotificationType = Wire(type); }
        }
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e)
    {
        var now = DateTime.Now;
        if (e.FailureType == ConnectionFailureType.MaintenanceHandoff)
        {
            Log(LogKind.Handoff, $"handoff: {e.ConnectionType} connection to {Format(e.EndPoint)} replaced on purpose (ConnectionFailed, FailureType MaintenanceHandoff: planned, not a fault)");
            lock (_sync) { var ep = EnsureEpisode("handoff", now); ep.Handoff ??= now; }
            return;
        }
        string text = $"connection failed: {e.FailureType} on {e.ConnectionType} to {Format(e.EndPoint)}: {Short(e.Exception?.Message)}";
        lock (_sync)
        {
            // every connection on the proxy fails at once; one line per event is enough
            if (text == _lastFailureText && now - _lastFailureAt < TimeSpan.FromSeconds(2)) return;
            _lastFailureText = text; _lastFailureAt = now;
            var ep = EnsureEpisode("unannounced", now);
            ep.SocketClosed ??= now;
        }
        Log(LogKind.Error, text);
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        var now = DateTime.Now;
        Log(LogKind.Handoff, $"connection restored: {e.ConnectionType} to {Format(e.EndPoint)}");
        lock (_sync) { if (_episode is { Closed: false } ep) { ep.Restored = now; ep.LastActivity = now; } }
    }

    private void OnLibraryLog(string category, LogLevel level, string message)
    {
        if (message.Contains("Maintenance notifications accepted")) lock (_sync) { _optIn = "accepted (CLIENT MAINT_NOTIFICATIONS ON replied +OK)"; }
        else if (message.Contains("Maintenance notifications refused")) lock (_sync) { _optIn = "refused: " + message[(message.IndexOf('(') + 1)..].TrimEnd(')'); }
        else if (message.Contains("Requesting maintenance notifications")) lock (_sync) { _optIn = "requested"; }
        else if (message.Contains("are not requested")) lock (_sync) { _optIn = "not requested (multi-group connection)"; }
        else if (message.Contains("Maintenance handoff"))
        {
            lock (_sync) { var ep = EnsureEpisode("handoff", DateTime.Now); ep.Handoff ??= DateTime.Now; }
        }
        // the connect handshake alone is forty Information lines; on screen, keep the ones about this feature (the full log is in the transcript)
        bool show = level >= LogLevel.Warning || message.Contains("aintenance", StringComparison.Ordinal) || message.Contains("topology", StringComparison.OrdinalIgnoreCase);
        int dot = category.LastIndexOf('.');
        string line = $"{(dot < 0 ? category : category[(dot + 1)..])}: {message}" + (level >= LogLevel.Warning ? $"  [{level}]" : "");
        if (show) Log(LogKind.Library, line);
        else lock (_sync) { _transcript.Add($"{DateTime.Now:HH:mm:ss.fff} [Library] {line}"); }
    }

    // ---- bookkeeping ----

    private void RecordOk(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _ok);
        double ms = elapsed.TotalMilliseconds;
        lock (_sync)
        {
            if (ms > _windowMaxMs) _windowMaxMs = ms;
            if (_episode is { Closed: false } ep && ms > ep.MaxMs) ep.MaxMs = ms;
        }
    }

    private void RecordError(string kind, MaintenanceNotificationType during, string message, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _errors);
        var now = DateTime.Now;
        string label = during == MaintenanceNotificationType.None ? kind : $"{kind} during {Wire(during)}";
        bool log;
        lock (_sync)
        {
            double ms = elapsed.TotalMilliseconds;
            if (ms > _windowMaxMs) _windowMaxMs = ms;
            _errorKinds[label] = _errorKinds.GetValueOrDefault(label) + 1;
            var ep = EnsureEpisode("unannounced", now);
            ep.Errors++;
            if (ms > ep.MaxMs) ep.MaxMs = ms;
            log = !_lastErrorLog.TryGetValue(label, out var last) || now - last > TimeSpan.FromSeconds(5);
            if (log) _lastErrorLog[label] = now;
        }
        if (log) Log(LogKind.Error, $"operation failed ({label}): {Short(message)}");
    }

    /// <summary>The open episode, or a new one. Caller holds the lock.</summary>
    private Episode EnsureEpisode(string trigger, DateTime now)
    {
        if (_episode is null || _episode.Closed)
        {
            _episode = new Episode { Trigger = trigger, Started = now };
        }
        _episode.LastActivity = now;
        return _episode;
    }

    /// <summary>Caller holds the lock.</summary>
    private void CloseEpisode(Episode ep)
    {
        ep.Closed = true;
        _lastEpisode = ep;
        _episodes.Add(ep);
        var text = "disruption: " + ep.Summary();
        _log.Enqueue(new LogLine(DateTime.Now, LogKind.Info, text));
        while (_log.Count > 40) _log.Dequeue();
        _transcript.Add($"{DateTime.Now:HH:mm:ss.fff} {text}");
    }

    private static string Wire(MaintenanceNotificationType type) => type switch
    {
        MaintenanceNotificationType.Moving => "MOVING",
        MaintenanceNotificationType.Migrating => "MIGRATING",
        MaintenanceNotificationType.Migrated => "MIGRATED",
        MaintenanceNotificationType.FailingOver => "FAILING_OVER",
        MaintenanceNotificationType.FailedOver => "FAILED_OVER",
        MaintenanceNotificationType.SlotMigrating => "SMIGRATING",
        MaintenanceNotificationType.SlotMigrated => "SMIGRATED",
        _ => type.ToString(),
    };

    private static string Format(EndPoint? ep) => ep switch
    {
        null => "?",
        DnsEndPoint d => $"{d.Host}:{d.Port}",
        IPEndPoint ip => ip.ToString(),
        _ => ep.ToString() ?? "?",
    };

    private static string Short(string? message)
    {
        if (message is null) return "";
        int cut = message.IndexOfAny(['\r', '\n']);
        if (cut >= 0) message = message[..cut];
        return message.Length <= 140 ? message : message[..140] + "...";
    }

    // ---- state for the UI ----

    public Snapshot Snapshot()
    {
        var now = DateTime.Now;
        lock (_sync)
        {
            var windows = new Window[_windowCount];
            for (int i = 0; i < _windowCount; i++)
            {
                windows[i] = _windows[(_windowHead - _windowCount + i + _windows.Length) % _windows.Length];
            }
            var o = _options;
            string settings = o is null ? "" :
                $"maintRelaxedTimeout {o.MaintenanceRelaxedTimeout.TotalSeconds:0}s, maintRelaxedWindowMax {o.MaintenanceRelaxedWindowMax.TotalSeconds:0}s, maintPostEventRelaxed {o.MaintenancePostEventRelaxedDuration.TotalSeconds:0}s; normal timeout {(o.AsyncTimeout / 1000.0):0}s";
            return new Snapshot(windows, _log.ToArray(), Target, _settings.Databases.Count, _servers, _dns,
                Mode, EndpointType, Resp2, Retry, Paused,
                _app is not null, _connectError, _optIn, RelaxedState(now), settings,
                _injectorStatus, _job, _episode is { Closed: false } ? _episode.Clone() : null, _lastEpisode?.Clone(),
                Volatile.Read(ref _ok), Volatile.Read(ref _errors), _errorKinds.OrderByDescending(p => p.Value).ToArray());
        }
    }

    /// <summary>Caller holds the lock. Derived from the notifications and the configured values; the library exposes no "relaxed now" flag.</summary>
    private string RelaxedState(DateTime now)
    {
        if (Mode.Mode == MaintenanceNotificationMode.Disabled) return "n/a (nothing is announced)";
        var parts = new List<string>();
        if (_relaxedType != MaintenanceNotificationType.None && _relaxedUntil is { } until && until > now)
        {
            parts.Add($"{Wire(_relaxedType)} in progress: timeouts relaxed, ~{(until - now).TotalSeconds:0}s of window left");
        }
        else if (_tailUntil is { } tail && tail > now)
        {
            parts.Add($"post-event tail: timeouts stay relaxed ~{(tail - now).TotalSeconds:0}s more");
        }
        if (_movingUntil is { } moving && moving > now) parts.Add($"MOVING grace ~{(moving - now).TotalSeconds:0}s left");
        return parts.Count == 0 ? "normal" : string.Join("; ", parts);
    }

    // ---- loops ----

    private async Task WorkerLoopAsync(int worker)
    {
        var token = _stop.Token;
        long sequence = worker;
        try
        {
            await Task.Delay(worker * 25, token); // stagger the start
            while (!token.IsCancellationRequested)
            {
                if (Paused) { await Task.Delay(100, token); continue; }
                await OneOperationAsync(worker, sequence++, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickerLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(250, token); } catch (OperationCanceledException) { break; }
            lock (_sync)
            {
                long ok = Volatile.Read(ref _ok), errors = Volatile.Read(ref _errors);
                _windows[_windowHead] = new Window((int)(ok - _okWindowBase), (int)(errors - _errorsWindowBase), _windowMaxMs, _pendingMarker);
                _okWindowBase = ok; _errorsWindowBase = errors;
                _windowMaxMs = 0; _pendingMarker = null;
                _windowHead = (_windowHead + 1) % _windows.Length;
                if (_windowCount < _windows.Length) _windowCount++;

                if (_episode is { Closed: false } ep && DateTime.Now - ep.LastActivity > EpisodeQuiet) CloseEpisode(ep);
            }
        }
    }

    /// <summary>Which proxy node each connection is on (CLIENT INFO laddr) and what the endpoint's DNS says, once a second.</summary>
    private async Task ProbeLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            var app = _app;
            var views = new List<ServerView>();
            if (app is not null)
            {
                foreach (var server in app.GetServers())
                {
                    string? node = null;
                    if (server.IsConnected)
                    {
                        try
                        {
                            var info = (await server.ExecuteAsync("CLIENT", "INFO")).ToString();
                            node = ParseField(info, "laddr");
                        }
                        catch { }
                    }
                    views.Add(new ServerView(Format(server.EndPoint), server.ServerType.ToString(), server.IsConnected, node));
                }
            }
            string[] dns = [];
            try
            {
                var host = Target.Endpoint.Split(':')[0];
                dns = IPAddress.TryParse(host, out _) ? [] : (await Dns.GetHostAddressesAsync(host, token)).Select(a => a.ToString()).Order().ToArray();
            }
            catch { }
            lock (_sync) { _servers = views.ToArray(); _dns = dns; }
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
        }
    }

    private static string? ParseField(string? clientInfo, string field)
    {
        if (clientInfo is null) return null;
        foreach (var token in clientInfo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(field + "=", StringComparison.Ordinal)) return token[(field.Length + 1)..].Trim();
        }
        return null;
    }

    private void Mark(string marker) { lock (_sync) { _pendingMarker = marker; } }

    private void Log(LogKind kind, string text)
    {
        lock (_sync)
        {
            var now = DateTime.Now;
            _log.Enqueue(new LogLine(now, kind, text));
            while (_log.Count > 40) _log.Dequeue();
            _transcript.Add($"{now:HH:mm:ss.fff} [{kind}] {text}");
        }
    }

    public string[] Transcript { get { lock (_sync) { return _transcript.ToArray(); } } }

    public Episode[] Episodes { get { lock (_sync) { return _episodes.Select(e => e.Clone()).ToArray(); } } }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll([.. _workers, _ticker, _probe]).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        if (_app is not null) await _app.DisposeAsync();
        _injector?.Dispose();
    }

    /// <summary>Forwards the library's own log lines (Information and above) to the event log.</summary>
    private sealed class DemoLoggerFactory(Action<string, LogLevel, string> sink) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new DemoLogger(categoryName, sink);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class DemoLogger(string category, Action<string, LogLevel, string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                sink(category, logLevel, formatter(state, exception));
            }
        }
    }
}
