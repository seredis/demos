// Compressed (roaring) bitmaps in Redis 8.12, measured against classic string bitmaps.
//
// Three targets, one workload:
//   "8.10 string"  - redis:8.10.3, classic string bitmaps: the baseline you run today
//   "8.12 string"  - the 8.12 server, same classic encoding (proves the win below comes from the
//                    encoding, not the server version)
//   "8.12 roaring" - the same 8.12 server, same data, OBJ_ENCODING_BITMAP_ROARING
//
// The headline: the ordinary bitmap commands (SETBIT/GETBIT/BITCOUNT/BITPOS/BITOP/BITFIELD) work
// unchanged on roaring bitmaps, so everything measured here runs through the ordinary IDatabase
// methods - no new client API involved. The one genuinely new user-facing command, BITRANGEINTARRAY,
// is sent through the v4 ad-hoc context API (see the extending/ demo); BITCONVERT and BITROAROP are
// INTERNAL commands (SYSCMD) - the replication/AOF carriers of the conversion and of BITOP's native
// result - so clients never send them, and there is nothing to add for them.
//
// Memory is read with MEMORY USAGE; timings are measured ON THE SERVER, from INFO commandstats
// (usec/calls per command), so client round trips play no part in the numbers.
//
// Prerequisites: `docker compose up -d --wait` (see README.md; the 8.12 image builds from source
// the first time, which takes a few minutes).

using RESPite.Messages;               // for ReadOnlyLease via the context API
using StackExchange.Redis;
using StackExchange.Redis.Protocol;   // RespCommand/RespFragment for the [Resp] declarations

const string Endpoint810 = "localhost:16431";
const string Endpoint812 = "localhost:16432";

const long Extent = 1L << 28;          // 268,435,456 bits: a 32 MiB string bitmap
const int SparseBits = 100_000;        // sparse-random population size
const int MeasuredBitcounts = 30;
const int MeasuredGetbits = 2_000;
const int MeasuredBitops = 10;
const int MeasuredSetbits = 50_000;

int failures = 0;

// allowAdmin: CONFIG SET/RESETSTAT, INFO and FLUSHALL are demo plumbing, not part of the feature
await using var conn810 = await ConnectionMultiplexer.ConnectAsync(Endpoint810 + ",abortConnect=false,allowAdmin=true");
await using var conn812 = await ConnectionMultiplexer.ConnectAsync(Endpoint812 + ",abortConnect=false,allowAdmin=true");
var db810 = conn810.GetDatabase();
var db812 = conn812.GetDatabase();
var server810 = conn810.GetServer(conn810.GetEndPoints()[0]);
var server812 = conn812.GetServer(conn812.GetEndPoints()[0]);

await server810.FlushDatabaseAsync();
await server812.FlushDatabaseAsync();
Console.WriteLine($"Connected: {Endpoint810} (v{server810.Version}) and {Endpoint812} (v{server812.Version})");
Console.WriteLine("(the 8.12 release candidate reports itself as 8.11.x, as Redis RCs do)");

// ---------------------------------------------------------------------------------------------
// The populations: built client-side as byte[], so all targets provably hold identical data.
// ---------------------------------------------------------------------------------------------
var populations = new[]
{
    Population.SparseRandom("sparse-a", SparseBits, Extent, seed: 1),
    Population.SparseRandom("sparse-b", SparseBits, Extent, seed: 2),
    Population.ClusteredRuns("clustered", runs: 100, runLength: 1_000, Extent, seed: 3),
    Population.DenseBlock("dense", bits: 1_000_000),
    Population.NoiseBytes("noise", bytes: 1 << 20, seed: 4), // ~50% random: roaring's worst case
};

Act(1, "one workload, two servers, three encodings");
// SET uploads each population as a plain string bitmap on both servers. The roaring twin on 8.12
// is the same bytes, copied and then converted: with bitmap-default-roaring on, the next WRITE to
// a string bitmap converts it in place (re-setting an already-set bit changes no data). Reads
// never convert, and a converted key stays roaring when the config goes back off.
{
    foreach (var pop in populations)
    {
        await db810.StringSetAsync(pop.Key, pop.Bytes);
        await db812.StringSetAsync(pop.Key, pop.Bytes);
        await db812.KeyCopyAsync(pop.Key, pop.RoarKey);
    }
    await server812.ConfigSetAsync("bitmap-default-roaring", "yes");
    foreach (var pop in populations)
    {
        await db812.StringSetBitAsync(pop.RoarKey, pop.AnySetBit, true); // already set: data unchanged
    }
    await server812.ConfigSetAsync("bitmap-default-roaring", "no");

    var pop0 = populations[0];
    Console.WriteLine($"   {Endpoint810} {pop0.Key,-16} TYPE {await TypeOf(db810, pop0.Key),-7} OBJECT ENCODING {await EncodingOf(db810, pop0.Key)}");
    Console.WriteLine($"   {Endpoint812} {pop0.Key,-16} TYPE {await TypeOf(db812, pop0.Key),-7} OBJECT ENCODING {await EncodingOf(db812, pop0.Key)}");
    Console.WriteLine($"   {Endpoint812} {pop0.RoarKey,-16} TYPE {await TypeOf(db812, pop0.RoarKey),-7} OBJECT ENCODING {await EncodingOf(db812, pop0.RoarKey)}");
    Check(await EncodingOf(db812, pop0.RoarKey) == "bitmap-roaring", "the twin converted");
}

Act(2, "memory: MEMORY USAGE, same data in each encoding");
{
    Console.WriteLine($"   {"population",-12} {"bits set",12} {"extent",10} {"8.10 string",14} {"8.12 string",14} {"8.12 roaring",14} {"ratio",8}");
    foreach (var pop in populations)
    {
        long m810 = await MemoryOf(db810, pop.Key);
        long m812s = await MemoryOf(db812, pop.Key);
        long m812r = await MemoryOf(db812, pop.RoarKey);
        Console.WriteLine($"   {pop.Name,-12} {pop.BitCount,12:N0} {Human(pop.Bytes.Length),10} {Human(m810),14} {Human(m812s),14} {Human(m812r),14} {(double)m812s / m812r,7:0.0}x");
        if (pop.Name is "sparse-a" or "clustered") Check(m812r * 10 < m812s, $"{pop.Name}: roaring is at least 10x smaller");
    }
    Console.WriteLine("   Sparse and clustered populations collapse; the ~50% noise row is the honest one -");
    Console.WriteLine("   incompressible data has no free lunch, and roaring concedes a little to overhead.");
}

Act(3, "same commands, same answers: the encoding is transparent to clients");
// Everything here is the ordinary IDatabase API - the client neither knows nor cares which
// encoding serves it. The expected values are computed client-side from the population itself.
{
    foreach (var pop in populations)
    {
        long c810 = await db810.StringBitCountAsync(pop.Key);
        long c812s = await db812.StringBitCountAsync(pop.Key);
        long c812r = await db812.StringBitCountAsync(pop.RoarKey);
        Check(c810 == pop.BitCount && c812s == pop.BitCount && c812r == pop.BitCount, $"{pop.Name}: BITCOUNT {pop.BitCount:N0} everywhere");

        long p812r = await db812.StringBitPositionAsync(pop.RoarKey, true);
        Check(p812r == pop.FirstSetBit, $"{pop.Name}: BITPOS agrees");
    }
    var sparse = populations[0];
    bool set810 = await db810.StringGetBitAsync(sparse.Key, sparse.AnySetBit);
    bool set812r = await db812.StringGetBitAsync(sparse.RoarKey, sparse.AnySetBit);
    bool clear812r = await db812.StringGetBitAsync(sparse.RoarKey, sparse.AnyClearBit);
    Check(set810 && set812r && !clear812r, "GETBIT spot checks agree");
    Console.WriteLine($"   BITCOUNT, BITPOS and GETBIT answered identically on all three targets ({populations.Length} populations).");
}

Act(4, "read performance, measured on the server (INFO commandstats, µs per call)");
// Identical suites per target: BITCOUNT over the sparse key, random GETBITs, and BITOP AND/OR of
// the two sparse keys into a destination. CONFIG RESETSTAT isolates each suite; the numbers are
// what the server itself accounts per call, so client round trips are not in them.
{
    var a = populations[0];
    var b = populations[1];
    var r810 = await MeasureReadSuite(db810, server810, a.Key, b.Key);
    var r812s = await MeasureReadSuite(db812, server812, a.Key, b.Key);
    var r812r = await MeasureReadSuite(db812, server812, a.RoarKey, b.RoarKey);

    Console.WriteLine($"   {"command",-34} {"8.10 string",14} {"8.12 string",14} {"8.12 roaring",14}");
    Console.WriteLine($"   {$"BITCOUNT ({Human(a.Bytes.Length)} extent)",-34} {r810.BitCount,14:N1} {r812s.BitCount,14:N1} {r812r.BitCount,14:N1}");
    Console.WriteLine($"   {"GETBIT (random offsets)",-34} {r810.GetBit,14:N1} {r812s.GetBit,14:N1} {r812r.GetBit,14:N1}");
    Console.WriteLine($"   {"BITOP AND/OR (a,b -> dest)",-34} {r810.BitOp,14:N1} {r812s.BitOp,14:N1} {r812r.BitOp,14:N1}");
    Console.WriteLine("   (GETBIT: a point read pays a small container-lookup cost on roaring - sub-microsecond either way)");
    Check(r812r.BitCount * 5 < r812s.BitCount, "BITCOUNT at least 5x faster on roaring");
    Check(r812r.BitOp * 5 < r812s.BitOp, "BITOP at least 5x faster on roaring");

    // the destination follows the operands: string operands build the full-extent string, native
    // operands a native result - which replicates as BITROAROP, an INTERNAL command (SYSCMD): the
    // client never sends it, and keeps saying BITOP
    await db812.StringBitOperationAsync(Bitwise.Or, "dest:op", a.Key, b.Key);
    long destString = await MemoryOf(db812, "dest:op");
    string? destStringEnc = await EncodingOf(db812, "dest:op");
    await db812.StringBitOperationAsync(Bitwise.Or, "dest:op", a.RoarKey, b.RoarKey);
    long destRoar = await MemoryOf(db812, "dest:op");
    string? destRoarEnc = await EncodingOf(db812, "dest:op");
    Console.WriteLine($"   BITOP OR dest: {Human(destString)} ({destStringEnc}) from string operands; {Human(destRoar)} ({destRoarEnc}) from roaring ones.");
    Check(destStringEnc == "raw" && destRoarEnc == "bitmap-roaring", "the result encoding follows the operands");
}

Act(5, "write performance: the same 50,000 SETBITs into a fresh key");
// The roaring key is created under bitmap-default-roaring=yes, so every write lands natively;
// the string phases run with it off. Same offsets, same order, fire-and-forget, drained with a
// PING before reading the stats.
{
    long[] offsets = populations[0].SetBits; // 100k sparse positions; first 50k used
    double w810 = await MeasureSetbits(db810, server810, "fresh", offsets);
    double w812s = await MeasureSetbits(db812, server812, "fresh", offsets);
    await server812.ConfigSetAsync("bitmap-default-roaring", "yes");
    double w812r = await MeasureSetbits(db812, server812, "fresh:roar", offsets);
    await server812.ConfigSetAsync("bitmap-default-roaring", "no");

    Console.WriteLine($"   {"command",-34} {"8.10 string",14} {"8.12 string",14} {"8.12 roaring",14}");
    Console.WriteLine($"   {$"SETBIT ({MeasuredSetbits:N0} fresh writes)",-34} {w810,14:N1} {w812s,14:N1} {w812r,14:N1}");
    Console.WriteLine($"   The first sparse SETBIT on a string allocates the whole {Human((int)(Extent / 8))} extent up front;");
    Console.WriteLine($"   roaring allocates as containers are touched. Encoding of the fresh roaring key: {await EncodingOf(db812, "fresh:roar")}.");
    Check(await EncodingOf(db812, "fresh:roar") == "bitmap-roaring", "config-on writes created a native key");
}

Act(6, "migrating a live key: flip the config, touch the key");
// There is no client-facing convert command - BITCONVERT exists but is INTERNAL, the carrier that
// replicates the transition to replicas/AOF. The migration story is operational: turn on
// bitmap-default-roaring, and each existing string bitmap converts on its next write. One-way and
// sticky: reads never convert, and the key stays native if the config is turned back off.
{
    var legacy = populations[2]; // clustered
    RedisKey key = "legacy";
    await db812.StringSetAsync(key, legacy.Bytes);
    long before = await MemoryOf(db812, key);
    string? encBefore = await EncodingOf(db812, key);

    await server812.ConfigSetAsync("bitmap-default-roaring", "yes");
    await db812.StringBitCountAsync(key); // a READ: does not convert
    string? encAfterRead = await EncodingOf(db812, key);
    await db812.StringSetBitAsync(key, legacy.AnySetBit, true); // a WRITE: converts in place
    await server812.ConfigSetAsync("bitmap-default-roaring", "no");

    long after = await MemoryOf(db812, key);
    string? encAfter = await EncodingOf(db812, key);
    Console.WriteLine($"   before: {encBefore}, {Human(before)}; after a read under the config: {encAfterRead};");
    Console.WriteLine($"   after one write: {encAfter}, {Human(after)} - and BITCOUNT still says {await db812.StringBitCountAsync(key):N0}.");
    Check(encBefore == "raw" && encAfterRead == "raw" && encAfter == "bitmap-roaring", "write converts, read does not");
    Check(await db812.StringBitCountAsync(key) == legacy.BitCount, "no data changed in the migration");
}

Act(7, "the one new command: BITRANGEINTARRAY");
// "Which bits are set in this range?" used to need a client-side scan of GETRANGE bytes or a
// BITPOS loop; 8.12 answers it directly (BITRANGEINTARRAY key start end [BYTE|BIT] [LIMIT n]
// [ZEROS]). The client API for it is PR stackexchange/StackExchange.Redis#3315
// (IDatabase.StringBitPositions); until that ships, the v4 ad-hoc surface sends it - see the
// extending/ demo for that machinery at full length.
{
    var sparse = populations[0];
    using var lease = await db812.Context.SendAsync<ReadOnlyLease<long>>(
        $"{Cmds.BitRangeIntArray}{sparse.RoarKey}{0L}{-1L}{Cmds.Bit}{Cmds.Limit}{10L}");
    long[] viaServer = lease.Span.ToArray();
    long[] expected = sparse.SetBits.Take(10).ToArray();
    Console.WriteLine($"   first 10 set bits of {sparse.RoarKey}: [{string.Join(", ", viaServer)}]");
    Check(viaServer.SequenceEqual(expected), "matches the positions the demo generated");

    try
    {
        using var nope = await db810.Context.SendAsync<ReadOnlyLease<long>>(
            $"{Cmds.BitRangeIntArray}{sparse.Key}{0L}{-1L}{Cmds.Bit}{Cmds.Limit}{10L}");
        Check(false, "8.10 should not know this command");
    }
    catch (RedisServerException ex)
    {
        Console.WriteLine($"   and on 8.10: {ex.Message}");
    }
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("All checks passed.");
    return 0;
}
Console.WriteLine($"{failures} check(s) FAILED.");
return 1;

void Act(int n, string title)
{
    Console.WriteLine();
    Console.WriteLine($"== Act {n}: {title} ==");
}

void Check(bool ok, string what)
{
    if (!ok)
    {
        failures++;
        Console.WriteLine($"   CHECK FAILED: {what}");
    }
}

static string Human(long bytes) => bytes switch
{
    >= 10 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
    >= 10 * 1024 => $"{bytes / 1024.0:N1} KB",
    _ => $"{bytes:N0} B",
};

// the ad-hoc v4 context calls: commands the demo inspects the server with
static ValueTask<string?> TypeOf(IDatabase db, RedisKey key)
    => db.Context.SendAsync<string?>($"{Cmds.Type}{key}");

static ValueTask<string?> EncodingOf(IDatabase db, RedisKey key)
    => db.Context.SendAsync<string?>($"{Cmds.ObjectCmd}{Cmds.Encoding}{key}");

static ValueTask<long> MemoryOf(IDatabase db, RedisKey key)
    => db.Context.SendAsync<long>($"{Cmds.Memory}{Cmds.Usage}{key}");

// one measured read suite: BITCOUNT xN, GETBIT xN (random offsets), BITOP AND/OR xN
async Task<(double BitCount, double GetBit, double BitOp)> MeasureReadSuite(IDatabase db, IServer server, RedisKey a, RedisKey b)
{
    await ResetStats(server);
    for (int i = 0; i < MeasuredBitcounts; i++) await db.StringBitCountAsync(a);
    var rng = new Random(42);
    for (int i = 0; i < MeasuredGetbits; i++)
    {
        _ = db.StringGetBitAsync(a, rng.NextInt64(Extent), CommandFlags.FireAndForget);
    }
    for (int i = 0; i < MeasuredBitops; i++)
    {
        await db.StringBitOperationAsync(Bitwise.And, "dest:op", a, b);
        await db.StringBitOperationAsync(Bitwise.Or, "dest:op", a, b);
    }
    await db.PingAsync(); // drain the fire-and-forget tail before reading the stats
    var stats = await CommandStats(server);
    return (stats["bitcount"], stats["getbit"], stats["bitop"]);
}

async Task<double> MeasureSetbits(IDatabase db, IServer server, RedisKey key, long[] offsets)
{
    await db.KeyDeleteAsync(key);
    await ResetStats(server);
    for (int i = 0; i < MeasuredSetbits; i++)
    {
        _ = db.StringSetBitAsync(key, offsets[i], true, CommandFlags.FireAndForget);
    }
    await db.PingAsync();
    return (await CommandStats(server))["setbit"];
}

static async ValueTask ResetStats(IServer server)
    => await server.Context.SendAsync<string>($"{Cmds.Config}{Cmds.Resetstat}");

// INFO commandstats: "cmdstat_bitcount: calls=30,usec=61572,usec_per_call=2052.40,..."
static async Task<Dictionary<string, double>> CommandStats(IServer server)
{
    var result = new Dictionary<string, double>();
    foreach (var group in await server.InfoAsync("commandstats"))
    {
        foreach (var pair in group)
        {
            var parts = pair.Value.Split(',');
            long calls = long.Parse(parts[0]["calls=".Length..]);
            long usec = long.Parse(parts[1]["usec=".Length..]);
            result[pair.Key["cmdstat_".Length..]] = (double)usec / calls;
        }
    }
    return result;
}

/// <summary>The commands the demo sends through the v4 context API, declared once (see SER309 and
/// the extending/ demo); everything measured uses the ordinary IDatabase methods instead.</summary>
internal static partial class Cmds
{
    [Resp("BITRANGEINTARRAY")]
    internal static partial RespCommand BitRangeIntArray { get; }

    [Resp("OBJECT")]
    internal static partial RespCommand ObjectCmd { get; }

    [Resp("TYPE")]
    internal static partial RespCommand Type { get; }

    [Resp("MEMORY")]
    internal static partial RespCommand Memory { get; }

    [Resp("CONFIG")]
    internal static partial RespCommand Config { get; }

    [Resp] // tokens inferred from the member names, upper-cased
    internal static partial RespFragment Encoding { get; }

    [Resp]
    internal static partial RespFragment Usage { get; }

    [Resp]
    internal static partial RespFragment Resetstat { get; }

    [Resp]
    internal static partial RespFragment Bit { get; }

    [Resp]
    internal static partial RespFragment Limit { get; }
}

/// <summary>A deterministic bitmap population: the exact bytes, and the facts the oracles need.</summary>
internal sealed class Population
{
    public required string Name { get; init; }
    public required byte[] Bytes { get; init; }
    public required long[] SetBits { get; init; }   // ascending, distinct
    public required long BitCount { get; init; }
    public required long AnyClearBit { get; init; }

    public RedisKey Key => $"pop:{Name}";
    public RedisKey RoarKey => $"pop:{Name}:roar";
    public long AnySetBit => SetBits[0];
    public long FirstSetBit => SetBits[0];

    private static Population Build(string name, IEnumerable<long> positions, long extent)
    {
        var distinct = positions.Distinct().Order().ToArray();
        var bytes = new byte[(extent + 7) / 8];
        foreach (long bit in distinct) bytes[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
        long clear = 0;
        while (Array.BinarySearch(distinct, clear) >= 0) clear++;
        return new Population { Name = name, Bytes = bytes, SetBits = distinct, BitCount = distinct.Length, AnyClearBit = clear };
    }

    public static Population SparseRandom(string name, int bits, long extent, int seed)
    {
        var rng = new Random(seed);
        return Build(name, Enumerable.Range(0, bits).Select(_ => rng.NextInt64(extent)), extent);
    }

    public static Population ClusteredRuns(string name, int runs, int runLength, long extent, int seed)
    {
        var rng = new Random(seed);
        IEnumerable<long> Positions()
        {
            for (int r = 0; r < runs; r++)
            {
                long start = rng.NextInt64(extent - runLength);
                for (int i = 0; i < runLength; i++) yield return start + i;
            }
        }
        return Build(name, Positions(), extent);
    }

    public static Population DenseBlock(string name, int bits)
        => Build(name, Enumerable.Range(0, bits).Select(static i => (long)i), bits);

    public static Population NoiseBytes(string name, int bytes, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[bytes];
        rng.NextBytes(buf);
        var bits = new List<long>();
        for (long i = 0; i < (long)bytes * 8; i++)
        {
            if ((buf[i >> 3] & (0x80 >> (int)(i & 7))) != 0) bits.Add(i);
        }
        return new Population { Name = name, Bytes = buf, SetBits = bits.ToArray(), BitCount = bits.Count, AnyClearBit = bits[0] == 0 ? FindClear(buf) : 0 };

        static long FindClear(byte[] buf)
        {
            for (long i = 0; i < (long)buf.Length * 8; i++)
            {
                if ((buf[i >> 3] & (0x80 >> (int)(i & 7))) == 0) return i;
            }
            throw new InvalidOperationException("no clear bit");
        }
    }
}
