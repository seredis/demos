// Interactive client-side caching demo for SE.Redis v4.
//
// One Redis container (see docker-compose.yml). The "app" connection has client-side caching on and
// reads a small catalogue continuously; a second "back door" connection writes, expires and flushes
// behind its back, so you can watch the server's invalidations arrive, and watch what happens when
// nothing announces a change (no tracking, keyspace events off, or a value you simply know is wrong)
// until the cache's TimeToLive or a manual Invalidate() catches it.
//
// Three cache settings can be varied independently, each a reconnect: the policy (C), the tracking
// mode (T: by-prefix, tracking, broadcast, keyspace notifications, none) and the admission rule (D:
// store on first miss, or only on a repeated miss). K flips the server's notify-keyspace-events, which
// is what the keyspace mode lives on.
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
                        height = AnsiConsole.Profile.Height;
                        width = AnsiConsole.Profile.Width;
                        layout = Ui.Build(height, out compact);
                        ctx.UpdateTarget(layout);
                    }
                    Ui.Update(layout, compact, engine.Snapshot());
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
                    while (Console.KeyAvailable) HandleKey(Console.ReadKey(intercept: true).Key);
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
    Console.WriteLine();
    foreach (var line in engine.Transcript) Console.WriteLine(line);
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
        case ConsoleKey.N: engine.OneOffBurst(); break;
        case ConsoleKey.B: engine.ToggleBypass(); break;
        case ConsoleKey.C: engine.CycleProfile(); break;
        case ConsoleKey.T: engine.CycleTracking(); break;
        case ConsoleKey.D: engine.ToggleAdmission(); break;
        case ConsoleKey.K: engine.ToggleKeyspaceEvents(); break;
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
        engine.ExpireSelected(); await Wait(7); // announced once the server's expire cycle notices it; the nil is then cached until re-seeded
        engine.RestoreMissing(); await Wait(3);
        engine.InvalidateSelected(); await Wait(2);
        engine.OneOffBurst(); await Wait(3);      // prefix profile: 25 more entries
        engine.ToggleBypass(); await Wait(3);  // every read reaches the server
        engine.ToggleBypass(); await Wait(3);
        engine.InvalidateAll(); await Wait(3);
        engine.FlushDb(); await Wait(3);       // whole cache discarded
        engine.RestoreMissing(); await Wait(3);

        // admission: only visible without the prefix's implicit opt-in, so switch to Tracking mode
        engine.CycleTracking(); await Wait(4);   // by-prefix -> tracking (OnRepeatedMiss)
        engine.OneOffBurst(); await Wait(3);     // 25 one-offs: none stored
        engine.ToggleAdmission(); await Wait(4); // OnFirstMiss
        engine.OneOffBurst(); await Wait(3);     // 25 one-offs: all stored
        engine.ToggleAdmission(); await Wait(1); // back to the default

        // invalidation: broadcast, then keyspace notifications instead of CLIENT TRACKING, then nothing
        engine.CycleTracking(); await Wait(3);   // broadcast
        engine.WriteSelected(); await Wait(2);   // heard, as under tracking
        engine.CycleTracking(); await Wait(3);   // keyspace, with notify-keyspace-events off
        engine.WriteSelected(); await Wait(7);   // not heard: stale until TimeToLive
        engine.ToggleKeyspaceEvents(); await Wait(1);
        engine.WriteSelected(); await Wait(3);   // heard: one miss, then cached again
        engine.ExpireSelected(); await Wait(3);  // heard too, unlike under CLIENT TRACKING
        engine.RestoreMissing(); await Wait(2);
        engine.ToggleKeyspaceEvents();           // leave the server as we found it
        engine.CycleTracking(); await Wait(3);   // none
        engine.WriteSelected(); await Wait(7);   // never heard: stale until TimeToLive
    }
    catch (OperationCanceledException) { }
    finally { quit.Cancel(); }

    Task Wait(int seconds) => Task.Delay(TimeSpan.FromSeconds(seconds), quit.Token);
}
