using Spectre.Console;
using Spectre.Console.Rendering;

namespace HitlessDemo;

/// <summary>
/// Renders a <see cref="Snapshot"/>. Colour is used throughout but never alone: each strip row has its own
/// words, notifications and handoffs are labelled in the log as well as coloured, and the action markers
/// are letters. Red is reserved for failed operations and faults that were not announced.
/// </summary>
static class Ui
{
    private static readonly Color Ok = Color.DodgerBlue1;
    private static readonly Color Bad = Color.Red;
    private static readonly Color Latency = Color.DarkOrange;
    private static readonly Color Notice = Color.Gold1;
    private static readonly Color Handoff = Color.MediumPurple1;
    private static readonly string[] Bars = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    private const int FooterRows = 2, StripRows = 8, FullHeaderRows = 9, CompactHeaderRows = 2, FullFaultRows = 9, CompactFaultRows = 8, MinMiddleRows = 14;
    private static int _logLines = 10;

    public static Layout Build(int height, out bool compact)
    {
        compact = height < FullHeaderRows + StripRows + FullFaultRows + MinMiddleRows + FooterRows;
        int header = compact ? CompactHeaderRows : FullHeaderRows;
        int faults = compact ? CompactFaultRows : FullFaultRows;
        _logLines = Math.Max(height - header - StripRows - faults - FooterRows - 2, 6);

        return new Layout("root").SplitRows(
            new Layout("header").Size(header),
            new Layout("strip").Size(StripRows),
            new Layout("middle").SplitColumns(
                new Layout("connection").Ratio(2),
                new Layout("log").Ratio(3)),
            new Layout("faults").Size(faults),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, bool compact, Snapshot s)
    {
        layout["header"].Update(Header(s, compact));
        layout["strip"].Update(Strip(s));
        layout["connection"].Update(Connection(s));
        layout["log"].Update(LogPanel(s));
        layout["faults"].Update(FaultsPanel(s, compact));
        layout["footer"].Update(Footer());
    }

    private static IRenderable Header(Snapshot s, bool compact)
    {
        var recent = s.Windows.Length > 8 ? s.Windows[^8..] : s.Windows; // the last ~2 seconds
        int ok = recent.Sum(w => w.Ok), errors = recent.Sum(w => w.Errors);
        Color color;
        string text;
        if (!s.Connected) { color = Bad; text = "not connected"; }
        else if (errors > 0) { color = Bad; text = $"{errors} failed"; }
        else if (s.Current is { Handoff: not null } ep && (DateTime.Now - ep.Handoff.Value).TotalSeconds < 3) { color = Handoff; text = "handing off"; }
        else if (s.Paused) { color = Color.Grey; text = "paused"; }
        else { color = Ok; text = $"{ok / 2} ops/s"; }

        IRenderable banner = compact
            ? new Markup($"[bold {color.ToMarkup()}]{Markup.Escape(text)}[/]").Centered()
            : new FigletText(text).Color(color).Centered();
        var status = new Markup(
            $"[bold]db:[/] {Markup.Escape(s.Target.Name)}   " +
            $"[bold]maintNotifications:[/] {Markup.Escape(s.Mode.Name)}   " +
            $"[bold]maintMovingEndpointType:[/] {Markup.Escape(s.EndpointType.Name)}   " +
            $"[bold]protocol:[/] {(s.Resp2 ? "RESP2" : "RESP3")}   " +
            $"[bold]retry:[/] {(s.Retry ? "on" : "off")}   " +
            $"[bold]relaxed timeouts:[/] {Markup.Escape(s.RelaxedState)}" +
            (s.Paused ? "   [bold]PAUSED[/]" : "")).Centered();
        return new Rows(banner, status);
    }

    private static IRenderable Strip(Snapshot s)
    {
        int width = Math.Clamp(SafeWidth() - 6, 20, 240);
        var windows = s.Windows.Length > width ? s.Windows[^width..] : s.Windows;
        int maxOk = Math.Max(1, windows.Length == 0 ? 1 : windows.Max(w => w.Ok));

        var ok = new System.Text.StringBuilder();
        var bad = new System.Text.StringBuilder();
        var latency = new System.Text.StringBuilder();
        var markers = new System.Text.StringBuilder();
        foreach (var w in windows)
        {
            ok.Append(w.Ok == 0 ? "[grey] [/]" : $"[{Ok.ToMarkup()}]{Bar(w.Ok, maxOk)}[/]");
            bad.Append(w.Errors == 0 ? "[grey] [/]" : $"[{Bad.ToMarkup()}]{Bar(w.Errors, Math.Max(1, w.Ok + w.Errors))}[/]");
            latency.Append(w.MaxMs <= 0 ? "[grey] [/]" : $"[{Latency.ToMarkup()}]{LatencyBar(w.MaxMs)}[/]");
            markers.Append(w.Marker is null ? "[grey]·[/]" : $"[bold]{Markup.Escape(w.Marker)}[/]");
        }

        var body = new Rows(
            new Markup(ok.ToString()),
            new Markup(bad.ToString()),
            new Markup(latency.ToString()),
            new Markup(markers.ToString()),
            new Markup($"[grey]rows: [{Ok.ToMarkup()}]ops completed[/] / [{Bad.ToMarkup()}]ops failed (share of window)[/] / [{Latency.ToMarkup()}]worst latency (log, 1ms to 10s)[/] / your actions (letters)   one column = 250ms, newest right[/]"));
        return new Panel(body).Header($" traffic: {Markup.Escape(s.Traffic)}, SET and GET alternating ").Border(BoxBorder.Rounded).Expand();
    }

    private static string Bar(int part, int whole)
    {
        if (whole <= 0 || part <= 0) return " ";
        int idx = (int)Math.Round((double)(Bars.Length - 1) * part / whole);
        return Bars[Math.Clamp(idx, 0, Bars.Length - 1)];
    }

    /// <summary>1ms is the bottom, 10s the top: a 50ms round trip sits about a third of the way up, a relaxed 10s wait at the top.</summary>
    private static string LatencyBar(double ms)
    {
        double f = Math.Log10(Math.Max(1, ms)) / 4.0;
        int idx = (int)Math.Round((Bars.Length - 1) * Math.Clamp(f, 0, 1));
        return Bars[idx];
    }

    private static IRenderable Connection(Snapshot s)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]database[/]", Markup.Escape(s.Target.Name) + (s.Target.BdbId is { } bdb ? $" [grey](bdb {bdb}{(s.TargetCount > 1 ? ", D switches" : "")})[/]" : ""));
        grid.AddRow("[grey]endpoint[/]", Markup.Escape(s.Target.Endpoint) + (s.Target.Tls ? " [grey](TLS)[/]" : ""));
        grid.AddRow("[grey]DNS A records[/]", s.Dns.Length == 0 ? "[grey]-[/]" : Markup.Escape(string.Join(", ", s.Dns)));
        if (!s.Connected)
        {
            grid.AddRow($"[{Bad.ToMarkup()} bold]not connected[/]", $"[{Bad.ToMarkup()}]{Markup.Escape(s.ConnectError ?? "")}[/]");
        }
        else
        {
            foreach (var server in s.Servers)
            {
                grid.AddRow("[grey]server[/]",
                    $"{Markup.Escape(server.EndPoint)} [grey]{Markup.Escape(server.Type)}[/]  " +
                    (server.Connected ? $"[{Ok.ToMarkup()}]connected[/]" : $"[{Bad.ToMarkup()}]disconnected[/]") +
                    (server.Node is null ? "" : $"  [grey]on proxy node[/] {Markup.Escape(server.Node)}"));
            }
        }
        grid.AddRow("[grey]opt-in[/]", Markup.Escape(s.OptIn));
        grid.AddRow("[grey]relaxed timeouts[/]", Markup.Escape(s.RelaxedState));
        if (s.RelaxedSettings.Length > 0) grid.AddRow("[grey]settings[/]", $"[grey]{Markup.Escape(s.RelaxedSettings)}[/]");
        grid.AddRow("[grey]fault injector[/]", Markup.Escape(s.InjectorStatus));
        string kinds = s.ErrorKinds.Length == 0 ? "" : $"  [grey]({Markup.Escape(string.Join(", ", s.ErrorKinds.Select(k => $"{k.Key} {k.Value}")))})[/]";
        grid.AddRow("[grey]totals[/]", $"{s.TotalOk:N0} ok, " + (s.TotalErrors == 0 ? "0 failed" : $"[{Bad.ToMarkup()}]{s.TotalErrors:N0} failed[/]") + kinds);
        // the long summaries go below the grid: in a NoWrap first column they would take the whole width
        var summaries = new Markup(
            "\n[grey]" + Markup.Escape($"maintNotifications={s.Mode.Name}: {s.Mode.Summary}") + "[/]\n\n" +
            "[grey]" + Markup.Escape($"maintMovingEndpointType={s.EndpointType.Name}: {s.EndpointType.Summary}") + "[/]");
        return new Panel(new Rows(grid, summaries)).Header(" connection ").Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(Math.Max(_logLines, 1)).Select(l =>
        {
            string text = Markup.Escape(l.Text);
            string body = l.Kind switch
            {
                LogKind.Notification => $"[{Notice.ToMarkup()} bold]{text}[/]",
                LogKind.Handoff => $"[{Handoff.ToMarkup()}]{text}[/]",
                LogKind.Error => $"[{Bad.ToMarkup()}]{text}[/]",
                LogKind.Fault => $"[bold]{text}[/]",
                LogKind.Library => $"[grey]lib: {text}[/]",
                _ => text,
            };
            return $"[grey]{l.At:HH:mm:ss.fff}[/] {body}";
        });
        return new Panel(new Markup(string.Join('\n', lines)))
            .Header($" events: [{Notice.ToMarkup()}]notifications[/], [{Handoff.ToMarkup()}]handoffs[/], [{Bad.ToMarkup()}]failures[/], [grey]library log[/] ")
            .Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable FaultsPanel(Snapshot s, bool compact)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn(new GridColumn().NoWrap()).AddColumn(new GridColumn().NoWrap()).AddColumn();
        foreach (var f in HandoffEngine.Faults)
        {
            string how = f.ActionType is not null ? $"POST /action {f.ActionType}" : $"{f.Effect} / {f.Trigger}";
            grid.AddRow($"[bold]{f.Key}[/]", Markup.Escape(f.Name), $"[{(f.Produces == "nothing" ? Bad.ToMarkup() : Notice.ToMarkup())}]{Markup.Escape(f.Produces)}[/]",
                $"[grey]{Markup.Escape(how)}  |  {Markup.Escape(f.Rladmin)}[/]");
        }
        var rows = new List<IRenderable> { grid };
        if (s.Job is { } job)
        {
            double seconds = ((job.Ended ?? DateTime.Now) - job.Started).TotalSeconds;
            string state = job.Ended is null ? $"[bold]{Markup.Escape(job.Status)}[/] {seconds:0}s" : $"{Markup.Escape(job.Status)} after {seconds:0}s";
            rows.Add(new Markup($"[grey]job:[/] {Markup.Escape(job.Name)} {state}" + (job.Detail is null ? "" : $" [grey]{Markup.Escape(job.Detail)}[/]")));
        }
        if (!compact)
        {
            var episode = s.Current ?? s.Last;
            rows.Add(new Markup(episode is null
                ? "[grey]disruption: none yet[/]"
                : $"[grey]{(s.Current is null ? "last disruption:" : "disruption in progress:")}[/] {Markup.Escape(episode.Summary())}"));
        }
        return new Panel(new Rows(rows)).Header(" faults: what the server operator does, and what each announces ").Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable Footer()
        => new Rows(
            new Markup("[bold]M[/] endpoint rebind  [bold]G[/] migrate  [bold]F[/] failover  [bold]A[/] maintenance mode  [bold]X[/] proxy restart (unannounced)  [bold]Space[/] pause  [bold]Q[/] quit"),
            new Markup("[bold]N[/] next maintNotifications  [bold]E[/] next maintMovingEndpointType  [bold]P[/] RESP3/RESP2  [bold]D[/] next database  [bold]R[/] toggle WithRetry()   (N, E, P and D reconnect)"));

    private static int SafeWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 110; }
        catch { return 110; }
    }
}
