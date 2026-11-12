// Ui.cs - renders a Snapshot. The main body is the two "web servers", stacked, each with a
// height chart of the price it is currently serving (one column per 150ms sample, newest on the
// right; taller = more expensive). Colour is used throughout but never alone: a stale column is
// red AND carries a '!' in the row beneath, freshness is written out in words on each server's
// status line, and your actions appear as letters on both strips. Blue (fresh) and red (stale)
// stay distinct under the common red/green deficiencies; red is reserved for stale data, as in
// the sibling demos.

using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace HybridCacheTui;

static class Ui
{
    private static readonly Color Fresh = Color.DodgerBlue1;
    private static readonly Color Stale = Color.Red;
    private static readonly string[] Bars = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    // the fixed chart scale; prices walk inside it in $2 steps (Engine.NextPrice)
    private const decimal ScaleMin = 6m, ScaleMax = 26m;

    private const int FullHeaderRows = 9, CompactHeaderRows = 3, ServerRows = 6, MinLogRows = 5, FooterRows = 3;
    private static int _logLines = 8;

    public static string Flavor(CacheMode mode) => mode switch
    {
        CacheMode.Stock => "stock HybridCache over IDistributedCache (passive: expiry)",
        _ => "StackExchange.Redis.HybridCache (active: push invalidation)",
    };

    public static Layout Build(int height, out bool compact)
    {
        compact = height < FullHeaderRows + (2 * ServerRows) + MinLogRows + FooterRows;
        int header = compact ? CompactHeaderRows : FullHeaderRows;
        int log = Math.Clamp(height - header - (2 * ServerRows) - FooterRows, MinLogRows, 16);
        _logLines = log - 2;

        return new Layout("root").SplitRows(
            new Layout("header").Size(header),
            new Layout("serverA").Size(ServerRows),
            new Layout("serverB").Size(ServerRows),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, bool compact, Snapshot s, string? phase)
    {
        layout["header"].Update(Header(s, compact, phase));
        layout["serverA"].Update(ServerPanel(s.A, s, " server A - the writer: every change below is made by A "));
        layout["serverB"].Update(ServerPanel(s.B, s, " server B - finds out only through the cache "));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer());
    }

    private static IRenderable Header(Snapshot s, bool compact, string? phase)
    {
        // the headline is server B's predicament: how long has it been serving a stale value?
        (string text, Color color) = s switch
        {
            { Switching: true } => ("switching", Color.Grey),
            { B.Stale: true } => ($"B stale {s.B.StaleFor.TotalSeconds:0.0}s", Stale),
            { Mode: CacheMode.Stock } => ("passive", Color.DarkOrange),
            _ => ("push", Fresh),
        };

        IRenderable banner = compact
            ? new Markup($"[bold {color.ToMarkup()}]{Markup.Escape(text)}[/]").Centered()
            : new FigletText(text).Color(color).Centered();

        var status = new Markup(
            $"[bold]implementation:[/] {Markup.Escape(Flavor(s.Mode))}   " +
            $"[bold]database truth:[/] {Markup.Escape(s.Truth?.ToString() ?? "-")}   " +
            $"[bold]options:[/] Expiration 5m, LocalCacheExpiration 10s" +
            (s.Paused ? "   [bold]PAUSED[/]" : "") +
            (phase is null ? "" : $"\n[bold yellow]{Markup.Escape(phase)}[/]")).Centered();
        return new Rows(banner, status);
    }

    private static IRenderable ServerPanel(ServerSnapshot server, Snapshot s, string header)
    {
        int width = Math.Clamp(SafeWidth() - 6, 20, 300);
        var cells = server.Cells.Length > width ? server.Cells[^width..] : server.Cells;

        var top = new StringBuilder();
        var bottom = new StringBuilder();
        var marks = new StringBuilder();
        foreach (var cell in cells)
        {
            if (!cell.HasValue)
            {
                top.Append(' ');
                bottom.Append("[grey]·[/]");
                marks.Append(cell.Marker == '\0' ? " " : $"[bold yellow]{cell.Marker}[/]");
                continue;
            }

            // 16 height levels across two rows: the bottom row fills first
            int level = (int)Math.Clamp(Math.Round((cell.Price - ScaleMin) / (ScaleMax - ScaleMin) * 15), 0, 15);
            string colour = (cell.Fresh ? Fresh : Stale).ToMarkup();
            bottom.Append($"[{colour}]{(level >= 8 ? "█" : Bars[level])}[/]");
            top.Append(level >= 8 ? $"[{colour}]{Bars[level - 8]}[/]" : " ");
            marks.Append(
                cell.Marker != '\0' ? $"[bold yellow]{cell.Marker}[/]"
                : cell.Fresh ? "[grey]·[/]"
                : $"[{Stale.ToMarkup()}]![/]");
        }

        string serving = server.Serving?.ToString() ?? "-";
        string state = server.QuietRemaining > TimeSpan.Zero
            ? $"[bold]QUIET {server.QuietRemaining.TotalSeconds:0}s (not reading)[/]"
            : server.Stale
                ? $"[bold {Stale.ToMarkup()}]! STALE for {server.StaleFor.TotalSeconds:0.0} s[/]"
                : $"[{Fresh.ToMarkup()}]fresh (matches the database)[/]";
        var status = new Markup(
            $"[bold]serving:[/] {Markup.Escape(serving)}   {state}   [grey]db queries: {server.DatabaseQueries}[/]");

        return new Panel(new Rows(status, new Markup(top.ToString()), new Markup(bottom.ToString()), new Markup(marks.ToString())))
            .Header(header)
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(Math.Max(_logLines, 1)).Select(l =>
            $"[grey]{l.At:HH:mm:ss.fff}[/] " +
            (l.Highlight ? $"[bold]{Markup.Escape(l.Text)}[/]" : Markup.Escape(l.Text)));
        return new Panel(new Markup(string.Join('\n', lines))).Header(" events ").Border(BoxBorder.Rounded).Expand();
    }

    private static IRenderable Footer() => new Rows(
        new Markup(
            $"[grey]height = price (${ScaleMin}-${ScaleMax} scale)   [{Fresh.ToMarkup()}]fresh[/] / [{Stale.ToMarkup()}]stale[/] " +
            "(a stale column also carries [red]![/] beneath)   letters = your actions   one column = 150 ms, newest right[/]").Centered(),
        new Markup(
            "[bold]U[/] update (SetAsync)  [bold]R[/] db changed; remove by key  [bold]T[/] invalidate tag 'catalog'  " +
            "[bold]G[/] invalidate tag, then writer quiet 15s  [bold]M[/] switch implementation  [bold]P[/] pause  [bold]Q[/] quit").Centered(),
        new Markup("[grey]the code: Setup.cs is the only file that differs between the implementations; CoreUsage.cs is the app[/]").Centered());

    private static int SafeWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 110;
        }
        catch
        {
            return 110;
        }
    }
}
