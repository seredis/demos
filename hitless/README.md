# Smart client handoffs (hitless upgrades)

An interactive terminal demo of SE.Redis's
[maintenance notifications](https://seredis.dev/ServerMaintenanceEvent): Redis Enterprise and Redis Cloud
tell a connected client, as RESP3 push frames on the connection itself, that a disruption is coming (a
shard is migrating, a node is failing over, the proxy you are on is being replaced), and the client acts on
it: timeouts are relaxed while the operation runs, the cluster topology is re-read when slots move, and a
connection is moved to the replacement proxy *before* the server closes the old one. One feature, three
names: Redis's client libraries call the cross-client contract **smart client handoffs**, its purpose is a
**hitless upgrade**, and the mechanism is **maintenance notifications**, which is what this library calls it.

**This demo brings no server.** The other demos in this repo spin up Redis in Docker; this one cannot,
because the notifications are a Redis Enterprise proxy feature. You need a Redis Enterprise cluster (or
Redis Cloud database) you are allowed to disrupt, and a way to trigger the disruptions: `rladmin` on the
cluster, or Redis's fault-injector tooling if you have it. Section 1 says exactly what the cluster needs.

One connection carries continuous `SET`/`GET` traffic from eight workers. The screen shows every
notification the client receives, every handoff it performs, what the library logged about it, and what
the traffic felt: completed and failed operations per 250ms, worst latency, and a one-line summary of
each disruption from the first notification to the server closing the old socket.

Four client settings can be changed while it runs, each a reconnect: **`N`** `maintNotifications`
(`Auto`, `Enabled`, `Disabled`), **`E`** `maintMovingEndpointType` (whether `MOVING` should name the
replacement proxy), **`P`** the protocol (RESP3, or RESP2, which silently disables the feature) and
**`D`** the database. **`R`** toggles `WithRetry()` without reconnecting. The comparison that carries the
demo is the same fault run under `Disabled` and then under `Auto`.

## 1. Infrastructure (bring your own)

What the cluster needs:

| Requirement | Why |
|---|---|
| Redis Enterprise 7.4+ (measured here on 8.0.22 and 8.6.2) or Redis Cloud | the notifications are emitted by the DMC proxy |
| cluster flag `client_maint_notifications` on, for proxy-routed databases | with it off, `CLIENT MAINT_NOTIFICATIONS` does not exist and the opt-in is refused |
| cluster flag `oss_cluster_client_maint_notifications` on, for `oss_cluster` databases | same, for the cluster-API routing path |
| RESP3 | the frames are RESP3 pushes. The client negotiates it by default; `P` shows what happens without it |
| a database with `replication: true` | failover needs a replica to promote |
| for `MOVING` with a named successor: `proxy_policy: single` | `MOVING` fires when a proxy is *replaced*. With one serving proxy, excluding its node relocates the endpoint; shrinking a multi-proxy set is silent (see below) |

Check the flags on the cluster rather than inferring them from client errors:

```bash
curl -sk -u "$USER:$PASSWORD" "https://$CLUSTER:9443/v1/cluster" \
  | jq '{client_maint_notifications, oss_cluster_client_maint_notifications}'
```

The databases this demo was built against, on a 3-node cluster:

| Name | Port | Routing | Shape |
|---|---|---|---|
| `hitless-standalone` | 12000 | proxy-routed (`oss_cluster: false`), `proxy_policy: single` | 2 shards, dense placement, replication on. The one serving proxy can be relocated (`MOVING`), the shards migrated or failed over, a node drained |
| `hitless-cluster` | 12001 | OSS cluster API (`oss_cluster: true`), `all-master-shards`, external IPs | 3 shards, sparse, replication on. The client holds a connection per primary and reconciles topology; shard movement reports as `SMIGRATING`/`SMIGRATED` |

**If you have Redis's fault injector** (an internal tool that provisions a cluster through env0 and exposes
the disruptive operations over HTTP on port 20324), the demo drives it: point it at the `endpoints.json`
the injector writes and the fault keys work. The cluster settings it needs are
`ENABLE_MAINTENANCE_NOTIFICATIONS=true` and `ENABLE_OSS_CLUSTER_MAINTENANCE_NOTIFICATIONS=true`, and a
`bdb_config.json` describing the two databases above.

**If you do not**, create a database of the shape above on any Redis Enterprise cluster, and when you press
a fault key the demo tells you the `rladmin` command to run; everything else on screen works the same,
because the client does the handoff on its own.

## 2. Run

Requires the .NET 10 SDK. Tell it where to connect, one of:

```bash
dotnet run -- --endpoints ~/aws/endpoints.json          # the fault injector's file; the injector is assumed at http://127.0.0.1:20324
dotnet run -- --endpoint host:port --password '...'     # one database, by hand; add --tls, --bdb <id>, --fault-injector <url>
dotnet run                                              # hitless.local.json in this folder (gitignored; see hitless.local.example.json)
```

`--mode Disabled` starts with notifications off, for a before-and-after. `--no-fault-injector` keeps the
fault keys to their by-hand text. `--transcript` prints every event, including the library's full connection
log, on exit. Nothing with a password in it is under version control.

The same settings can come from the environment, so a shell profile can hold them: `HITLESS_ENDPOINTS`
(a leading `~/` is expanded), `HITLESS_FAULT_INJECTOR`, `HITLESS_MODE`, or for a single database
`HITLESS_ENDPOINT`, `HITLESS_PASSWORD`, `HITLESS_USER`, `HITLESS_TLS`, `HITLESS_BDB`.

```bash
export HITLESS_ENDPOINTS=~/aws/endpoints.json   # then plain `dotnet run` from this folder
```

Hands-free, for a scripted run or a smoke test: `--fire M --fire-after 8 --quit-after 80` presses one fault
key after a delay and exits later, so with `--transcript` the whole episode lands in a log. To run against a
local build of the library rather than the NuGet package:
`dotnet run -p:SERProject=/path/to/StackExchange.Redis.csproj`.

Use a wide terminal with a large font; the layout adapts to the height.

What is on screen:

- **Banner**: operations per second over the last two seconds, or in red the number that failed, or
  "handing off" while a connection is being replaced. Under it, the current settings and whether the
  client's timeouts are currently relaxed.
- **Traffic strip**: one column per 250ms. Completed operations (blue), the share that failed (red), the
  worst latency in the window on a log scale from 1ms to 10s (orange), and a letter for each key you
  pressed, so cause and effect line up.
- **Connection**: the endpoint, its DNS A records, each server the client holds with the proxy node it is
  actually connected to (`CLIENT INFO`, so a `MOVING` visibly changes it), whether the opt-in was
  accepted, the relaxed-timeout state, totals, and what the current settings mean.
- **Events**: notifications (gold), handoffs and restores (purple), failures (red), and the lines the
  library logs about the feature (grey). A `ConnectionFailed` with `FailureType == MaintenanceHandoff`
  is a planned replacement, not a fault; it is labelled as such.
- **Faults**: the five operations, what each announces, and the `rladmin` equivalent. The job status while
  the fault injector runs one, and the summary of the last disruption: when the first notification
  arrived, when the handoff happened, when the server closed the old socket, how many operations failed,
  and the worst latency.

Keys:

| Key | Action | What to watch |
|-----|--------|---------------|
| `M` | **endpoint rebind**: the one serving proxy is excluded, so the endpoint relocates | `MOVING seq N grace 15s successor <node>:<port>`, then `handoff` within a second, and the server's `on proxy node` changes. About 17s later the server closes the old socket: with `Auto`, nothing is on it. Under `Disabled`: no warning, the socket closes under in-flight commands, red column |
| `G` | **migrate shards** to a free node | `MIGRATING` then `MIGRATED` a few seconds apart; timeouts relaxed between and for a 20s tail; no reconnect. `shards ["N"]` names the shard |
| `F` | **failover** to the replica | `FAILING_OVER` then `FAILED_OVER`, same shape as a migration |
| `A` | **maintenance mode** on a node: shards leave it, then its proxy is retired | all three in one run, `MIGRATING :n`, `MIGRATED :n+1`, `MOVING :n+2`: one sequence counter across event types |
| `X` | **proxy restart**: the DMC process is killed | the negative control: no notification is possible, so every mode sees a connection failure and reconnects blind. Compare with `M` |
| `N` | next `maintNotifications` (reconnects) | `Auto` -> `Enabled` -> `Disabled`; see below |
| `E` | next `maintMovingEndpointType` (reconnects) | `Auto` -> `ServerDefault` -> `ExternalFqdn` -> `ExternalIp`; see below |
| `P` | RESP3 / RESP2 (reconnects) | under `Auto` the opt-in is refused with "not supported in RESP2 mode" and the connection carries on without the feature; under `Enabled` the connection is rejected and the screen says so |
| `D` | next database (reconnects) | proxy-routed vs OSS cluster API |
| `R` | toggle `GetDatabase().WithRetry()` | an operation that fails on a dropped connection is replayed: an unannounced close (`X`, or `M` under `Disabled`) costs latency instead of errors |
| `Space` | pause traffic | |
| `Q` | quit | prints totals and one line per disruption |

`maintNotifications` (`MaintenanceNotificationMode`), cycled with `N`:

| Mode | Meaning | On screen |
|---|---|---|
| `Auto` | ask during the handshake; carry on if refused. What the Redis Cloud, Redis Enterprise and Azure Managed Redis providers select | `opt-in: accepted`. Faults are announced and handled |
| `Enabled` | require it: a refusal, or ending up on RESP2, rejects the connection | the same when it works; press `P` to see the rejection |
| `Disabled` | never ask. **The default for any endpoint no provider recognises**, which includes a self-managed Redis Enterprise cluster | `opt-in: not requested`. `M` closes the socket with no warning; `G` and `F` run with normal timeouts |

`maintMovingEndpointType` (`MaintenanceEndpointType`), cycled with `E`. Only `MOVING` is affected:

| Value | `MOVING` carries | The client then |
|---|---|---|
| `Auto` | the replacement proxy, as `external-ip` without TLS or `external-fqdn` with it (a certificate cannot validate a bare address) | reconnects to it at once, before DNS has moved |
| `ServerDefault` | `_` (the bare opt-in, as earlier versions sent) | must re-resolve DNS, which has been measured trailing the notification by 4 to 19s against a 15s window, or recycle at half the window; on a bad run the socket is closed first |
| `ExternalFqdn` / `ExternalIp` | that form explicitly | as `Auto`; the explicit forms exist for deployments where the automatic choice is wrong |

## What to expect from each fault

Measured on a 3-node Redis Enterprise 8.0.22 cluster with eight workers at about 450 operations a second;
the `M` row was re-measured on 8.6.2 with StackExchange.Redis 3.4.35.
Timings are relative to pressing the key; the fault injector takes a few seconds to reach the cluster.

| Key | Notifications `Disabled` | Notifications `Auto` |
|---|---|---|
| `M` endpoint rebind | nothing announced. The socket is closed at about +23s under live traffic: a red tick, `connection failed: SocketClosed`, a handful of failed operations (the in-flight ones plus anything issued during the few hundred milliseconds of reconnect) | `MOVING` at about +6s with a 15s grace and the successor address; the handoff follows within a second, `on proxy node` changes, traffic never stops. The server closes the old socket at about +23s with nothing on it. **0 failed** |
| `G` migrate shards | no connection is dropped either way. Latency spikes to around a second while each shard moves; with the normal 5s timeout nothing fails | the same, plus `MIGRATING`/`MIGRATED` per shard and a relaxed-timeout window between them and for 20s after. The difference only shows when a move is slow enough to breach the normal timeout |
| `F` failover | as `G`: a latency spike of about a second, no drop | `FAILING_OVER`/`FAILED_OVER`, about 7s apart, relaxed timeouts in between; 12s end to end |
| `A` maintenance mode | the shard migration behaves as `G`. Whether the rebind at the end hits you depends on whether your proxy is on the node being drained; if it is, a silent close as under `M` at about +35s, otherwise nothing | `MIGRATING`, `MIGRATED`, `MOVING`, handoff: the whole chain with consecutive sequence numbers in one run, 45s. The `MOVING` only arrives if your proxy was on the drained node (compare the job output's `source_node` with `on proxy node`) |
| `X` proxy restart | **identical in both modes**: the proxy is killed, nothing can be announced, the socket closes and the client reconnects blind | identical. This is the control: it shows the feature is the warning, not a cleverer reconnect. `R` turns the failures into a latency spike by replaying the failed operations |

Two honest caveats for the stage. First, the harm of an unannounced close is bounded by the commands in
flight plus the reconnect time, so with a few workers and a fast reconnect the `Disabled` column is a blip,
not an outage; the story is announced versus unannounced, and it scales with connection count and reconnect
cost (TLS, distance), not with anything this demo can add. Second, the `0 failed` column needs
StackExchange.Redis 3.4.35 or later, which this project references. Earlier versions (to 3.4.31) waited out
the whole `MOVING` window before replacing the connection, so under `Auto` they recycled at about +21s and
failed the commands in flight at that moment; the fix
([PR #3347](https://github.com/seredis/StackExchange.Redis/pull/3347)) holds new work in the backlog while
the in-flight commands drain, so the recycle happens within a round trip of the notification. Use `M` for
the before-and-after, since it always retires the proxy you are on.

## Things worth knowing

- **A self-managed cluster is `Disabled` by default.** The client turns the feature on when the hostname
  looks like Redis Cloud (`.redislabs.com`, `.redis.io`) or Azure Managed Redis; otherwise it has no way to
  know, and the connection works without ever receiving a notification. Say `maintNotifications=Auto` or
  `defaults=enterprise` explicitly. This demo always sets the mode explicitly, whatever the hostname.
- **`MOVING` fires when a proxy is replaced, not when the proxy set shrinks.** Narrowing a proxy policy,
  or excluding one node of three, closes the retired connections in silence. The `single` policy on
  `hitless-standalone` is what makes `M` a substitution.
- **DNS is not what to wait on.** After a `MOVING`, the replacement proxy accepts connections within
  seconds while the endpoint's DNS record can still name the retiring node past the point the socket closes.
  That is why asking for the successor (`E` at `Auto`) matters.
- **The server replays the last completion** (`MIGRATED` or `FAILED_OVER`) to each connection that opts
  in, however long ago it happened. The library logs it as `(catch-up)` and does not raise an event or relax
  timeouts for it, because the frame carries no time. You may see such a line right after a reconnect.
- **Not in a connection group.** Inside `ConnectGroupAsync` (the geo failover demo) maintenance
  notifications are disabled for now, whatever the setting; the library logs a warning saying so.
- **Timeouts are relaxed, not disabled.** The window uses `maintRelaxedTimeout` (10s) as a floor, lasts at
  most `maintRelaxedWindowMax` (30s), and stays relaxed for `maintPostEventRelaxed` (20s) after a completion.
  A timeout that does occur inside a window says so: `RedisTimeoutException.MaintenanceType`.

## Code tour (for presenting)

The library-facing code is isolated so it can be shown on its own, e.g. `cat Setup.cs`:

- [`Setup.cs`](Setup.cs) - the **complete** connection setup: `MaintenanceNotifications`,
  `MaintenanceMovingEndpointType` and a `LoggerFactory` on `ConfigurationOptions`, the modes and endpoint
  types with what each means, and the three event subscriptions (`ServerMaintenanceEvent`,
  `ConnectionFailed`, `ConnectionRestored`) that only exist to put things on screen.
- [`CoreUsage.cs`](CoreUsage.cs) - the **complete** traffic path: one plain `StringSetAsync` or
  `StringGetAsync` per operation. Nothing there knows a handoff is happening; the only trace is
  `MaintenanceType` on the exception if one does occur.
- Everything else is demo scaffolding: `HandoffEngine.cs` (traffic, event bookkeeping, the disruption
  summary, the probe that shows which proxy node a connection is on), `FaultInjector.cs` (a thin HTTP
  client for the fault injector), `DemoSettings.cs` (where to connect), `Ui.cs` (the Spectre layout),
  `Program.cs` (the render loop).

## 3. Cleanup

Nothing local. The cluster is yours; if the fault injector provisioned it, it expires on its own schedule.
