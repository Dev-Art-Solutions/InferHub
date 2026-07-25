# inferhub-migrate

Copies a populated InferHub vector store from one provider to another — `local` ↔ `postgres` ↔
`qdrant`, any pair, either direction.

Every release since the vector store became pluggable (v2.2) carried the same confession: switching
providers on a populated deployment meant re-ingesting from the original documents. That is awkward
advice from a system that deliberately **does not keep** your original documents — chunk text, a
content hash and metadata, not the file. With a third backend on the table the caveat stopped being
tolerable, so v3.3 deletes it.

## Running it

```bash
# Dry run first: what would be copied, and where. Writes nothing.
dotnet run --project tools/InferHub.Migrate -- \
  --from local:./data/vectors --to qdrant:http://localhost:6333 --dry-run

# Then for real.
dotnet run --project tools/InferHub.Migrate -- \
  --from local:./data/vectors --to qdrant:http://localhost:6333
```

Or publish it once and run the binary:

```bash
dotnet publish tools/InferHub.Migrate -c Release -o ./migrate
./migrate/inferhub-migrate --from ./appsettings.json --to qdrant:https://qdrant.internal:6333 --to-key "$QDRANT_KEY"
```

## Specifying each side

`--from` and `--to` each take one of two forms.

**Provider shorthand**, for the common case:

```
local:./data/vectors
postgres:Host=localhost;Database=inferhub;Username=inferhub;Password=inferhub
postgres://user:pw@host:5432/inferhub
qdrant:http://localhost:6333
```

**A path to a JSON config file** holding a `VectorStore` section — a coordinator's own
`appsettings.json` will do, and is the honest way to migrate with the exact settings the hub uses
(collection prefix, schema, table prefix, timeouts). `Enabled` is forced on for that side regardless
of what the file says.

> A secret on a command line lands in your shell history. When a connection string or an API key is
> involved, prefer the config-file form; `--from-key` / `--to-key` exist for the Qdrant key when you
> would rather not write a file.

## Options

| Flag | Purpose |
|---|---|
| `--from` / `--to` | Provider shorthand or JSON config path. **Required.** |
| `--collection <name>` | Copy just this one collection (default: all of them). |
| `--dry-run` | Report the plan and write nothing. |
| `--batch-size <n>` | Records read per page (default 256). |
| `--parallel <n>` | Concurrent upserts into the target (default 4). |
| `--from-key` / `--to-key` | Qdrant API key for that side. |

Exit codes: `0` clean, `1` at least one collection skipped or short (the table says which), `2` bad
arguments, `130` cancelled.

## What it does, and what it deliberately does not

- **Creates each collection on the target with the same dimension and distance**, then streams the
  records across in pages. A target collection that already exists with a *different* dimension or
  distance is **skipped with a reason** — upserting 768-float vectors into a 384 collection fails per
  record, and copying into a different distance would succeed and silently rank differently.
- **Re-running is safe.** Ids are the caller's own and chunk ids are deterministic (v2.5), so a second
  run overwrites rather than duplicating. An interrupted run is resumed by running it again.
- **It never deletes.** A record in the target that is not in the source is left alone. A migration
  tool that removes data nobody asked it to remove is a worse failure than one that leaves a stale
  record behind. If you want an exact mirror, drop the target collection first.
- **It checks the target's own count.** "The upserts returned" is not the same claim as "the data is
  there", so the summary reports what the target says it holds and exits non-zero if that is short.
- **It is not in the images.** Like the eval harness, this is a standalone console tool. Moving data
  between stores is an operator's deliberate action, never something a running coordinator should do
  to itself — a hub that migrated itself would be a second write path and a second truth.
- **It is not a backup tool.** It copies a live store into another live store. For a point-in-time
  copy, use the target engine's own backup (a Postgres dump, a Qdrant snapshot, a tarball of the
  local data directory).

### One thing to expect: cosine collections come out of Qdrant normalised

Qdrant stores the **unit-normalised** vector in a `Cosine` collection (under `Dot` and `Euclid` it
stores exactly what you sent). So migrating a cosine collection *out of* Qdrant gives you vectors
pointing the same way with length 1 — `[0.1, 0.9, 0]` arrives as `[0.1104…, 0.9938…, 0]`.

This is safe and does not need working around: cosine similarity is scale-invariant, so the target
returns **the same ids in the same order with the same scores**. It is documented here because it is
surprising, and because someone diffing raw floats across a migration would otherwise reasonably
conclude the copy was broken. It isn't.

Writes go one record at a time because that is what `IVectorStore` offers; `--parallel` hides the
round trip rather than adding a batch method three providers would carry for one caller. On a local
network expect roughly a few thousand records a second into Qdrant or Postgres — run it from a host
near the target, not across the internet.

## Migrating *into* Qdrant is also how a v3.1 collection gains hybrid search

A collection created on v3.1 is dense-only: it keeps answering vector queries forever, but its
keyword search stays coarse because there is no sparse vector to search. Migrating it (into the same
Qdrant under a different prefix, or into a fresh one) re-creates it in the current v3.2+ hybrid
shape — named dense vector plus IDF sparse vector — so server-side fusion starts working, without
re-ingesting a single document.
