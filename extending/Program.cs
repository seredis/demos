// What would someone implementing an exotic module (JSON.*, FT.*, BF.*, ...) as an EXTERNAL
// caller need to do on the v4 context API? This demo plays that module author, using two real
// server commands as stand-ins so it runs against stock Redis:
//
//   SUBSTR  - genuinely absent from the client's command table (not even in RedisCommand), so it
//             exercises the unknown-command path exactly as JSON.GET would; GETRANGE is its alias,
//             which gives us an oracle to check the answer against
//   LCS     - a reply shape the client's generic types don't cover, so it earns a handler of our
//             own over RespReader; the built-in StringLongestCommonSubsequence* is the oracle
//
// StrMatch.cs is "the library": the command group, the [Resp] declarations, the commands, the
// reply handler. This file is the consumer, walking up the three levels and then through the
// things a module author must get right: keys vs values, run-time arguments, RESP2 vs RESP3
// replies, and the retry category.
//
// Prerequisites: `docker compose up -d --wait` (see README.md).

using StackExchange.Redis;
using StackExchange.Redis.Caching;
using StackExchange.Redis.KeyspaceIsolation;
using StrMatch;

const string Endpoint = "localhost:16421";

int failures = 0;

// The app connection. RESP3 is the default story for v4; act 6 connects a second, RESP2,
// connection to show the same reply spelled differently on the wire.
var options = ConfigurationOptions.Parse(Endpoint + ",abortConnect=false,allowAdmin=true");
options.Protocol = RedisProtocol.Resp3;
await using var conn = await ConnectionMultiplexer.ConnectAsync(options);
var db = conn.GetDatabase();

// A library announces itself once per multiplexer (CLIENT SETINFO lib-name, visible in
// CLIENT LIST): idempotent, applies to every connection including later ones. Act 8 reads it back.
conn.AddLibraryNameSuffix("StrMatch");

// A clean, deterministic slate (allowAdmin is for this and INFO in act 7; the library needs neither).
var server = conn.GetServer(conn.GetEndPoints()[0]);
await server.FlushDatabaseAsync();

// The sample data, borrowed from the LCS documentation: the common subsequence is "mytext".
RedisKey keyA = "sample:a", keyB = "sample:b";
await db.StringSetAsync(keyA, "ohmytext");
await db.StringSetAsync(keyB, "mynewtext");

Console.WriteLine($"Connected to {Endpoint} (RESP3); sample:a=\"ohmytext\", sample:b=\"mynewtext\"");

// ---------------------------------------------------------------------------------------------
Act(1, "the ad-hoc call (ExecuteResp: works on 3.x too)");
// One call, no surface: the command name plus one ordered argument collection. RedisKeyOrValue
// has no implicit conversion from string or int - which argument is a KEY is not recoverable
// from the bytes, so the caller must say. The reply is a RespResult: a leased, undecoded view
// over the raw reply - dispose it, and read out of it before you do.
{
    using var reply = await db.ExecuteRespAsync(
        "SUBSTR", new RedisKeyOrValue[] { (RedisKey)keyA, (RedisValue)0, (RedisValue)3 });
    RedisValue value = reply.ReadScalar().ReadRedisValue();
    Show("db.ExecuteRespAsync(\"SUBSTR\", [key, 0, 3])", value);
    Check(value == "ohmy", "SUBSTR 0..3 of ohmytext");
}

// ---------------------------------------------------------------------------------------------
Act(2, "the same call, interpolated (db.Context.SendAsync<T>($\"...\"))");
// No argument collection at all: each hole is written straight into a pooled buffer as UTF-8 -
// the interpolated string is never built as a string. Literal text is tokenized ("SUBSTR" is the
// command), and the TYPE of each hole decides key-ness: {keyA} is a RedisKey, so it is routed,
// prefixed and tracked. Name the reply type you want.
{
    // The analyzer flags the literal here (SER309): fine for an ad-hoc call, but parsed and
    // encoded on every send - a library declares the command once instead; see StrMatch.cs.
#pragma warning disable SER309
    RedisValue value = await db.Context.SendAsync<RedisValue>($"SUBSTR {keyA} {0} {3}");
#pragma warning restore SER309
    Show("db.Context.SendAsync<RedisValue>($\"SUBSTR {keyA} {0} {3}\")", value);
    Check(value == "ohmy", "interpolated SUBSTR");
}

// ---------------------------------------------------------------------------------------------
Act(3, "a surface of your own (db.StrMatch().SubstrAsync(...))");
// The same send, given a home: a group struct holding the context, an accessor on any keyspace
// target, and commands as extension methods. This is exactly the shape the client's own command
// groups use, and it needs no cooperation from the client.
{
    RedisValue sub = await db.StrMatch().SubstrAsync(keyA, 0, 3);
    RedisValue lcs = await db.StrMatch().LongestCommonAsync(keyA, keyB);
    Show("db.StrMatch().SubstrAsync(keyA, 0, 3)", sub);
    Show("db.StrMatch().LongestCommonAsync(keyA, keyB)", lcs);

    // the oracles: the same commands through the client's own API
    Check(sub == await db.StringGetRangeAsync(keyA, 0, 3), "SUBSTR agrees with StringGetRange");
    Check(lcs == await db.StringLongestCommonSubsequenceAsync(keyA, keyB), "LCS agrees with the built-in");
}

// ---------------------------------------------------------------------------------------------
Act(4, "keys are keys: WithKeyPrefix rewrites them - IF the library declared them");
// A prefixed database rewrites every RedisKey without the module code knowing. The trap: a key
// held as a string binds as a VALUE - no prefix, no cluster slot, no cache invalidation - and it
// behaves identically on a single plain server, which is exactly why it survives testing.
{
    IDatabase tenant = db.WithKeyPrefix("tenant:42:");
    await tenant.StringSetAsync("doc", "redis in action");

    // the server-side truth: the data lives under the prefixed name
    Show("plain db.StringGetAsync(\"tenant:42:doc\")", await db.StringGetAsync("tenant:42:doc"));

    RedisValue viaKey = await tenant.StrMatch().SubstrAsync("doc", 0, 4);
    RedisValue viaValue = await tenant.StrMatch().SubstrOopsAsync("doc", 0, 4);
    Show("tenant.StrMatch().SubstrAsync(\"doc\", 0, 4)     [key: RedisKey]", $"\"{viaKey}\"");
    Show("tenant.StrMatch().SubstrOopsAsync(\"doc\", 0, 4) [key as string]", $"\"{viaValue}\"");

    Check(viaKey == "redis", "RedisKey hole saw the tenant's data");
    Check(!viaKey.IsNullOrEmpty && viaValue.IsNullOrEmpty, "string hole missed it");
    Console.WriteLine("   The broken form sent the key as a VALUE, so the prefix was not applied: the server was");
    Console.WriteLine("   asked for bare 'doc', a key that does not exist (SUBSTR answers it with an empty string).");
    Console.WriteLine("   On a cluster it would also have no slot, and with a client cache no invalidation - but on");
    Console.WriteLine("   a single plain server the two forms behave identically, which is exactly why the bug");
    Console.WriteLine("   survives testing. If your library has one test, make it a prefixed-context test.");
}

// ---------------------------------------------------------------------------------------------
Act(5, "arguments decided at run time: Compose, and a span as one hole");
// A single interpolated string needs every argument at the call site. MatchesAsync starts the
// command with Compose and appends MINMATCHLEN only when asked (see StrMatch.cs); a variable
// number of KEYS needs no Compose at all - a ReadOnlySpan<RedisKey> is one hole, every element
// routed and prefixed like any other key.
{
    var all = await db.StrMatch().MatchesAsync(keyA, keyB);
    var min4 = await db.StrMatch().MatchesAsync(keyA, keyB, minMatchLength: 4);
    Console.WriteLine($"   MatchesAsync(a, b)                     -> {Describe(all)}");
    Console.WriteLine($"   MatchesAsync(a, b, minMatchLength: 4)  -> {Describe(min4)}");
    Check(all.Matches.Count == 2 && min4.Matches.Count == 1, "MINMATCHLEN appended only when asked");

    long existing = await db.StrMatch().CountExistingAsync([keyA, keyB, "sample:missing"]);
    Show("CountExistingAsync([a, b, missing])", existing);
    Check(existing == 2, "EXISTS with a span of keys");
}

// ---------------------------------------------------------------------------------------------
Act(6, "the reader: one handler, both protocols");
// The LCS IDX reply is a flat array under RESP2 and a map under RESP3. The library's handler
// (StrMatch.cs, MatchesHandler) never notices: it tests IsScalar/IsAggregate and reads the
// fields by name. It records reader.Prefix purely so this act can show the wire difference.
{
    var resp2Options = ConfigurationOptions.Parse(Endpoint + ",abortConnect=false");
    resp2Options.Protocol = RedisProtocol.Resp2;
    await using var resp2Conn = await ConnectionMultiplexer.ConnectAsync(resp2Options);

    var viaResp3 = await db.StrMatch().MatchesAsync(keyA, keyB, minMatchLength: 4);
    var viaResp2 = await resp2Conn.GetDatabase().StrMatch().MatchesAsync(keyA, keyB, minMatchLength: 4);
    Console.WriteLine($"   RESP3 wire shape: {viaResp3.WireShape,-5}  ->  {Describe(viaResp3)}");
    Console.WriteLine($"   RESP2 wire shape: {viaResp2.WireShape,-5}  ->  {Describe(viaResp2)}");
    Check(viaResp3.WireShape != viaResp2.WireShape, "the wire really did differ");
    Check(Describe(viaResp3) == Describe(viaResp2), "the parsed result did not");

    // the oracle: the client's own LCS parser sees the same matches
    var oracle = await db.StringLongestCommonSubsequenceWithMatchesAsync(keyA, keyB, 4);
    Check(oracle.Matches.Length == viaResp3.Matches.Count
        && oracle.LongestMatchLength == viaResp3.TotalLength, "agrees with the built-in LCS parser");
}

// ---------------------------------------------------------------------------------------------
Act(7, "the retry category: declared read-only, and therefore cacheable");
// The client has no retry-category entry for a command it does not know, so it assumes the
// worst: never replayed after a reconnect, and never cached. StrMatch declares SUBSTR read-only
// (WithRetryCategory(CommandRetryReadOnly) - caller-wins, so it is a default, not an override).
// With a client-side cache enabled, that declaration is visible: repeated reads stop reaching
// the server, and a write to the key invalidates the cached reply.
{
    var cachedOptions = ConfigurationOptions.Parse(Endpoint + ",abortConnect=false");
    cachedOptions.Protocol = RedisProtocol.Resp3; // invalidations arrive as RESP3 push messages
    cachedOptions.ClientCache = new CacheOptions
    {
        TrackingMode = CacheTrackingMode.Tracking,
        Admission = CacheAdmission.OnFirstMiss,
        DefaultPolicy = new CachePolicy { TimeToLive = TimeSpan.FromSeconds(60) },
    };
    await using var cachedConn = await ConnectionMultiplexer.ConnectAsync(cachedOptions);
    var cachedDb = cachedConn.GetDatabase();

    long before = await ServerSubstrCallsAsync();
    for (int i = 0; i < 5; i++)
    {
        await cachedDb.StrMatch().SubstrAsync(keyA, 0, 3);
    }
    long afterReads = await ServerSubstrCallsAsync();
    Console.WriteLine($"   5 SubstrAsync reads -> the server ran SUBSTR {afterReads - before} time(s)"
        + $"  (client cache: {cachedConn.GetClientCache()?.Count} entries)");
    Check(afterReads - before == 1, "4 of 5 reads served from the client cache");

    await db.StringSetAsync(keyA, "ohmytext"); // another connection writes the key...
    await Task.Delay(500);                     // ...the invalidation push arrives...
    await cachedDb.StrMatch().SubstrAsync(keyA, 0, 3);
    long afterWrite = await ServerSubstrCallsAsync();
    Console.WriteLine($"   write to the key from elsewhere, read again -> the server ran SUBSTR {afterWrite - afterReads} more time(s)");
    Check(afterWrite - afterReads == 1, "the write invalidated the cached reply");
    Console.WriteLine("   Without the declared category, every read would be a round trip - and none would be");
    Console.WriteLine("   replayed after a reconnect. A read whose answer changes without a write to its keys");
    Console.WriteLine("   should add CommandFlags.NoClientCache alongside the category.");
}

// ---------------------------------------------------------------------------------------------
Act(8, "the closing credit: lib-name");
// AddLibraryNameSuffix (top of this file) rode along on the handshake; the ad-hoc API is a fine
// way to peek at it, since CLIENT INFO has no dedicated API either.
{
#pragma warning disable SER309 // ad-hoc, once: the literal is fine here
    string? info = await db.Context.SendAsync<string>($"CLIENT INFO");
#pragma warning restore SER309
    string? libName = info?.Split(' ').FirstOrDefault(static t => t.StartsWith("lib-name=", StringComparison.Ordinal));
    Show("CLIENT INFO", libName ?? "(no lib-name reported)");
    Check(libName is not null && libName.Contains("StrMatch"), "the suffix reached the server");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("All acts agreed with their oracles.");
    return 0;
}
Console.WriteLine($"{failures} check(s) FAILED.");
return 1;

void Act(int n, string title)
{
    Console.WriteLine();
    Console.WriteLine($"== Act {n}: {title} ==");
}

void Show(string what, object? value) => Console.WriteLine($"   {what} -> {value}");

void Check(bool ok, string what)
{
    if (!ok)
    {
        failures++;
        Console.WriteLine($"   CHECK FAILED: {what}");
    }
}

static string Describe(SubstringMatchResult result)
    => $"len={result.TotalLength}, matches=[{string.Join(", ", result.Matches.Select(static m => $"a[{m.FirstStart}..{m.FirstEnd}]=b[{m.SecondStart}..{m.SecondEnd}] ({m.Length})"))}]";

// INFO commandstats: how many times the server has actually run SUBSTR - the measurement that
// shows which reads the client cache absorbed.
async Task<long> ServerSubstrCallsAsync()
{
    foreach (var group in await server.InfoAsync("commandstats"))
    {
        foreach (var pair in group)
        {
            if (pair.Key == "cmdstat_substr")
            {
                // calls=N,usec=...
                var calls = pair.Value.Split(',')[0];
                return long.Parse(calls["calls=".Length..]);
            }
        }
    }
    return 0;
}
