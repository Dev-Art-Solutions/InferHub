# InferHub 3.15.1 — the first buildable tag of phase 47

**Use this rather than `v3.15.0`.** The code is the same phase — async image jobs, per-step
progress, cooperative cancel — and the full notes are in
[`v3.15.0`](.claude/release-notes-v3.15.0.md), which still describe it accurately.

## Why there is a patch with no code change

`v3.15.0` was tagged, pushed, and **no workflow run was ever created for it**. Not a failing run —
no run at all: the last run on the repository was for the *previous* commit, three pushes later,
with nothing queued, waiting or in progress, and `ghcr.io/dev-art-solutions/inferhub-coordinator`
answering `404` for `3.15.0` while `3.14.1` still pulled fine. So `v3.15.0` exists as a git tag and
as nothing else, exactly as `v3.0.0` does.

This release exists because the fix for that had to be *in* a tag to be usable.

## What actually changed

**`docker-publish.yml` gained a `workflow_dispatch` trigger**, and that is the whole diff.

With only a `push` trigger there was no fallback at all: `gh workflow run` answers `422 Workflow
does not have 'workflow_dispatch' trigger`, and deleting and re-pushing the tag produced no event
either — so a tag whose push event was dropped could not be built by any means. Now it can:

```
gh workflow run docker-publish.yml --ref v3.15.1
```

**Dispatch it against the tag, not a branch.** The `type=semver` patterns read the version out of
`github.ref`; run against `main` they produce no version tag and the build would push `latest`
alone, which is a worse outcome than the failure it was meant to fix.

## Everything else is unchanged from 3.15.0

Same async job surface, same five routes, same store, same statuses. `dotnet test` green at 1102
passed / 46 skipped. Zero new `PackageReference`; `InferHub.Shared.csproj` still empty. Nothing to do
to upgrade from 3.14.

## If the images are still missing after this

Then the trigger was never the problem and the block is above the repository — an org-level Actions
spending limit is the overwhelmingly likely cause, and it needs an org admin to check, because
`gh api orgs/…/actions/permissions` and the billing endpoint both require `admin:org`. The symptom
is exact and worth recognising on sight: **pushes are accepted, no run is created, and nothing is
reported anywhere a non-admin can see it.**
