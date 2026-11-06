# SE.Redis demos

Self-contained, runnable demos for [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis)
features. Each folder has its own README, its own `docker-compose.yml` for the Redis servers it
needs, and pins the library version it targets, so demos can sit on different releases.

| Demo | Shows | Library |
|----------|-------|---------|
| [`geofailover/`](geofailover/) | Connection groups: weighted members, health checks, circuit breaker, failover and failback, per-command retry categories. A scripted console run and an interactive TUI. | 3.4.x |
| [`caching/`](caching/) | Client-side caching in v4: the tracking modes (`CLIENT TRACKING` per-key and broadcast, keyspace notifications, none), admission on first or repeated miss, the policies (`TimeToLive`, `RefreshAfter`, grace), manual `Invalidate`, `GetClientCache()` counters, and the hit rate measured on the server. Interactive TUI. | 4.0 alpha |
| [`hitless/`](hitless/) | Smart client handoffs (hitless upgrades): maintenance notifications over RESP3 (`MOVING`, `MIGRATING`/`MIGRATED`, `FAILING_OVER`/`FAILED_OVER`, slot migration), the modes (`Auto`, `Enabled`, `Disabled`), asking `MOVING` for the successor, relaxed timeouts, and the traffic a disruption costs with and without them. Interactive TUI. **Bring your own Redis Enterprise cluster**: no Docker setup. | 3.4.x |
| [`extending/`](extending/) | Extending the client in v4 as an external module author (think `JSON.*`/`FT.*`), played out on real commands: the ad-hoc `ExecuteResp`, the interpolated `db.Context.SendAsync<T>($"…")` form, a command surface of your own (`[Resp]`, command groups), `WithKeyPrefix` and the key-vs-value trap, `Compose` for run-time arguments, a custom reply handler over `RespReader` under both RESP2 and RESP3, and retry categories (and the client-side caching they unlock). Scripted console run. | 4.0 alpha |

All demos need the .NET 10 SDK. The caching, geo failover and extending demos also need Docker with the compose plugin; the hitless demo needs a Redis Enterprise cluster instead (see its README).
