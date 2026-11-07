# Vector sets (Redis 8.0)

An interactive terminal demo of Redis 8.0's vector sets (`VADD` / `VSIM` / `VINFO` and friends),
through StackExchange.Redis's first-class `IDatabase.VectorSet*` API: `VectorSetAdd` with
`VectorSetAddRequest` (quantization, attributes), `VectorSetSimilaritySearch` with
`VectorSetSimilaritySearchRequest` (`COUNT`, `WITHSCORES`, `WITHATTRIBUTES`, `FILTER`, exact
search), and `VectorSetInfo`.

The scenario is **nearest-neighbour image matching over MNIST digits**, chosen because it is the
rare vector workload that is honest *and* self-contained: the 784 raw pixels of each 28×28 image
are the vector — no neural net, no embedding service, no API key — and both the query and its
matches are rendered right in the terminal, so you can see with your own eyes whether the
neighbours actually look alike. (In a real system the vectors would come out of a model; the Redis
and client mechanics are identical — vector sets index vectors, where they come from is your
business.)

The corpus (5,000 digits, held apart from the 5,000 used as queries) is loaded **three times with
the same data but different quantization**, so the trade-off is measured, not asserted:

| key | quantization | memory | behaviour |
|-----|--------------|--------|-----------|
| `digits:f32` | `NOQUANT` (float32) | ~18.4MB | the reference |
| `digits:q8` | `Q8` (int8, **the server default**) | ~7.2MB | recall barely moves |
| `digits:bin` | `BIN` (1 bit/component) | ~3.9MB | recall visibly drops — yet usually still finds the right digit |

Things the demo surfaces, each observable on screen:

- **the matches, as images** — a query digit (held out of the corpus) and its 5 nearest
  neighbours, drawn in the terminal; a match with the wrong label is marked `✗`;
- **recall, measured** — every search also runs the ground truth (exact scan of the float32 copy)
  and shows the overlap, so HNSW's approximation and quantization's loss are numbers, not vibes;
- **`FILTER` is a hard constraint** — `.label == 7` returns only sevens no matter what the query
  looks like ("the sevens most similar to this three" is a perfectly good question);
- **exact vs approximate** — toggle `TRUTH`-style exact search on the same key: HNSW error goes
  away, quantization error stays;
- **`VINFO` + `MEMORY USAGE`** — quant type, dimension, size, HNSW max level, and what the copy
  actually costs, per key, in the header.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

One `redis:8.10.3` container (vector sets ship in Redis 8.0+), on host port `16461`.

## 2. Run

Needs the .NET 10 SDK. The first run downloads the MNIST test set (~1.6MB) into `data/` and
imports 3×5,000 vectors (a couple of seconds); restarts detect the loaded corpus and skip the
import.

```bash
dotnet run                 # interactive
dotnet run -- --scripted   # hands-free tour, prints the transcript, exits non-zero on any error
```

Keys: `N` new random query · `S` cycle key (f32 → q8 → bin) · `E` exact vs HNSW · `F` cycle
`FILTER` · `Q` quit.

The scripted run asserts ≥70% top-1 label accuracy on Q8 over 20 held-out queries (raw-pixel 1-NN
on MNIST comfortably clears it), 100% filter purity, and that exact-f32 agrees with itself.

## 3. Teardown

```bash
docker compose down
```
