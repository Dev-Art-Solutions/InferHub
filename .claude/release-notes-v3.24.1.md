# InferHub v3.24.1 — an incomplete write is not a picture that lives forever

v3.24.0's whole argument is that a durable image lives for `Images:Jobs:RetentionSeconds` **and not
one second longer**. Pulling the published image and restarting it with a seeded archive found the
one path where that was not true.

`FileImageJobArchive` writes temp-file-then-move, which is this project's usual discipline — a record
torn by a crash mid-write is a job whose state nobody can read. What it did not do was clean up after
a write that **failed**. A `{id}.{n}.bin.tmp` left behind is:

- invisible to every route (nothing enumerates `.tmp`),
- reached by no sweep (the sweep walks *records*, and this file has none), and therefore
- **permanent** — somebody's generated image, on a disk, past every window that was supposed to
  bound it.

The likely cause is not exotic. It is the disk filling up *during* the write, which is the ordinary
failure mode of writing pictures to one; the archive catches that exception on purpose, because a
full disk must cost the archive rather than the render. It was catching it and leaving the file.

**The fix is two places, because there are two cases:**

- `WriteAtomic` deletes the temp file in its `catch` and rethrows — the case where this process
  survives its own failure.
- `FileImageJobArchive.Load` deletes every stray `.tmp` in the directory — the case where it did not.
  Load is the only moment at which "left over from a previous process" is knowable, and it already
  runs before the hub serves anything.

`ImageJobDurabilityTests.AnIncompleteWriteLeftByAPreviousProcessIsNotAPictureThatLivesForever`
seeds exactly the shape a crash leaves and asserts both halves: the orphan is gone, and the real
job beside it is untouched — which is the part that would be easy to break while fixing this.

## Everything else is v3.24.0

No API change, no config change, no behaviour change with `Persistence=none` (the default), and the
other four images are unaffected. `dotnet test InferHub.sln`: **1 276 passed, 48 skipped, 1 failed**
— and the failure is worth naming rather than rounding off.

`ToolRuntimeTests.ARequestThatOverrunsIsKilledAndReportedAsAFailedJob` went red on this machine
while the D7 containers were running. It asserts that after a wedged request kills its worker, the
**next** request succeeds — against a manifest whose `requestTimeoutSeconds` is **1**, which means
spawning a fresh child process and completing a round trip inside one second. It **reproduces on the
v3.24.0 tag with these changes stashed**, it passed on the same box an hour earlier, and CI's own
full run on the release commit was green. So: a load-sensitive budget in a test fixture, unrelated to
this release, and left alone here rather than loosened inside a patch that is about something else.

## Why this is a patch and not a note in the next release

The claim it protects is a *retention* claim, and a retention claim with a hole in it is worse than
no claim, because the docs are what somebody relies on when deciding whether to turn the key on.
Everything else v3.24.0 verified on the published image passed — see that release's notes for the
table.
