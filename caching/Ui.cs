using Spectre.Console;
using Spectre.Console.Rendering;

namespace CachingDemo;

/// <summary>
/// Renders a <see cref="Snapshot"/>. Colour is used throughout but never alone: local reads and server
/// reads have their own glyphs and words, stale rows carry a '!' as well as a colour, and the action
/// markers under the strip are letters. Blue for "answered locally" and orange for "went to the server"
/// stay distinct under the common red/green deficiencies; red is reserved for stale data and errors.
/// </summary>
static class Ui
{
    private static readonly Color Local = Color.DodgerBlue1;
    private static readonly Color Server = Color.DarkOrange;
    private static readonly Color Stale = Color.Red;
    private static readonly string[] Bars = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    private const int FooterRows = 1, StripRows = 7, FullHeaderRows = 9, CompactHeaderRows = 2, MinMiddleRows = 12, MinLogRows = 5;
    private static int _logLines = 10;

    public static Layout Build(int height, out bool compact)
    {
        compact = height < FullHeaderRows + StripRows + MinMiddleRows + MinLogRows + FooterRows;
        int header = compact ? CompactHeaderRows : FullHeaderRows;
        int remaining = Math.Max(height - header - StripRows - FooterRows, MinMiddleRows + MinLogRows);
        int log = Math.Clamp(remaining * 2 / 5, MinLogRows, 16);
        _logLines = log - 2;

        return new Layout("root").SplitRows(
            new Layout("header").Size(header),
            new Layout("strip").Size(StripRows),
            new Layout("middle").SplitColumns(
                new Layout("keys").Ratio(3),
                new Layout("counters").Ratio(2)),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, bool compact, Snapshot s)
    {
        layout["header"].Update(Header(s, compact));
        layout["strip"].Update(Strip(s));
        layout["keys"].Update(KeysTable(s));
        layout["counters"].Update(Counters(s));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer(s));
    }

    private static IRenderable Header(Snapshot s, bool compact)
    {
        // hit rate over the last ~2 seconds (8 windows), so a key press shows up quickly
        var recent = s.Windows.Length > 8 ? s.Windows[^8..] : s.Windows;
        int reads = recent.Sum(w => w.Reads), server = recent.Sum(w => w.ServerReads);
        int rate = reads == 0 ? 0 : (int)Math.Round(100.0 * Math.Max(0, reads - server) / reads);
        var color = !s.Cache.Enabled || s.Bypass ? Server : rate >= 80 ? Local : Server;
        string text = reads == 0 ? "idle" : $"{rate}% local";

        IRenderable banner = compact
            ? new Markup($"[bold {color.ToMarkup()}]{text}[/]").Centered()
            : new FigletText(text).Color(color).Centered();
        var status = new Markup(
            $"[bold]profile:[/] {Markup.Escape(s.Profile.Name)}   " +
            $"[bold]tracking:[/] {Markup.Escape(s.Cache.Mode)}   " +
            $"[bold]last 250ms:[/] {s.LastWindowReads} reads, {s.LastWindowServerReads} reached the server" +
            (s.Bypass ? "   [bold]BYPASS (CacheOptOut)[/]" : "") +
            (s.Paused ? "   [bold]PAUSED[/]" : "")).Centered();
        return new Rows(banner, status);
    }

    private static IRenderable Strip(Snapshot s)
    {
        int width = Math.Clamp(SafeWidth() - 6, 20, 240);
        var windows = s.Windows.Length > width ? s.Windows[^width..] : s.Windows;

        var local = new System.Text.StringBuilder();   // bar height = share answered locally
        var server = new System.Text.StringBuilder();  // bar height = reads that reached the server, relative to reads
        var markers = new System.Text.StringBuilder();
        foreach (var w in windows)
        {
            if (w.Reads == 0)
            {
                local.Append("[grey] [/]"); server.Append("[grey] [/]");
            }
            else
            {
                int hits = Math.Max(0, w.Reads - w.ServerReads);
                local.Append($"[{Local.ToMarkup()}]{Bar(hits, w.Reads)}[/]");
                server.Append($"[{Server.ToMarkup()}]{Bar(Math.Min(w.ServerReads, w.Reads), w.Reads)}[/]");
            }
            markers.Append(w.Marker is null ? "[grey]·[/]" : $"[bold]{Markup.Escape(w.Marker)}[/]");
        }

        var body = new Rows(
            new Markup(local.ToString()),
            new Markup(server.ToString()),
            new Markup(markers.ToString()),
            new Markup($"[grey]rows: [{Local.ToMarkup()}]answered locally[/] / [{Server.ToMarkup()}]reached the server[/] / your actions (W write, X expire, I invalidate, A all, F flush, S seed, N one-offs, B bypass, C profile)   one column = 250ms, newest right[/]"));
        return new Panel(body).Header(" where each read was answered ").Border(BoxBorder.Rounded).Expand();
    }

    private static string Bar(int part, int whole)
    {
        if (whole <= 0 || part <= 0) return " ";
        int idx = (int)Math.Round((double)(Bars.Length - 1) * part / whole);
        return Bars[Math.Clamp(idx, 0, Bars.Length - 1)];
    }

    private static IRenderable KeysTable(Snapshot s)
    {
        var table = new Table().Border(TableBorder.Simple).Expand();
        table.AddColumn(" ");
        table.AddColumn("key");
        table.AddColumn(new TableColumn($"[{Local.ToMarkup()}]app sees (via cache)[/]"));
        table.AddColumn(new TableColumn($"[{Server.ToMarkup()}]server has[/]"));
        table.AddColumn(new TableColumn("state"));
        table.AddColumn(new TableColumn("last read").RightAligned());
        foreach (var k in s.Keys)
        {
            bool stale = k.Cached != k.Server;
            string state = stale
                ? (k.Server is null ? $"[{Stale.ToMarkup()} bold]! STALE: gone on server[/]" : $"[{Stale.ToMarkup()} bold]! STALE[/]")
                : "[grey]in sync[/]";
            table.AddRow(
                k.Selected ? "[bold]>[/]" : " ",
                k.Selected ? $"[bold]{Markup.Escape(k.Key)}[/]" : Markup.Escape(k.Key),
                Markup.Escape(k.Cached ?? "(nil)"),
                Markup.Escape(k.Server ?? "(nil)"),
                state,
                k.LastMicros <= 0 ? "[grey]-[/]" : k.LastMicros < 1000 ? $"{k.LastMicros:0} µs" : $"{k.LastMicros / 1000:0.0} ms");
        }
        return new Panel(table).Header(" the catalogue: up/down selects a row ").Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable Counters(Snapshot s)
    {
        var c = s.Cache;
        var grid = new Grid().AddColumn().AddColumn(new GridColumn().RightAligned());
        grid.AddRow("[bold]GetClientCache()[/]", c.Enabled ? "" : "[grey]null (no cache)[/]");
        if (c.Enabled)
        {
            grid.AddRow("[grey]Count[/]", c.Count.ToString());
            grid.AddRow("[grey]Bytes[/]", c.Bytes.ToString("N0"));
            grid.AddRow("[grey]Stored[/]", c.Stored.ToString());
            grid.AddRow("[grey]Evicted[/]", c.Evicted.ToString());
            grid.AddRow("[grey]Promoted[/]", c.Promoted.ToString());
            grid.AddRow("[grey]Expired[/]", c.Expired.ToString());
            grid.AddRow("[grey]Refreshes[/]", c.Refreshes.ToString());
            grid.AddRow("[grey]ServedStale[/]", c.ServedStale.ToString());
            grid.AddRow("[grey]TrackedKeys[/]", c.TrackedKeys.ToString());
        }
        if (c.Enabled)
        {
            grid.AddRow("[grey]tracking mode[/]", Markup.Escape(c.Mode));
            grid.AddRow("[grey]admission[/]", Markup.Escape(c.Admission));
        }
        grid.AddEmptyRow();
        grid.AddRow("[bold]hit rate[/]", "");
        grid.AddRow("[grey]reads the app issued (client count)[/]", c.Reads.ToString("N0"));
        grid.AddRow($"[grey]GETs the server saw (INFO commandstats)[/]", $"[{Server.ToMarkup()}]{c.ServerReads:N0}[/]");
        long localReads = Math.Max(0, c.Reads - c.ServerReads);
        grid.AddRow($"[grey]answered locally (the difference)[/]", $"[{Local.ToMarkup()}]{localReads:N0}[/]" + (c.Reads > 0 ? $"  ({100.0 * localReads / c.Reads:0.0}%)" : ""));
        grid.AddEmptyRow();
        grid.AddRow("[grey]" + Markup.Escape(s.Profile.Summary) + "[/]", "");
        return new Panel(grid).Header(" counters ").Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(Math.Max(_logLines, 1))
            .Select(l => $"[grey]{l.At:HH:mm:ss.fff}[/] {Markup.Escape(l.Text)}");
        return new Panel(new Markup(string.Join('\n', lines))).Header(" events ").Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable Footer(Snapshot s)
        => new Markup(
            "[bold]↑↓[/] select  [bold]W[/] write behind back  [bold]X[/] expire 1s  [bold]I[/] Invalidate(key)  [bold]A[/] InvalidateAll  " +
            "[bold]F[/] FLUSHDB  [bold]S[/] re-seed  [bold]N[/] one-off reads  [bold]B[/] bypass  [bold]C[/] next profile  [bold]P[/] pause  [bold]Q[/] quit");

    private static int SafeWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 110; }
        catch { return 110; }
    }
}
