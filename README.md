# SE.Redis demos

Self-contained, runnable demos for [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis)
features. Each folder has its own README, its own `docker-compose.yml` for the Redis servers it
needs, and pins the library version it targets, so demos can sit on different releases.

| Demo | Shows | Library |
|----------|-------|---------|
| [`geofailover/`](geofailover/) | Connection groups: weighted members, health checks, circuit breaker, failover and failback, per-command retry categories. A scripted console run and an interactive TUI. | 3.4.x |
| [`caching/`](caching/) | Client-side caching in v4: the tracking modes (`CLIENT TRACKING` per-key and broadcast, keyspace notifications, none), admission on first or repeated miss, the policies (`TimeToLive`, `RefreshAfter`, grace), manual `Invalidate`, `GetClientCache()` counters, and the hit rate measured on the server. Interactive TUI. | 4.0 alpha |
| [`hitless/`](hitless/) | Smart client handoffs (hitless upgrades): maintenance notifications over RESP3 (`MOVING`, `MIGRATING`/`MIGRATED`, `FAILING_OVER`/`FAILED_OVER`, slot migration), the modes (`Auto`, `Enabled`, `Disabled`), asking `MOVING` for the successor, relaxed timeouts, and the traffic a disruption costs with and without them. Interactive TUI. **Bring your own Redis Enterprise cluster**: no Docker setup. | 3.4.x |
| [`roaring/`](roaring/) | Compressed (roaring) bitmaps in Redis 8.12, measured against classic string bitmaps on an 8.10 baseline: `MEMORY USAGE` across population shapes (~100× sparse, ~5,000× clustered, parity on noise), server-side µs/call for `BITCOUNT`/`BITOP`/`SETBIT` from `INFO commandstats`, the transparent encoding (same `IDatabase` calls throughout), config-driven live migration, and the one new user-facing command, `BITRANGEINTARRAY`. Scripted console run; the 8.12 RC builds from source. | 4.0 alpha |

All demos need the .NET 10 SDK. The caching, geo failover and roaring demos also need Docker with the compose plugin; the hitless demo needs a Redis Enterprise cluster instead (see its README).
