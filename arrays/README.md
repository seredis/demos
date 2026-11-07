# Native arrays (Redis 8.8)

An interactive terminal demo of Redis 8.8's native array type — 18 `AR*` commands: indexed slots,
a server-side write cursor, ring-buffer semantics, range aggregates and predicate search — through
StackExchange.Redis's first-class `IDatabase.Array*` API (`ArrayRing`, `ArrayGetRange`,
`ArrayOperation`, `ArrayGrep` with `ArrayGrepRequest`, `ArraySeek`, `ArrayNext`, `ArrayLastItems`,
`ArrayInfo`, ...).

The scenario is a **telemetry ring**: one array, `telemetry:cpu`, 48 slots, one CPU reading every
200ms via `ARRING` — the pattern that previously took an `LPUSH`+`LTRIM` dance (or client-side
index arithmetic over a hash) and still left aggregation to the client. Here **the server owns the
ring**: the cursor, the wrap-around, the overwrite of the oldest slot — and the queries run where
the data lives.

Things the demo surfaces, each observable on screen:

- **the ring, live** — a 48-column sparkline from `ARGETRANGE 0 47` (empty slots and all, drawn as
  `·`), with the cursor (`ARNEXT`) marked: watch it sweep and wrap;
- **server-side aggregates** — `AROP 0 47 SUM|MIN|MAX|USED`: the stats panel is computed by the
  server, one integer per reply; no values cross the wire to be averaged;
- **server-side search** — `G` runs `ARGREP 0 47 RE '^(9[0-9]|100)$'`: the slots holding a 90+
  reading come back as indexes, found by a regex evaluated in the server; `J` injects a spike to
  give it something to find;
- **the cursor is state you can move** — `S` sends `ARSEEK 0`: the next write lands in slot 0, age
  be damned; `X` retcons the last reading with `ARSET` (which, unlike the cursor writes, does
  *not* move the cursor);
- **recency independent of position** — `ARLASTITEMS 5 REV` returns the most recently *written*
  values, which after wraps and retcons is not "the last five indexes";
- **storage internals** — `ARINFO`: arrays are sliced and sparse (only touched slices are
  allocated), so a huge, mostly-empty array is cheap.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

One `redis:8.10.3` container (native arrays ship in Redis 8.8), on host port `16481`.

## 2. Run

Needs the .NET 10 SDK.

```bash
dotnet run                 # interactive
dotnet run -- --scripted   # hands-free tour, prints the transcript, exits non-zero on any error
```

Keys: `J` inject a spike · `G` `ARGREP` for 90+ readings · `S` `ARSEEK 0` · `X` retcon the last
reading (`ARSET`) · `P` pause the producer · `Q` quit.

The scripted run asserts the ring fills (`ARLEN` = 48) and wraps, that `ARGREP` finds the injected
spike server-side, and that `ARSEEK 0` really rewinds the cursor.

## 3. Teardown

```bash
docker compose down
```
