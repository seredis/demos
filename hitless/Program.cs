// Interactive smart-client-handoff (hitless upgrade) demo for SE.Redis 3.4.
//
// Bring your own server: a Redis Enterprise database with the cluster's client_maint_notifications flag
// on (see README). One connection carries continuous SET/GET traffic; the server is then put through the
// operations that emit maintenance notifications (shard migration, failover, endpoint rebind, maintenance
// mode), and the screen shows what arrived, what the client did about it, and what the traffic felt.
//
// Four client settings can be varied, each a reconnect: maintNotifications (N: Auto, Enabled, Disabled),
// maintMovingEndpointType (E), the protocol (P: RESP3 or RESP2, which silently disables the feature), and
// the database (D). R toggles WithRetry() without reconnecting.
//
// Faults are triggered through Redis's fault injector when one is configured; otherwise the keys show the
// rladmin command to run by hand and the rest of the screen works the same.
//
//   dotnet run -- --endpoints ~/aws/endpoints.json          databases from the fault injector's file
//   dotnet run -- --endpoint host:port --password ...       one database, by hand
//   dotnet run                                              hitless.local.json in this folder

using HitlessDemo;
using Spectre.Console;

DemoSettings settings;
try
{
    settings = DemoSettings.Load(args.Where(a => a != "--transcript").ToArray());
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: dotnet run -- [--endpoints <endpoints.json>] [--settings <hitless.local.json>]");
    Console.Error.WriteLine("                     [--endpoint host:port [--password P] [--user U] [--tls | --tls-insecure] [--name N] [--bdb ID]]");
    Console.Error.WriteLine("                     [--fault-injector http://127.0.0.1:20324 | --no-fault-injector] [--cluster-index N] [--mode Auto|Enabled|Disabled]");
    Console.Error.WriteLine("Environment: HITLESS_ENDPOINTS, HITLESS_ENDPOINT, HITLESS_PASSWORD, HITLESS_USER, HITLESS_TLS, HITLESS_BDB, HITLESS_FAULT_INJECTOR");
    return 2;
}

await using var engine = new HandoffEngine(settings);
try
{
    await engine.StartAsync();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not connect:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.MarkupLine("Check the endpoint and password, and that the cluster is up (see README). What the library reported:");
    foreach (var line in engine.Transcript) Console.Error.WriteLine("  " + line);
    return 1;
}

var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };

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
                    // Spectre's renderer faults when the window shrinks mid-frame; drop the frame, rebuild at the next geometry
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
    // a resize during the final paint; the demo is over, losing the last frame costs nothing
}

var final = engine.Snapshot();
AnsiConsole.MarkupLine($"\n{final.TotalOk:N0} operations completed, {final.TotalErrors:N0} failed");
foreach (var episode in engine.Episodes) Console.WriteLine("  " + episode.Summary());
if (args.Contains("--transcript"))
{
    Console.WriteLine();
    foreach (var line in engine.Transcript) Console.WriteLine(line);
}
return 0;

void HandleKey(ConsoleKey key)
{
    switch (key)
    {
        case ConsoleKey.N: engine.CycleMode(); break;
        case ConsoleKey.E: engine.CycleEndpointType(); break;
        case ConsoleKey.P: engine.ToggleProtocol(); break;
        case ConsoleKey.D: engine.NextDatabase(); break;
        case ConsoleKey.R: engine.ToggleRetry(); break;
        case ConsoleKey.Spacebar: engine.TogglePause(); break;
        case ConsoleKey.Q or ConsoleKey.Escape: quit.Cancel(); break;
        default:
            foreach (var fault in HandoffEngine.Faults)
            {
                if (fault.Key == key) { engine.RunFault(fault); break; }
            }
            break;
    }
}
