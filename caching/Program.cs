// Interactive client-side caching demo for SE.Redis v4.
//
// One Redis container (see docker-compose.yml). The "app" connection has client-side caching on and
// reads a small catalogue continuously; a second "back door" connection writes, expires and flushes
// behind its back, so you can watch the server's invalidations arrive, and watch the two cases the
// server cannot announce (expiry, and a value you simply know is wrong) until the cache's TimeToLive
// or a manual Invalidate() catches them.
//
// Hit rate is measured on the server (INFO commandstats): a read answered locally never reaches it.
//
// Prerequisites: `docker compose up -d --wait` in this folder.
//
//   dotnet run                 interactive
//   dotnet run -- --scripted   hands-free tour of the keys, then exit

using CachingDemo;
using Spectre.Console;

bool scripted = args.Contains("--scripted");
if (args.Any(a => a != "--scripted"))
{
    Console.Error.WriteLine("Usage: dotnet run [-- --scripted]");
    return 2;
}

await using var engine = new CacheEngine();
try
{
    await engine.StartAsync();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not connect:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.MarkupLine("Start the infrastructure first: [bold]docker compose up -d --wait[/]");
    return 1;
}

var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
var script = scripted ? Task.Run(RunScriptAsync) : Task.CompletedTask;

int height = AnsiConsole.Profile.Height;
var layout = Ui.Build(height, out bool compact);
await AnsiConsole.Live(layout)
    .AutoClear(false)
    .Overflow(VerticalOverflow.Crop)
    .StartAsync(async ctx =>
    {
        while (!quit.IsCancellationRequested)
        {
            if (AnsiConsole.Profile.Height != height)
            {
                height = AnsiConsole.Profile.Height;
                layout = Ui.Build(height, out compact);
                ctx.UpdateTarget(layout);
            }
            Ui.Update(layout, compact, engine.Snapshot());
            try { ctx.Refresh(); }
            catch (ArgumentOutOfRangeException) { /* terminal too small for a frame; try again next tick */ }

            if (!Console.IsInputRedirected)
            {
                while (Console.KeyAvailable) HandleKey(Console.ReadKey(intercept: true).Key);
            }
            try { await Task.Delay(100, quit.Token); } catch (OperationCanceledException) { }
        }
    });

await script;
var final = engine.Snapshot();
long local = Math.Max(0, final.Cache.Reads - final.Cache.ServerReads);
AnsiConsole.MarkupLine($"\n{final.Cache.Reads:N0} reads issued, {final.Cache.ServerReads:N0} reached the server, {local:N0} answered locally");
if (scripted)
{
    // one column per 250ms window: share answered locally as a digit (0-9 = 0-99%), markers on the line below
    var pct = string.Concat(final.Windows.Select(w => w.Reads == 0 ? "-" : Math.Min(9, 10 * Math.Max(0, w.Reads - w.ServerReads) / w.Reads).ToString()));
    var marks = string.Concat(final.Windows.Select(w => w.Marker ?? "."));
    Console.WriteLine($"local/10: {pct}");
    Console.WriteLine($"actions:  {marks}");
}
return 0;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.UpArrow: engine.SelectNext(-1); break;
        case ConsoleKey.DownArrow: engine.SelectNext(+1); break;
        case ConsoleKey.W: engine.WriteSelected(); break;
        case ConsoleKey.X: engine.ExpireSelected(); break;
        case ConsoleKey.I: engine.InvalidateSelected(); break;
        case ConsoleKey.A: engine.InvalidateAll(); break;
        case ConsoleKey.F: engine.FlushDb(); break;
        case ConsoleKey.S: engine.RestoreMissing(); break;
        case ConsoleKey.B: engine.ToggleBypass(); break;
        case ConsoleKey.C: engine.CycleProfile(); break;
        case ConsoleKey.P: engine.TogglePause(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        await Wait(4);                       // warm up: every key cached after its first read
        engine.WriteSelected(); await Wait(3); // server pushes an invalidation; one miss, then cached again
        engine.ExpireSelected(); await Wait(7); // not announced: stale until TimeToLive (5s) retires it
        engine.RestoreMissing(); await Wait(3);
        engine.InvalidateSelected(); await Wait(2);
        engine.ToggleBypass(); await Wait(3);  // every read reaches the server
        engine.ToggleBypass(); await Wait(3);
        engine.InvalidateAll(); await Wait(3);
        engine.FlushDb(); await Wait(3);       // whole cache discarded
        engine.RestoreMissing(); await Wait(3);
    }
    catch (OperationCanceledException) { }
    finally { quit.Cancel(); }

    Task Wait(int seconds) => Task.Delay(TimeSpan.FromSeconds(seconds), quit.Token);
}
