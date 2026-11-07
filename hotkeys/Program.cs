// Interactive TUI for Redis 8.6's HOTKEYS: server-side per-key CPU and network profiling, driven
// through StackExchange.Redis's first-class IServer.HotKeys* API.
//
// One Redis container (see docker-compose.yml). A deliberately lopsided workload runs the whole
// time - Zipf-distributed GETs, a CPU-heavy SINTERCARD, a bytes-heavy LRANGE - and HOTKEYS tells
// the three kinds of "hot" apart on a live leaderboard.
//
// Prerequisites: `docker compose up -d --wait` in this folder.
//
//   dotnet run                 interactive
//   dotnet run -- --scripted   hands-free tour, then exit (non-zero on any error)

using HotKeysDemo;
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
AnsiConsole.MarkupLine($"\n{final.OpsSent:N0} commands sent; last report: " +
    (final.Result is { } r ? $"{r.CpuByKey.Length} keys by CPU, {r.NetworkByKey.Length} by bytes" : "none"));
if (scripted)
{
    Console.WriteLine();
    foreach (var line in engine.Transcript) Console.WriteLine(line);
    if (engine.HadErrors)
    {
        Console.WriteLine("FAILED: errors were logged");
        return 1;
    }
    if (!ScriptChecksPassed(engine, out string why))
    {
        Console.WriteLine($"FAILED: {why}");
        return 1;
    }
    Console.WriteLine("scripted run OK");
}
return 0;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.C: engine.StartCapture(); break;
        case ConsoleKey.X: engine.StopCapture(); break;
        case ConsoleKey.R: engine.ResetCapture(); break;
        case ConsoleKey.S: engine.Spike(); break;
        case ConsoleKey.M: engine.ToggleSort(); break;
        case ConsoleKey.D1 or ConsoleKey.NumPad1: engine.CycleSampleRatio(); break;
        case ConsoleKey.W: engine.ToggleWorkload(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        await Wait(2);               // the workload warms up
        engine.StartCapture();       // 30s capture, but the script reads it long before that
        await Wait(8);               // Zipf head + SINTERCARD + LRANGE all accumulate

        engine.Spike(); await Wait(9); // a cold key takes 60% of reads; it must climb the board

        engine.ToggleSort(); await Wait(2); // same data, ranked by bytes instead
        engine.StopCapture(); await Wait(2);
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

static bool ScriptChecksPassed(Engine engine, out string why)
{
    var s = engine.Snapshot();
    if (s.Result is not { } r)
    {
        why = "no HOTKEYS report was collected";
        return false;
    }
    // the Zipf head must rank by CPU (volume), tags:* must rank by CPU (SINTERCARD),
    // and catalog must rank by bytes (LRANGE)
    if (!r.CpuByKey.Any(static e => e.Key.StartsWith("user:0", StringComparison.Ordinal)))
    {
        why = "no Zipf-head user key in the CPU report";
        return false;
    }
    if (!r.CpuByKey.Any(static e => e.Key.StartsWith("tags:", StringComparison.Ordinal)))
    {
        why = "SINTERCARD's sets did not rank by CPU";
        return false;
    }
    if (!r.NetworkByKey.Any(static e => e.Key == "catalog"))
    {
        why = "catalog (LRANGE) did not rank by network bytes";
        return false;
    }
    why = "";
    return true;
}
