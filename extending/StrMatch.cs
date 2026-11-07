// Everything in this file is what the EXTENDING LIBRARY ships; it needs no change to
// StackExchange.Redis. Pretend "StrMatch" is a module (think JSON.*, FT.*, BF.*) and that
// SUBSTR and LCS are its commands - they are real server commands, so the demo runs against
// stock Redis, but SUBSTR genuinely isn't in the client's command table, so it exercises the
// unknown-command path exactly as JSON.GET would.

using RESPite.Messages;               // RespReader, RespPrefix: the reply-side types
using StackExchange.Redis;
using StackExchange.Redis.Protocol;   // RespCommand, RespFragment, IRespHandler: the request/reply plumbing

namespace StrMatch;

/// <summary>The command group: a context plus a name, and nothing else.</summary>
/// <remarks>The context is what carries the database number, the key prefix and the connection;
/// holding it (rather than an <see cref="IDatabase"/>) is what makes the commands work identically
/// on a database, a batch, a transaction, or a prefixed context.</remarks>
public readonly struct StrMatchCommands(RespContext context)
{
    /// <summary>The context these commands are sent through.</summary>
    public RespContext Context { get; } = context;
}

/// <summary>One match from <c>LCS ... IDX</c>: where the common substring sits in each string.</summary>
/// <remarks>Offsets are inclusive, as the server reports them.</remarks>
public readonly record struct SubstringMatch(long FirstStart, long FirstEnd, long SecondStart, long SecondEnd, long Length);

/// <summary>The parsed <c>LCS ... IDX WITHMATCHLEN</c> reply: a reply shape only this library knows.</summary>
public sealed class SubstringMatchResult
{
    /// <summary>The individual matches, longest/rightmost first, as the server reports them.</summary>
    public required IReadOnlyList<SubstringMatch> Matches { get; init; }

    /// <summary>The total length of the longest common subsequence.</summary>
    public required long TotalLength { get; init; }

    /// <summary>How the reply was spelled on the wire: <see cref="RespPrefix.Array"/> under RESP2,
    /// <see cref="RespPrefix.Map"/> under RESP3. Recorded by the demo to show the difference; the
    /// handler never branches on it.</summary>
    public required RespPrefix WireShape { get; init; }
}

/// <summary>The accessor that reaches the group, and the commands themselves.</summary>
public static partial class StrMatchExtensions
{
    // ---- the commands and tokens, declared once ----------------------------------------------
    // The [Resp] generator ships in the StackExchange.Redis package: it writes the property body,
    // and checks the name at build time (one token, printable ASCII - a typo is a build warning,
    // not a server error at run time). A RespCommand resolves through the command map, so a caller
    // who renamed or disabled a command is still honoured; a RespFragment is a pre-framed operand.

    [Resp("SUBSTR")]
    private static partial RespCommand Substr { get; }

    [Resp("LCS")]
    private static partial RespCommand Lcs { get; }

    [Resp] // token inferred from the member name, upper-cased: "IDX"
    private static partial RespFragment Idx { get; }

    [Resp]
    private static partial RespFragment Minmatchlen { get; }

    [Resp]
    private static partial RespFragment Withmatchlen { get; }

    [Resp("EXISTS")]
    private static partial RespCommand Exists { get; }

    // ---- the accessor -------------------------------------------------------------------------

    /// <summary>The StrMatch commands, on any keyspace target: a database, batch or transaction -
    /// and a database context itself, so a prefixed context reaches it too.</summary>
    /// <remarks>The cast steps out of the typed world into the plumbing, deliberately: the group
    /// keeps the context (with its key prefix and database), not the target.</remarks>
    public static StrMatchCommands StrMatch<TTarget>(this TTarget target) where TTarget : IRespKeyspaceTarget
        => new((RespContext)target.Context);

    // ---- the commands -------------------------------------------------------------------------

    /// <summary><c>SUBSTR</c>: the substring between two inclusive offsets.</summary>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one
    /// already written still runs on the server, and its reply is discarded.</param>
    /// <remarks>The client has no retry-category entry for a command it does not know, so it assumes
    /// the worst: never replayed after a reconnect, never cached. <c>SUBSTR</c> is a pure read, so
    /// the library says so - <c>WithRetryCategory</c> is caller-wins, so this is a default, not an
    /// override. Declaring it read-only also makes it client-side cacheable (see act 7).</remarks>
    public static ValueTask<RedisValue> SubstrAsync(
        this in StrMatchCommands strMatch,
        RedisKey key,
        long start,
        long end,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => strMatch.Context.SendAsync<RedisValue>(
            $"{Substr}{key}{start}{end}",
            flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly),
            cancellationToken: cancellationToken);

    /// <summary>DELIBERATELY WRONG - the bug act 4 exists to demonstrate. The key parameter is a
    /// <see langword="string"/>, and the author forgot the <c>(RedisKey)</c> cast: a string hole
    /// binds as a <b>value</b>, so the "key" takes no part in cluster routing, key prefixing, or
    /// cache invalidation. Identical behaviour on a single plain server; wrong everywhere else.</summary>
    public static ValueTask<RedisValue> SubstrOopsAsync(
        this in StrMatchCommands strMatch,
        string key, // a key held as a string: the trap
        long start,
        long end,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => strMatch.Context.SendAsync<RedisValue>(
            $"{Substr}{key}{start}{end}", // should be {(RedisKey)key}
            flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly),
            cancellationToken: cancellationToken);

    /// <summary><c>LCS</c>: the longest common subsequence of two string keys.</summary>
    /// <param name="cancellationToken"><inheritdoc cref="SubstrAsync" path="/param[@name='cancellationToken']"/></param>
    public static ValueTask<RedisValue> LongestCommonAsync(
        this in StrMatchCommands strMatch,
        RedisKey first,
        RedisKey second,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => strMatch.Context.SendAsync<RedisValue>(
            $"{Lcs}{first}{second}",
            flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly),
            cancellationToken: cancellationToken);

    /// <summary><c>LCS ... IDX [MINMATCHLEN n] WITHMATCHLEN</c>: where the common substrings sit.</summary>
    /// <param name="minMatchLength">Ignore matches shorter than this; 0 reports them all.</param>
    /// <param name="cancellationToken"><inheritdoc cref="SubstrAsync" path="/param[@name='cancellationToken']"/></param>
    /// <remarks>An optional modifier cannot be one interpolated string, so the command is started
    /// with <c>Compose</c> and appended to; each <c>AppendFormatted</c> adds one argument, typed
    /// exactly as a hole would be. The reply is a shape only this library knows, so a handler of
    /// its own reads it - see <see cref="MatchesHandler"/>.</remarks>
    public static ValueTask<SubstringMatchResult> MatchesAsync(
        this in StrMatchCommands strMatch,
        RedisKey first,
        RedisKey second,
        long minMatchLength = 0,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
    {
        var ctx = strMatch.Context;
        var request = ctx.Compose($"{Lcs}{first}{second}{Idx}");
        if (minMatchLength > 0)
        {
            request.AppendFormatted(Minmatchlen);
            request.AppendFormatted(minMatchLength);
        }
        request.AppendFormatted(Withmatchlen);
        return ctx.SendAsync(
            ref request,
            flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly),
            MatchesHandler.Instance,
            cancellationToken);
    }

    /// <summary><c>EXISTS key [key ...]</c>: how many of the named keys exist.</summary>
    /// <param name="cancellationToken"><inheritdoc cref="SubstrAsync" path="/param[@name='cancellationToken']"/></param>
    /// <remarks>A variable number of keys does not need <c>Compose</c>: a span is one hole, and
    /// every element is appended as a key - routed, prefixed and tracked like any other. A span
    /// parameter accepts an array, a slice, or a collection expression, and costs the caller
    /// nothing to build.</remarks>
    public static ValueTask<long> CountExistingAsync(
        this in StrMatchCommands strMatch,
        ReadOnlySpan<RedisKey> keys,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => keys.IsEmpty
            ? new(0L) // "none of nothing exists" needs no round trip
            : strMatch.Context.SendAsync<long>(
                $"{Exists}{keys}",
                flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly),
                cancellationToken: cancellationToken);

    // ---- the reply handler ----------------------------------------------------------------------

    /// <summary>Reads the <c>LCS ... IDX WITHMATCHLEN</c> reply where it landed, via <see cref="RespReader"/>:
    /// <c>{"matches": [[[a1,a2],[b1,b2],len], ...], "len": n}</c>.</summary>
    /// <remarks>
    /// <para>A singleton, not a lambda: the handler is stateless, so one instance serves every call
    /// and the send allocates nothing for it.</para>
    /// <para>Two habits worth copying. The shape is tested with <c>IsScalar</c>/<c>IsAggregate</c>,
    /// never by comparing <c>Prefix</c> to a literal: RESP2 spells this reply as a flat array and
    /// RESP3 as a map, and the category tests cover both. And the fields are read <b>nominally</b>
    /// ("matches", "len"), not positionally: the reply is documented as a map, so the server is
    /// free to order it - or extend it - as it likes; an unknown field is skipped, not an error.</para>
    /// </remarks>
    private sealed class MatchesHandler : IRespHandler<SubstringMatchResult>
    {
        public static readonly MatchesHandler Instance = new();

        public SubstringMatchResult Parse(ref RespReader reader)
        {
            var shape = reader.Prefix; // recorded for the demo's RESP2-vs-RESP3 act; never branched on

            SubstringMatch[]? matches = null;
            long totalLength = 0;

            var fields = reader.AggregateChildren();
            while (fields.MoveNext() && fields.Value.IsScalar)
            {
                bool isMatches = fields.Value.Is("matches"u8);
                bool isLen = fields.Value.Is("len"u8);
                if (!fields.MoveNext()) break; // field name with no value: out of data

                if (isMatches && fields.Value.IsAggregate)
                {
                    matches = fields.Value.ReadPastArray(ReadMatch);
                }
                else if (isLen && fields.Value.IsScalar)
                {
                    totalLength = fields.Value.ReadInt64();
                }
                // any other field: added by a future server; skipped harmlessly
            }

            return new SubstringMatchResult
            {
                Matches = matches ?? [],
                TotalLength = totalLength,
                WireShape = shape,
            };
        }

        /// <summary>One match: <c>[[firstStart, firstEnd], [secondStart, secondEnd], length]</c>
        /// (the trailing length is present because the library always sends <c>WITHMATCHLEN</c>).</summary>
        private static SubstringMatch ReadMatch(ref RespReader reader)
        {
            var parts = reader.AggregateChildren();
            parts.DemandNext();
            var (firstStart, firstEnd) = ReadPair(ref parts.Value);
            parts.DemandNext();
            var (secondStart, secondEnd) = ReadPair(ref parts.Value);
            parts.DemandNext();
            long length = parts.Value.ReadInt64();
            return new SubstringMatch(firstStart, firstEnd, secondStart, secondEnd, length);
        }

        /// <summary>A two-element array: <c>[start, end]</c>.</summary>
        private static (long Start, long End) ReadPair(ref RespReader reader)
        {
            var parts = reader.AggregateChildren();
            parts.DemandNext();
            long start = parts.Value.ReadInt64();
            parts.DemandNext();
            long end = parts.Value.ReadInt64();
            return (start, end);
        }
    }
}
