# desktop/ — agent context

**Scope: `desktop/` and `tests/InferHub.Tests.Desktop/`.** InferHub Desktop: a control panel for one node — the one
installed on this machine (service, systemd, a console) or the app's own — and the way a node runs as a unit of its
own, without a hub. Phase 115.

> **Read the root `CLAUDE.md` first**, then `src/InferHub.Node/Control/CLAUDE.md`: everything this app knows of a
> node is the control API (112), and its sharing switch is 113.

## Related context

- The API it drives, the token, the restart contract: `src/InferHub.Node/Control/CLAUDE.md`
- Solo mode, the node's local API it rides on (37): `src/InferHub.Node/CLAUDE.md`

## Layout

```
desktop/
  Directory.Build.props        imports the root props; <Version> here is the APP's (0.1.0), not the node's
  InferHub.Desktop.Core/       package-free: DesktopSettings/AppPaths, NodeControlClient (+ the SSE reader),
                               NodeLocator, EmbeddedNode. No Avalonia type. Tested by tests/InferHub.Tests.Desktop.
  InferHub.Desktop/            Avalonia 12 (Fluent), built in code — no XAML: App (theme, tray), AppController
                               (attach/detach, the 3 s status poll, the events follow), MainWindow (the tabs)
.github/workflows/desktop-release.yml   the app's own release, on desktop/v* tags
```

## Decisions recorded here

### Phase 115 (InferHub Desktop)

**D1, load-bearing — Avalonia in one project, outside the images (rule 5's line).** `desktop/InferHub.Desktop` is
the only project in the repository that takes UI packages. It references no node project: what it knows of a node
is the control API over HTTP, so an attached node and an embedded one are the same client — and the app built
against 3.76 can drive a later node. The client logic lives in `InferHub.Desktop.Core`, which takes no package at
all and is tested without a window. *Rejected:* a web UI inside the node — 112 keeps the node's pages to two, and a
tray icon, a child process and a token on disk are a desktop app's job.

**D2 — attach first, run its own second.** On start [NodeLocator](InferHub.Desktop.Core/NodeLocator.cs) tries, in
order: the URL and token in the app's settings, then a `control.token` it can read in the app's own node data, the
Windows service's `%ProgramData%\InferHub\Node`, `Program Files\InferHub Node`, `/var/lib/inferhub-node`,
`/opt/inferhub-node` — each probed against `http://localhost:5081` (or the settings' URL). A token file it cannot read
(the service's is administrators-only) is named, and the user pastes the token; the file is never widened. Nothing
found → it offers its own node: [EmbeddedNode](InferHub.Desktop.Core/EmbeddedNode.cs) starts `node/InferHub.Node`
beside the app as a child, local API on loopback (a free port when 5081 is taken), data and `node.local.json` under
the app's data directory (`INFERHUB_DESKTOP_HOME` or LocalApplicationData/InferHub), `Coordinator__Enabled=false`
as an environment *default* — `node.local.json` is read later, so "join a network" from the Settings tab wins — and
`INFERHUB_SUPERVISOR=desktop`, so 112 D3's restart is the app's to honour: exit 75 → started again. Closing the
window keeps it in the tray (`CloseToTray`); Quit kills an embedded node's process tree. A service is never stopped
by the app.

**D3 — its own release, in this repository, on its own tags.** `desktop/vX.Y.Z` →
`desktop-release.yml`: win-x64 (zip), linux-x64 (tar.gz), macOS arm64 and x64 (an `.app` in a zip), each
self-contained with a self-contained node for the same platform in `node/`, plus `SHA256SUMS.txt`. The node's `v*`
workflows do not fire (GitHub's `*` does not match `/`), and the desktop release is created with `--latest=false`:
"latest" on this repository is the node's, and the node's self-update reads it (101 D3). Release notes come from
`desktop/release-notes/vX.Y.Z.md`. Unsigned: Windows SmartScreen and macOS Gatekeeper warn on first run, and the
release notes say how past that.

**D4 — a destructive action asks, because the node is often shared.** The app's own node drives the machine's real
Ollama — the same one other programs on the box use — so Delete model asks first. Screenshots of the window for docs
come from the app itself (`INFERHUB_DESKTOP_SCREENSHOTS=<dir>` renders each tab with `RenderTargetBitmap`), never a
screen capture: one taken during development caught another window's private content.

**What was not established.** The macOS and Linux packages are built in CI only — never run on a real desktop
before 0.1.0. The tray menu on Linux depends on the desktop environment's StatusNotifier support.

### Phase 116 (desktop/v0.2.0 — sharing hours and a daily allowance)

The Overview tab edits `Node:Sharing:Hours` (one line per window), `TimeZone` and `DailyNetworkMinutes` through
`PUT /api/control/sharing`, and shows the day's usage from `status.sharing.allowance`.
[SharingTerms](InferHub.Desktop.Core/SharingTerms.cs) only splits lines and words the summary — **the node parses
and validates the hours**, and its 400 is what the status bar shows. The editor fills once per connection, so the
3 s poll never overwrites what the owner is typing. Needs a 3.77 node: a 3.76 node answers the save with its own 400
("mode must be one of …"), and its status has no terms, so the summary reads "any hour".

### Phase 117 (desktop/v0.3.0 — models kept for this machine)

The Models tab has a **Shared** box per model (shown only when the node says `shared` — 3.78+), and the Overview
names what is kept. [SharedModels](InferHub.Desktop.Core/SharedModels.cs) turns one box into the node's lists: an
owner's `Include` list is changed in place (unticking its last name is refused — an empty `Include` means every
model), otherwise the model goes on or off `Exclude`, so a model pulled later is shared as before. Both lists are
sent each time; the node validates.
