using System.Diagnostics;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;

namespace HitlessDemo;

// CoreUsage.cs - how the app consumes the database: plain IDatabase code, alternating SET and GET.
// Nothing here knows that a handoff is happening; the only trace a disruption leaves on this path is
// that a timeout or connection exception, if one does occur, says which maintenance notification was
// in effect (MaintenanceType), which separates "the deployment was moving" from "this query is slow".
sealed partial class HandoffEngine
{
    /// <summary>One operation, exactly as any application would issue it.</summary>
    private async Task OneOperationAsync(int worker, long sequence, CancellationToken token)
    {
        var db = _db;
        if (db is null) { await Task.Delay(100, token); return; }

        var key = _keys[(worker * 7 + (int)(sequence % _keys.Length)) % _keys.Length];
        var started = Stopwatch.GetTimestamp();
        try
        {
            if ((sequence & 1) == 0) await db.StringSetAsync(key, sequence);
            else await db.StringGetAsync(key);
            RecordOk(Stopwatch.GetElapsedTime(started)); // demo bookkeeping, nothing more
        }
        catch (RedisTimeoutException ex)
        {
            RecordError("timeout", ex.MaintenanceType, ex.Message, Stopwatch.GetElapsedTime(started));
        }
        catch (RedisConnectionException ex)
        {
            RecordError("connection", ex.MaintenanceType, ex.Message, Stopwatch.GetElapsedTime(started));
            await Task.Delay(50, token); // a dead connection fails fast; do not spin
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordError(ex.GetType().Name, MaintenanceNotificationType.None, ex.Message, Stopwatch.GetElapsedTime(started));
            await Task.Delay(50, token);
        }
    }
}
