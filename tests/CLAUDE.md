# tests/ — agent context

**Scope: `tests/`.** What the suites are for, which project holds what, and the testing discipline
this repository has arrived at over sixty releases — most of it the hard way.

> **Read the root `CLAUDE.md` first.**

## The projects, and why there are four of them

| Project | Holds | Typical run |
|---|---|---|
| `InferHub.Tests.Common` | fixtures, fakes, hosts, helpers. **Not a test project** — a library the other four reference. | — |
| `InferHub.Tests.Shared` | pure: contracts, renderers, stores, translators, the context contract | seconds |
| `InferHub.Tests.Coordinator` | endpoints, routing, admission, vector, cluster, metrics, console | seconds |
| `InferHub.Tests.Node` | backends, supervisor, tool runtime, solo mode, profiles | seconds–minutes |
| `InferHub.Tests.Mesh` | real Kestrel **and** real SignalR **and** real child processes | minutes |

```powershell
dotnet test tests/InferHub.Tests.Shared        # the edit loop
dotnet test tests/InferHub.Tests.Coordinator
dotnet test tests/InferHub.Tests.Node
dotnet test tests/InferHub.Tests.Mesh          # the slow, honest one
dotnet test InferHub.sln                       # everything, as CI does
```

**`Mesh` is what justifies the other three.** It holds the parity suites, the wire-size tests and
everything that spawns a process or opens a socket — the slow ones everybody used to pay for on
every run. Separating it means a mesh failure is *visibly* a mesh failure rather than one red line
in a run of twelve hundred.

**What the split does and does not buy.** It buys a fast edit loop and two agents in two files;
it does **not** buy build isolation, because `Common` references both hosts and every test project
references `Common`. Pretending otherwise would be a design fighting itself — see 52 D3.

## The discipline, and what each rule cost to learn

- **xUnit**, `Using Include="Xunit"` set globally. Tests rely on `InternalsVisibleTo`, so prefer
  `internal` over `public` for new helper types unless a node needs them via the shared contracts.
- **`SmokeTests` exercises the wire-up.** A new endpoint or DI registration shows up there first.

**Cross the wire, or you are testing a stub.** Nine of these lessons are in the phase notes and
every one of them shipped broken first:

- `NodeHubStreamingTests` uses a **real Kestrel host and a real `HubConnection`** because
  `StreamChunks` hung every stream for several releases while every test stubbed `IDispatcher`.
- `ImageWireSizeTests` pushes **3 MB** across a real connection because phase 41 "proved"
  attachments with a **16-byte** file and phase 42 then tore the connection down with 300 KB.
- `OllamaSupervisorTests` uses a **real closed port** and a **real accept-and-never-answer
  listener**, because a stubbed `HttpMessageHandler` can only echo the exception the test author
  already believed in — and the belief was wrong on Windows.
- `ToolSecurityTests` drives a **real child process** and asks it what environment it got.
- `QdrantVectorStoreTests` runs against a **real Qdrant**, which is how anybody found out that
  Cosine collections return the *normalised* vector.

**Guard the guard.** Every comparison suite carries a test that the comparison can still detect a
difference: `TheComparisonActuallyDetectsADifference` in the parity suites,
`EveryPathInTheReadSetIsActuallyReadByTheConsole` in `ConsoleContractTests`, and
`EveryPhaseDecisionBlockSurvivesTheSplitExactlyOnce` here. **A check that has drifted away from its
subject is worse than none, because it reads as coverage.**

**Parse it back; do not string-match it.** `PrometheusMetricsTests` reads the exposition format
with a real reader rather than asserting substrings, because substring assertions pass happily on
output no Prometheus can consume — and they would miss a decimal comma, which only appears on a
Bulgarian or German host and sinks the whole scrape.

**Assert the survival, not the response.** After anything that crosses the wire, the question is
"is the node still registered?" — not "did we get a 200". That distinction is the entire lesson of
v3.10.0.

**A zero you constructed to fill a field is not a measurement** (v3.13.1). Absence-stays-absence
has its own test per phase, and it is easiest to break in exactly the code that argues for it.

## Gated suites

Some tests need something that is not on every machine and skip cleanly when it is absent:
`PythonWorkerFactAttribute`, `OllamaSupervisorFactAttribute`, and the Postgres/Qdrant integration
suites. **Skipped is not passed** — the counts in a release note say how many were skipped, and
that number is part of the claim.

## The `heavy-mesh` collection (phase 53)

`ImageJobTests`, `ToolUploadTests` and `SoloUploadParityTests` share a collection with
parallelisation **off**, because the first asserts on queue position and a bounded wait while the
other two push tens of megabytes through a real mesh. Run together they made the queue test see a
genuinely full queue. **Loosening its timing was the alternative and would have been wrong** — it was
asserting the thing it exists to assert; what had gone missing was the machine. Put a suite here when
its cost is measured in megabytes, not when it is merely slow.
