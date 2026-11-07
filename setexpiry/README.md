# Set-member expiration (Redis 8.12)

An interactive terminal demo of Redis 8.12's per-member set expiration
([redis/redis#15938](https://github.com/redis/redis/pull/15938)): `SADDEX`, `SEXPIRE`/`SPEXPIRE`
(`EXPIREAT` variants too), `STTL`/`SPTTL`, `SEXPIRETIME`/`SPEXPIRETIME` and `SPERSIST` — the
hash-field-TTL idea (7.4's `HEXPIRE`) brought to sets.

The scenario is a **presence board**: one set, `presence:lobby`, where each member carries its own
TTL. Someone active keeps being re-added with `SADDEX EX` ("touch"); someone who goes quiet simply
disappears — no sweeper job, no per-user shadow keys. Bots simulate the traffic on different
rhythms: the ones whose touch period is under the 12s TTL stay resident, the slower ones keep
lapsing and rejoining, which is the feature doing its job. `host` is added with a plain `SADD` and
never expires: TTL-carrying and permanent members coexist in one set.

StackExchange.Redis has **no API for this family yet**, so the whole surface is driven through the
v4 context API: [`SetExpiry.cs`](SetExpiry.cs) is the mini-library ([`Resp`]-declared commands,
`Compose` for the optional `MNX`/`MXX`/`NX`/`XX`/`GT`/`LT` tokens and `MEMBERS n ...` blocks, a
span of members as one hole, `ReadOnlyLease<long>` replies). The [`extending/`](../extending/) demo
explains that machinery at full length; this demo is it earning its keep on a genuinely new server
feature.

Things the demo surfaces, each observable on screen:

- **per-member TTLs, live** — the board polls `SMEMBERS` + `SPTTL` every 250ms and draws each
  member's countdown;
- **the reply codes** — every manual action logs the raw per-member code and what it means
  (`1` applied, `0` condition not met, `-2` no such member, `2` deleted because the expiry was
  already past);
- **conditions** — `SEXPIRE 25 GT` lengthens a shorter TTL (`[1]`) but loses to a *pinned* member
  (`[0]`: no expiry counts as infinite), and `SADDEX MNX` is **all-or-nothing across the batch**:
  one existing member vetoes the whole call, and nothing is added at all;
- **`SPERSIST`** — pins a member (TTL ∞); pinned does not mean permanent — `SREM` still works;
- **notifications, including 8.12's new subkey channels** — the event log is fed by
  `notify-keyspace-events "EsgT"`: the classic `__keyevent@0__` channels count `sadd`/`sexpire`/
  `srem`/`spersist`, and the new `T` (subkey-event) class delivers
  `__subkeyevent@0__:sexpired` with a payload that **names the member that lapsed**, not just the
  key (`<keylen>:<key>|<len>:<member>,...`);
- **`SPTTL` as the cache-opt-out case** — a read whose answer changes without a write (time
  passes), so the library declares it `CommandRetryReadOnly | CacheOptOut`: the textbook case for
  that pairing.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

8.12 is at the release-candidate stage with no official image yet, so this builds `8.12-rc2` from
the source tag (a few minutes the first time; it shares Docker's layer cache with the
[`roaring/`](../roaring/) demo, which uses the same build). The server listens on host port
`16441`, and reports itself as `8.11.x` in `INFO`, as Redis RCs do.

## 2. Run

Needs the .NET 10 SDK; the project pins the v4 alpha for the context API.

```bash
dotnet run                 # interactive
dotnet run -- --scripted   # hands-free tour, prints the transcript, exits non-zero on any error
```

Keys: `↑`/`↓` select · `A` add a guest (20s TTL) · `T` touch (`SADDEX EX 15`) · `G` lengthen
(`SEXPIRE 25 GT`) · `P` pin (`SPERSIST`) · `X` expire in 2s · `K` kick (`SREM`) · `N` the `MNX`
all-or-nothing demo · `B` pause/resume the bots · `Q` quit.

Pause the bots (`B`) and just watch: the TTL bars run down, members start `! fading`, and the
`sexpired` notifications name them as they go.

## 3. Teardown

```bash
docker compose down
```
