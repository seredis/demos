# SE.Redis demos

Self-contained, runnable demos for [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis)
features. Each folder has its own README, its own `docker-compose.yml` for the Redis servers it
needs, and pins the library version it targets, so demos can sit on different releases.

| Demo | Shows | Library |
|----------|-------|---------|
| [`geofailover/`](geofailover/) | Connection groups: weighted members, health checks, circuit breaker, failover and failback, per-command retry categories. A scripted console run and an interactive TUI. | 3.4.x |
| `caching/` | Client-side caching in v4. Coming. | 4.x |

All demos need Docker with the compose plugin and the .NET 10 SDK.
