Run nodes as a Windows service. v2.1.0 adds a first-class way to run an InferHub
node as a native Windows service — auto-start on boot, restart-on-failure recovery,
and logging to the Windows Event Log, installed with a single PowerShell script.
The interesting part is the shape: rather than bolt Windows hosting onto the core
worker, the node's DI wiring is extracted into one shared composition root
(`AddInferHubNode`) that both the cross-platform console host and a new,
Windows-only host compose. Same node, cleaner packaging; dev/console and Linux
paths are untouched.

## What's new

- **Shared composition root — `AddInferHubNode`.** The node's entire DI wiring
  (the three `ValidateOnStart` options blocks, backend selection, `INodeIdentity`,
  `CoordinatorConnection`, the hosted `Worker`, vector replica store) moves out of
  `InferHub.Node/Program.cs` into `NodeHostBuilderExtensions.AddInferHubNode(this
  IHostApplicationBuilder)`. The console `Program.cs` is now a two-liner that calls
  it. One place to register node services; the two hosts can never drift.
- **New project `InferHub.Node.WindowsService`.** A thin `Sdk.Worker` host
  (`InferHub.Node.Service.exe`) that calls `AddWindowsService()` +
  `AddInferHubNode()`, enables the Windows Event Log by default, and sets a 30s
  shutdown grace so in-flight jobs can drain on stop/reboot. It compiles on the
  Linux CI matrix too — `AddWindowsService()` no-ops off Windows.
- **Optional `Node:DataDirectory`.** New config key relocating writable node state
  (the `.inferhub-node-id` file) to e.g. `C:\ProgramData\InferHub\Node`, so a
  least-privilege service account that cannot write next to the exe still works.
  Default is unchanged (content root), so existing installs are unaffected.
- **Deployment tooling under `deploy/windows/`.** `install-service.ps1` (auto /
  delayed-auto start, restart-on-failure recovery, data-directory ACL, optional
  machine-scoped enrollment secret; idempotent — re-running updates in place),
  `uninstall-service.ps1`, `update-service.ps1`, and an end-to-end runbook
  (`deploy/windows/README.md`). No WiX/MSI — plain PowerShell + `sc.exe`.

## Compatibility

- **Nothing changes for existing usage.** `dotnet run --project src/InferHub.Node`
  behaves exactly as v2.0.0 — same startup log, same connect-retry loop, same
  `.inferhub-node-id` behaviour. The refactor is behaviour-preserving.
- **Core stayed host-agnostic.** No Windows/ASP.NET types leaked into
  `InferHub.Shared` or the node's `Configuration/`. The only new dependency is
  first-party `Microsoft.Extensions.Hosting.WindowsServices`, in the new project
  only.
- `dotnet test` green: 199 tests (a composition test locks the shared wiring; an
  identity test locks `Node:DataDirectory`).

## What's next

The Linux analog is the same host pattern with `builder.Services.AddSystemd()` and
a `.service` unit file — a trivial future add on top of the `AddInferHubNode`
extraction (a second thin host, same composition root).

---

## Release / distribution checklist (for the maintainer)

InferHub is an application, not a NuGet package — the release artifact is a GitHub
release attachment, distinct from the OllamaClient-style NuGet flow.

1. Tag `v2.1.0`, cut the GitHub release.
2. `dotnet publish src/InferHub.Node.WindowsService -c Release -r win-x64`, zip the
   publish output together with `deploy/windows/*.ps1` + `deploy/windows/README.md`,
   attach the zip to the release.
3. Flip the plan's status line (`plan/node-windows-service.md`) to
   `DONE ✓ (v2.1.0, YYYY-MM-DD)`.

## Outward announcements (drafts — post/publish with Iliya)

**Static docs site (`inferhub.devart.solutions`, separate repo):** add a
`#idocs_windows_service` section ("Running a node as a Windows service") near
quick-start, a matching `.idocs-navigation` link (mandatory — an orphan section
breaks scrollspy), a `Node:DataDirectory` row in the node-config section, and a
changelog/roadmap row for the v2.1.0 deployment milestone. Match the site's own
theme — do NOT apply the blog's `#00d4ff` inline-link rule there.

**Blog post (`blog.devart.solutions`, devart.solutions MCP connector):**
release/news format, ~400–700 words, EN primary. Angle = the design, not a
changelog dump: why a separate host instead of a one-line `AddWindowsService()`,
keeping the node cross-platform, one composition root shared by both hosts. HTML
fragment (no `<h1>`/wrappers), `<pre><code>` with angle brackets escaped, links
inline-styled `style="color:#00d4ff;text-decoration:underline;"` (confirm hex
against live theme). Must link the repo
(https://github.com/Dev-Art-Solutions/InferHub). `list_posts` first to confirm a
unique slug (suggest `inferhub-node-windows-service`), then one `create_post` with
`isVisible_*` set explicitly — default to a draft, confirm visibility with Iliya.

**Facebook (`facebook.com/DevArtSolutions`), drafted:**
> InferHub nodes can now run as a proper Windows service — auto-start on boot,
> restart-on-failure, logs in Event Viewer, installed with a single PowerShell
> script. Same node, cleaner packaging; the dev and Linux paths are untouched.
> Under the hood both hosts share one composition root, so they can't drift.
> https://github.com/Dev-Art-Solutions/InferHub

**X (`@DevArtSolutions`), drafted:**
> InferHub 2.1: run a node as a Windows service — auto-start, restart-on-failure,
> Event Viewer logging, one PowerShell install script. The node stays
> cross-platform; a separate thin host does the Windows packaging, both sharing one
> composition root. https://github.com/Dev-Art-Solutions/InferHub
