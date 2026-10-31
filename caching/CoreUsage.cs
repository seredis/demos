using System.Diagnostics;
using StackExchange.Redis;

namespace CachingDemo;

// CoreUsage.cs - how the app consumes the database: plain IDatabase code. The cache was attached
// once, at connect time (Setup.cs); no read here knows or cares whether it is answered locally.
// The one per-call flourish available is CommandFlags.CacheOptOut (the 'B' key), which forces a
// round trip for that read only.
sealed partial class CacheEngine
{
    /// <summary>One read, exactly as any application would issue it.</summary>
    private async Task ReadKeyAsync(int k, CancellationToken token)
    {
        var db = _db;
        if (db is null) return;
        var flags = Bypass ? CommandFlags.CacheOptOut : CommandFlags.None;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await db.StringGetAsync(_keys[k], flags);
            RecordRead(k, value, Stopwatch.GetElapsedTime(started)); // demo bookkeeping, nothing more
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"read failed: {ex.GetType().Name}: {ex.Message}");
            await Task.Delay(500, token);
        }
    }
}
