// Interactive TUI for Redis 8.8's native arrays (ARRING / ARGETRANGE / AROP / ARGREP / ARSEEK),
// through StackExchange.Redis's first-class IDatabase.Array* API.
//
// One Redis container (see docker-compose.yml). A telemetry ring: one array, 48 slots, a CPU
// reading every 200ms via ARRING - the server owns the cursor, the wrap-around and the overwrite;
// AROP aggregates and ARGREP spike-hunting run where the data lives.
//
// Prerequisites: `docker compose up -d --wait` in this folder.
//
//   dotnet run                 interactive
//   dotnet run -- --scripted   hands-free tour, then exit (non-zero on any error)

using ArraysDemo;
using Spectre.Console;

bool scripted = args.Contains("--scripted");
if (args.Any(a => a != "--scripted"))
{
    Console.Error.WriteLine("Usage: dotnet run [-- --scripted]");
    return 2;
}

await using var engine = new Engine();
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

int height = AnsiConsole.Profile.Height, width = AnsiConsole.Profile.Width;
var layout = Ui.Build(height);
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
                        height = AnsiConsole.Profile.Height;
                        width = AnsiConsole.Profile.Width;
                        layout = Ui.Build(height);
                        ctx.UpdateTarget(layout);
                    }
                    Ui.Update(layout, engine.Snapshot());
                    ctx.Refresh();
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Spectre's renderer faults when the window shrinks mid-frame; drop the frame
                    // and rebuild at the next geometry
                    height = -1;
                }

                if (!Console.IsInputRedirected)
                {
                    while (Console.KeyAvailable) HandleKey(Console.ReadKey(intercept: true).Key);
                }
                try { await Task.Delay(100, quit.Token); } catch (OperationCanceledException) { }
            }
        });
}
catch (ArgumentOutOfRangeException)
{
    // a resize during Spectre's final paint faults the same way; the demo is over - losing the
    // last frame costs nothing
}

await script;
var final = engine.Snapshot();
AnsiConsole.MarkupLine($"\n{final.Writes:N0} readings written, {final.Wraps} wrap(s), {engine.GrepMatches} ARGREP hit(s)");
if (scripted)
{
    Console.WriteLine();
    foreach (var line in engine.Transcript) Console.WriteLine(line);
    string? failure =
        engine.HadErrors ? "errors were logged"
        : final.Length != Engine.RingSize ? $"the ring never filled (ARLEN {final.Length})"
        : final.Wraps < 1 ? "the ring never wrapped"
        : engine.GrepMatches < 1 ? "ARGREP never found the injected spike"
        : final.Next >= 10 ? $"ARSEEK 0 did not rewind the cursor (ARNEXT {final.Next})"
        : null;
    if (failure is not null)
    {
        Console.WriteLine($"FAILED: {failure}");
        return 1;
    }
    Console.WriteLine("scripted run OK");
}
return 0;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.J: engine.InjectSpike(); break;
        case ConsoleKey.G: engine.Grep(); break;
        case ConsoleKey.S: engine.SeekToStart(); break;
        case ConsoleKey.X: engine.RetconLast(); break;
        case ConsoleKey.P: engine.ToggleProducer(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        await Wait(11);                   // 48 slots at 200ms: the ring fills and wraps
        engine.InjectSpike(); await Wait(1);
        engine.Grep(); await Wait(1);     // the spike is findable server-side
        engine.RetconLast(); await Wait(1);
        engine.SeekToStart(); await Wait(1.5); // the cursor rewinds; writes continue from slot 0
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]script failed:[/] {Markup.Escape(ex.Message)}");
    }
    finally
    {
        quit.Cancel();
    }

    async Task Wait(double seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds));
}
