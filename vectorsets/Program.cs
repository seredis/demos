// Interactive TUI for Redis 8.0's vector sets (VADD / VSIM / VINFO), through StackExchange.Redis's
// first-class IDatabase.VectorSet* API.
//
// Nearest-neighbour image matching over MNIST digits: the 784 raw pixels of each 28×28 image ARE
// the vector - no neural net, no embedding service, so the demo stays self-contained - and both
// the query and its matches are drawn in the terminal, so you can see with your own eyes whether
// the neighbours really look alike. The corpus is loaded three times (NOQUANT / Q8 / BIN), so
// quantization's memory-vs-recall trade-off is measurable on identical queries.
//
// Prerequisites: `docker compose up -d --wait` in this folder. The MNIST test set (~1.6MB) is
// downloaded into data/ on first run.
//
//   dotnet run                 interactive
//   dotnet run -- --scripted   hands-free tour, then exit (non-zero on any error)

using Spectre.Console;
using VectorSetsDemo;

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
if (scripted)
{
    Console.WriteLine();
    foreach (var line in engine.Transcript) Console.WriteLine(line);
    string? failure = engine.HadErrors ? "errors were logged" : ScriptFailure;
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
        case ConsoleKey.N: engine.NewQuery(); break;
        case ConsoleKey.S: engine.CycleKey(); break;
        case ConsoleKey.E: engine.ToggleExact(); break;
        case ConsoleKey.F: engine.CycleFilter(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        while (!engine.Ready) await Wait(0.5); // download + VADD import
        await Wait(1);

        // 1-NN accuracy on Q8 (the default quantization) over 20 held-out queries: raw-pixel
        // matching on MNIST should get the label right most of the time
        engine.Set(keyIndex: 1, filterIndex: 0, exact: false);
        int correct = 0, total = 20;
        for (int i = 0; i < total; i++)
        {
            engine.NewQuery();
            await Wait(0.4);
            var matches = engine.Matches;
            if (matches.Length > 0 && matches[0].Label == engine.QueryLabel) correct++;
        }
        if (correct < total * 7 / 10) ScriptFailure = $"top-1 accuracy {correct}/{total} on Q8 (expected ≥ 70%)";

        // the FILTER must be a hard constraint: only sevens, whatever the query looks like
        engine.Set(filterIndex: 1);
        await Wait(1);
        if (engine.Matches.Any(static m => m.Label != 7)) ScriptFailure ??= "FILTER '.label == 7' returned a non-7";

        // binary quantization: 1 bit per component; recall drops measurably vs exact f32
        engine.Set(keyIndex: 2, filterIndex: 0);
        await Wait(1);
        double binRecall = engine.Recall;

        // exact search on the same BIN key: HNSW error goes away, quantization error stays
        engine.Set(exact: true);
        await Wait(1);

        engine.Set(keyIndex: 0, exact: true); // f32 + exact = the ground truth itself
        await Wait(1);
        if (engine.Recall < 0.999) ScriptFailure ??= $"exact f32 search disagreed with itself (recall {engine.Recall:P0})";
        if (binRecall < 0) ScriptFailure ??= "no BIN recall was measured";
    }
    catch (Exception ex)
    {
        ScriptFailure = ex.Message;
    }
    finally
    {
        quit.Cancel();
    }

    async Task Wait(double seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds));
}

partial class Program
{
    private static string? ScriptFailure;
}
