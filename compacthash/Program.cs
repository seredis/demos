// A measured console run of Redis 8.10's compact hashes: HIMPORT loads schema-shaped hashes
// through a session-local *field-set*, and keys that share a schema store the field names ONCE
// (a "template") instead of once per key. StackExchange.Redis wraps the session bookkeeping in
// HashImport.Create(...) + IDatabase.HashImport[Async](...): the PREPARE is injected lazily on
// whichever connection each import actually uses, so reconnects and multiplexing just work.
//
// The same orders dataset is loaded twice at several sizes - plain HSET vs HIMPORT - and the
// numbers come from the server: used_memory deltas, MEMORY USAGE (which includes a key's share
// of its template), OBJECT ENCODING, and the INFO template counters.
//
// Prerequisites: `docker compose up -d --wait` in this folder. Then: dotnet run
// (non-interactive; exits non-zero if any assertion fails).

using System.Diagnostics;
using Spectre.Console;
using StackExchange.Redis;

const string Endpoint = "localhost:16471";
const int BatchSize = 1_000;
int[] sizes = [100, 5_000, 50_000];

// a realistic schema: 14 descriptive field names, ~170 bytes of names per key when stored plainly
RedisValue[] fields =
[
    "order_id", "customer_id", "status", "currency", "subtotal_cents", "tax_cents",
    "shipping_cents", "placed_at", "updated_at", "warehouse", "carrier", "tracking_code",
    "email_hash", "loyalty_tier",
];

ConnectionMultiplexer muxer;
try
{
    muxer = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,allowAdmin=true");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not connect:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.MarkupLine("Start the infrastructure first: [bold]docker compose up -d --wait[/]");
    return 1;
}
await using var _ = muxer;
var db = muxer.GetDatabase();
var server = muxer.GetServer(muxer.GetEndPoints()[0]);

AnsiConsole.MarkupLine($"[bold]compact hashes (Redis 8.10)[/] - {(await server.InfoAsync("server"))[0].First(static p => p.Key == "redis_version").Value}");
AnsiConsole.MarkupLine($"schema: {fields.Length} fields, {fields.Sum(static f => ((string)f!).Length)} bytes of field names per key when every key stores its own copy\n");

var results = new List<Run>();
foreach (int n in sizes)
{
    results.Add(await LoadAsync(n, useImport: false));
    results.Add(await LoadAsync(n, useImport: true));
}

// ---- the point, in one table ----

var table = new Table().Border(TableBorder.Rounded)
    .Title("[bold]the same data, twice - field names per key vs field names per schema[/]");
table.AddColumn("keys");
table.AddColumn("loaded via");
table.AddColumn(new TableColumn("keys/s").RightAligned());
table.AddColumn(new TableColumn("used_memory Δ").RightAligned());
table.AddColumn(new TableColumn("per key").RightAligned());
table.AddColumn(new TableColumn("MEMORY USAGE avg").RightAligned());
table.AddColumn("encoding");
table.AddColumn(new TableColumn("templates").RightAligned());

foreach (var r in results)
{
    bool import = r.Via == "HIMPORT";
    table.AddRow(
        $"{r.Keys:N0}",
        import ? "[green]HIMPORT[/]" : "HSET",
        $"{r.KeysPerSecond:N0}",
        $"{r.UsedMemoryDelta / 1024.0 / 1024.0:0.00}MB",
        $"{(double)r.UsedMemoryDelta / r.Keys:0} B",
        $"{r.MemoryUsageAvg:0} B",
        r.Encoding,
        import ? $"{r.Templates} ({r.TemplateKeys:N0} keys, {r.TemplateBytes / 1024.0:0.0}KB)" : "[grey]-[/]");
}
AnsiConsole.Write(table);

// per-key convergence: plain stays flat; the template run approaches values-only cost
var plain50K = results.First(static r => r is { Keys: 50_000, Via: "HSET" });
var import50K = results.First(static r => r is { Keys: 50_000, Via: "HIMPORT" });
var plain100 = results.First(static r => r is { Keys: 100, Via: "HSET" });
var import100 = results.First(static r => r is { Keys: 100, Via: "HIMPORT" });
double saved = 1 - (double)import50K.UsedMemoryDelta / plain50K.UsedMemoryDelta;
double savedPerKey100 = 1 - import100.MemoryUsageAvg / plain100.MemoryUsageAvg;
AnsiConsole.MarkupLine($"""

    At 50,000 keys the shared schema saves [bold]{saved:P0}[/] of hash memory
    ({plain50K.UsedMemoryDelta / 1024.0 / 1024.0:0.0}MB → {import50K.UsedMemoryDelta / 1024.0 / 1024.0:0.0}MB): the field names exist once, not 50,000 times.
    MEMORY USAGE even charges each key its fair share of the template ({import50K.TemplateBytes / 1024.0:0.0}KB / {import50K.TemplateKeys:N0} keys).
    The per-key win is already there at 100 keys ({plain100.MemoryUsageAvg:0} B → {import100.MemoryUsageAvg:0} B, [bold]{savedPerKey100:P0}[/]): the
    template amortizes after a handful of keys. Only the used_memory Δ column needs scale - at 100
    keys that reading drowns in allocator noise, a property of the measurement, not of the feature.
    Reads are unchanged: an imported key is an ordinary hash with a smarter encoding ({import50K.Encoding}).
    """);

// ---- assertions (this demo doubles as a regression check) ----

var failures = new List<string>();
if (import50K.Templates < 1 || import50K.TemplateKeys != 50_000)
{
    failures.Add($"expected one template covering 50,000 keys; saw {import50K.Templates} / {import50K.TemplateKeys}");
}
if (!import50K.Encoding.StartsWith("template", StringComparison.Ordinal))
{
    failures.Add($"expected a template-* encoding, saw {import50K.Encoding}");
}
if (import50K.UsedMemoryDelta >= plain50K.UsedMemoryDelta)
{
    failures.Add("HIMPORT did not reduce memory at 50,000 keys");
}
// the honest per-key metric must show the win at small scale too: MEMORY USAGE includes the
// key's template share, so this holds as soon as the 1.6KB template has a handful of keys
if (import100.MemoryUsageAvg >= plain100.MemoryUsageAvg)
{
    failures.Add($"HIMPORT did not reduce per-key MEMORY USAGE at 100 keys ({plain100.MemoryUsageAvg:0} B -> {import100.MemoryUsageAvg:0} B)");
}

// an imported key is an ordinary hash: HGETALL round-trips the exact values (the last run's
// data is still loaded)
var readBack = (await db.HashGetAllAsync("order:00000000")).ToDictionary(static e => (string)e.Name!, static e => e.Value);
var expected = ExpectedValues(0);
for (int f = 0; f < fields.Length; f++)
{
    if (!readBack.TryGetValue((string)fields[f]!, out var value) || value != expected.Span[f])
    {
        failures.Add($"HGETALL mismatch on {fields[f]}: {value} != {expected.Span[f]}");
        break;
    }
}
if (failures.Count > 0)
{
    foreach (var f in failures) AnsiConsole.MarkupLine($"[red]FAILED:[/] {Markup.Escape(f)}");
    return 1;
}
AnsiConsole.MarkupLine("\n[green]run OK[/]");
return 0;

// ---- helpers ----

async Task<Run> LoadAsync(int n, bool useImport)
{
    await server.FlushDatabaseAsync();
    await Task.Delay(200); // let the allocator settle before the baseline reading
    long before = await UsedMemoryAsync();

    await using var import = useImport ? HashImport.Create(fields) : null;
    var sw = Stopwatch.StartNew();
    var batch = new List<Task>(BatchSize);
    for (int i = 0; i < n; i++)
    {
        RedisKey key = $"order:{i:00000000}";
        if (import is not null)
        {
            batch.Add(db.HashImportAsync(key, import, ExpectedValues(i)));
        }
        else
        {
            var values = ExpectedValues(i);
            var entries = new HashEntry[fields.Length];
            for (int f = 0; f < fields.Length; f++) { entries[f] = new(fields[f], values.Span[f]); }
            batch.Add(db.HashSetAsync(key, entries));
        }
        if (batch.Count == BatchSize)
        {
            await Task.WhenAll(batch);
            batch.Clear();
        }
    }
    await Task.WhenAll(batch);
    sw.Stop();

    await Task.Delay(200);
    long delta = await UsedMemoryAsync() - before;

    // sample MEMORY USAGE over 100 keys (it reports a key's own cost PLUS its template share)
    long usageSum = 0;
    int samples = Math.Min(100, n);
    for (int i = 0; i < samples; i++)
    {
        usageSum += (long)await db.ExecuteAsync("MEMORY", "USAGE", (RedisKey)$"order:{i * (n / samples):00000000}");
    }
    string encoding = (string)(await db.ExecuteAsync("OBJECT", "ENCODING", (RedisKey)"order:00000000"))!;

    var stats = await server.InfoAsync("stats");
    long templates = InfoValue(stats, "hash_templates");
    long templateKeys = InfoValue(stats, "hash_template_keys");
    long templateBytes = InfoValue(await server.InfoAsync("memory"), "used_memory_hash_templates");

    string via = useImport ? "HIMPORT" : "HSET";
    AnsiConsole.MarkupLine(
        $"loaded [bold]{n:N0}[/] × {fields.Length}-field hashes via [bold]{via}[/] in {sw.Elapsed.TotalSeconds:0.0}s " +
        $"({n / sw.Elapsed.TotalSeconds:N0} keys/s): +{delta / 1024.0 / 1024.0:0.00}MB, encoding {encoding}" +
        (useImport ? $", {templates} template(s) over {templateKeys:N0} keys" : ""));

    return new Run(n, via, n / sw.Elapsed.TotalSeconds, delta, (double)usageSum / samples, encoding, templates, templateKeys, templateBytes);
}

ReadOnlyMemory<RedisValue> ExpectedValues(int i)
{
    var rng = new Random(i); // deterministic: both runs load byte-identical data
    return new RedisValue[]
    {
        $"ORD-{i:00000000}",
        $"C-{rng.Next(1, 2_000_000):0000000}",
        rng.Next(5) switch { 0 => "placed", 1 => "paid", 2 => "picked", 3 => "shipped", _ => "delivered" },
        rng.Next(3) switch { 0 => "USD", 1 => "EUR", _ => "GBP" },
        rng.Next(500, 250_000),
        rng.Next(0, 25_000),
        rng.Next(0, 2_500),
        DateTimeOffset.FromUnixTimeSeconds(1_760_000_000 + rng.Next(0, 30_000_000)).ToString("O"),
        DateTimeOffset.FromUnixTimeSeconds(1_760_000_000 + rng.Next(30_000_000, 34_000_000)).ToString("O"),
        $"WH-{(char)('A' + rng.Next(6))}{rng.Next(1, 9)}",
        rng.Next(4) switch { 0 => "ups", 1 => "fedex", 2 => "dhl", _ => "royal-mail" },
        $"1Z{rng.Next():X8}{rng.Next():X8}",
        $"{rng.NextInt64():x16}",
        rng.Next(4) switch { 0 => "none", 1 => "bronze", 2 => "silver", _ => "gold" },
};
}

async Task<long> UsedMemoryAsync() => InfoValue(await server.InfoAsync("memory"), "used_memory");

static long InfoValue(IGrouping<string, KeyValuePair<string, string>>[] info, string key)
    => info.SelectMany(static g => g).Where(p => p.Key == key).Select(static p => long.Parse(p.Value)).FirstOrDefault();

sealed record Run(
    int Keys,
    string Via,
    double KeysPerSecond,
    long UsedMemoryDelta,
    double MemoryUsageAvg,
    string Encoding,
    long Templates,
    long TemplateKeys,
    long TemplateBytes);
