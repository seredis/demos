// Engine.cs - demo scaffolding, not example code (the files worth reading are Setup.cs and
// CoreUsage.cs). Runs two "web servers" against ONE implementation at a time, samples what each
// one serves on a fixed tick (that sampling is the chart), and applies the keyboard actions.
// The M key rebuilds both servers through the other branch of Setup.cs: same application code,
// different invalidation transport.

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace HybridCacheTui;

/// <summary>One sample on a server's strip: what it served, whether that matched the database
/// at that instant, and any action marker. <see cref="HasValue"/> false = no sample (quiet,
/// switching, or a read error).</summary>
readonly record struct Cell(decimal Price, bool Fresh, char Marker, bool HasValue);

sealed record LogLine(DateTime At, string Text, bool Highlight);

sealed record ServerSnapshot(
    string Name,
    Cell[] Cells,
    Product? Serving,
    bool Stale,
    TimeSpan StaleFor,
    int DatabaseQueries,
    TimeSpan QuietRemaining);

sealed record Snapshot(
    CacheMode Mode,
    bool Switching,
    bool Paused,
    Product? Truth,
    ServerSnapshot A,
    ServerSnapshot B,
    LogLine[] Log);

sealed class Engine : IAsyncDisposable
{
    public const int ProductId = 1;
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan QuietSpan = TimeSpan.FromSeconds(15);

    private const int RingSize = 600;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<LogLine> _log = new();
    private readonly Random _random = new();
    private Task _loop = Task.CompletedTask;

    private sealed class Server(string name)
    {
        public string Name { get; } = name;
        public readonly Cell[] Cells = new Cell[RingSize];
        public int Head, Count;
        public CatalogService? Service;
        public ServiceProvider? Provider;
        public Product? Serving;
        public int LastVersion;
        public DateTime? StaleSince;
        public char PendingMarker;
        public bool CaughtUpReported = true;
        public DateTime LastErrorLogged;
    }

    private readonly Server _a = new("server A");
    private readonly Server _b = new("server B");
    private Database? _database;
    private DateTime _quietUntil, _changeAt;
    private int _changeVersion;
    private bool _switching, _actionBusy;

    public CacheMode Mode { get; private set; }
    public bool Paused { get; private set; }

    public async Task StartAsync(CacheMode mode)
    {
        await ActivateAsync(mode, firstRun: true);
        _loop = Task.Run(SampleLoopAsync);
    }

    // ---- commands from the keyboard (fire-and-forget; one at a time) ----

    /// <summary>U: server A writes a new price through the cache (SetAsync).</summary>
    public void Update() => RunAction('U', async () =>
    {
        var product = _database!.NextVersion(ProductId, NextPrice());
        MarkChange(product.Version);
        Log($"server A: SetAsync {product}  (write-through: database, then cache)", highlight: true);
        await _a.Service!.UpdateProductAsync(product);
    });

    /// <summary>R: the database changed behind the cache; server A evicts the key.</summary>
    public void Remove() => RunAction('R', async () =>
    {
        var product = _database!.Save(_database.NextVersion(ProductId, NextPrice()));
        MarkChange(product.Version);
        Log($"database changed behind the cache -> {product}; server A: RemoveAsync product/{ProductId}", highlight: true);
        await _a.Service!.RemoveProductAsync(ProductId);
    });

    /// <summary>T: a new price list; server A invalidates the 'catalog' tag and keeps reading.</summary>
    public void InvalidateTag() => RunAction('T', async () =>
    {
        var product = _database!.Save(_database.NextVersion(ProductId, NextPrice()));
        MarkChange(product.Version);
        Log($"new price list -> {product}; server A: RemoveByTagAsync(\"catalog\")", highlight: true);
        await _a.Service!.InvalidateCatalogAsync();
    });

    /// <summary>G: the same tag invalidation, but server A then goes quiet - nothing
    /// re-populates redis, so the invalidation itself is server B's only way to find out.</summary>
    public void QuietInvalidate() => RunAction('G', async () =>
    {
        var product = _database!.Save(_database.NextVersion(ProductId, NextPrice()));
        MarkChange(product.Version);
        _quietUntil = DateTime.UtcNow + QuietSpan;
        Log($"new price list -> {product}; server A: RemoveByTagAsync(\"catalog\"), then quiet for {QuietSpan.TotalSeconds:0}s", highlight: true);
        await _a.Service!.InvalidateCatalogAsync();
    });

    /// <summary>M: tear both servers down and rebuild them on the other implementation.
    /// Same application code (CoreUsage.cs); only Setup.cs differs.</summary>
    public void SwitchMode() => RunAction('M', () => ActivateAsync(Mode == CacheMode.Stock ? CacheMode.Push : CacheMode.Stock, firstRun: false));

    public void TogglePause()
    {
        Paused = !Paused;
        Log(Paused ? "sampling paused" : "sampling resumed", highlight: false);
    }

    // ---- state for the UI ----

    public Snapshot Snapshot()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var quiet = _quietUntil > now ? _quietUntil - now : TimeSpan.Zero;
            return new Snapshot(
                Mode, _switching, Paused,
                _database?.Get(ProductId),
                View(_a, quiet),
                View(_b, TimeSpan.Zero),
                [.. _log]);
        }

        static ServerSnapshot View(Server s, TimeSpan quiet)
        {
            var cells = new Cell[s.Count];
            for (int i = 0; i < s.Count; i++)
            {
                cells[i] = s.Cells[(s.Head - s.Count + i + RingSize) % RingSize];
            }

            var staleFor = s.StaleSince is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
            return new ServerSnapshot(s.Name, cells, s.Serving, s.StaleSince is not null, staleFor,
                s.Service?.DatabaseQueries ?? 0, quiet);
        }
    }

    // ---- internals ----

    private async Task ActivateAsync(CacheMode mode, bool firstRun)
    {
        lock (_sync)
        {
            if (_switching && !firstRun)
            {
                return;
            }

            _switching = true;
        }

        try
        {
            Log($"starting {Ui.Flavor(mode)} ...", highlight: true);
            var (oldA, oldB) = (_a.Provider, _b.Provider);
            lock (_sync)
            {
                (_a.Service, _b.Service) = (null, null);
                (_a.Provider, _b.Provider) = (null, null);
            }

            if (oldA is not null) await oldA.DisposeAsync();
            if (oldB is not null) await oldB.DisposeAsync();

            // a fresh database and a fresh keyspace per activation: every run starts clean
            var database = new Database();
            database.Save(new Product(ProductId, "coffee", 10.00m, Version: 1));

            var (providerA, providerB) = await Setup.CreateServersAsync(mode);
            var serviceA = new CatalogService(_a.Name, providerA.GetRequiredService<HybridCache>(), database);
            var serviceB = new CatalogService(_b.Name, providerB.GetRequiredService<HybridCache>(), database);

            // warm both near caches, as a running application would be
            _ = await serviceA.GetProductAsync(ProductId);
            _ = await serviceB.GetProductAsync(ProductId);

            lock (_sync)
            {
                _database = database;
                Mode = mode;
                (_a.Provider, _a.Service) = (providerA, serviceA);
                (_b.Provider, _b.Service) = (providerB, serviceB);
                (_a.LastVersion, _b.LastVersion) = (1, 1);
                (_a.StaleSince, _b.StaleSince) = (null, null);
                (_a.CaughtUpReported, _b.CaughtUpReported) = (true, true);
                _quietUntil = default;
            }

            Log($"ready: {Ui.Flavor(mode)}; both servers warm", highlight: false);
        }
        catch (Exception ex)
        {
            Log($"could not start: {ex.Message} - is redis up? (docker compose up -d --wait)", highlight: true);
            if (firstRun)
            {
                throw;
            }
        }
        finally
        {
            lock (_sync) { _switching = false; }
        }
    }

    private async Task SampleLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            if (!Paused && !_switching && _a.Service is not null && _b.Service is not null)
            {
                bool quiet = DateTime.UtcNow < _quietUntil;
                var readA = quiet ? null : ReadAsync(_a);
                var readB = ReadAsync(_b);
                Record(_a, readA is null ? null : await readA);
                Record(_b, await readB);
            }

            try
            {
                await Task.Delay(TickInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<Product?> ReadAsync(Server server)
    {
        if (server.Service is not { } service)
        {
            return null;
        }

        try
        {
            // the sampling IS ordinary application traffic: a read through the cache
            return await service.GetProductAsync(ProductId);
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - server.LastErrorLogged > TimeSpan.FromSeconds(5))
            {
                server.LastErrorLogged = DateTime.UtcNow;
                Log($"{server.Name}: read failed: {ex.Message}", highlight: true);
            }

            return null;
        }
    }

    private void Record(Server server, Product? product)
    {
        lock (_sync)
        {
            char marker = server.PendingMarker;
            server.PendingMarker = '\0';

            if (product is null || _database is null)
            {
                Push(server, new Cell(0, false, marker, HasValue: false));
                return;
            }

            bool fresh = product.Version >= _database.Get(ProductId).Version;
            Push(server, new Cell(product.Price, fresh, marker, HasValue: true));
            server.Serving = product;
            server.StaleSince = fresh ? null : server.StaleSince ?? DateTime.UtcNow;

            if (product.Version != server.LastVersion)
            {
                server.LastVersion = product.Version;
                string suffix = "";
                if (!server.CaughtUpReported && product.Version >= _changeVersion)
                {
                    server.CaughtUpReported = true;
                    suffix = $"  -  {(DateTime.UtcNow - _changeAt).TotalSeconds:0.00} s after the change";
                }

                Log($"{server.Name} now serves {product}{suffix}", highlight: false);
            }
        }

        static void Push(Server server, Cell cell)
        {
            server.Cells[server.Head] = cell;
            server.Head = (server.Head + 1) % RingSize;
            if (server.Count < RingSize)
            {
                server.Count++;
            }
        }
    }

    private void MarkChange(int version)
    {
        lock (_sync)
        {
            _changeAt = DateTime.UtcNow;
            _changeVersion = version;
            (_a.CaughtUpReported, _b.CaughtUpReported) = (false, false);
        }
    }

    /// <summary>A new price: a random walk in $2 steps, kept inside the chart's fixed
    /// $8-$24 scale so every change is a visible step.</summary>
    private decimal NextPrice()
    {
        decimal current = _database!.Get(ProductId).Price;
        decimal step = 2m * _random.Next(1, 4) * (_random.Next(2) == 0 ? -1 : 1);
        decimal next = Math.Clamp(current + step, 8m, 24m);
        return next == current ? Math.Clamp(current - step, 8m, 24m) : next;
    }

    private void RunAction(char marker, Func<Task> action)
    {
        lock (_sync)
        {
            if (_actionBusy || _switching || (_a.Service is null && marker != 'M'))
            {
                return;
            }

            _actionBusy = true;
            (_a.PendingMarker, _b.PendingMarker) = (marker, marker);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log($"action failed: {ex.Message}", highlight: true);
            }
            finally
            {
                lock (_sync) { _actionBusy = false; }
            }
        });
    }

    private void Log(string text, bool highlight)
    {
        lock (_sync)
        {
            _log.Enqueue(new LogLine(DateTime.Now, text, highlight));
            while (_log.Count > 14)
            {
                _log.Dequeue();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        if (_a.Provider is not null) await _a.Provider.DisposeAsync();
        if (_b.Provider is not null) await _b.Provider.DisposeAsync();
    }
}
