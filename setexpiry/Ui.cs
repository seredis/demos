using Spectre.Console;
using Spectre.Console.Rendering;

namespace SetExpiryDemo;

/// <summary>
/// Renders a <see cref="Snapshot"/>. Colour is never the only signal: a pinned member says "pinned"
/// as well as being yellow, a fading one carries a '!' as well as being red, and the TTL is always
/// printed as a number next to its bar.
/// </summary>
static class Ui
{
    private const int HeaderRows = 4, LogRows = 12, FooterRows = 2, MinTableRows = 8;
    private static readonly TimeSpan BarScale = TimeSpan.FromSeconds(25); // a full bar
    private static int _logLines = LogRows - 2;

    public static Layout Build(int height)
    {
        int log = Math.Clamp(height - HeaderRows - FooterRows - MinTableRows, 5, LogRows);
        _logLines = log - 2;
        return new Layout("root").SplitRows(
            new Layout("header").Size(HeaderRows),
            new Layout("members"),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, Snapshot s)
    {
        layout["header"].Update(Header(s));
        layout["members"].Update(Members(s));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer());
    }

    private static IRenderable Header(Snapshot s)
    {
        var title = new Markup(
            $"[bold]set-member expiration (Redis 8.12)[/]  -  one set, [bold]presence:lobby[/]; every member carries its own TTL" +
            (s.BotsPaused ? "   [bold red]BOTS PAUSED[/]" : "")).Centered();
        var stats = new Markup(
            $"[bold]SCARD:[/] {s.Rows.Length}   " +
            $"[bold]joins (sadd):[/] {s.Joins}   " +
            $"[bold]TTL writes (sexpire):[/] {s.Refreshes}   " +
            $"[bold]lapsed (sexpired):[/] {s.ExpiredMembers}   " +
            $"[bold]kicked (srem):[/] {s.Removed}   " +
            $"[bold]pinned (spersist):[/] {s.Persists}   " +
            $"[bold]notify-keyspace-events:[/] \"{s.NotifyFlags}\"").Centered();
        return new Panel(new Rows(title, stats)).Expand();
    }

    private static IRenderable Members(Snapshot s)
    {
        var table = new Table().Expand().Border(TableBorder.Rounded).Title("[bold]SMEMBERS + SPTTL, polled every 250ms[/]");
        table.AddColumn(" ");
        table.AddColumn("member");
        table.AddColumn(new TableColumn("TTL").RightAligned());
        table.AddColumn("");
        table.AddColumn("state");

        for (int i = 0; i < s.Rows.Length; i++)
        {
            var (name, ttlMs) = s.Rows[i];
            string sel = i == s.Selected ? "[bold]>[/]" : " ";
            string ttl, bar, state;
            switch (ttlMs)
            {
                case -1:
                    (ttl, bar, state) = ("[yellow]∞[/]", "[yellow]══════════[/]", "[yellow]pinned[/]");
                    break;
                case -2:
                    (ttl, bar, state) = ("[grey]-[/]", "", "[grey]gone between poll and SPTTL[/]");
                    break;
                default:
                    double seconds = ttlMs / 1000.0;
                    int blocks = Math.Clamp((int)Math.Round(10 * ttlMs / BarScale.TotalMilliseconds), 0, 10);
                    string color = seconds < 3 ? "red" : seconds < 6 ? "darkorange" : "green";
                    ttl = $"[{color}]{seconds,5:0.0}s[/]";
                    bar = $"[{color}]{new string('█', blocks)}[/][grey]{new string('░', 10 - blocks)}[/]";
                    state = seconds < 3 ? "[red]! fading[/]" : "";
                    break;
            }
            table.AddRow(sel, Markup.Escape(name), ttl, bar, state);
        }
        if (s.Rows.Length == 0)
        {
            table.AddRow(" ", "[grey](the set is empty - and therefore the key does not exist)[/]", "", "", "");
        }
        return table;
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        var lines = s.Log.TakeLast(_logLines).Select(static l =>
            l.Contains("ERR") ? $"[red]{Markup.Escape(l)}[/]"
            : l.Contains("sexpired") ? $"[darkorange]{Markup.Escape(l)}[/]"
            : Markup.Escape(l));
        return new Panel(new Markup(string.Join("\n", lines))).Header("commands and notifications").Expand();
    }

    private static IRenderable Footer() => new Markup(
        "[bold]↑↓[/] select  [bold]A[/] add guest (20s)  [bold]T[/] touch (SADDEX 15s)  [bold]G[/] lengthen (SEXPIRE 25 GT)  " +
        "[bold]P[/] pin (SPERSIST)  [bold]X[/] expire in 2s  [bold]K[/] kick (SREM)  [bold]N[/] MNX demo  [bold]B[/] bots  [bold]Q[/] quit").Centered();
}
