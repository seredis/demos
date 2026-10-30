# Geo-Failover Demo

This repo provides docker-based infrastructure and two .NET demos showcasing geo failover/failback
with the [SE.Redis](https://github.com/StackExchange/StackExchange.Redis) connection group API
(`ConnectionMultiplexer.ConnectGroupAsync`):

- [`console/`](console/): a scripted console run, a .NET port of the
  [redis-py example](https://github.com/redis-developer/redis-py-geo-failover-example). Prints what
  happens, exits with a summary.
- [`tui/`](tui/): an interactive terminal UI for presenting. Stop and start the regions from the
  keyboard, pin a manual failover, switch the traffic between a retried and a non-retried command,
  and watch which server every operation actually hit.

Both share the `docker-compose.yml` in the repo root.

Locally, the "geo distributed" setup is simulated by two independent standalone Redis containers.
They do not replicate to each other, so this is two separate datasets behind one client, not
Active-Active: after a failover the new member starts cold, and anything written to it during the
outage is never seen by the other member. That is fine for a cache, where a miss is recoverable and a
lost write only costs a recompute. The console demo's traffic masks it by writing each key immediately
before reading it; the TUI shows it directly, by reading each server's keys outside the group. In
production the two endpoints would be a Redis Active-Active deployment, where the datasets converge
and the same client code gives a consistent view. The goal here is purely to show how the client
fails over from one endpoint to the other and falls back once the preferred endpoint recovers.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

This starts two standalone Redis 8.2 containers:

| Service      | Container    | Host port | Weight in the demos |
|--------------|--------------|-----------|---------------------|
| `redis-east` | `redis-east` | `16401`   | `1.0` (active)      |
| `redis-west` | `redis-west` | `16402`   | `0.5` (standby)     |

Verify both are healthy:

```bash
docker compose ps
```

Both demos need the .NET 10 SDK. Connection groups ship in StackExchange.Redis 3.1.0 and later;
the projects reference the current release.

## 2a. Console demo

```bash
cd console
dotnet run
```

1. Connects a connection group to the two endpoints.
2. Generates continuous GET/SET traffic in the background, through a retrying database
   (`GetDatabase().WithRetry()`), so operations in flight during the failover are replayed on
   the new member rather than failing.
3. Automatically stops the `redis-east` container - the circuit breaker trips, the group moves
   traffic to `redis-west`, and the `ConnectionChanged` event reports the switch. No exceptions
   surface.
4. Automatically restarts `redis-east` - health checks detect the recovery, and once it has been
   stable for `FailbackDelay` (5 seconds here) traffic falls back to the highest-weight member.
5. Prints a summary and exits (exit code 1 if any traffic error surfaced).

Typical output is shown in [console/output.md](console/output.md).

By default the retry policy allows 5 attempts per operation, which is enough to bridge the failover
window, so no error reaches the traffic loop. To see what a surfaced fault looks like, lower it:

```bash
dotnet run -- --max-attempts 1
```

With a single attempt the operations in flight when `redis-east` stops fail outright, and the app
prints the fault classification SE.Redis attaches to them (`kind`, `connection`, `notApplied`, and the
command's retry `category`). `notApplied=True` means the client knows the server never ran the command.

The container stop/start is fully automated - the app shells out to `docker compose` and prints
a banner before doing so. Watch the output for the `>>> ACTIVE MEMBER SWITCHED` lines and the
per-member `[Disconnected]` / `[Reconnected]` events.

If you prefer to trigger the outage manually instead, comment out the `DockerCompose("stop", ...)`
/ `DockerCompose("start", ...)` lines in `console/Program.cs` and run these from a second terminal
while the app is running:

```bash
docker compose stop redis-east    # trigger failover
docker compose start redis-east   # trigger failback
```

## 2b. Interactive TUI

```bash
cd tui
dotnet run                  # interactive
dotnet run -- --scripted    # hands-free: the console demo's timeline, with the live display
```

Use a wide terminal with a large font; the layout adapts to the height, and the banner collapses to a
single line when there is no room for it.

At startup each server is stamped with a `region` key (`EAST` or `WEST`) over a direct connection.
Every operation then does two things through the group: the payload write, and a `GET region`. The
answer to that read is ground truth for which server served the operation, independent of what the
group reports as active.

What is on screen:

- **Banner**: the active member's name, in its colour.
- **Heartbeat strip**: one cell per operation, newest on the right. The glyph and colour give the
  region that answered (`█` blue east, `░` orange west). A second row marks retried operations
  (`r`, inferred from latency: there is no per-attempt callback) and errors (`!`).
- **Member cards**: endpoint, weight, active/standby, connected, healthy, and whether the demo has
  stopped its container.
- **What each server holds**: `tick` and `hits` read directly from each server. The standby's values
  freeze, and writes made during an outage stay on the server that took them.
- **Events**: `ConnectionChanged` events and the demo's own actions, newest at the bottom.

Keys:

| Key | Action |
|-----|--------|
| `E` / `W` | stop or start that region's container (`docker compose stop`/`start`) |
| `F` | pin a manual failover to the other member (`TryFailoverTo`) |
| `R` | clear the manual failover; weight-based selection resumes |
| `M` | switch the payload between `SET tick` (last-wins, retried by default) and `INCR hits` (accumulating, not retried by default) |
| `A` | switch the retry budget between 5 attempts and 1 (no retries) |
| `P` | pause/resume traffic |
| `Q` | quit |

A suggested walk-through: let it run on east, press `E`, watch the strip turn orange with a brief
`r` where the in-flight operations were replayed, press `E` again and watch it turn blue after the
5s failback delay. Then press `M` and repeat: the same outage now puts `!` cells on the strip,
because an `INCR` cannot safely be replayed. `A` shows the same thing from the other direction.

Colour is used throughout but never alone: region is also carried by glyph and by name, state is
written out in words, and errors have their own glyph. Blue and orange stay distinct under the common
red/green deficiencies. `NO_COLOR=1` turns colour off entirely.

## 3. Cleanup

```bash
docker compose down
```

## How the configuration maps from redis-py

| redis-py (`MultiDbConfig`)          | SE.Redis (`MultiGroupOptions`)                              |
|-------------------------------------|-------------------------------------------------------------|
| `DatabaseConfig(weight=...)`        | `ConnectionGroupMember { Weight = ... }`                    |
| `command_retry=Retry(...)`          | `RetryPolicy`, applied via `GetDatabase().WithRetry()`      |
| `min_num_failures`                  | `CircuitBreaker.MinimumNumberOfFailures`                    |
| `failures_detection_window`         | `CircuitBreaker.MetricsWindowSize`                          |
| `health_check_interval`             | `HealthCheckInterval`                                       |
| `auto_fallback_interval`            | `FailbackDelay` (measured from the member's last failure)   |
| `ActiveDatabaseChanged` listener    | `ConnectionChanged` event, `ChangeType.ActiveChanged`       |

One difference worth knowing: SE.Redis decides what to retry per command. `SET` and `GET` are
replayed after an ambiguous fault; an `INCR` would not be by default, because a replay could
count twice. See the [failover documentation](https://seredis.dev/Failover)
for the retry categories.
