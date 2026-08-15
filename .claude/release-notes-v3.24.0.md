# InferHub v3.24.0 — an image job can survive the hub that made it, if you say so

Until this release a deploy was indistinguishable from a lie. You submitted a render, got a job id,
the hub restarted, and the id came back `404 job_not_found` — **byte-identical to an id that never
existed**, because that is what the isolation rule requires a stranger's id to look like. The client
cannot tell "your picture is gone" from "you made that up", and the second reading is the one it
takes.

```bash
docker run … -e Images__Jobs__Persistence=file -v inferhub_data:/data …

POST /api/images/jobs                      → 202 {"id":"…"}      # ~90s of SDXL
docker compose restart coordinator                                # mid-flight
GET  /api/images/jobs/{id}                 → 200 state=failed reason=hub_restarted
GET  /api/images/jobs/{other}/content/0    → 200 image/png        # finished before the restart
```

**`Images:Jobs:Persistence` defaults to `none`, which is v3.23 byte for byte** — no directory is
created, no file is opened, nothing is listed at startup, and `ImageJobRetentionTests` passes
unchanged.

## This is rule 4's fourth recorded exception, and it was argued at the rule

The project's "no persisted state" rule has carried a paragraph about the image job registry since
v3.15, and that paragraph set the condition for this release:

> *"If a future phase wants durable jobs it is a fourth exception and must be argued **in this
> rule**, not added in the endpoint — because the moment a result survives a restart, 'where are my
> pictures kept' stops having the answer 'nowhere, for five minutes' and becomes a data-retention
> question somebody has to own."*

So the answer is now written down rather than avoided: with `file`, generated pictures are under
`Images:Jobs:DataDirectory` for `RetentionSeconds`. **This is the only one of the four exceptions
that stores user content** — the other three are vectors somebody ingested, append-only counts, and
fleet configuration — which is why it is off until an operator turns it on, and why the rest of this
release is about what durability is *not* allowed to change.

## Durability does not extend retention, and the window is applied on load

A hub that was down for an hour comes back and deletes everything past `RetentionSeconds`
**before it serves the first request**. Restarting is never a way to keep a picture longer than the
operator allowed.

The alternative — load everything and let the five-second sweeper catch up — was rejected for two
reasons. It is a window in which a week-old image is fetchable, and *a retention policy that is wrong
for five seconds is wrong*. And it puts the only enforcement of this release's central promise on a
timer that starts **after** the endpoints do, in the crash-recovery path, on a box nobody is
watching, where a resurrection looks exactly like the feature working. That is the bug nobody would
ever find.

The **byte ceiling** is re-applied on load for the same reason: an operator who lowered
`MaxRetainedBytes` while the hub was down gets the lower number honoured on the first boot rather
than on the first render.

## Nothing durable holds your prompt — which is why an interrupted job is never resumed

`ArchivedImageJob` has **no field that could hold a prompt**, a negative prompt, an uploaded picture
or a mask, and there is deliberately no flag to add one. That is `UsageRecord`'s discipline from
v2.4 pointed at a disk for the first time: *a field is an invitation.*

The consequence is the visible half of this release. A job that was `queued`, `running` or
`cancelling` comes back **`failed`, reason `hub_restarted`**, carrying:

> *the hub restarted while this job was in flight; it was not resumed, because nothing durable holds
> a prompt (a prompt is content). Submit it again.*

Re-dispatching it would have required writing down the one thing the privacy rule forbids. And even
with the request in hand it would be wrong for v3.15's reason, one level up: a silent retry spends
the GPU minutes and the ledger units **twice for one request**, which is exactly why a node
disappearing mid-job fails with `node_lost` instead of quietly starting again.

The worker's own **error** is kept, and that is not a hole in the same claim: it is the model's
sentence about a size, a licence or a busy card. `ImageRenderer` has never let a prompt echo back —
`revised_prompt` is null by policy — and this is v3.17's line about the trigger phrase again, that a
constant of the model may be recorded where the caller's words may not.

## Read-once means read-once from the disk too

Delivery, LRU eviction, the retention sweep and a failure that clears a job's images each **unlink
the file in the same operation** that drops the bytes, under the store's own lock. Otherwise
durability would quietly switch off the rule `Images:Jobs:KeepAfterRead` exists to make somebody turn
off on purpose.

**This is why the on-disk format is a file per job** rather than the append-log-and-snapshot the
affinity and profile stores use: an ops log would have to be *compacted* to reclaim a delivered
picture, so "read once" would be true of the API and false of the directory until a compaction ran.
`File.Delete` is the whole mechanism.

## No `postgres`, and the HA consequence is stated rather than mitigated

Symmetry with `Usage:Persistence` and `Fleet:Profiles:Persistence` was considered and declined. Those
store counts and configuration rows; half a gigabyte of PNGs in a `bytea` column is WAL amplification
proportional to every render, a TOAST table, and a `pg_dump` of the usage ledger's database that now
contains pictures. Symmetry is not a reason to put the wrong thing in a database.

And under `Cluster:Enabled`, image jobs are **per instance**. A promoted standby does not hold the
old primary's pictures and will answer `404` for them. v3.0's "both hubs share the same Postgres, so
there is no new source of truth" does not stretch to a local directory, and a shared filesystem is a
deployment choice this project neither requires nor tests.

## Three sentences that had become false were changed rather than left standing

- The `410` body's **"nothing was written to disk"** is now chosen from the key. Under the default it
  is still the whole truth and still the sentence people quote.
- The console's Images panel picks its line from a new `persistence` field on
  `GET /api/images/jobs`, so it cannot say "held in memory, gone on restart" over a hub configured to
  keep them.
- `Images:Jobs` in both `appsettings.json` files, the README and the site say what `file` costs, in
  the same voice the old sentence used.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Images:Jobs:Persistence` | `none` | `none` or `file`. An unrecognised value **fails startup naming the key** rather than falling back to `none`, which would silently drop every job on the next restart — the exact failure the key is turned on to prevent. |
| `Images:Jobs:DataDirectory` | `./data/images` | One `{id}.json` record and one `{id}.{n}.bin` per image. All five container images set `/data/images`, under their existing `chown app:app /data` — the container permissions trap, seventh path headed off. |

Solo mode reads the same key into the same class on the same day, so "does a standalone node keep an
image longer than a hub does" stays a question that cannot have two answers.

## Tests

`dotnet test InferHub.sln` — **1 276 passed, 48 skipped**, 0 failed.

- **`InferHub.Tests.Shared/ImageJobDurabilityTests`** (new, 12 cases) — the round trip through a
  **real directory**; retention applied on load with the files deleted; a lowered ceiling honoured on
  load; `queued`/`running` back as `hub_restarted` with an **empty queue** afterwards; delivery
  unlinking the file and a restart not bringing it back; `KeepAfterRead` surviving too; a restored
  job still visible only to its own client; an unreadable record costing that job and not the
  startup; a missing `.bin` making the job `expired` rather than handing back a truncated PNG; and
  the archived document asserted to have **no field that could hold a prompt**.
- **`InferHub.Tests.Mesh/ImagePrivacyTests.NoPromptSurvivesOnDiskWhenJobsArePersisted`** (new) — the
  harder half of that last one: a real prompt, through a real hub, into a real directory, and then
  **every byte of every file it wrote** searched for the phrase. The `.bin` files are pixels and are
  read as text on purpose, because a leak would not announce which file it was in.
- **`InferHub.Tests.Mesh/ConsoleContractTests`** — `persistence` joins the Images panel's read set,
  so a payload that stopped carrying it fails rather than leaving the console asserting the old
  sentence.
- The existing image suites are unchanged and green, which is the claim that matters most here: with
  `Persistence=none` this release is v3.23.

## Verified on the published image, and it found one real hole

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.24.0`, pulled anonymously (Gotcha 1 confirmed
again — no manual visibility flip, for the eleventh time). No GPU involved: the archive is seeded by
hand and the hub is restarted, which is exactly the failure this release is about.

| Check | Result |
|---|---|
| Default config | **No `/data/images` at all** — not created, not opened. |
| `Images__Jobs__Persistence=file` | Directory created at boot, **owned by `app`** — the container permissions trap did not fire a seventh time. |
| A finished job, after `docker restart` | `200`, `state: succeeded`, record intact; `content/0` returned the **byte-exact** payload with `X-InferHub-Image-Projection: flat`. |
| The second `GET` | `410 job_expired` carrying the **new conditional sentence**: *"the copy under Images:Jobs:DataDirectory was unlinked in the same operation."* And the `.bin` was gone from the volume while the record stayed. |
| A job that was `running` when the hub died | `200`, `state: failed`, `reason: hub_restarted`, with the "it was not resumed, because nothing durable holds a prompt" sentence. |
| A job two weeks past its window | `404`, and **both its record and its bytes were deleted on load** — D2, on the artifact rather than in a unit test. |
| `GET /api/images/jobs` | `persistence: "file"`. |
| `Images__Jobs__Persistence=postgres` | Host refuses to start: *"Images:Jobs:Persistence 'postgres' is not recognised; use 'none' or 'file'. There is deliberately no 'postgres': image bytes are not row data."* |

**The hole it found: an incomplete write is a picture that lives forever.** `WriteAtomic` is
temp-file-then-move, and a `.tmp` left behind — by a crash between the two steps, or by the disk
filling *during* the write, which is the ordinary failure mode of writing pictures to a disk — is
invisible to every route and reached by no sweep. It would outlive the retention window
permanently, which is the one thing this format may not do. A seeded `.tmp` was still on the volume
after a restart.

Fixed in **v3.24.1**: the temp file is deleted in the `catch` (this process survived) and every
stray `.tmp` is deleted at load (it did not). `ImageJobDurabilityTests` gained the case.

*The seeded-archive method is worth keeping.* Every claim above except the byte payload is about a
process that is **not running any more**, and a unit test can only assert that the code would have
done the right thing. Writing three JSON records onto the volume and restarting the container asks
the artifact.

## What was not established

- **Whether a video-sized result makes the per-job file write a latency problem.** Nothing here
  measures it. A 25 MB PNG batch is written inside the store's lock, which is deliberate (ordering:
  a write scheduled for after the lock can be overtaken by the next one and leave the disk
  describing a state the store has already left) and is fine against a job that took a card ninety
  seconds — but v3.25's video jobs produce tens of megabytes and nobody has timed it. That number is
  the verification release's.
- **No published image has been pulled and run for this release yet.** The one check it owes is small
  and specific: a job that survives a `docker restart`, and a directory that stays empty with the key
  unset.
- **fsync.** Files are written temp-then-`File.Move`, which is atomic per file; the directory is not
  fsynced, so a host that loses power mid-write may lose the most recent record. That is the same
  trade `FileAffinityStore` makes and the same one a five-minute retention window makes acceptable.

## Rules

**Rule 5 survived again.** Zero new `PackageReference` — `System.Text.Json` and `System.IO` ship in
the shared framework — and `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`. **Rule 2 holds**: the archive reports its failures through an
`Action<string, Exception>` the host passes, so nothing in the shared library needs `ILogger`, and a
full disk costs the archive rather than the render. **Rule 7 is untouched**, and was the constraint
that shaped the feature rather than a box ticked after it.
