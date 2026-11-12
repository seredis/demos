# HybridCache: active vs passive invalidation

Two `HybridCache` implementations, the same application code: the in-box
[`HybridCache`](https://learn.microsoft.com/aspnet/core/performance/caching/hybrid) over
`IDistributedCache`-on-redis, and
[`StackExchange.Redis.HybridCache`](https://github.com/seredis/StackExchange.Redis.HybridCache)
built on [client-side caching](https://seredis.dev/ClientSideCaching). Each demo runs two "web
servers" — each with its own near cache — sharing one redis and one database, and asks the same
question: **when server A changes something, how long does server B keep serving the old value?**

| | |
|---|---|
| `Setup.cs`, stock mode | `AddHybridCache` + `AddStackExchangeRedisCache`: a `MemoryCache` near cache per server, **no invalidation channel** — a foreign change is seen when the local copy *expires* (passive) |
| `Setup.cs`, push mode | `AddRedisHybridCache`: the multiplexer's client cache is the near cache, and redis **pushes invalidations** into it (`CLIENT TRACKING`, RESP3) — a foreign change evicts the local copy immediately (active) |
| `CoreUsage.cs` | the application's cache consumption — identical for both |
| everything else | demo machinery, also identical for both |

Both modes use the same entry options (`Expiration` 5 min, `LocalCacheExpiration` 10 s); what
changes is the invalidation transport, not the policy.

Two demos share this folder's `docker-compose.yml`:

- [`console/`](console/): a scripted run. Prints what happens, exits with a side-by-side summary.
- [`tui/`](tui/): an interactive terminal UI for presenting. Make changes from the keyboard, watch
  each server's chart react — and switch implementation live, from inside the running app.

## 1. Setup infrastructure

Requires Docker with the compose plugin. From this folder:

```bash
docker compose up -d --wait     # one redis 8.2 on host port 16412
```

Both demos need the .NET 10 SDK. They reference the published
`StackExchange.Redis.HybridCache` package (alpha), which brings the StackExchange.Redis v4 alpha
with it; the in-box stack is also referenced, so both implementations run in one process (the
stock stack's `StackExchange.Redis` dependency is a `>= 2.7.27` floor, not a pin, so the graph
unifies onto the v4 alpha — a combination that has been verified working, both here and in a
dedicated probe of the stock stack on v4).

## 2a. Console demo

```bash
cd console
dotnet run                      # both modes, compared
dotnet run -- stock             # just the in-box HybridCache (passive)
dotnet run -- push              # just StackExchange.Redis.HybridCache (active)
```

Four scenarios; each measures how long **server B** keeps serving a value that server A has
already changed:

1. **update** — server A writes a new price through the cache (`SetAsync`);
2. **remove** — the database changed behind the cache; server A evicts the key
   (`RemoveAsync`), and the next read — wherever it lands — reloads from the database;
3. **tag invalidation** — the price list changes; server A calls `RemoveByTagAsync("catalog")`
   and keeps reading (so its next read re-populates redis);
4. **tag invalidation, invalidator quiet** — the same, but server A goes silent afterwards:
   nothing re-populates redis, so the invalidation itself is server B's only way to find out.

Measured output (one run, this machine):

```
== time until each server served the new value ==
                                                   stock (passive)       SE.Redis (active)
                                               server A   server B     server A   server B
   1. update (SetAsync)                          0.00 s     9.03 s       0.00 s     0.00 s
   2. remove (RemoveAsync)                       0.02 s    10.00 s       0.02 s     0.02 s
   3. tag invalidation                           0.02 s    10.01 s       0.02 s     0.02 s
   4. tag invalidation, invalidator quiet       (quiet)     > 15 s      (quiet)     0.02 s
```

### Why

- **Stock, scenarios 1–3**: nothing tells server B's `MemoryCache` anything. B serves its local
  copy until that copy's `LocalCacheExpiration` passes, then refetches from redis. The ~9–10 s
  is exactly that setting: staleness is a knob you trade against backend traffic (10 s stale
  ⇔ a round trip per entry per 10 s, per server). A key-based remove (scenario 2) would stay
  bounded by that knob even if server A went quiet — the entry really is gone from redis, so
  B's refetch misses and reloads; tags are the case where that stops being true.
- **Stock, scenario 4** is worse than the knob suggests: server B's copy *did* expire during
  the wait, and B *did* refetch from redis — and still served the old value. `RemoveByTagAsync`
  doesn't delete entries; it writes a tag timestamp that readers compare against, and each
  stock process memoizes a tag's timestamp the first time it needs it, permanently
  (`DefaultHybridCache.TagInvalidation.cs`: `_tagInvalidationTimes` is only updated by a
  *local* `RemoveByTagAsync`). A tag invalidated elsewhere is invisible until something writes
  a *fresh entry* for B to fetch — in scenario 3 that was server A's next read; here, nothing —
  so B stays stale until the entry itself leaves redis (`Expiration`, 5 minutes).
- **SE.Redis, all four**: both the entries and the tag-timestamp keys live in the client
  cache, and redis pushes an invalidation for any of them the moment they change — a foreign
  `SetAsync` evicts B's entry copy, a foreign `RemoveByTagAsync` evicts B's tag copy. B's next
  read notices within its poll interval (~20 ms of it is the round trip to refetch).

The same `LocalCacheExpiration` is configured in both modes: in the stock mode it is the
*mechanism*; in the SE.Redis mode it is a safety net behind the push channel (see the
[usage docs](https://github.com/seredis/StackExchange.Redis.HybridCache/blob/main/docs/usage.md)
for the degraded modes where it matters).

## 2b. Interactive TUI

```bash
cd tui
dotnet run                  # interactive, starting on the stock implementation
dotnet run -- push          # interactive, starting on StackExchange.Redis.HybridCache
dotnet run -- --scripted    # hands-free: the console demo's timeline, on both implementations
```

Use a wide terminal with a large font; the banner collapses when there is no room for it.

What is on screen:

- **Banner**: the active implementation — and whenever server B is behind, a live count of how
  long it has been serving a stale value, in red.
- **Two server boxes**, stacked: server A (the writer: every change is made by A) and server B
  (which finds out only through the cache). Each box charts the price that server is currently
  serving — one column per 150 ms read through the cache, height = price — in blue while it
  matches the database and red while it is stale, with a `!` row beneath stale columns and your
  action keys marked as letters on both strips. A change appears as a step: watch how long B's
  step lags A's.
- **Events**: every transition, with "n.nn s after the change" when a server catches up.

Keys:

| Key | Action |
|-----|--------|
| `U` | server A updates the price through the cache (`SetAsync`) |
| `R` | the database changed behind the cache; server A evicts the key (`RemoveAsync`) |
| `T` | a new price list: server A calls `RemoveByTagAsync("catalog")` and keeps reading |
| `G` | the same, but server A then goes **quiet** for 15 s — nothing re-populates redis |
| `M` | **switch implementation** — tears both servers down and rebuilds them through the other branch of `Setup.cs`; the application code does not change |
| `P` | pause sampling |
| `Q` | quit |

A suggested walk-through: start on stock, press `U` and watch server B hold the old height in
red for ~10 seconds (the `LocalCacheExpiration` knob). Press `G` and watch B stay red with no
end in sight — the tag memo flaw described above. Then press `M`, repeat the same keys, and
watch both charts step together within a frame.

### Code tour (for presenting)

The library-facing code is isolated so it can be shown on its own, e.g. `bat Setup.cs`:

- [`tui/Setup.cs`](tui/Setup.cs) — the **complete** difference between the two implementations:
  one method per mode, same entry options, same keyspace sharing. The `M` key just re-runs the
  other method.
- [`tui/CoreUsage.cs`](tui/CoreUsage.cs) — the **complete** application: the classic
  read-through `GetOrCreateAsync`, write-through `SetAsync`, `RemoveAsync` and
  `RemoveByTagAsync`, against the abstract `HybridCache`. Nothing in it knows which
  implementation is running.
- Everything else is demo scaffolding: `Engine.cs` (sampling, actions, mode switching),
  `Ui.cs` (the Spectre layout), `Program.cs` (the render loop), `Database.cs` (a pretend
  system of record). The console demo carries the same split (`Setup.cs`, `CoreUsage.cs`,
  with `Scenario.cs` as the script).

Colour is used throughout but never alone: staleness is also carried by the `!` row and written
out in words with its duration, actions are letters, and the implementation is named in text.
Blue and red stay distinct under the common red/green deficiencies; red is reserved for stale
data, as in the sibling demos. `NO_COLOR=1` turns colour off entirely.

## 3. Cleanup

```bash
docker compose down
```
