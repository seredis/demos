// Interactive TUI for Redis 8.12's set-member expiration (SADDEX / SEXPIRE / SPTTL / SPERSIST),
// driven entirely through the v4 context API, because the client has no API for these yet - see
// SetExpiry.cs for "the library" and the extending/ demo for that machinery explained.
//
// One Redis 8.12 container (see docker-compose.yml; it builds from source the first time). A
// presence board: members of one set each carry their own TTL; bots touch themselves on their own
// rhythms, so some stay resident and some keep lapsing and rejoining. The event log is fed by
// keyspace notifications, including 8.12's new subkey channels (__subkeyevent@0__:sexpired), which
// name the member that expired, not just the key.
//
// Prerequisites: `docker compose up -d --wait` in this folder.
//
//   dotnet run                 interactive
//   dotnet run -- --scripted   hands-free tour, then exit (non-zero on any error)

using SetExpiryDemo;
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
AnsiConsole.MarkupLine($"\n{final.Joins} joins, {final.Refreshes} TTL writes, {final.ExpiredMembers} members lapsed, {final.Removed} kicked");
if (scripted)
{
    Console.WriteLine();
    foreach (var line in engine.Transcript) Console.WriteLine(line);
    if (engine.HadErrors)
    {
        Console.WriteLine("FAILED: errors were logged");
        return 1;
    }
    if (engine.ExpiredMembers == 0)
    {
        Console.WriteLine("FAILED: no sexpired notification was observed");
        return 1;
    }
    Console.WriteLine("scripted run OK");
}
return 0;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.UpArrow: engine.SelectNext(-1); break;
        case ConsoleKey.DownArrow: engine.SelectNext(+1); break;
        case ConsoleKey.A: engine.AddGuest(); break;
        case ConsoleKey.T: engine.TouchSelected(); break;
        case ConsoleKey.G: engine.LengthenSelected(); break;
        case ConsoleKey.P: engine.PersistSelected(); break;
        case ConsoleKey.X: engine.ExpireSoonSelected(); break;
        case ConsoleKey.K: engine.KickSelected(); break;
        case ConsoleKey.N: engine.AllNewDemo(); break;
        case ConsoleKey.B: engine.ToggleBots(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
    }
}

async Task RunScriptAsync()
{
    try
    {
        await Wait(3);                    // the bots fill the lobby
        engine.AddGuest(); await Wait(2); // guest-1 joins with a 20s TTL

        engine.Select("guest");
        engine.TouchSelected(); await Wait(1);    // SADDEX refresh to 15s
        engine.LengthenSelected(); await Wait(1); // SEXPIRE 25 GT -> [1] lengthened
        engine.AllNewDemo(); await Wait(1);       // MNX veto: all-or-nothing across the batch
        engine.PersistSelected(); await Wait(1);  // the guest is pinned: TTL shows as infinite
        engine.LengthenSelected(); await Wait(2); // GT vs no expiry (= infinite) -> [0] condition not met

        engine.Select("alice");
        engine.ExpireSoonSelected(); await Wait(5); // sexpired names alice; her bot re-adds her

        engine.Select("guest");
        engine.KickSelected(); await Wait(2);     // pinned does not mean permanent: SREM still works

        await Wait(12);                           // the slow bots lapse on their own rhythm
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
