using Spectre.Console;
using Spectre.Console.Rendering;

namespace HotKeysDemo;

/// <summary>
/// Renders a <see cref="Snapshot"/>. The leaderboard always shows both metrics side by side - the
/// point of the demo is that "hot by CPU" and "hot by bytes" are different lists - with the active
/// sort named in the title as well as implied by the order.
/// </summary>
static class Ui
{
    private const int HeaderRows = 4, LogRows = 12, FooterRows = 2, MinTableRows = 8;
    private static int _logLines = LogRows - 2;

    public static Layout Build(int height)
    {
        int log = Math.Clamp(height - HeaderRows - FooterRows - MinTableRows, 5, LogRows);
        _logLines = log - 2;
        return new Layout("root").SplitRows(
            new Layout("header").Size(HeaderRows),
            new Layout("board"),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, Snapshot s)
    {
        layout["header"].Update(Header(s));
        layout["board"].Update(Board(s));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer());
    }

    private static IRenderable Header(Snapshot s)
    {
        string capture = s.Result switch
        {
            null => "[grey]no capture - press C[/]",
            { TrackingActive: true } => $"[green]CAPTURING[/] {s.CaptureSecondsLeft,4:0.0}s left  sample 1/{s.Result.SampleRatio}",
            _ => $"[yellow]finished[/] ({s.Result.CollectionDuration.TotalSeconds:0.0}s measured; R to reset)",
        };
        var title = new Markup(
            $"[bold]HOTKEYS (Redis 8.6)[/]  -  server-side per-key CPU and network profiling   {capture}" +
            (s.WorkloadPaused ? "   [bold red]WORKLOAD PAUSED[/]" : "")).Centered();
        var stats = new Markup(
            $"[bold]workload:[/] {s.OpsPerSecond,6:0} ops/s ({s.OpsSent:N0} total)   " +
            (s.SpikeKey is not null ? $"[bold red]SPIKE on {s.SpikeKey}[/]   " : "") +
            $"[bold]totals:[/] CPU {s.Result?.TotalCpuMs ?? 0:0}ms, net {s.Result?.TotalNetworkBytes ?? 0:N0}B   " +
            $"[bold]next capture:[/] SAMPLE {s.NextSampleRatio}").Centered();
        return new Panel(new Rows(title, stats)).Expand();
    }

    private static IRenderable Board(Snapshot s)
    {
        var table = new Table().Expand().Border(TableBorder.Rounded)
            .Title($"[bold]HOTKEYS GET, polled every 500ms - ranked by {(s.SortByNetwork ? "network bytes" : "CPU time")}[/]");
        table.AddColumn("#");
        table.AddColumn("key");
        table.AddColumn(new TableColumn("CPU µs").RightAligned());
        table.AddColumn("");
        table.AddColumn(new TableColumn("net bytes").RightAligned());
        table.AddColumn("");

        if (s.Result is not { } r || (r.CpuByKey.Length == 0 && r.NetworkByKey.Length == 0))
        {
            table.AddRow(" ", "[grey](no data - start a capture with C and let the workload run)[/]", "", "", "", "");
            return table;
        }

        // merge the two reports: the server ranks each metric separately (COUNT per metric)
        var merged = new Dictionary<string, (long Cpu, long Net)>();
        foreach (var (key, cpu) in r.CpuByKey) { merged[key] = (cpu, 0); }
        foreach (var (key, bytes) in r.NetworkByKey)
        {
            merged[key] = merged.TryGetValue(key, out var v) ? (v.Cpu, bytes) : (0, bytes);
        }
        var rows = merged.OrderByDescending(kv => s.SortByNetwork ? kv.Value.Net : kv.Value.Cpu).ToArray();
        long maxCpu = Math.Max(1, rows.Max(static kv => kv.Value.Cpu));
        long maxNet = Math.Max(1, rows.Max(static kv => kv.Value.Net));

        int rank = 1;
        foreach (var (key, (cpu, net)) in rows)
        {
            bool spiked = key == s.SpikeKey;
            string name = spiked ? $"[bold red]{Markup.Escape(key)} ←spike[/]" : Markup.Escape(key);
            table.AddRow(
                rank.ToString(),
                name,
                cpu == 0 ? "[grey]-[/]" : $"{cpu:N0}",
                Bar(cpu, maxCpu, "deepskyblue1"),
                net == 0 ? "[grey]-[/]" : $"{net:N0}",
                Bar(net, maxNet, "orange1"));
            rank++;
        }
        return table;
    }

    private static string Bar(long value, long max, string color)
    {
        int blocks = Math.Clamp((int)Math.Round(12.0 * value / max), value > 0 ? 1 : 0, 12);
        return $"[{color}]{new string('█', blocks)}[/][grey]{new string('░', 12 - blocks)}[/]";
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(_logLines).Select(static l =>
            l.Contains("ERR") ? $"[red]{Markup.Escape(l)}[/]"
            : l.Contains("SPIKE") ? $"[darkorange]{Markup.Escape(l)}[/]"
            : Markup.Escape(l));
        return new Panel(new Markup(string.Join("\n", lines))).Header("commands and events").Expand();
    }

    private static IRenderable Footer() => new Markup(
        "[bold]C[/] start capture (30s)  [bold]X[/] stop  [bold]R[/] reset  [bold]S[/] spike a cold key  " +
        "[bold]M[/] sort CPU/net  [bold]1[/] cycle sample ratio  [bold]W[/] pause workload  [bold]Q[/] quit").Centered();
}
