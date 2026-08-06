# InferHub 3.15.1 — a hand trigger on the publish workflow

**The release is [`v3.15.0`](.claude/release-notes-v3.15.0.md)** — that is the phase, and its images
are published. This patch adds one thing to CI and changes no product code.

## What changed

`docker-publish.yml` gained a `workflow_dispatch` trigger. That is the whole diff.

Until now the workflow had only a `push` trigger, so there was no way to build a tag whose push event
did not arrive: `gh workflow run` answers `422 Workflow does not have 'workflow_dispatch' trigger`,
and deleting and re-pushing a tag is not a reliable substitute. Now a tag can be built on demand:

```
gh workflow run docker-publish.yml --ref v3.15.1
```

**Dispatch it against the tag, not a branch.** The `type=semver` patterns read the version out of
`github.ref`; run against `main` they produce no version tag and the build would push `latest`
alone.

## Why it was added, and what that episode actually was

While releasing 3.15.0 it looked for a long while as though GitHub Actions had stopped working
entirely: three pushes produced no visible run, `queued`/`waiting`/`in_progress` all reported zero,
and an anonymous GHCR check answered `404` for `3.15.0` while `3.14.1` pulled fine.

**That reading was wrong, and the way it was wrong is worth keeping.** Actions was running; its
*reporting* was not. The tag push event was delivered **hours late**, and once it was, the API went
on describing the run as `queued` long after it had finished — the published
`inferhub-coordinator:3.15.0` carries `org.opencontainers.image.revision =
2829d3ad675647d8d7b5cc7f9ba3e3adf3559e34`, the phase-47 commit, built at `19:53:09Z`, while
`gh run view` still said `queued` and listed no jobs at all.

The lesson is the one this repository already had, pointed at CI instead of at code: **ask the
artifact, not the dashboard.** A registry manifest and an image label are facts; a run status is a
report, and a report can be stale for hours. `gh run watch --exit-status` disagreed with
`gh run view` on the same run id in the same minute.

The trigger is still worth having — a tag whose event is dropped or delayed by hours is a tag you
want to be able to build now rather than by re-pushing and waiting again.
