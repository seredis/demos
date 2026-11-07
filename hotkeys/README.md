# HOTKEYS (Redis 8.6)

An interactive terminal demo of Redis 8.6's `HOTKEYS` family: server-side, per-key profiling of
**CPU time** and **network bytes**, with optional sampling and slot filtering — "`top` for keys",
measured where the work actually happens. StackExchange.Redis has first-class support on
`IServer`: `HotKeysStart` / `HotKeysGet` / `HotKeysStop` / `HotKeysReset`, with the report
materialized as `HotKeysResult`.

The scenario: a deliberately lopsided workload runs the whole time, built so that *three different
kinds of "hot"* are in play at once —

- **hot by volume** — GETs over 400 `user:NNN` keys follow a Zipf distribution, like real cache
  traffic: a few keys take most of the hits, so the Zipf head ranks on both metrics;
- **hot by CPU, invisible by bytes** — `SINTERCARD` over two 5000-member sets (`tags:a`/`tags:b`):
  real work for the server, a one-integer reply on the wire;
- **hot by bytes, invisible by CPU** — `LRANGE catalog 0 -1`: ~20KB per reply, almost nothing to
  compute.

Only a profiler that measures both metrics separately can tell those stories apart, and that is
exactly what the leaderboard shows: ranked by CPU, `tags:*` surfaces and `catalog` does not; ranked
by bytes, they swap.

Things the demo surfaces, each observable on screen:

- **captures are sessions** — `HOTKEYS START METRICS CPU NET COUNT 12 DURATION 30 SAMPLE n`
  starts one (a second `START` while one runs is refused by the server); `GET` is polled live
  while it runs, keeps answering after it finishes, and `RESET` discards the report;
- **the spike** — press `S` and a key from the cold tail suddenly takes 60% of reads; watch it
  climb the board in a couple of polls — the operational story (find the key that is melting this
  shard *right now*) in miniature;
- **sampling** — `1` cycles the `SAMPLE` ratio for the next capture (1 → 8 → 64): measure every
  command, or every Nth — the ranking survives sampling, the absolute numbers shrink, and the
  profiling overhead drops with it;
- **totals vs per-key** — the header shows the session totals (`total-cpu-time-user/sys`,
  `total-net-bytes`) alongside the per-key report, which is capped at `COUNT` keys *per metric*.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

One `redis:8.10.3` container (`HOTKEYS` needs 8.6+), on host port `16451`.

## 2. Run

Needs the .NET 10 SDK.

```bash
dotnet run                 # interactive
dotnet run -- --scripted   # hands-free tour, prints the transcript, exits non-zero on any error
```

Keys: `C` start a 30s capture · `X` stop it early (the data stays readable) · `R` reset ·
`S` spike a cold key · `M` rank by CPU / by bytes · `1` cycle the sample ratio · `W` pause the
workload · `Q` quit.

The scripted run asserts the three stories: a Zipf-head `user:` key and the `SINTERCARD` sets must
rank by CPU, and `catalog` must rank by network bytes.

## 3. Teardown

```bash
docker compose down
```
