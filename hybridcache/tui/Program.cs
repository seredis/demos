// Interactive HybridCache invalidation demo: the in-box (passive, expiry-based) implementation
// vs StackExchange.Redis.HybridCache (active, push-based), in one app, running the same
// application code. The M key tears the two "web servers" down and rebuilds them on the other
// implementation - the only code that differs is Setup.cs.
//
// Prerequisites: `docker compose up -d --wait` in the hybridcache/ folder.
//
//   dotnet run                 interactive, starting on the stock implementation
//   dotnet run -- push         interactive, starting on StackExchange.Redis.HybridCache
//   dotnet run -- --scripted   hands-free: the console demo's timeline on both implementations

using HybridCacheTui;
using Spectre.Console;

bool scripted = args.Contains("--scripted");
var startMode = args.Contains("push") ? CacheMode.Push : CacheMode.Stock;
if (args.Any(a => a is not ("--scripted" or "push" or "stock")))
{
    Console.Error.WriteLine("Usage: dotnet run [-- stock|push] [-- --scripted]");
    return 2;
}

await using var engine = new Engine();
try
{
    await engine.StartAsync(startMode);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not connect:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.MarkupLine("Start the infrastructure first: [bold]docker compose up -d --wait[/]");
    return 1;
}

var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };

string? phase = scripted ? "starting" : null;
var script = scripted ? Task.Run(RunScriptAsync) : Task.CompletedTask;

int height = AnsiConsole.Profile.Height, width = AnsiConsole.Profile.Width;
var layout = Ui.Build(height, out bool compact);
try
{
    await AnsiConsole.Live(layout)
        .AutoClear(false)
        .Overflow(VerticalOverflow.Crop)
        .StartAsync(async ctx =>
        {
            while (!quit.IsCancellationRequested)
            {
                try
                {
                    if (AnsiConsole.Profile.Height != height || AnsiConsole.Profile.Width != width)
                    {
                        // terminal resized: rebuild the row budget and swap the live target
                        height = AnsiConsole.Profile.Height;
                        width = AnsiConsole.Profile.Width;
                        layout = Ui.Build(height, out compact);
                        ctx.UpdateTarget(layout);
                    }

                    Ui.Update(layout, compact, engine.Snapshot(), phase);
                    ctx.Refresh();
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Spectre's renderer faults when the window shrinks mid-frame (Segment.SplitLines
                    // is handed an area that no longer exists) - and UpdateTarget refreshes internally,
                    // so the whole frame needs the guard; drop the frame, rebuild at the next geometry
                    height = -1;
                }

                if (!Console.IsInputRedirected)
                {
                    while (Console.KeyAvailable)
                    {
                        HandleKey(Console.ReadKey(intercept: true).Key);
                    }
                }

                try { await Task.Delay(100, quit.Token); } catch (OperationCanceledException) { }
            }
        });
}
catch (ArgumentOutOfRangeException)
{
    // after the loop returns, Spectre paints one final frame from inside StartAsync; a resize at the
    // wrong moment faults that paint the same way, and only this call site can catch it. The demo is
    // over - losing the last frame costs nothing.
}

await script;
return 0;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.U: engine.Update(); break;
        case ConsoleKey.R: engine.Remove(); break;
        case ConsoleKey.T: engine.InvalidateTag(); break;
        case ConsoleKey.G: engine.QuietInvalidate(); break;
        case ConsoleKey.M: engine.SwitchMode(); break;
        case ConsoleKey.P: engine.TogglePause(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        await Step("1/8 stock: both servers warm; A and B agree", 5);
        engine.Update();
        await Step("2/8 stock: A updates via SetAsync - B serves the OLD price until its local copy expires (~10s)", 14);
        engine.InvalidateTag();
        await Step("3/8 stock: A invalidates the 'catalog' tag and keeps reading - again ~10s of stale", 14);
        engine.QuietInvalidate();
        await Step("4/8 stock: tag invalidation but A goes quiet - nothing re-populates redis; B stays stale", 20);
        engine.SwitchMode();
        await Step("5/8 switching to StackExchange.Redis.HybridCache (push invalidation) - same app code", 6);
        engine.Update();
        await Step("6/8 push: the same update - redis pushes the invalidation; B follows immediately", 8);
        engine.InvalidateTag();
        await Step("7/8 push: the same tag invalidation - immediate again", 8);
        engine.QuietInvalidate();
        await Step("8/8 push: even with A quiet, the push already told B everything", 12);
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        quit.Cancel();
    }

    async Task Step(string label, int seconds)
    {
        phase = label;
        await Task.Delay(TimeSpan.FromSeconds(seconds), quit.Token);
    }
}
