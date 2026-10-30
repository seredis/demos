Typical output:

``` txt
> docker compose up -d --wait
[+] Running 3/3
 ✔ Network geofailoverdemo_default  Created                                                                                                                                      0.1s 
 ✔ Container redis-west             Healthy                                                                                                                                      2.8s 
 ✔ Container redis-east             Healthy                                                                                                                                      2.8s 

> docker compose ps
NAME         IMAGE       COMMAND                  SERVICE      CREATED          STATUS                    PORTS
redis-east   redis:8.2   "docker-entrypoint.s…"   redis-east   12 seconds ago   Up 12 seconds (healthy)   0.0.0.0:16401->6379/tcp, [::]:16401->6379/tcp
redis-west   redis:8.2   "docker-entrypoint.s…"   redis-west   12 seconds ago   Up 12 seconds (healthy)   0.0.0.0:16402->6379/tcp, [::]:16402->6379/tcp

> dotnet run
=== Connecting group to redis-east:16401 (weight=1.0) and redis-west:16402 (weight=0.5) ===
--- Phase 1: traffic flows to the active member (redis-east) (running traffic for 10s) ---
    traffic ok: 20 ops, active=redis-east (weight=1)
    traffic ok: 40 ops, active=redis-east (weight=1)
    traffic ok: 60 ops, active=redis-east (weight=1)
    traffic ok: 80 ops, active=redis-east (weight=1)
    traffic ok: 100 ops, active=redis-east (weight=1)

!!! Stopping the ACTIVE member's container 'redis-east' - expect a failover

--- Phase 2: traffic continues on redis-west, no exceptions (running traffic for 15s) ---
>>> ACTIVE MEMBER SWITCHED: redis-east (weight=1) -> redis-west (weight=0.5)
13:20:10 [Disconnected] redis-east
    traffic ok: 102 ops, active=redis-west (weight=0.5)
    traffic ok: 102 ops, active=redis-west (weight=0.5)
    traffic ok: 104 ops, active=redis-west (weight=0.5)
    traffic ok: 124 ops, active=redis-west (weight=0.5)
    traffic ok: 144 ops, active=redis-west (weight=0.5)
    traffic ok: 164 ops, active=redis-west (weight=0.5)
    traffic ok: 184 ops, active=redis-west (weight=0.5)
    traffic ok: 204 ops, active=redis-west (weight=0.5)

!!! Restarting 'redis-east' - expect it to reconnect, then automatic failback within ~5s

--- Phase 3: waiting for failback to redis-east (running traffic for 15s) ---
13:20:26 [Reconnected] redis-east
    traffic ok: 226 ops, active=redis-west (weight=0.5)
    traffic ok: 246 ops, active=redis-west (weight=0.5)
>>> ACTIVE MEMBER SWITCHED: redis-west (weight=0.5) -> redis-east (weight=1)
    traffic ok: 266 ops, active=redis-east (weight=1)
    traffic ok: 286 ops, active=redis-east (weight=1)
    traffic ok: 306 ops, active=redis-east (weight=1)
    traffic ok: 324 ops, active=redis-east (weight=1)
    traffic ok: 344 ops, active=redis-east (weight=1)
    traffic ok: 364 ops, active=redis-east (weight=1)
--- Phase 4: traffic flows on the restored active member (running traffic for 5s) ---
    traffic ok: 384 ops, active=redis-east (weight=1)
    traffic ok: 404 ops, active=redis-east (weight=1)
    traffic ok: 424 ops, active=redis-east (weight=1)

=== Scenario finished: 424 operations, 0 errors ===
```
