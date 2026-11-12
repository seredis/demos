using SetExpiry;
using StackExchange.Redis;

namespace SetExpiryDemo;

// CoreUsage.cs - how the app consumes the database: every set-expiration command the demo issues,
// in one place, one small method per command. The plumbing (connection, keyspace notifications,
// bot scheduling, polling cadence, UI) lives in Engine.cs; the Log(...) calls here are demo
// bookkeeping, nothing more. The SetExpiry() surface itself is SetExpiry.cs - the mini-library a
// client would ship for redis/redis#15938.
sealed partial class Engine
{
    /// <summary>"host" joins with plain SADD: no TTL, never expires - TTL-carrying and permanent
    /// members coexist in one set.</summary>
    private async Task AddHostAsync()
    {
        await _db.SetAddAsync(Key, "host");
        Log("host joined via plain SADD: no TTL, pinned until removed");
    }

    /// <summary>The background traffic: each bot touches itself with SADDEX EX 12 on its own
    /// period - under the TTL it stays resident, over it it lapses and rejoins.</summary>
    private async Task BotTouchAsync(string name)
        => await _db.SetExpiry().AddAsync(Key, [name], BotTtl);

    /// <summary>'A': a new guest joins with its own TTL - SADDEX is add-and-expire in one call.</summary>
    private async Task JoinAsync(string name)
    {
        bool changed = await _db.SetExpiry().AddAsync(Key, [name], GuestTtl);
        Log($"SADDEX EX {GuestTtl.TotalSeconds:0} MEMBERS 1 {name} -> {(changed ? 1 : 0)}: joined with a {GuestTtl.TotalSeconds:0}s TTL");
    }

    /// <summary>'T': activity - present with a fresh TTL whether or not the member was there.</summary>
    private async Task TouchAsync(string name)
    {
        await _db.SetExpiry().AddAsync(Key, [name], TouchTtl);
        Log($"SADDEX EX {TouchTtl.TotalSeconds:0} MEMBERS 1 {name}: activity - present with a fresh {TouchTtl.TotalSeconds:0}s TTL either way");
    }

    /// <summary>'G': SEXPIRE ... GT only lengthens - and loses to a pinned member, because no
    /// expiry counts as infinite.</summary>
    private async Task LengthenAsync(string name)
    {
        using var codes = await _db.SetExpiry().ExpireAsync(Key, LengthenTtl, [name], SetExpireWhen.GreaterThan);
        long code = codes.Length > 0 ? codes.Span[0] : SetExpireReply.NoSuchMember;
        Log($"SEXPIRE {LengthenTtl.TotalSeconds:0} GT MEMBERS 1 {name} -> [{code}]: {code switch
        {
            SetExpireReply.Applied => "lengthened",
            SetExpireReply.ConditionNotMet => "GT not met - the current expiry is already later (or none)",
            SetExpireReply.NoSuchMember => "no such member",
            _ => "deleted (expiry already past)",
        }}");
    }

    /// <summary>'P': SPERSIST pins the member (TTL ∞). Pinned is not permanent - SREM still works.</summary>
    private async Task PersistAsync(string name)
    {
        using var codes = await _db.SetExpiry().PersistAsync(Key, [name]);
        long code = codes.Length > 0 ? codes.Span[0] : -2;
        Log($"SPERSIST MEMBERS 1 {name} -> [{code}]: {code switch
        {
            1 => "pinned - the member no longer expires",
            -1 => "already had no expiry",
            _ => "no such member",
        }}");
    }

    /// <summary>'X': a 2s fuse - then __subkeyevent@0__:sexpired names the member that lapsed.</summary>
    private async Task ExpireSoonAsync(string name)
    {
        using var codes = await _db.SetExpiry().ExpireAsync(Key, TimeSpan.FromSeconds(2), [name]);
        Log($"SEXPIRE 2 MEMBERS 1 {name} -> [{(codes.Length > 0 ? codes.Span[0] : -2)}]: watch for sexpired...");
    }

    /// <summary>'K': the ordinary SREM; nothing about expiry changes it.</summary>
    private async Task KickAsync(string name)
    {
        bool removed = await _db.SetRemoveAsync(Key, name);
        Log($"SREM {name} -> {(removed ? 1 : 0)} (the ordinary command; nothing about expiry changes SREM)");
    }

    /// <summary>'N': SADDEX MNX is all-or-nothing across the batch - one existing member vetoes
    /// the whole call, and nothing is added at all.</summary>
    private async Task AllNewAsync(string existing, string newcomer)
    {
        bool changed = await _db.SetExpiry().AddAsync(Key, [existing, newcomer], GuestTtl, SetAddWhen.AllNew);
        Log($"SADDEX MNX EX 20 MEMBERS 2 {existing} {newcomer} -> {(changed ? 1 : 0)}: " +
            (changed ? "all were new; added" : $"MNX is all-or-nothing: {existing} already exists, so {newcomer} was NOT added either"));
    }

    /// <summary>The board itself: SMEMBERS for who is present, SPTTL for how long - polled every 250ms.</summary>
    private async Task<(string Name, long TtlMs)[]> ReadBoardAsync()
    {
        RedisValue[] members = await _db.SetMembersAsync(Key);
        Array.Sort(members, static (x, y) => string.CompareOrdinal(x, y));
        if (members.Length == 0) return [];
        using var ttls = await _db.SetExpiry().TimeToLiveAsync(Key, members);
        var rows = new (string, long)[members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            rows[i] = (members[i].ToString(), i < ttls.Length ? ttls.Span[i] : -2);
        }
        return rows;
    }
}
