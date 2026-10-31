using StackExchange.Redis;

namespace GeoFailoverTui;

// CoreUsage.cs - the traffic, exactly as an application would write it. The group (Setup.cs) hands
// back an ordinary IDatabaseAsync; nothing below knows about members, health checks or failover.
sealed partial class DemoEngine
{
    /// <summary>One operation: the payload write, then a read that proves which region answered.</summary>
    private async Task<Region> WriteAndProveAsync(IDatabaseAsync db)
    {
        // the payload write: what the 'M' key toggles. SET is a last-wins write, safely replayed
        // after an ambiguous fault; INCR accumulates, so the library will NOT retry it by default
        // (a replay could count twice).
        if (Mode == TrafficMode.Set) await db.StringSetAsync("tick", Interlocked.Increment(ref _tick));
        else await db.StringIncrementAsync("hits");

        // the proof of origin: a read-only command, always within the default retry category
        return ParseRegion(await db.StringGetAsync("region"));
    }
}
