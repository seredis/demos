# Typical output

```
Connected to localhost:16421 (RESP3); sample:a="ohmytext", sample:b="mynewtext"

== Act 1: the ad-hoc call (ExecuteResp: works on 3.x too) ==
   db.ExecuteRespAsync("SUBSTR", [key, 0, 3]) -> ohmy

== Act 2: the same call, interpolated (db.Context.SendAsync<T>($"...")) ==
   db.Context.SendAsync<RedisValue>($"SUBSTR {keyA} {0} {3}") -> ohmy

== Act 3: a surface of your own (db.StrMatch().SubstrAsync(...)) ==
   db.StrMatch().SubstrAsync(keyA, 0, 3) -> ohmy
   db.StrMatch().LongestCommonAsync(keyA, keyB) -> mytext

== Act 4: keys are keys: WithKeyPrefix rewrites them - IF the library declared them ==
   plain db.StringGetAsync("tenant:42:doc") -> redis in action
   tenant.StrMatch().SubstrAsync("doc", 0, 4)     [key: RedisKey] -> "redis"
   tenant.StrMatch().SubstrOopsAsync("doc", 0, 4) [key as string] -> ""
   The broken form sent the key as a VALUE, so the prefix was not applied: the server was
   asked for bare 'doc', a key that does not exist (SUBSTR answers it with an empty string).
   On a cluster it would also have no slot, and with a client cache no invalidation - but on
   a single plain server the two forms behave identically, which is exactly why the bug
   survives testing. If your library has one test, make it a prefixed-context test.

== Act 5: arguments decided at run time: Compose, and a span as one hole ==
   MatchesAsync(a, b)                     -> len=6, matches=[a[4..7]=b[5..8] (4), a[2..3]=b[0..1] (2)]
   MatchesAsync(a, b, minMatchLength: 4)  -> len=6, matches=[a[4..7]=b[5..8] (4)]
   CountExistingAsync([a, b, missing]) -> 2

== Act 6: the reader: one handler, both protocols ==
   RESP3 wire shape: Map    ->  len=6, matches=[a[4..7]=b[5..8] (4)]
   RESP2 wire shape: Array  ->  len=6, matches=[a[4..7]=b[5..8] (4)]

== Act 7: the retry category: declared read-only, and therefore cacheable ==
   5 SubstrAsync reads -> the server ran SUBSTR 1 time(s)  (client cache: 1 entries)
   write to the key from elsewhere, read again -> the server ran SUBSTR 1 more time(s)
   Without the declared category, every read would be a round trip - and none would be
   replayed after a reconnect. A read whose answer changes without a write to its keys
   should add CommandFlags.NoClientCache alongside the category.

== Act 8: the closing credit: lib-name ==
   CLIENT INFO -> lib-name=SE.Redis-StrMatch

All acts agreed with their oracles.
```
