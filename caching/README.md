# Client-side caching (v4)

An interactive terminal demo of SE.Redis v4's
[client-side caching](https://seredis.dev/ClientSideCaching): the client keeps a local copy of read
results and Redis tells it, as keys change, which copies are no longer valid (`CLIENT TRACKING`, over
RESP3). The feature is in the v4 alpha; this project references it directly.

Two connections to one Redis server:

- the **app** connection has the cache configured and reads a small catalogue (`demo:item:1..8`)
  continuously, a few hundred reads a second. The key mix is skewed (zipf-adjacent: `item:1` gets
  ~27% of reads, `item:8` ~6% — the `reads` column shows the histogram), and the first touch of
  each key is jittered so the catalogue does not expire, and so miss, in lockstep;
- the **back door** connection has no cache. It writes, expires and flushes keys behind the app's back,
  and reads the truth for the display.

Three cache settings can be changed independently while it runs, each a reconnect (`CacheOptions` are
settled at connect time): the **policy** (`C`: how entries age), the **tracking mode** (`T`: how the
server tells the client about changes, from `CLIENT TRACKING` through keyspace notifications to nothing
at all) and the **admission** rule (`D`: whether a missed reply is stored on its first miss or only on a
repeated one). `K` flips the server's `notify-keyspace-events`, which is what the keyspace mode lives on.

The hit rate is measured on the server, from `INFO commandstats`: a read answered locally is a `GET`
the server never sees. That is how the library's own tests measure it, and it cannot be fooled by the
client. The library has no hit counter by design (a hit records nothing).

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

One standalone Redis 8.2 container, `redis-cache`, on host port `16411`.

## 2. Run

Requires the .NET 10 SDK.

```bash
dotnet run                  # interactive
dotnet run -- --scripted    # hands-free tour (about 90s), then exit with a transcript of every event
```

Use a wide terminal with a large font; the layout adapts to the height.

What is on screen:

- **Banner**: the share of the last two seconds' reads that were answered locally, and the current
  policy, tracking mode (requested, and the `EffectiveTrackingMode` the library reports), admission
  rule and server `notify-keyspace-events` value.
- **Strip**: one column per 250ms. Top row, how much of that window was answered locally (blue);
  second row, how much reached the server (orange); third row, a letter for each action you took, so
  cause and effect line up.
- **The catalogue**: each key, how often the app reads it (a bar and a count — the skewed read mix
  as a histogram), the value the app sees through the cache, the value the server actually holds,
  and whether they agree. A row goes red with a `!` when they do not.
- **Counters**: everything `GetClientCache()` exposes (`Count`, `Bytes`, `Stored`, `Evicted`,
  `Promoted`, `Expired`, `Refreshes`, `ServedStale`, `TrackedKeys`, `EffectiveTrackingMode`), and the
  server-side measurement of reads issued against reads that reached it.
- **Events**: what the demo did, what the library reported, and how long each row was out of sync
  with the server (probed every 250ms), which is the cost of the chosen tracking mode made visible.

Keys:

| Key | Action | What to watch |
|-----|--------|---------------|
| `↑` `↓` | select a catalogue row | |
| `W` | `SET` the selected key from the back door | under the `CLIENT TRACKING` modes the server pushes an invalidation: one orange tick, the app sees the new value, then it is local again. Under `keyspace` only if the server publishes events; under `none` never: the row goes `! STALE` until `TimeToLive` |
| `X` | `EXPIRE` the selected key in 1s from the back door | the server announces an expiry **when it notices it**: on this 8-key database the active expire cycle finds it within a few hundred ms, so the row is barely stale; on a large keyspace expiry is lazy and the push can lag, which is what `TimeToLive` is for. Under `keyspace` the `x` event does the same; under `none` the row is `! STALE: gone on server` until `TimeToLive` |
| `I` | `GetClientCache().Invalidate(key)` | the manual fix for a value you know is wrong: dropped at once, nothing sent to the server |
| `A` | `GetClientCache().InvalidateAll()` | every row misses once |
| `F` | `FLUSHDB` from the back door | `CLIENT TRACKING` announces a flush, so the whole cache is discarded; keyspace notifications have no per-key event for it, so under `keyspace` every row is `! STALE` until `TimeToLive` |
| `S` | re-seed missing keys from the back door | |
| `N` | 25 reads of fresh, never-seen in-prefix keys, once each | admission: `OnFirstMiss` stores all 25 (`Count` +25); `OnRepeatedMiss` stores none. Under `by-prefix` all 25 are stored whatever the admission rule, see below |
| `B` | toggle `CommandFlags.CacheOptOut` on every read | what the same traffic costs with no cache: every read reaches the server |
| `C` | next policy (reconnects) | see below |
| `T` | next tracking mode (reconnects) | see below |
| `D` | toggle admission between `OnRepeatedMiss` and `OnFirstMiss` (reconnects) | press `N` under the `tracking` mode before and after |
| `K` | `CONFIG SET notify-keyspace-events` on the server, between `""` and `KA`, from the back door | no reconnect: under `keyspace` the subscription is already there, this decides whether anything is published to it. Restored on exit |
| `P` | pause reads | |
| `Q` | quit | |

Policies (`CachePolicy`), cycled with `C`:

| Policy | Settings | Shows |
|--------|----------|-------|
| `plain` | `TimeToLive` 5s | entries live until invalidated or aged out |
| `refresh-after` | `TimeToLive` 5s, `RefreshAfter` 3s | steadily-read entries are refreshed in the background before they expire: `Refreshes` climbs, `Expired` does not |
| `grace` | `TimeToLive` 5s, `InvalidationGracePeriod` 1.5s | after `W`, the old value is deliberately served for up to 1.5s while one refresh runs: `ServedStale` climbs and the row is briefly `! STALE` |
| `no cache` | none | `GetClientCache()` is null; every read reaches the server |

Tracking modes (`CacheTrackingMode`), cycled with `T`. All but `tracking` give `Prefixes = ["demo:"]`
(`Tracking` rejects prefixes: the server announces what was read, so there is nothing to filter):

| Mode | Wire | Shows |
|------|------|-------|
| `by-prefix` | `TrackingMode` left at `Default`, which with `Prefixes` resolves to `TrackingByPrefix`: `CLIENT TRACKING ON OPTIN`, and each in-prefix read goes out behind `CLIENT CACHING YES` | the library's default when prefixes are given. An in-prefix read is an implicit `CacheOptIn`, so it is stored on its **first** miss regardless of admission: `N` adds 25 and `D` changes nothing |
| `tracking` | `CLIENT TRACKING ON OPTOUT`, no prefixes | the library's default without prefixes: the server remembers every key this client read (`TrackedKeys`) and announces only those. Admission applies: with the default `OnRepeatedMiss`, `N` adds nothing and the hot keys cost one extra round trip before they cache |
| `broadcast` | `CLIENT TRACKING BCAST PREFIX demo:` | the server keeps no per-client table and announces every in-prefix change by anyone. Behaves like `tracking` from the app's side; the cost moved to the client's inbox |
| `keyspace` | no `CLIENT TRACKING`: a subscription to `__keyspace@*__:demo:*` | for servers or proxies without `CLIENT TRACKING`. With `notify-keyspace-events` off (the Redis default, and this container's) it hears nothing and behaves as `none`; press `K` and `W` and `X` are heard again. `F` is not heard either way: there is no keyspace event for a flush. Pub/sub is also at-most-once, where `CLIENT TRACKING` rides the connection it protects |
| `none` | nothing negotiated | the baseline: another client's write is never heard about and `TimeToLive` is the only bound. Works over RESP2 |

Admission (`CacheAdmission`), toggled with `D`: `OnRepeatedMiss` (the library default) remembers a
first miss in a small filter and only stores the reply when the same request misses again, so a key read
once never enters the cache; `OnFirstMiss` stores every cacheable reply at once. It only matters where
the read is not already opted in, so compare `N` under `tracking` (or `broadcast`, `keyspace`, `none`),
not under `by-prefix`. The default is why a quick by-hand test without prefixes can look like the cache
is not working: the second read of a key is the first one stored.

The counters panel shows the `EffectiveTrackingMode` and `Options.Admission` the library reports for the
current connection, and the server's live `notify-keyspace-events` value. The "hit rate" lines in that
panel mix two sources on purpose: reads issued is the app's own count, GETs seen is the server's, and the
difference is what never left the process. The `TimeToLive` is deliberately short so that the expiry
case is visible inside a demo; production values are minutes.

Colour is used throughout but never alone: local and server reads have their own rows and words, stale
rows carry a `!`, and the action markers are letters. `NO_COLOR=1` turns colour off.

## Code tour (for presenting)

The library-facing code is isolated so it can be shown on its own, e.g. `cat Setup.cs`:

- [`Setup.cs`](Setup.cs) — the **complete** connection setup: the policies (`CachePolicy`), the
  tracking modes (`CacheTrackingMode` plus `Prefixes`), the admission rule (`CacheAdmission`), the
  one method that composes the current choice of each into a `CacheOptions`, the app connection that
  attaches it via `ConfigurationOptions.ClientCache`, and the uncached back-door connection. Attaching
  the cache is the only thing this app does differently from an uncached one.
- [`CoreUsage.cs`](CoreUsage.cs) — the **complete** read path: one plain `StringGetAsync` per read.
  No code there knows whether a read is answered locally; the only per-call option is
  `CommandFlags.CacheOptOut`.
- Everything else is demo scaffolding: `CacheEngine.cs` (traffic shaping, back-door actions, server
  measurement), `Ui.cs` (the Spectre layout), `Program.cs` (the render loop).

## 3. Cleanup

```bash
docker compose down
```
