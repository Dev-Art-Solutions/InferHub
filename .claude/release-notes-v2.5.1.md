# InferHub v2.5.1 — the vector store actually works in Docker now

A patch release with one fix, and it is a serious one.

## The bug

**Enabling the local vector store in a published container crashed the coordinator at startup.**
Not degraded — dead:

```
System.UnauthorizedAccessException: Access to the path '/app/data' is denied.
  at InferHub.Coordinator.Vector.LocalVectorStore..ctor(...)
```

Both images run as `USER app`, which cannot write `/app`. `VectorStore:DataDirectory` defaults to
`./data/vectors`, which inside the container resolves to `/app/data`. So the moment you set
`VectorStore__Enabled=true`, the host failed to start.

Mounting a volume did not save you either. Docker seeds a fresh named volume from the image's
contents at the mount point — **including its ownership** — and a mount point that does not exist in
the image is created **root-owned**. `-v vol:/data` therefore failed for exactly the same reason,
which means the **documented compose stack was broken too**. Nobody noticed because
`INFERHUB_VECTORS_ENABLED` defaults to `false`.

The node had the same latent fault: its `Vector:ReplicaDirectory` defaults to
`./data/vector-replicas` → `/app/data`, so a containerised node would have died the moment the
coordinator assigned it a vector replica.

This has been shipped and broken **since v2.3**, when the images first landed. It made v2.5's
headline feature — document ingestion, which is useless without a vector store — unusable in Docker,
the recommended install.

## The fix

Two lines in each Dockerfile, and both are load-bearing:

```dockerfile
RUN mkdir -p /data && chown app:app /data      # makes the volume case work
ENV VectorStore__DataDirectory=/data/vectors   # makes the bare-image case work
```

The compose stack also gains a `node_replicas` volume, so a node no longer re-pulls the whole corpus
from the hub on every restart.

## How it was found, and the lesson

By pulling the published image on a clean machine and turning the feature on. Nothing else would
have caught it: the unit tests pass, and a from-source end-to-end passes, because from source the
working directory *is* writable. The artefact users actually install was dead on arrival while every
signal we were watching stayed green.

Phase 22 left "GHCR images pulled and smoke-tested from a clean machine" as an unticked box. This is
what was behind it.

## Verified

The published-style image, pulled clean, with the store enabled: starts healthy, creates a
collection, serves the ingestion routes, persists to disk, correctly returns `500 partial` with an
honest error when no node advertises the embedding model, and rejects a scanned PDF with `422`.
Both the bare-image path and the named-volume path.

## Upgrade

`docker compose pull` — or bump your image tag to `2.5.1`. No config change is needed: the image now
declares its own writable data location. If you had explicitly set `VectorStore__DataDirectory` to
something under `/app`, move it (or just drop the setting and take the default).
