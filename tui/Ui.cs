using Spectre.Console;
using Spectre.Console.Rendering;

namespace GeoFailoverTui;

/// <summary>
/// Renders a <see cref="Snapshot"/> into a Spectre.Console layout.
///
/// Accessibility: colour is never the only signal. Region is also encoded by glyph (east = solid
/// block, west = light shade) and by name; retried and failed operations get their own glyphs on
/// a marker row; member state is written out in words. Blue and orange were chosen because they
/// stay distinct for the common red/green deficiencies; red is used only for errors and is always
/// paired with a '!' glyph or the word ERROR. Setting NO_COLOR disables colour entirely.
/// </summary>
static class Ui
{
    public static readonly Color EastColor = Color.DodgerBlue1;
    public static readonly Color WestColor = Color.DarkOrange;

    private const string EastGlyph = "█";
    private const string WestGlyph = "░";
    private const string UnknownGlyph = "?";

    /// <summary>Fixed row budget for each band; the middle band takes what is left.</summary>
    private const int FooterRows = 1, StripRows = 6, FullHeaderRows = 9, CompactHeaderRows = 2, MinMiddleRows = 9, MinLogRows = 5;

    /// <summary>Builds a layout that fits <paramref name="height"/> rows; <paramref name="compact"/> is set when there is no room for the banner.</summary>
    private static int _logLines = 10;

    public static Layout Build(int height, out bool compact)
    {
        compact = height < FullHeaderRows + StripRows + MinMiddleRows + MinLogRows + FooterRows;
        int header = compact ? CompactHeaderRows : FullHeaderRows;
        int remaining = Math.Max(height - header - StripRows - FooterRows, MinMiddleRows + MinLogRows);
        int log = Math.Clamp(remaining * 2 / 5, MinLogRows, 14);
        _logLines = log - 2; // panel border takes two rows

        return new Layout("root").SplitRows(
            new Layout("header").Size(header),
            new Layout("strip").Size(StripRows),
            new Layout("middle").SplitColumns(
                new Layout("east"),
                new Layout("west"),
                new Layout("data")),
            new Layout("log").Size(log),
            new Layout("footer").Size(FooterRows));
    }

    public static void Update(Layout layout, bool compact, DemoEngine engine, Snapshot s, string? scriptedPhase)
    {
        layout["header"].Update(Header(engine, s, scriptedPhase, compact));
        layout["strip"].Update(Strip(s));
        layout["east"].Update(MemberCard(s.East, EastColor, EastGlyph));
        layout["west"].Update(MemberCard(s.West, WestColor, WestGlyph));
        layout["data"].Update(DataCard(s));
        layout["log"].Update(LogPanel(s));
        layout["footer"].Update(Footer(engine, scriptedPhase));
    }

    private static IRenderable Header(DemoEngine engine, Snapshot s, string? scriptedPhase, bool compact)
    {
        var (active, member) = engine.ActiveRegion();
        var color = ColorOf(active);
        var name = member?.Name.ToUpperInvariant().Replace("REDIS-", "") ?? "NONE";

        IRenderable banner = compact
            ? new Markup($"[bold {color.ToMarkup()}]{GlyphOf(active)} {GlyphOf(active)} {name} {GlyphOf(active)} {GlyphOf(active)}[/]").Centered()
            : new FigletText(name).Color(color).Centered();
        var status = new Markup(
            $"[bold]active member:[/] {Styled(active, member?.Name ?? "(none)")}   " +
            $"traffic: [bold]{(engine.Mode == TrafficMode.Set ? "SET tick" : "INCR hits")}[/]   " +
            $"MaxAttempts: [bold]{engine.MaxAttempts}[/]" +
            (engine.ManualFailover ? "   [bold]MANUAL failover pinned[/]" : "") +
            (engine.TrafficPaused ? "   [bold]traffic PAUSED[/]" : "") +
            (scriptedPhase is null ? "" : $"   [grey]{Markup.Escape(scriptedPhase)}[/]")).Centered();
        return new Rows(banner, status);
    }

    private static IRenderable Strip(Snapshot s)
    {
        int width = Math.Clamp(SafeWidth() - 6, 20, 240);
        var cells = s.Cells.Length > width ? s.Cells[^width..] : s.Cells;

        var regions = new System.Text.StringBuilder();
        var markers = new System.Text.StringBuilder();
        foreach (var c in cells)
        {
            regions.Append(Styled(c.Region, GlyphOf(c.Region)));
            if (c.Failed) markers.Append("[red bold]![/]");
            else if (c.Retried) markers.Append("[yellow bold]r[/]");
            else markers.Append("[grey]·[/]");
        }

        var body = new Rows(
            new Markup(regions.ToString()),
            new Markup(markers.ToString()),
            new Markup($"[grey]{s.Operations} ops   {s.Retries} retried (r)   {s.Errors} errors (!)   newest on the right[/]"));
        return new Panel(body)
            .Header($" each cell = one operation   {Styled(Region.East, EastGlyph)} east   {Styled(Region.West, WestGlyph)} west   {UnknownGlyph} unknown ")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable MemberCard(MemberView m, Color color, string glyph)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]endpoint[/]", Markup.Escape(m.Endpoint));
        grid.AddRow("[grey]weight[/]", m.Weight.ToString("0.0"));
        grid.AddRow("[grey]role[/]", m.IsActive ? "[bold]ACTIVE[/]" : "standby");
        grid.AddRow("[grey]connection[/]", m.IsConnected ? "connected" : "[bold]disconnected[/]");
        grid.AddRow("[grey]health[/]", m.IsUnhealthy ? "[bold]UNHEALTHY[/]" : "healthy");
        grid.AddRow("[grey]container[/]", m.ContainerStopped ? "[bold]STOPPED[/]" : "running");

        var header = $" {Styled(m.Region, glyph)} [bold]{Markup.Escape(m.Name)}[/]" + (m.IsActive ? " [bold]<- active[/] " : " ");
        return new Panel(grid)
            .Header(header)
            .Border(m.IsActive ? BoxBorder.Double : BoxBorder.Rounded)
            .BorderColor(color)
            .Expand();
    }

    private static IRenderable DataCard(Snapshot s)
    {
        var table = new Table().Border(TableBorder.Simple).Expand();
        table.AddColumn("[grey]key[/]");
        table.AddColumn(new TableColumn(Styled(Region.East, $"{EastGlyph} east")).RightAligned());
        table.AddColumn(new TableColumn(Styled(Region.West, $"{WestGlyph} west")).RightAligned());
        table.AddRow("tick", Value(s.East.Tick), Value(s.West.Tick));
        table.AddRow("hits", Value(s.East.Hits), Value(s.West.Hits));

        var note = new Markup("[grey]read directly from each server, not via the group.\n" +
                              "the two servers do not replicate: the standby's values\n" +
                              "freeze, and writes made during an outage stay where\n" +
                              "they landed. a cache is fine with that.[/]");
        return new Panel(new Rows(table, note))
            .Header(" what each server actually holds ")
            .Border(BoxBorder.Rounded)
            .Expand();

        static string Value(string? v) => v is null ? "[grey](unreachable)[/]" : Markup.Escape(v);
    }

    private static IRenderable LogPanel(Snapshot s)
    {
        // newest at the bottom; only as many as fit, or the panel crops the wrong end
        var lines = s.Log.TakeLast(Math.Max(_logLines, 1))
            .Select(l => $"[grey]{l.At:HH:mm:ss.fff}[/] {Styled(l.Region, GlyphOf(l.Region))} {Styled(l.Region, Markup.Escape(l.Text))}");
        return new Panel(new Markup(string.Join('\n', lines)))
            .Header(" events ")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable Footer(DemoEngine engine, string? scriptedPhase)
    {
        if (scriptedPhase is not null) return new Markup("[grey]scripted run; press Q to abort[/]");
        return new Markup(
            "[bold]E[/] stop/start east  [bold]W[/] stop/start west  [bold]F[/] manual failover  [bold]R[/] restore auto  " +
            $"[bold]M[/] mode ({(engine.Mode == TrafficMode.Set ? "SET" : "INCR")})  [bold]A[/] attempts ({engine.MaxAttempts})  [bold]P[/] pause  [bold]Q[/] quit");
    }

    public static Color ColorOf(Region r) => r switch
    {
        Region.East => EastColor,
        Region.West => WestColor,
        _ => Color.Grey,
    };

    private static string GlyphOf(Region r) => r switch
    {
        Region.East => EastGlyph,
        Region.West => WestGlyph,
        _ => UnknownGlyph,
    };

    private static string Styled(Region r, string markup)
        => r == Region.Unknown ? markup : $"[{ColorOf(r).ToMarkup()}]{markup}[/]";

    private static int SafeWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 110; }
        catch { return 110; }
    }
}
