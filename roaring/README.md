# Compressed (roaring) bitmaps (Redis 8.12)

A scripted, measured demo of Redis 8.12's headline feature: [redis-roaring integrated into the
core](https://github.com/redis/redis/pull/15331) as a new `bitmap` type
(`OBJECT ENCODING bitmap-roaring`), behind the `bitmap-default-roaring` config (off by default).

Two servers, one workload, three targets:

| Target | Server | Encoding |
|---|---|---|
| `8.10 string` | `redis:8.10.3` | classic string bitmap — the baseline you run today |
| `8.12 string` | 8.12-rc2, built from source | same classic encoding — proves the win is the encoding, not the version |
| `8.12 roaring` | the same 8.12 server | the same data, converted |

The part that makes this interesting for client code: **there is almost nothing new to call.** The
ordinary bitmap commands — `SETBIT`, `GETBIT`, `BITCOUNT`, `BITPOS`, `BITOP`, `BITFIELD` — work
unchanged on roaring bitmaps, so everything measured here goes through the ordinary `IDatabase`
methods. Of the three new commands in the 8.12 command table, two are **internal** (`SYSCMD`)
propagation primitives that clients never send: `BITCONVERT` carries the string→roaring transition
to replicas/AOF, and `BITROAROP` carries `BITOP`'s native result (the client keeps saying `BITOP`).
The one user-facing addition is `BITRANGEINTARRAY` ("which bits are set in this range?"), which has
a client PR in flight ([#3315](https://github.com/seredis/StackExchange.Redis/pull/3315),
`IDatabase.StringBitPositions`); until that ships, this demo sends it through the v4 ad-hoc context
API — the machinery the [`extending/`](../extending/) demo covers at full length.

## The acts

1. **One workload, three encodings** — each population is built client-side as a `byte[]` and
   `SET` to both servers, so every target provably holds identical data. The roaring twins are made
   by flipping `bitmap-default-roaring` on and re-setting an already-set bit: a *write* converts a
   string bitmap in place (a read never does), and the conversion is one-way — the key stays
   roaring when the config goes back off.
2. **Memory** — `MEMORY USAGE` across five populations: sparse-random, clustered runs, a dense
   block, and ~50% random noise. Sparse collapses ~100×, clustered runs ~5,000×; the noise row is
   the honest one — incompressible data concedes a little to container overhead.
3. **Transparency** — `BITCOUNT`, `BITPOS` and `GETBIT` answer identically on all three targets,
   checked against values computed client-side from the populations themselves.
4. **Read performance** — measured *on the server* from `INFO commandstats` (µs/call), so client
   round trips play no part: `BITCOUNT` ~14× faster, `BITOP AND`/`OR` ~12× faster on roaring;
   `GETBIT` honestly a touch slower (a container lookup instead of a byte index — sub-µs either
   way). A `BITOP OR` destination is the full 40 MB extent from string operands and ~600 KB native
   from roaring ones.
5. **Write performance** — the same 50,000 sparse `SETBIT`s into a fresh key per target; the string
   encoding also pays the full zero-filled extent on the first high-offset write.
6. **Migrating a live key** — the operational story, since there is no client-facing convert
   command: turn the config on, and each existing string bitmap converts on its next write.
   Encoding and memory before/after, with `BITCOUNT` unchanged.
7. **`BITRANGEINTARRAY`** — the first 10 set-bit offsets straight from the server
   (`... 0 -1 BIT LIMIT 10`), checked against the generated positions — and the same call failing
   on 8.10 with `unknown command`.

Every act checks against an oracle; the run exits non-zero if any disagree.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

This starts `redis:8.10.3` on host port `16431`, and — because 8.12 is at the release-candidate
stage with no official image yet — builds `8.12-rc2` from the source tag (roaring is in core; no
modules needed) on host port `16432`. The first `up` therefore takes a few minutes; swap the
`build:` for `image: redis:8.12` in `docker-compose.yml` once that image exists. Note the RC
reports itself as `8.11.x` in `INFO`, as Redis release candidates do.

## 2. Run

Needs the .NET 10 SDK; the project pins the v4 alpha for the ad-hoc context API.

```bash
dotnet run
```

Typical output is shown in [output.md](output.md).

## 3. Teardown

```bash
docker compose down
```
