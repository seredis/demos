using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ArraysDemo;

/// <summary>
/// Renders a <see cref="Snapshot"/>. The ring is a 48-column sparkline strip; the cursor row
/// marks where the next write lands, and ARGREP hits carry a '!' row as well as being red -
/// colour is never the only signal. An empty slot is '·'.
/// </summary>
static class Ui
{
    private const int HeaderRows = 4, StripRows = 7, LogRows = 12, FooterRows = 2;
    private static readonly char[] Spark = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
    private static int _logLines = LogRows - 2;

    public static Layout Build(int height)
    {
        int log = Math.Clamp(height - HeaderRows - StripRows - FooterRows - 6, 4, LogRows);
        _logLines = log - 2;
        return new Layout("root").SplitRows(
            new Layout("header").Size(HeaderRows),
            new Layout("strip").Size(StripRows),
            new Layout("stats"),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, Snapshot s)
    {
        layout["header"].Update(Header(s));
        layout["strip"].Update(Strip(s));
        layout["stats"].Update(Stats(s));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer());
    }

    private static IRenderable Header(Snapshot s)
    {
        var title = new Markup(
            $"[bold]native arrays (Redis 8.8)[/]  -  one ring buffer, [bold]telemetry:cpu[/], {Engine.RingSize} slots; the server owns the cursor" +
            (s.ProducerPaused ? "   [bold red]PRODUCER PAUSED[/]" : "")).Centered();
        var stats = new Markup(
            $"[bold]writes (arring):[/] {s.Writes:N0}   " +
            $"[bold]wraps:[/] {s.Wraps}   " +
            $"[bold]ARLEN:[/] {s.Length}   " +
            $"[bold]AROP USED:[/] {s.Used}   " +
            $"[bold]cursor (ARNEXT):[/] {s.Next}").Centered();
        return new Panel(new Rows(title, stats)).Expand();
    }

    private static IRenderable Strip(Snapshot s)
    {
        var hits = s.GrepHits.ToHashSet();
        var bars = new StringBuilder();
        var marks = new StringBuilder();
        var cursor = new StringBuilder();
        for (int i = 0; i < s.Slots.Length; i++)
        {
            if (s.Slots[i] is { } v)
            {
                char bar = Spark[Math.Clamp((int)(v * Spark.Length / 101), 0, Spark.Length - 1)];
                bars.Append(hits.Contains(i) ? $"[red]{bar}[/]" : v >= 90 ? $"[darkorange]{bar}[/]" : $"[green]{bar}[/]");
            }
            else
            {
                bars.Append("[grey]·[/]");
            }
            marks.Append(hits.Contains(i) ? "[red]![/]" : " ");
            cursor.Append(i == s.Next ? "[bold yellow]▲[/]" : " ");
        }
        var rows = new Rows(
            new Markup(bars.ToString()),
            new Markup(marks.ToString()),
            new Markup(cursor.ToString()),
            new Markup($"[grey]0{new string(' ', Engine.RingSize - 4)}{Engine.RingSize - 1}[/]   [yellow]▲[/] = where the next ARRING write lands"));
        return new Panel(rows).Header($"ARGETRANGE 0 {Engine.RingSize - 1}, polled every 250ms").Expand();
    }

    private static IRenderable Stats(Snapshot s)
    {
        double avg = s.Used > 0 ? (double)s.Sum / s.Used : 0;
        var aggregates = new Panel(new Markup(
            $"[bold]AROP 0 {Engine.RingSize - 1} ...[/] computed server-side:\n" +
            $"SUM {s.Sum}   MIN {s.Min}   MAX {(s.Max >= 90 ? $"[red]{s.Max}[/]" : s.Max.ToString())}   " +
            $"avg {avg:0.0} (SUM/USED, client-side)")).Header("aggregates").Expand();
        var recent = new Panel(new Markup(
            s.Recent.Length == 0 ? "[grey]-[/]" :
            string.Join("  ", s.Recent.Select(static v => v >= 90 ? $"[red]{v}[/]" : v.ToString())) +
            "   [grey](newest first)[/]")).Header("ARLASTITEMS 5 REV").Expand();
        var info = new Panel(new Markup(s.Info is { } i
            ? $"count {i.Count}   len {i.Length}   next-insert-index {i.NextInsertIndex}\n" +
              $"slices {i.Slices} × {i.SliceSize} slots   directory {i.DirectorySize} [grey](sparse: only touched slices are allocated)[/]"
            : "[grey]pending[/]")).Header("ARINFO").Expand();
        return new Columns(aggregates, recent, info);
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(_logLines).Select(static l =>
            l.Contains("ERR") ? $"[red]{Markup.Escape(l)}[/]"
            : l.Contains("spike") || l.Contains("ARGREP") ? $"[darkorange]{Markup.Escape(l)}[/]"
            : Markup.Escape(l));
        return new Panel(new Markup(string.Join("\n", lines))).Header("commands and events").Expand();
    }

    private static IRenderable Footer() => new Markup(
        "[bold]J[/] inject a spike  [bold]G[/] ARGREP for 90+ readings  [bold]S[/] ARSEEK 0 (rewind the cursor)  " +
        "[bold]X[/] ARSET: retcon the last reading to 0  [bold]P[/] pause producer  [bold]Q[/] quit").Centered();
}
