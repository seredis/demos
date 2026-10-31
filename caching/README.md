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
dotnet run -- --scripted    # hands-free tour, then exit
```

Use a wide terminal with a large font; the layout adapts to the height.

What is on screen:

- **Banner**: the share of the last two seconds' reads that were answered locally.
- **Strip**: one column per 250ms. Top row, how much of that window was answered locally (blue);
  second row, how much reached the server (orange); third row, a letter for each action you took, so
  cause and effect line up.
- **The catalogue**: each key, how often the app reads it (a bar and a count — the skewed read mix
  as a histogram), the value the app sees through the cache, the value the server actually holds,
  and whether they agree. A row goes red with a `!` when they do not.
- **Counters**: everything `GetClientCache()` exposes (`Count`, `Bytes`, `Stored`, `Evicted`,
  `Promoted`, `Expired`, `Refreshes`, `ServedStale`, `TrackedKeys`, `EffectiveTrackingMode`), and the
  server-side measurement of reads issued against reads that reached it.
- **Events**: what the demo did and what the library reported.

Keys:

| Key | Action | What to watch |
|-----|--------|---------------|
| `↑` `↓` | select a catalogue row | |
| `W` | `SET` the selected key from the back door | the server pushes an invalidation: one orange tick, the app sees the new value, then it is local again |
| `X` | `EXPIRE` the selected key in 1s from the back door | expiry is **not** announced: the row goes `! STALE` and stays so until `TimeToLive` (5s) retires the entry |
| `I` | `GetClientCache().Invalidate(key)` | the manual fix for a value you know is wrong: dropped at once, nothing sent to the server |
| `A` | `GetClientCache().InvalidateAll()` | every row misses once |
| `F` | `FLUSHDB` from the back door | the whole cache is discarded; keys are gone until re-seeded |
| `S` | re-seed missing keys from the back door | |
| `N` | 25 reads of fresh, never-seen keys, once each | admission: the prefix profiles store all 25 (`Count` +25); the `doorkeeper` profile stores none |
| `B` | toggle `CommandFlags.CacheOptOut` on every read | what the same traffic costs with no cache: every read reaches the server |
| `C` | next cache profile (reconnects) | see below |
| `P` | pause reads | |
| `Q` | quit | |

Profiles, cycled with `C`:

| Profile | Policy | Shows |
|---------|--------|-------|
| `plain` | `TimeToLive` 5s | entries live until invalidated or aged out |
| `refresh-after` | `TimeToLive` 5s, `RefreshAfter` 3s | steadily-read entries are refreshed in the background before they expire: `Refreshes` climbs, `Expired` does not |
| `grace` | `TimeToLive` 5s, `InvalidationGracePeriod` 1.5s | after `W`, the old value is deliberately served for up to 1.5s while one refresh runs: `ServedStale` climbs and the row is briefly `! STALE` |
| `doorkeeper` | no `Prefixes`, `TimeToLive` 5s | `Tracking` mode with default admission: a reply is stored on its second miss, so `N` adds nothing and the hot keys still cache after one extra round trip |
| `no cache` | none | `GetClientCache()` is null; every read reaches the server |

Every profile also sets `SweepInterval` to 1s. In 4.0.146-alpha ([issue #3318](https://github.com/StackExchange/StackExchange.Redis/issues/3318)) an entry that reaches `TimeToLive` is
not replaced by the next fill until the sweep reclaims it (10s by default), so with a 5s `TimeToLive`
the plain profile would otherwise miss for five seconds in every ten. Invalidated entries are replaced
at once; only expiry is affected, and only until the fill path treats an expired entry as dead.

The prefix profiles cache an in-prefix key on its first miss. Without prefixes (the `doorkeeper`
profile) the default is to cache on the second miss, which is why a quick by-hand test can look like it
is not working; the counters panel shows the resolved tracking mode and admission rule for the current
profile. The "hit rate" lines in that panel mix two sources on purpose: reads issued is the app's own
count, GETs seen is the server's, and the difference is what never left the process. The `TimeToLive` is deliberately short so that the expiry case is visible
inside a demo; production values are minutes.

Colour is used throughout but never alone: local and server reads have their own rows and words, stale
rows carry a `!`, and the action markers are letters. `NO_COLOR=1` turns colour off.

## Code tour (for presenting)

The library-facing code is isolated so it can be shown on its own, e.g. `cat Setup.cs`:

- [`Setup.cs`](Setup.cs) — the **complete** connection setup: the cache profiles (`CacheOptions` /
  `CachePolicy`), the app connection that attaches one of them via `ConfigurationOptions.ClientCache`,
  and the uncached back-door connection. Attaching the cache is the only thing this app does
  differently from an uncached one.
- [`CoreUsage.cs`](CoreUsage.cs) — the **complete** read path: one plain `StringGetAsync` per read.
  No code there knows whether a read is answered locally; the only per-call option is
  `CommandFlags.CacheOptOut`.
- Everything else is demo scaffolding: `CacheEngine.cs` (traffic shaping, back-door actions, server
  measurement), `Ui.cs` (the Spectre layout), `Program.cs` (the render loop).

## 3. Cleanup

```bash
docker compose down
```
