using StackExchange.Redis;

namespace SetExpiryDemo;

/// <summary>
/// A presence board over one set: <c>presence:lobby</c>. Members are users; each carries its own
/// expiration (SADDEX/SEXPIRE), so going quiet means quietly disappearing - no sweeper job, no
/// per-user shadow keys. "host" is added with a plain SADD and so never expires: TTL-carrying and
/// permanent members coexist in one set.
///
/// Bots simulate activity: each touches itself with SADDEX EX 12 on its own period. The ones whose
/// period is longer than the TTL keep lapsing and rejoining, which is the feature doing its job.
///
/// Everything observed comes from the server: membership and TTLs are polled (SMEMBERS + SPTTL),
/// and the event log is fed by keyspace notifications - including 8.12's new subkey channels,
/// where __subkeyevent@0__:sexpired names WHICH member expired.
///
/// This file is the plumbing; every command the demo issues lives in CoreUsage.cs.
/// </summary>
sealed partial class Engine : IAsyncDisposable
{
    private const string Endpoint = "localhost:16441";
    private const string NotifyFlags = "EsgT"; // E keyevent, s set class, g generic (del), T subkeyevent
    public static readonly RedisKey Key = "presence:lobby";
    private static readonly TimeSpan BotTtl = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TouchTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GuestTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LengthenTtl = TimeSpan.FromSeconds(25);

    private ConnectionMultiplexer? _muxer;
    private IDatabase _db = null!;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];
    private (string Name, long TtlMs)[] _rows = [];
    private int _selected;
    private int _guests;
    private long _joins, _refreshes, _expiredMembers, _removed, _persists;
    private bool _botsPaused;
    private bool _errors;

    /// <summary>Every log line, for the scripted mode's final dump.</summary>
    public List<string> Transcript { get; } = [];

    public long ExpiredMembers { get { lock (_lock) { return _expiredMembers; } } }

    public bool HadErrors { get { lock (_lock) { return _errors; } } }

    private sealed class Bot(string name, TimeSpan period)
    {
        public string Name => name;
        public TimeSpan Period => period;
        public DateTime Next { get; set; } = DateTime.UtcNow;
    }

    // periods under the 12s TTL keep a member alive; over it, the member lapses and rejoins
    private readonly Bot[] _bots =
    [
        new("alice", TimeSpan.FromSeconds(5)),
        new("bob", TimeSpan.FromSeconds(8)),
        new("carol", TimeSpan.FromSeconds(10)),
        new("dana", TimeSpan.FromSeconds(16)),
        new("evan", TimeSpan.FromSeconds(22)),
    ];

    public async Task StartAsync()
    {
        _muxer = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
        _db = _muxer.GetDatabase();
        var server = _muxer.GetServer(_muxer.GetEndPoints()[0]);
        await server.FlushDatabaseAsync();
        await server.ConfigSetAsync("notify-keyspace-events", NotifyFlags);

        var sub = _muxer.GetSubscriber();
        // 8.12's subkey notification channels: the payload names the members, not just the key
        await sub.SubscribeAsync(RedisChannel.Literal("__subkeyevent@0__:sexpired"), (_, payload) =>
        {
            foreach (var member in ParseSubkeys(payload.ToString()))
            {
                lock (_lock) { _expiredMembers++; }
                Log($"sexpired: [{member}] lapsed (__subkeyevent@0__:sexpired named it)");
            }
        });
        await sub.SubscribeAsync(RedisChannel.Literal("__keyevent@0__:sadd"), (_, _) =>
        {
            lock (_lock) { _joins++; }
        });
        await sub.SubscribeAsync(RedisChannel.Literal("__keyevent@0__:sexpire"), (_, _) =>
        {
            lock (_lock) { _refreshes++; } // every SADDEX EX / SEXPIRE that applied a TTL
        });
        await sub.SubscribeAsync(RedisChannel.Literal("__keyevent@0__:srem"), (_, _) =>
        {
            lock (_lock) { _removed++; }
        });
        await sub.SubscribeAsync(RedisChannel.Literal("__keyevent@0__:spersist"), (_, _) =>
        {
            lock (_lock) { _persists++; }
        });
        await sub.SubscribeAsync(RedisChannel.Literal("__keyevent@0__:del"), (_, _) =>
            Log("del: the set emptied, so the key itself is gone"));

        await AddHostAsync(); // plain SADD: no TTL - the two kinds of member coexist
        Log($"notify-keyspace-events \"{NotifyFlags}\": keyevent + set + generic + subkeyevent channels");

        _ = Task.Run(() => BotLoopAsync(_stop.Token));
        _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    // ---- background ----

    private async Task BotLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!_botsPaused)
            {
                var now = DateTime.UtcNow;
                foreach (var bot in _bots)
                {
                    if (bot.Next <= now)
                    {
                        bot.Next = now + bot.Period + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500));
                        Fire(() => BotTouchAsync(bot.Name));
                    }
                }
            }
            try { await Task.Delay(200, token); } catch (OperationCanceledException) { }
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var rows = await ReadBoardAsync();
                lock (_lock)
                {
                    _rows = rows;
                    _selected = rows.Length == 0 ? 0 : Math.Clamp(_selected, 0, rows.Length - 1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"ERR poll: {ex.Message}");
                lock (_lock) { _errors = true; }
            }
            try { await Task.Delay(250, token); } catch (OperationCanceledException) { }
        }
    }

    // ---- keyboard actions ----

    public void SelectNext(int delta)
    {
        lock (_lock)
        {
            if (_rows.Length > 0) _selected = (_selected + delta + _rows.Length) % _rows.Length;
        }
    }

    /// <summary>Scripted mode: select the first member whose name starts with <paramref name="prefix"/>.</summary>
    public bool Select(string prefix)
    {
        lock (_lock)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _selected = i;
                    return true;
                }
            }
        }
        return false;
    }

    public void AddGuest()
    {
        string name = $"guest-{Interlocked.Increment(ref _guests)}";
        Fire(() => JoinAsync(name));
    }

    public void TouchSelected()
    {
        if (SelectedName() is not { } name) return;
        Fire(() => TouchAsync(name));
    }

    public void LengthenSelected()
    {
        if (SelectedName() is not { } name) return;
        Fire(() => LengthenAsync(name));
    }

    public void PersistSelected()
    {
        if (SelectedName() is not { } name) return;
        Fire(() => PersistAsync(name));
    }

    public void ExpireSoonSelected()
    {
        if (SelectedName() is not { } name) return;
        Fire(() => ExpireSoonAsync(name));
    }

    public void KickSelected()
    {
        if (SelectedName() is not { } name) return;
        Fire(() => KickAsync(name));
    }

    /// <summary>The MNX lesson: the condition is all-or-nothing across the batch.</summary>
    public void AllNewDemo()
    {
        if (SelectedName() is not { } name) return;
        string newcomer = $"newcomer-{Random.Shared.Next(100, 999)}";
        Fire(() => AllNewAsync(name, newcomer));
    }

    public void ToggleBots()
    {
        bool paused;
        lock (_lock) { paused = _botsPaused = !_botsPaused; }
        Log(paused ? "bots paused: nobody touches anything; watch the TTLs run down" : "bots resumed");
    }

    // ---- state out ----

    public Snapshot Snapshot()
    {
        lock (_lock)
        {
            return new Snapshot(
                _rows,
                _selected,
                _joins,
                _refreshes,
                _expiredMembers,
                _removed,
                _persists,
                _botsPaused,
                NotifyFlags,
                [.. _log.TakeLast(14)]);
        }
    }

    private string? SelectedName()
    {
        lock (_lock)
        {
            return _rows.Length == 0 ? null : _rows[_selected].Name;
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

    /// <summary>Subkey-channel payloads carry the key and members length-prefixed:
    /// <c>&lt;keylen&gt;:&lt;key&gt;|&lt;len&gt;:&lt;member&gt;[,&lt;len&gt;:&lt;member&gt;...]</c>.</summary>
    internal static List<string> ParseSubkeys(string payload)
    {
        var result = new List<string>();
        int bar = payload.IndexOf('|');
        int pos = bar + 1; // the key (before the bar) is known here; the members follow
        while (pos < payload.Length)
        {
            int colon = payload.IndexOf(':', pos);
            if (colon < 0 || !int.TryParse(payload.AsSpan(pos, colon - pos), out int len)) break;
            result.Add(payload.Substring(colon + 1, len));
            pos = colon + 1 + len + 1; // past the member and the ',' separator
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_muxer is not null) await _muxer.DisposeAsync();
    }
}

/// <summary>Everything the UI renders, captured atomically.</summary>
sealed record Snapshot(
    (string Name, long TtlMs)[] Rows,
    int Selected,
    long Joins,
    long Refreshes,
    long ExpiredMembers,
    long Removed,
    long Persists,
    bool BotsPaused,
    string NotifyFlags,
    string[] Log);
