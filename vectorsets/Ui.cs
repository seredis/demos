using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace VectorSetsDemo;

/// <summary>
/// Renders a <see cref="Snapshot"/>. The query digit is drawn at 28×14 (two vertical pixels per
/// character row), the matches at half that again; the shade ramp carries the grayscale, so no
/// per-cell colour markup is needed. A match whose label disagrees with the query's is marked
/// with ✗ as well as red - colour is never the only signal.
/// </summary>
static class Ui
{
    private const int HeaderRows = 5, LogRows = 11, FooterRows = 2, MinBodyRows = 16;
    private static readonly char[] Ramp = [' ', '░', '▒', '▓', '█'];
    private static int _logLines = LogRows - 2;

    public static Layout Build(int height)
    {
        int log = Math.Clamp(height - HeaderRows - FooterRows - MinBodyRows, 4, LogRows);
        _logLines = log - 2;
        return new Layout("root").SplitRows(
            new Layout("header").Size(HeaderRows),
            new Layout("body"),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, Snapshot s)
    {
        layout["header"].Update(Header(s));
        layout["body"].Update(Body(s));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer());
    }

    private static IRenderable Header(Snapshot s)
    {
        var title = new Markup(
            "[bold]vector sets (Redis 8.0)[/]  -  nearest-neighbour image matching: the 784 raw pixels are the vector").Centered();
        string info = s.Info is { } i
            ? $"[bold]VINFO:[/] quant {i.Quantization}, dim {i.Dimension}, size {i.Length}, max-level {i.MaxLevel}, MEMORY USAGE {i.MemoryBytes / 1024.0 / 1024.0:0.0}MB"
            : "[grey]VINFO pending[/]";
        var line2 = new Markup(
            $"[bold]key:[/] {s.Key} = {Markup.Escape(s.QuantLabel)}   {info}").Centered();
        var line3 = new Markup(
            $"[bold]search:[/] {(s.Exact ? "[yellow]exact (linear scan)[/]" : "HNSW (approximate)")}   " +
            $"[bold]FILTER:[/] {Markup.Escape(s.FilterLabel)}   " +
            $"[bold]last VSIM:[/] {s.LastQueryMs:0.00}ms   " +
            $"[bold]recall vs exact-f32:[/] {(s.Recall < 0 ? "[grey]-[/]" : Recall(s.Recall))}").Centered();
        return new Panel(new Rows(title, line2, line3)).Expand();
    }

    private static string Recall(double recall) => recall switch
    {
        >= 0.999 => $"[green]{recall:P0}[/]",
        >= 0.6 => $"[darkorange]{recall:P0}[/]",
        _ => $"[red]{recall:P0}[/]",
    };

    private static IRenderable Body(Snapshot s)
    {
        if (!s.Ready || s.QueryImage is null)
        {
            return new Panel(new Markup($"[bold]{Markup.Escape(s.Phase)}[/]").Centered())
                .Header("loading").Expand();
        }

        var query = new Panel(new Text(Render(s.QueryImage, scale: 1)))
            .Header($"query: a handwritten [bold]{s.QueryLabel}[/] (held out of the corpus)");

        var columns = new List<IRenderable> { query };
        foreach (var (match, image) in s.Matches)
        {
            bool wrong = match.Label != s.QueryLabel;
            string caption = wrong
                ? $"[red]✗ {match.Label}[/] [grey]{match.Score:0.000}[/]"
                : $"[green]{match.Label}[/] [grey]{match.Score:0.000}[/]";
            columns.Add(new Panel(new Rows(new Text(Render(image, scale: 2)), new Markup(caption).Centered()))
                .Header($"d:{match.CorpusIndex:0000}"));
        }
        if (s.Matches.Length == 0)
        {
            columns.Add(new Panel(new Markup("[grey]no matches (try F: the filter may exclude everything similar)[/]")));
        }
        return new Columns(columns) { Expand = false };
    }

    /// <summary>28×28 grayscale → text, merging <paramref name="scale"/>×2·<paramref name="scale"/> pixel
    /// blocks into one character from the shade ramp.</summary>
    private static string Render(byte[] image, int scale)
    {
        int cols = Mnist.Width / scale, rows = Mnist.Height / (2 * scale);
        var sb = new StringBuilder((cols + 1) * rows);
        for (int row = 0; row < rows; row++)
        {
            if (row > 0) sb.Append('\n');
            for (int col = 0; col < cols; col++)
            {
                int sum = 0, n = 0;
                for (int dy = 0; dy < 2 * scale; dy++)
                {
                    for (int dx = 0; dx < scale; dx++)
                    {
                        sum += image[(row * 2 * scale + dy) * Mnist.Width + col * scale + dx];
                        n++;
                    }
                }
                sb.Append(Ramp[Math.Min(Ramp.Length - 1, sum / n * Ramp.Length / 256)]);
            }
        }
        return sb.ToString();
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(_logLines).Select(static l =>
            l.Contains("ERR") ? $"[red]{Markup.Escape(l)}[/]" : Markup.Escape(l));
        return new Panel(new Markup(string.Join("\n", lines))).Header("commands").Expand();
    }

    private static IRenderable Footer() => new Markup(
        "[bold]N[/] new query  [bold]S[/] cycle key (f32/q8/bin)  [bold]E[/] exact vs HNSW  " +
        "[bold]F[/] cycle FILTER  [bold]Q[/] quit").Centered();
}
