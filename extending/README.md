# Extending the client (v4): your own commands

A scripted, code-first demo of what a **module library author** — someone shipping `JSON.*`, `FT.*`,
`BF.*`, or any command StackExchange.Redis has no API for — needs from the v4 surface, as a purely
**external caller**: nothing here requires any change to the client. It covers, in order:

1. the ad-hoc escape hatch, `db.ExecuteResp(Async)` (this one also works on 3.x);
2. the v4 interpolated form, `db.Context.SendAsync<T>($"SUBSTR {key} {0} {3}")` — no argument
   array, no boxing, and the string is never built: each hole is written straight into a pooled
   buffer as UTF-8 (see [Ad-hoc commands](https://seredis.dev/Execute));
3. a command surface of your own — `db.StrMatch().SubstrAsync(key, 0, 3)` — the same shape the
   client's built-in command groups use: a group struct holding a `RespContext`, an accessor on
   any `IRespKeyspaceTarget` (database, batch, transaction, prefixed context), and `[Resp]`-declared
   commands that the generator in the package checks at build time;
4. **keys are keys**: the type of each hole decides. A `RedisKey` hole is routed, prefixed and
   tracked; a key held as a `string` binds as a *value* and silently loses all three. The demo runs
   a correct and a deliberately-broken command through `db.WithKeyPrefix("tenant:42:")` and shows
   the broken one miss — behaviour that is invisible on a plain single server, which is exactly why
   it survives testing;
5. arguments decided at run time: `Compose` + `AppendFormatted` for an optional modifier
   (`MINMATCHLEN`), and a `ReadOnlySpan<RedisKey>` as a single hole for a variable number of keys;
6. **the reader API**: a reply shape only the library knows (`LCS ... IDX WITHMATCHLEN`), parsed by
   an `IRespHandler<T>` over `RespReader` — run under both RESP2 and RESP3 to show the same handler
   coping with the reply spelled as a flat array and as a map, because it tests
   `IsScalar`/`IsAggregate` and reads fields by name rather than comparing `Prefix` to a literal;
7. **the retry category**: the client has no entry for a command it does not know, so it assumes
   the worst — never replayed after a reconnect, never cached. The library declares its reads
   `CommandRetryReadOnly` (caller-wins, so it is a default, not an override), and the demo makes the
   effect visible with a client-side cache: five reads reach the server once, measured from
   `INFO commandstats`, and a write from another connection invalidates the cached reply;
8. `AddLibraryNameSuffix`: the library announces itself, and `CLIENT INFO` (sent through the ad-hoc
   API, naturally) reports `lib-name=SE.Redis-StrMatch`.

## The pretend module

Inventing a module would mean building and loading one server-side, so the demo picks on two real
commands instead — a pretend "StrMatch module", runnable against stock Redis:

- **`SUBSTR`** is genuinely unknown to the client — no API, not even a `RedisCommand` entry — so it
  exercises the unknown-command path exactly as `JSON.GET` would, and its alias `GETRANGE` provides
  an oracle to check every answer against.
- **`LCS`** has a nested, protocol-sensitive reply (`IDX` matches), which is what makes a custom
  reply handler worth demonstrating; the built-in `StringLongestCommonSubsequenceWithMatches` is
  the oracle.

[`StrMatch.cs`](StrMatch.cs) is "the library": the group, the accessor, the `[Resp]` declarations,
the commands, and the reply handler — the file to read if you are writing one of these.
[`Program.cs`](Program.cs) is the consumer, one act per bullet above. Every act checks its result
against an oracle and the run exits non-zero if any disagree.

The code deliberately leaves two `#pragma warning disable SER309` in place: the analyzer that ships
in the package flags literal command text in interpolated sends ("parsed and encoded on every
call"), which is correct advice for a library — and exactly what `StrMatch.cs` does instead with
`[Resp]` — but fine to wave off for a one-off ad-hoc call.

## 1. Setup infrastructure

Requires Docker with the compose plugin.

```bash
docker compose up -d --wait
```

This starts one standalone Redis 8.2 container, `redis-extending`, on host port `16421`
(`LCS` needs Redis 7.0+).

## 2. Run

Needs the .NET 10 SDK. The context surface (`db.Context`, `SendAsync`, `Compose`, `[Resp]`) is a
v4 feature; the project pins the v4 alpha.

```bash
dotnet run
```

Typical output is shown in [output.md](output.md).

## 3. Teardown

```bash
docker compose down
```
