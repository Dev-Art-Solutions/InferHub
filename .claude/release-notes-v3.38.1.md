# InferHub v3.38.1 — v3.38.0 could not build its own images

Published the same day as v3.38.0, for the reason v3.35.1 and v3.36.1 were: CI caught what a local
`dotnet build`/`dotnet test` run does not, because neither one builds a Docker image.

Two defects, both in the mechanical parts of moving `PostgresVectorStore` into a new project rather
than in the store itself:

- **Every image that builds the node or the coordinator failed.** `InferHub.Node.csproj` and
  `InferHub.Coordinator.csproj` gained a `ProjectReference` to the new `InferHub.Shared.Postgres`
  project, but none of the five Dockerfiles that build those two images (`InferHub.Coordinator/Dockerfile`,
  `InferHub.Node/Dockerfile`, and its `.ollama`/`.diffusion`/`.tools` variants) copied that project
  into the build context. `dotnet restore`/`publish` failed inside every one of them — caught by the
  `docker-build` CI job, not by the test suite, because a from-source `dotnet build` never notices a
  missing `COPY` line. Fixed by adding `InferHub.Shared.Postgres` to the same two-stage copy (csproj
  first for restore caching, full source second) every other project already gets.
- **A stray cross-reference in `CLAUDE.md`.** The phase-71 rule-5 amendment pointed at
  `` `InferHub.Shared/CLAUDE.md` `` — missing the `src/` prefix every other pointer in this repository
  carries — so `ContextContractTests.EveryCrossAreaPointerResolves` failed on `main`, correctly.

Both are documentation/build-plumbing mistakes; nothing about `PostgresVectorStore`'s behaviour,
`RetrievalHost`'s postgres branch, or the validator changed. No code from v3.38.0 was touched.

**This is why the D7 discipline (pull the published image and run it) exists**, and it is also why
this release still has not done it: v3.38.0 never produced a pullable image to begin with. Do that
check against `v3.38.1` before calling this phase verified in practice.
