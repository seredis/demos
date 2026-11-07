# SE.Redis demos

Self-contained, runnable demos for [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis)
features. Each folder has its own README, its own `docker-compose.yml` for the Redis servers it
needs, and pins the library version it targets, so demos can sit on different releases.

| Demo | Shows | Library |
|----------|-------|---------|
| [`geofailover/`](geofailover/) | Connection groups: weighted members, health checks, circuit breaker, failover and failback, per-command retry categories. A scripted console run and an interactive TUI. | 3.4.x |
| [`caching/`](caching/) | Client-side caching in v4: the tracking modes (`CLIENT TRACKING` per-key and broadcast, keyspace notifications, none), admission on first or repeated miss, the policies (`TimeToLive`, `RefreshAfter`, grace), manual `Invalidate`, `GetClientCache()` counters, and the hit rate measured on the server. Interactive TUI. | 4.0 alpha |
| [`hitless/`](hitless/) | Smart client handoffs (hitless upgrades): maintenance notifications over RESP3 (`MOVING`, `MIGRATING`/`MIGRATED`, `FAILING_OVER`/`FAILED_OVER`, slot migration), the modes (`Auto`, `Enabled`, `Disabled`), asking `MOVING` for the successor, relaxed timeouts, and the traffic a disruption costs with and without them. Interactive TUI. **Bring your own Redis Enterprise cluster**: no Docker setup. | 3.4.x |
| [`compacthash/`](compacthash/) | Compact hashes + `HIMPORT` in Redis 8.10 via `HashImport`: the same 14-field orders dataset loaded plain vs through a shared-schema template at 100/5k/50k keys — 43% less hash memory and ~2.5× the load throughput at scale, with the template counters, encodings and `MEMORY USAGE` fair-share all measured on the server. Scripted console run. | 3.4.x |

All demos need the .NET 10 SDK. The caching and geo failover demos also need Docker with the compose plugin; the hitless demo needs a Redis Enterprise cluster instead (see its README).
