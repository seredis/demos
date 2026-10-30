// Interactive geo failover demo for SE.Redis connection groups.
//
// Two standalone Redis containers (see ../docker-compose.yml) stand in for two regions. Each is
// stamped with a "region" key, so every read through the group proves which server answered it.
// Keys stop and start the containers, pin a manual failover, switch the traffic between a
// last-wins SET (retried by default) and an accumulating INCR (not retried by default), and
// drop the retry budget to a single attempt.
//
// Prerequisites: `docker compose up -d --wait` in the repo root.
//
//   dotnet run                 interactive
//   dotnet run -- --scripted   hands-free: stop east, wait, start east, wait, exit

using GeoFailoverTui;
using Spectre.Console;
using Region = GeoFailoverTui.Region;

bool scripted = args.Contains("--scripted");
if (args.Any(a => a != "--scripted"))
{
    Console.Error.WriteLine("Usage: dotnet run [-- --scripted]");
    return 2;
}

await using var engine = new DemoEngine();
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

string? phase = scripted ? "starting" : null;
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
                // terminal resized: rebuild the row budget and swap the live target
                height = AnsiConsole.Profile.Height;
                layout = Ui.Build(height, out compact);
                ctx.UpdateTarget(layout);
            }
            Ui.Update(layout, compact, engine, engine.Snapshot(), phase);
            try { ctx.Refresh(); }
            catch (ArgumentOutOfRangeException) { /* terminal too small for a frame; try again next tick */ }

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

await script;
var final = engine.Snapshot();
AnsiConsole.MarkupLine($"\n{final.Operations} operations, {final.Retries} retried, {final.Errors} errors");
return final.Errors == 0 ? 0 : 1;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.E: engine.ToggleContainer(Region.East); break;
        case ConsoleKey.W: engine.ToggleContainer(Region.West); break;
        case ConsoleKey.F: engine.FailoverToOther(); break;
        case ConsoleKey.R: engine.RestoreAutomatic(); break;
        case ConsoleKey.M: engine.ToggleMode(); break;
        case ConsoleKey.A: engine.ToggleAttempts(); break;
        case ConsoleKey.P: engine.TogglePause(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        await Step("phase 1/4: traffic on east", 8);
        engine.ToggleContainer(Region.East);
        await Step("phase 2/4: east stopped, expect failover to west", 15);
        engine.ToggleContainer(Region.East);
        await Step("phase 3/4: east restarted, expect failback after 5s stable", 15);
        await Step("phase 4/4: back on east", 5);
    }
    catch (OperationCanceledException) { }
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
