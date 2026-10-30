# Geo-Failover Demo

This repo provides docker-based infrastructure and an E2E console app showcasing geo
failover/failback with the [SE.Redis](https://github.com/StackExchange/StackExchange.Redis)
connection group API (`ConnectionMultiplexer.ConnectGroupAsync`). It is a .NET port of the
[redis-py example](https://github.com/redis-developer/redis-py-geo-failover-example).

Locally, the "geo distributed" setup is simulated by two independent standalone Redis containers.
There is no replication between them - in production this would be a Redis Active-Active
deployment where data is synchronized. The goal here is purely to demonstrate how the client
fails over from one endpoint to another and falls back once the preferred endpoint recovers.

## What the demo does

1. Connects a connection group to two Redis endpoints: `redis-east` (weight `1.0`, the active
   member) and `redis-west` (weight `0.5`).
2. Generates continuous GET/SET traffic in the background, through a retrying database
   (`GetDatabase().WithRetry()`), so operations in flight during the failover are replayed on
   the new member rather than failing.
3. Automatically stops the `redis-east` container - the circuit breaker trips, the group moves
   traffic to `redis-west`, and the `ConnectionChanged` event reports the switch. No exceptions
   surface.
4. Automatically restarts `redis-east` - health checks detect the recovery, and once it has been
   stable for `FailbackDelay` (5 seconds here) traffic falls back to the highest-weight member.
5. Prints a summary and exits (exit code 1 if any traffic error surfaced).

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

This starts two standalone Redis 8.2 containers:

| Service      | Container    | Host port | Weight in the demo |
|--------------|--------------|-----------|--------------------|
| `redis-east` | `redis-east` | `16401`   | `1.0` (active)     |
| `redis-west` | `redis-west` | `16402`   | `0.5` (standby)    |

Verify both are healthy:

```bash
docker compose ps
```

## 2. Run the scenario

Requires the .NET 10 SDK. Connection groups ship in StackExchange.Redis 3.1.0 and later; the
project references the current release.

```bash
dotnet run
```

The container stop/start is fully automated - the app shells out to `docker compose` and prints
a banner before doing so. Watch the output for the `>>> ACTIVE MEMBER SWITCHED` lines and the
per-member `[Disconnected]` / `[Reconnected]` events.

If you prefer to trigger the outage manually instead, comment out the `DockerCompose("stop", ...)`
/ `DockerCompose("start", ...)` lines in `Program.cs` and run these from a second terminal while
the app is running:

```bash
docker compose stop redis-east    # trigger failover
docker compose start redis-east   # trigger failback
```

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
count twice. See the [failover documentation](https://stackexchange.github.io/StackExchange.Redis/Failover)
for the retry categories.
