# SE.Redis demos

Self-contained, runnable demos for [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis)
features. Each folder has its own README, its own `docker-compose.yml` for the Redis servers it
needs, and pins the library version it targets, so demos can sit on different releases.

| Demo | Shows | Library |
|----------|-------|---------|
| [`arrays/`](arrays/) | Native arrays in Redis 8.8 via `IDatabase.Array*`: a 48-slot telemetry ring where the server owns the cursor and the wrap-around (`ARRING`/`ARSEEK`/`ARNEXT`), `AROP` aggregates and `ARGREP` regex search run where the data lives, plus `ARSET` retcons, `ARLASTITEMS` recency and `ARINFO` slice internals. Interactive TUI. | 3.4.x |
| [`caching/`](caching/) | Client-side caching in v4: the tracking modes (`CLIENT TRACKING` per-key and broadcast, keyspace notifications, none), admission on first or repeated miss, the policies (`TimeToLive`, `RefreshAfter`, grace), manual `Invalidate`, `GetClientCache()` counters, and the hit rate measured on the server. Interactive TUI. | 4.0 alpha |
| [`compacthash/`](compacthash/) | Compact hashes + `HIMPORT` in Redis 8.10 via `HashImport`: the same 14-field orders dataset loaded plain vs through a shared-schema template at 100/5k/50k keys — 43% less hash memory and ~2.5× the load throughput at scale, with the template counters, encodings and `MEMORY USAGE` fair-share all measured on the server. Scripted console run. | 3.4.x |
| [`geofailover/`](geofailover/) | Connection groups: weighted members, health checks, circuit breaker, failover and failback, per-command retry categories. A scripted console run and an interactive TUI. | 3.4.x |
| [`hitless/`](hitless/) | Smart client handoffs (hitless upgrades): maintenance notifications over RESP3 (`MOVING`, `MIGRATING`/`MIGRATED`, `FAILING_OVER`/`FAILED_OVER`, slot migration), the modes (`Auto`, `Enabled`, `Disabled`), asking `MOVING` for the successor, relaxed timeouts, and the traffic a disruption costs with and without them. Interactive TUI. **Bring your own Redis Enterprise cluster**: no Docker setup. | 3.4.x |
| [`hotkeys/`](hotkeys/) | `HOTKEYS` in Redis 8.6: server-side per-key CPU and network profiling via `IServer.HotKeysStart/Get/Stop/Reset` — a live "`top` for keys" over a Zipf workload where hot-by-volume, hot-by-CPU (`SINTERCARD`) and hot-by-bytes (`LRANGE`) are different keys; spikes, sampling ratios, capture sessions. Interactive TUI. | 3.4.x |
| [`setexpiry/`](setexpiry/) | Set-member expiration in Redis 8.12 (`SADDEX`, `SEXPIRE`, `SPTTL`, `SPERSIST`) as a live presence board: per-member TTL countdowns, the reply codes, the `GT`/`MNX` conditions (including `MNX`'s all-or-nothing batch veto), and 8.12's new subkey notification channels, where `__subkeyevent@0__:sexpired` names the member that lapsed. No client API exists yet, so the whole surface runs through the v4 context machinery. Interactive TUI; the 8.12 RC builds from source. | 4.0 alpha |
| [`vectorsets/`](vectorsets/) | Vector sets in Redis 8.0 via `IDatabase.VectorSet*`: nearest-neighbour image matching over MNIST digits rendered in the terminal — the query and its matches as images, recall measured against exact-f32 ground truth, `NOQUANT`/`Q8`/`BIN` as three copies of one corpus (18.4/7.2/3.9MB), `FILTER` expressions, exact vs HNSW. Interactive TUI. | 3.4.x |

All demos need the .NET 10 SDK. The caching, geo failover and set-expiry demos also need Docker with the compose plugin; the hitless demo needs a Redis Enterprise cluster instead (see its README).
