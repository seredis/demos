# Typical output

```
Connected: localhost:16431 (v8.10.3) and localhost:16432 (v8.11.241)
(the 8.12 release candidate reports itself as 8.11.x, as Redis RCs do)

== Act 1: one workload, two servers, three encodings ==
   localhost:16431 pop:sparse-a     TYPE string  OBJECT ENCODING raw
   localhost:16432 pop:sparse-a     TYPE string  OBJECT ENCODING raw
   localhost:16432 pop:sparse-a:roar TYPE bitmap  OBJECT ENCODING bitmap-roaring

== Act 2: memory: MEMORY USAGE, same data in each encoding ==
   population       bits set     extent    8.10 string    8.12 string   8.12 roaring    ratio
   sparse-a           99,991    32.0 MB        40.0 MB        40.0 MB       409.3 KB   100.1x
   sparse-b           99,985    32.0 MB        40.0 MB        40.0 MB       409.1 KB   100.1x
   clustered          99,120    32.0 MB        40.0 MB        40.0 MB        7,557 B  5550.2x
   dense           1,000,000   122.1 KB       128.0 KB       128.0 KB        1,177 B   111.4x
   noise           4,196,025 1,024.0 KB     1,280.0 KB     1,280.0 KB     1,031.7 KB     1.2x
   Sparse and clustered populations collapse; the ~50% noise row is the honest one -
   incompressible data has no free lunch, and roaring concedes a little to overhead.

== Act 3: same commands, same answers: the encoding is transparent to clients ==
   BITCOUNT, BITPOS and GETBIT answered identically on all three targets (5 populations).

== Act 4: read performance, measured on the server (INFO commandstats, µs per call) ==
   command                               8.10 string    8.12 string   8.12 roaring
   BITCOUNT (32.0 MB extent)                   718.8          706.6           54.6
   GETBIT (random offsets)                       0.2            0.1            0.2
   BITOP AND/OR (a,b -> dest)               15,940.0       16,985.4        1,283.9
   (GETBIT: a point read pays a small container-lookup cost on roaring - sub-microsecond either way)
   BITOP OR dest: 40.0 MB (raw) from string operands; 619.6 KB (bitmap-roaring) from roaring ones.

== Act 5: write performance: the same 50,000 SETBITs into a fresh key ==
   command                               8.10 string    8.12 string   8.12 roaring
   SETBIT (50,000 fresh writes)                  0.4            0.4            0.1
   The first sparse SETBIT on a string allocates the whole 32.0 MB extent up front;
   roaring allocates as containers are touched. Encoding of the fresh roaring key: bitmap-roaring.

== Act 6: migrating a live key: flip the config, touch the key ==
   before: raw, 40.0 MB; after a read under the config: raw;
   after one write: bitmap-roaring, 7,545 B - and BITCOUNT still says 99,120.

== Act 7: the one new command: BITRANGEINTARRAY ==
   first 10 set bits of pop:sparse-a:roar: [1833, 2713, 3439, 8287, 8816, 14024, 14657, 25152, 26151, 30051]
   and on 8.10: ERR unknown command 'BITRANGEINTARRAY', with args beginning with: 'pop:sparse-a' '0' '-1' 'BIT' 'LIMIT' '10' 

All checks passed.
```
