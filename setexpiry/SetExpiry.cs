// Everything in this file is what a client library for Redis 8.12's set-member expiration would
// ship (SADDEX / SEXPIRE / SPTTL / SPERSIST and friends, redis/redis#15938). StackExchange.Redis
// has no API for these yet, so this is the extending/ demo's machinery earning its keep on a
// genuinely new server feature: [Resp]-declared commands, Compose for the grammar's optional
// tokens, a span of members as one hole, and ReadOnlyLease<long> replies - no boxing, nothing
// allocated per call beyond the lease.

using StackExchange.Redis;
using StackExchange.Redis.Protocol;

namespace SetExpiry;

/// <summary>The command group: a context plus a name.</summary>
public readonly struct SetExpiryCommands(RespContext context)
{
    /// <summary>The context these commands are sent through.</summary>
    public RespContext Context { get; } = context;
}

/// <summary>Conditions for <c>SADDEX</c>. These are <b>all-or-nothing across the batch</b>: one
/// member that breaks the condition vetoes the whole call (reply 0, nothing changed).</summary>
public enum SetAddWhen
{
    /// <summary>No condition: add the missing, re-expire the present.</summary>
    Always,

    /// <summary><c>MNX</c>: only if none of the members exist yet.</summary>
    AllNew,

    /// <summary><c>MXX</c>: only if all of the members already exist.</summary>
    AllExisting,
}

/// <summary>Conditions for <c>SEXPIRE</c>, per member (unlike <see cref="SetAddWhen"/>).</summary>
public enum SetExpireWhen
{
    /// <summary>No condition.</summary>
    Always,

    /// <summary><c>NX</c>: only when the member has no expiry.</summary>
    HasNoExpiry,

    /// <summary><c>XX</c>: only when the member has an expiry.</summary>
    HasExpiry,

    /// <summary><c>GT</c>: only when the new expiry is later than the current one.</summary>
    GreaterThan,

    /// <summary><c>LT</c>: only when the new expiry is earlier than the current one.</summary>
    LessThan,
}

/// <summary>The per-member reply codes of <c>SEXPIRE</c>.</summary>
public static class SetExpireReply
{
    /// <summary>-2: the member does not exist.</summary>
    public const long NoSuchMember = -2;

    /// <summary>0: the NX/XX/GT/LT condition was not met; nothing changed.</summary>
    public const long ConditionNotMet = 0;

    /// <summary>1: the expiration was set or updated.</summary>
    public const long Applied = 1;

    /// <summary>2: the member was deleted, because the expiration was already in the past.</summary>
    public const long DeletedAlreadyPast = 2;
}

/// <summary>The accessor and the commands.</summary>
public static partial class SetExpiryExtensions
{
    [Resp("SADDEX")]
    private static partial RespCommand SaddEx { get; }

    [Resp("SEXPIRE")]
    private static partial RespCommand SExpire { get; }

    [Resp("SPTTL")]
    private static partial RespCommand SPttl { get; }

    [Resp("SPERSIST")]
    private static partial RespCommand SPersist { get; }

    [Resp] // tokens inferred from the member names, upper-cased
    private static partial RespFragment Members { get; }

    [Resp]
    private static partial RespFragment Ex { get; }

    [Resp]
    private static partial RespFragment Keepttl { get; }

    [Resp]
    private static partial RespFragment Mnx { get; }

    [Resp]
    private static partial RespFragment Mxx { get; }

    [Resp]
    private static partial RespFragment Nx { get; }

    [Resp]
    private static partial RespFragment Xx { get; }

    [Resp]
    private static partial RespFragment Gt { get; }

    [Resp]
    private static partial RespFragment Lt { get; }

    /// <summary>The set-member-expiration commands, on any keyspace target.</summary>
    public static SetExpiryCommands SetExpiry<TTarget>(this TTarget target) where TTarget : IRespKeyspaceTarget
        => new((RespContext)target.Context);

    /// <summary><c>SADDEX key [MNX|MXX] [EX seconds|KEEPTTL] MEMBERS n member...</c>: add members
    /// and/or (re)set their expiration in one call.</summary>
    /// <param name="ttl">The time to live to give every named member; <see langword="null"/> sends
    /// <c>KEEPTTL</c> (existing expirations are kept; new members get none).</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one
    /// already written still runs on the server, and its reply is discarded.</param>
    /// <returns><see langword="true"/> when at least one member was added, updated or deleted.</returns>
    /// <remarks>A write: replaying it after a reconnect converges (the same members end up present
    /// with the same relative expiry), so it is declared <c>CommandRetryWriteChecked</c> -
    /// caller-wins, so this sets a default, not an override.</remarks>
    public static ValueTask<bool> AddAsync(
        this in SetExpiryCommands setExpiry,
        RedisKey key,
        ReadOnlySpan<RedisValue> members,
        TimeSpan? ttl,
        SetAddWhen when = SetAddWhen.Always,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
    {
        if (members.IsEmpty) return new(false);
        var ctx = setExpiry.Context;
        var request = ctx.Compose($"{SaddEx}{key}");
        switch (when)
        {
            case SetAddWhen.AllNew: request.AppendFormatted(Mnx); break;
            case SetAddWhen.AllExisting: request.AppendFormatted(Mxx); break;
        }
        if (ttl is { } t)
        {
            request.AppendFormatted(Ex);
            request.AppendFormatted((long)t.TotalSeconds);
        }
        else
        {
            request.AppendFormatted(Keepttl);
        }
        request.AppendFormatted(Members);
        request.AppendFormatted((long)members.Length);
        request.AppendFormatted(members);
        return ctx.SendAsync<bool>(ref request, flags.WithRetryCategory(CommandFlags.CommandRetryWriteChecked), cancellationToken: cancellationToken);
    }

    /// <summary><c>SEXPIRE key seconds [NX|XX|GT|LT] MEMBERS n member...</c>: set per-member
    /// expirations. One reply code per member, in order - see <see cref="SetExpireReply"/>.</summary>
    /// <param name="cancellationToken"><inheritdoc cref="AddAsync" path="/param[@name='cancellationToken']"/></param>
    public static ValueTask<ReadOnlyLease<long>> ExpireAsync(
        this in SetExpiryCommands setExpiry,
        RedisKey key,
        TimeSpan ttl,
        ReadOnlySpan<RedisValue> members,
        SetExpireWhen when = SetExpireWhen.Always,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
    {
        if (members.IsEmpty) return new(ReadOnlyLease<long>.Empty);
        var ctx = setExpiry.Context;
        var request = ctx.Compose($"{SExpire}{key}{(long)ttl.TotalSeconds}");
        switch (when)
        {
            case SetExpireWhen.HasNoExpiry: request.AppendFormatted(Nx); break;
            case SetExpireWhen.HasExpiry: request.AppendFormatted(Xx); break;
            case SetExpireWhen.GreaterThan: request.AppendFormatted(Gt); break;
            case SetExpireWhen.LessThan: request.AppendFormatted(Lt); break;
        }
        request.AppendFormatted(Members);
        request.AppendFormatted((long)members.Length);
        request.AppendFormatted(members);
        return ctx.SendAsync<ReadOnlyLease<long>>(ref request, flags.WithRetryCategory(CommandFlags.CommandRetryWriteChecked), cancellationToken: cancellationToken);
    }

    /// <summary><c>SPTTL key MEMBERS n member...</c>: remaining time to live per member, in
    /// milliseconds; -1 for a member with no expiry, -2 for one that does not exist.</summary>
    /// <param name="cancellationToken"><inheritdoc cref="AddAsync" path="/param[@name='cancellationToken']"/></param>
    /// <remarks>Read-only, so it is safe to replay - but its answer changes <b>without a write</b>
    /// (time passes), which is exactly the read that must opt out of the client-side cache
    /// (<see cref="CommandFlags.CacheOptOut"/>): the category alone would freeze a cached TTL on screen.</remarks>
    public static ValueTask<ReadOnlyLease<long>> TimeToLiveAsync(
        this in SetExpiryCommands setExpiry,
        RedisKey key,
        ReadOnlySpan<RedisValue> members,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => members.IsEmpty
            ? new(ReadOnlyLease<long>.Empty)
            : setExpiry.Context.SendAsync<ReadOnlyLease<long>>(
                $"{SPttl}{key}{Members}{(long)members.Length}{members}",
                flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly) | CommandFlags.CacheOptOut,
                cancellationToken: cancellationToken);

    /// <summary><c>SPERSIST key MEMBERS n member...</c>: remove per-member expirations. Per member:
    /// 1 cleared, -1 had none, -2 does not exist.</summary>
    /// <param name="cancellationToken"><inheritdoc cref="AddAsync" path="/param[@name='cancellationToken']"/></param>
    public static ValueTask<ReadOnlyLease<long>> PersistAsync(
        this in SetExpiryCommands setExpiry,
        RedisKey key,
        ReadOnlySpan<RedisValue> members,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => members.IsEmpty
            ? new(ReadOnlyLease<long>.Empty)
            : setExpiry.Context.SendAsync<ReadOnlyLease<long>>(
                $"{SPersist}{key}{Members}{(long)members.Length}{members}",
                flags.WithRetryCategory(CommandFlags.CommandRetryWriteChecked),
                cancellationToken: cancellationToken);
}
