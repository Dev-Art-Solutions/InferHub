# InferHub v3.66.0 — a Windows node from one setup, and a node that keeps itself current

Until now a Windows node meant `dotnet publish`, copying files into `Program Files`, editing
`appsettings.json` there, a machine-wide environment variable for the enrollment secret, and an elevated
`install-service.ps1` — and the next update overwrote the `appsettings.json` you had edited. Nothing told
anyone that a node was three releases behind.

## What's new

**`InferHub-Node-Setup-3.66.0-win-x64.exe`** (attached to this release, ~35 MB, self-contained — no .NET
runtime needed) installs the node as the **InferHubNode** Windows service and asks how to run it:

- **Node mode** — join a coordinator (URL + enrollment secret), or **solo** (its own local API: address and
  API key; a non-loopback address without a key is refused in the wizard, because the node would refuse
  to start).
- **This node** — name, labels (`gpu=3090, room=lab`), max concurrent jobs, VRAM budget.
- **Ollama** — its endpoint.
- **Service account** — LocalSystem, or the least-privilege virtual account `NT SERVICE\InferHubNode`.
- **Updates** — automatically / when an admin says / report only / never.

The service starts delayed-automatic and restarts on failure. **The answers go to
`C:\ProgramData\InferHub\Node\node.settings.json`**, readable by SYSTEM and Administrators only (plus read
for the virtual account) — never into `Program Files`. It layers after `appsettings.json` and before
environment variables, so an update replaces every program file and keeps every setting. Running the setup
again shows the previous answers; an empty secret keeps the current one. Silent installs take the same
answers as parameters (`/Mode=`, `/CoordinatorUrl=`, `/EnrollmentSecret=`, `/NodeName=`, `/Labels=`,
`/MaxConcurrency=`, `/VramBudgetMiB=`, `/OllamaEndpoint=`, `/Account=`, `/Updates=`, `/UpdateSource=`), and
a silent run with none of them keeps every setting — which is how a node updates itself.

**Updates**, under three node flags that are all **off** in the shipped `appsettings.json` (a Docker or
dev node phones nobody):

| | `Update:Check` | `Update:Auto` | `Update:AllowFromHub` |
|---|---|---|---|
| Automatically | ✓ | ✓ | ✓ |
| When an admin says | ✓ | | ✓ |
| Report only | ✓ | | |

- **`Update:Check`** — every 6 h the node asks GitHub's release list for the newest `vX.Y.Z` above itself
  **with its setup and `.sha256` attached**, and reports it.
- **`Update:Auto`** — the node applies it by itself: waits for running jobs (up to `Update:DrainTimeout`,
  30 min, then goes anyway), downloads the setup, checks the SHA-256, and runs it silently. The setup stops
  the service, replaces it and starts it again — **also when it fails**.
- **When it is off, the other ways:** the coordinator's console has a new **Versions & updates** panel
  (version, available release, state, mode, last result; **Check** and **Update** buttons), backed by
  `POST /api/admin/nodes/{id}/update/check|apply`; and on the box, Start menu → *Check for InferHub Node
  updates*, or `InferHub.Node.Service.exe update [--check] [--yes]`, which elevates itself.
- After the restart the node reports what the update came to: `updated 3.65.99 → 3.66.0 (requested by …)`,
  or a failure naming the setup's log.
- `/api/status` carries `nodes[].update` for every v3.66 node (Docker ones too: `canApply:false` and the
  reason). The hub refuses an apply with **409 and the node's own reason** — its operator chose "never",
  it runs under the virtual account, it was not installed by the setup — and the node refuses again for
  itself if the command arrives anyway.

New workflow `windows-installer.yml` builds the setup on every tag and attaches it to the release.

The wire changes are additive: a `ReportUpdateState` hub method and a `NodeUpdate` node method. A v3.65 hub
ignores the report; a v3.65 node is shown in the panel as "a release before v3.66 — updated by hand".

## Measured — a real setup on a real box (Windows 11, against a real hub)

Hub and Ollama on the same box; two setups built from this commit, **3.65.99** (the "old" one) and
**3.66.0**, and a local server serving a release list in GitHub's shape (`/UpdateSource=`):

- **Silent install** of 3.65.99 with `/Mode=mesh /CoordinatorUrl=… /EnrollmentSecret=… /NodeName=ih101-test
  /Labels=phase=101,room=lab /MaxConcurrency=2 /Updates=request`: exit 0; service `InferHubNode`,
  `AUTO_START (DELAYED)`, `LocalSystem`, running. The node enrolled as `ih101-test` with max concurrency 2.
  The settings file held exactly the answers; `icacls` showed `Administrators:F, SYSTEM:F` only, and a
  non-elevated read was **access denied**.
- **An admin's Update**: *Check* through the hub → `available: 3.66.0`; *Update* → 202, the node went
  `applying`, dropped off and was back as **3.66.0 in ~10 s**, reporting `updated 3.65.99 → 3.66.0 (requested
  by local)`; name, labels and secret kept.
- **`Update:Auto`**: 3.65.99 reinstalled silently with only `/Updates=auto` (every other answer kept from
  the previous run). **66 s later** — the one-minute first check — the node updated itself to 3.66.0 with no
  one touching it: `requested by Update:Auto`.
- **Report only + by hand**: `update --check` named 3.66.0; `update --yes` downloaded, verified and ran the
  setup; the node came back as 3.66.0, `requested by AI on the box`. The hub refused *Update* for this node
  with **409** "has Update:AllowFromHub off: its operator updates it by hand"; an unknown node is 404.
- **A bad checksum**: a release whose `.sha256` did not match was refused — "does not match its published
  SHA-256 … it was deleted and nothing was run" — and the service kept running.
- **Virtual account**: reinstalled with `/Account=virtual`: service runs as `NT SERVICE\InferHubNode`, the
  settings file gained `NT SERVICE\InferHubNode:(R)`, the node enrolled, and reported `canApply: false` —
  "the node runs as NT SERVICE\InferHubNode, which cannot run a setup; run the service as LocalSystem, or
  update it by hand with the setup".
- **Uninstall** (silent): service and program files gone; `C:\ProgramData\InferHub\Node` kept (node id,
  settings, downloaded setups) so a reinstall is the same node.

Found and fixed on that box before the tag: `update --check` from an ordinary prompt could not read the
admin-only settings (it now elevates itself, even to check); a console that paused after starting the
setup would have held the exe the setup must replace (it no longer pauses then); the publish output
carried `appsettings.Development.json` and a dev `data/` folder (excluded from the setup).

Tests: `NodeUpdateTests` and `NodeSettingsFileTests` (Node), `NodeUpdateRegistryTests` (Coordinator),
`NodeUpdateMeshTests` (a real hub and a real node over SignalR: Update reaches the node, which downloads,
verifies and starts the setup; a node whose operator said no refuses even a command the hub sends anyway),
and `ConsoleContractTests` extended to every field the new panel reads.

## Not established

- **The wizard was not clicked through.** Every page's value was driven through its silent-install
  parameter, which feeds the same page objects, and previous answers were shown to carry over between
  runs; the pages' own *Next* validations (URL shape, labels, a non-loopback solo address without a key,
  the virtual account with automatic updates) have not been seen by a person.
- **The console's panel was not opened in a browser.** Its fields are covered by `ConsoleContractTests` and
  its buttons call the two endpoints exercised above; nobody has looked at it.
- **The published coordinator image** was not pulled and run for this release; the hub side was run from
  source at the tagged commit. The setup, which is this release's artifact, was checked as published (below).
- **Not code-signed.** SmartScreen will warn on a fresh download ("unrecognized app"). The checksum guards
  a download, not the release; a node that updates itself runs what is published.
- Only `win-x64`. No ARM64 setup.
- The coordinator does not update itself; it ships as an image.

## Addendum — the published setup, through the real GitHub release

`windows-installer.yml` attached `InferHub-Node-Setup-3.66.0-win-x64.exe` (35 282 159 bytes) and its
`.sha256` to v3.66.0 about three minutes after the tag. A node installed from the local 3.65.99 setup with
**no** `/UpdateSource` — so asking `api.github.com` — and *when an admin says*:

- *Check* through the hub → `available: 3.66.0`, `releaseUrl:
  https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.66.0`: the real API's JSON, the real asset
  names, picked by the shipped code.
- *Update* → `downloading` → `applying` → back in **14 s** as **`3.66.0+773ee9e`** — the tagged commit,
  i.e. the setup GitHub serves, downloaded, checked against its published SHA-256 and run by the node.
  `lastUpdate: updated 3.65.99 → 3.66.0`.
- *Check* again → `up-to-date`.
- Uninstalled; the box is as it was.
