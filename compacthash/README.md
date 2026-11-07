# Compact hashes + HIMPORT (Redis 8.10)

A measured console run of Redis 8.10's compact hashes: when many hashes share a schema, the field
names can be stored **once per schema** (a *template*) instead of once per key. `HIMPORT` is the
bulk-load path that creates them: a *field-set* names the schema's fields, and each import then
supplies only the values, positionally.

The `HIMPORT` family is **session-local** — a field-set is `PREPARE`d on one physical connection
and referenced by name from each `HIMPORT SET` — which a multiplexer deliberately hides.
StackExchange.Redis's `HashImport` type owns that mismatch: `HashImport.Create(fields)` describes
the schema, `IDatabase.HashImport[Async](key, import, values)` sends one import, and the `PREPARE`
is injected lazily on whichever connection the write lands on (the same trick as `SELECT`), so
reconnects, pipelining and multi-node fan-out all just work. Disposing the field-set sends a
best-effort `HIMPORT DISCARD`.

The run loads the same orders dataset (14 descriptive fields — `order_id`, `customer_id`,
`status`, ... — ~170 bytes of names per key when stored plainly) twice at 100 / 5,000 / 50,000
keys: once with plain `HSET`, once through `HIMPORT`. Everything in the table comes from the
server: `used_memory` deltas, `MEMORY USAGE` (which charges each key its fair share of its
template), `OBJECT ENCODING`, and the `INFO` counters (`hash_templates`, `hash_template_keys`,
`used_memory_hash_templates`).

Typical result:

| keys | via | keys/s | used_memory Δ | per key | encoding |
|------|-----|-------:|--------------:|--------:|----------|
| 50,000 | `HSET` | ~114k | 20.95MB | 439 B | `listpack` |
| 50,000 | `HIMPORT` | ~281k | **12.00MB** | **252 B** | `template-listpack` |

— **43% less hash memory** (one 1.6KB template instead of 50,000 copies of the field names) *and*
~2.5× the load throughput, since each import ships only values. The per-key win is already there
at 100 keys — `MEMORY USAGE` (which charges each key its fair share of the template) reads
387 B → 225 B, the same ~42% as at 50,000 — because the template is only 1.6KB, amortized after a
handful of keys. The `used_memory Δ` column only converges at the larger sizes: at 100 keys the
~70KB signal drowns in allocator and dict-sizing noise, which is a property of that measurement,
not of the feature. Reads are untouched: an imported key is an ordinary hash with a smarter
encoding, and the run ends by asserting `HGETALL` round-trips the exact values.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

One `redis:8.10.3` container (compact hashes ship in Redis 8.10), on host port `16471`.

## 2. Run

Needs the .NET 10 SDK.

```bash
dotnet run   # non-interactive; prints the table, exits non-zero if any assertion fails
```

The run flushes the database between phases — don't point it at a server you care about.

## 3. Teardown

```bash
docker compose down
```
