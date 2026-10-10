# Running an InferHub node as a Windows service

## The setup (v3.66+) — the easy way

Download `InferHub-Node-Setup-X.Y.Z-win-x64.exe` from the
[releases page](https://github.com/Dev-Art-Solutions/InferHub/releases) and run it. It installs the node
as the **InferHubNode** service (delayed automatic start, restart on failure, Event Log logging) and asks:

| Page | What it sets |
|---|---|
| Node mode | join a coordinator, or **solo** (its own local API, no hub) |
| Coordinator | `Coordinator:Url` and `Coordinator:EnrollmentSecret` (must match the hub's `Auth:NodeEnrollmentSecret`) |
| Local API (solo) | `LocalApi:Urls` and an API key — a non-loopback address needs one, or the node refuses to start |
| This node | `Node:Name`, `Node:Labels` (`gpu=3090, room=lab`), `Node:MaxConcurrency`, `Node:Vram:BudgetMiB` |
| Ollama | `Ollama:Endpoint` |
| Service account | LocalSystem (can apply updates) or the virtual account `NT SERVICE\InferHubNode` (least privilege, cannot) |
| Updates | automatically / when an admin says / report only / never — see below |

The answers go to **`C:\ProgramData\InferHub\Node\node.settings.json`**, readable by SYSTEM and
Administrators only (it holds the secret), **not** into `Program Files` — so an update replaces every
program file and keeps every setting. It layers after `appsettings.json` and before environment
variables; anything the setup does not ask (other engines, tools, retrieval) goes in the same file by
hand, as JSON. Running the setup again shows your previous answers; leave the secret empty to keep it.

**Silent install** (every parameter optional):

```powershell
InferHub-Node-Setup-3.66.0-win-x64.exe /VERYSILENT /SUPPRESSMSGBOXES `
  /Mode=mesh /CoordinatorUrl=https://hub.example:5080/ /EnrollmentSecret=<secret> `
  /NodeName=gpu-01 /Labels=gpu=3090,room=lab /MaxConcurrency=4 /VramBudgetMiB=24000 `
  /OllamaEndpoint=http://localhost:11434/ /Account=system /Updates=auto
```

`/Mode=solo /LocalApiUrls=http://0.0.0.0:5081 /LocalApiKey=<key>` for a solo node; `/UpdateSource=<url>`
points the updater at a mirror of GitHub's release list. **A silent run with none of these keeps the
current settings** — that is exactly how the node updates itself.

**Uninstall** from *Apps* (or `unins000.exe /VERYSILENT`). The settings and the node's identity stay in
`C:\ProgramData\InferHub\Node` so a reinstall comes back as the same node; the interactive uninstaller
offers to delete them.

### Updates

| The setup's answer | `Update:Check` | `Update:Auto` | `Update:AllowFromHub` | What happens |
|---|---|---|---|---|
| Automatically | true | true | true | the node installs a new release by itself, once idle (waits up to 30 min for running jobs) |
| When an admin says | true | false | true | the coordinator's console shows it under **Versions & updates**; **Update** applies it |
| Report only | true | false | false | the console shows it; you update on the box |
| Never | false | false | false | nothing is checked |

Every node can also be updated **on the box**: Start menu → *Check for InferHub Node updates*, or

```powershell
& "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" update          # asks first
& "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" update --check  # only looks
```

Releases come from `https://api.github.com/repos/Dev-Art-Solutions/InferHub/releases` every 6 hours. A
release counts once its setup **and** its `.sha256` are attached. Applying one downloads the setup to
`C:\ProgramData\InferHub\Node\updates`, checks the SHA-256, and runs it silently: it stops the service,
replaces the files and starts it again — **also when it fails**, so a bad update never leaves the node
down. The console then shows `updated 3.65.0 → 3.66.0`, or the failure with the setup's log path.

> **What the checksum is for.** It is published in the same release as the setup, so it catches a broken
> or truncated download — not a hostile release. A node that updates itself runs whatever is published
> there; if that is not a trust you want to extend, choose *report only* and update by hand.

Only a node **installed by the setup** and running as **LocalSystem** can apply an update. A node under
the virtual account, a hand-copied `dotnet publish`, or a Docker container reports the release and says
why it cannot apply it (`docker pull` is a container's updater).

**Coming from `install-service.ps1`?** Run the setup over it: same service name, same directory. Move any
edits you made to `appsettings.json` into `node.settings.json` (the setup replaces `appsettings.json`), and
remove the machine variables the script set, which would otherwise override the setup's answers:
`[Environment]::SetEnvironmentVariable('Coordinator__EnrollmentSecret', $null, 'Machine')`.

## Without the setup — the scripts

The scripts below install the `InferHub.Node.WindowsService` host
(`InferHub.Node.Service.exe`) from your own build. The service reuses the
node's exact composition root (`AddInferHubNode`), so it behaves identically to
`dotnet run --project src/InferHub.Node` — only the packaging differs. A node installed this way
**cannot apply updates by itself** (there is no setup to run); it can still report them.

> Linux equivalent: the same host pattern with `builder.Services.AddSystemd()` and a
> `.service` unit file. Same composition root, different lifetime integration.

To build the setup yourself: `./deploy/windows/installer/build-installer.ps1` (needs Inno Setup 6).

## 1. Publish

Self-contained single file (no .NET runtime required on the box):

```powershell
dotnet publish src/InferHub.Node.WindowsService -c Release -r win-x64
```

Framework-dependent (smaller; requires the .NET 10 runtime installed on the box):

```powershell
dotnet publish src/InferHub.Node.WindowsService -c Release -r win-x64 --self-contained false
```

Output lands under
`src/InferHub.Node.WindowsService/bin/Release/net10.0/win-x64/publish/`
(`InferHub.Node.Service.exe` + `appsettings.json`).

## 2. Copy to the install location

```powershell
$publish = "src/InferHub.Node.WindowsService/bin/Release/net10.0/win-x64/publish"
New-Item -ItemType Directory -Force "C:\Program Files\InferHub\Node" | Out-Null
Copy-Item "$publish\*" "C:\Program Files\InferHub\Node" -Recurse -Force
```

## 3. Configure

- Set `Coordinator:Url` in `C:\Program Files\InferHub\Node\appsettings.json` to the real
  coordinator (not `localhost`).
- Set the enrollment secret as a **machine** environment variable (never commit it):
  ```powershell
  [Environment]::SetEnvironmentVariable('Coordinator__EnrollmentSecret','<secret>','Machine')
  ```
  It must match the coordinator's `Auth:NodeEnrollmentSecret`. (`install-service.ps1` can
  set this for you via `-EnrollmentSecret`.)

## 4. Install

Run **elevated** (Administrator):

```powershell
./install-service.ps1 -BinaryPath "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" -DelayedStart
```

The script:

- creates the writable data directory (default `C:\ProgramData\InferHub\Node`) and grants
  the service account Modify access — the node identity file (`.inferhub-node-id`) lives
  here, so it survives under a least-privilege account (`Node:DataDirectory`);
- registers the service with automatic (or delayed-auto) start;
- configures restart-on-failure recovery (restart after 5s, 5s, then 30s);
- sets `Node__DataDirectory` (and, if passed, `Coordinator__EnrollmentSecret`) as machine
  env vars;
- starts the service and prints its status.

Re-running `install-service.ps1` updates an existing service instead of failing.

## 5. Verify

- `services.msc` → the service is **Running**.
- **Event Viewer** → Windows Logs → **Application** → startup line from `InferHub Node`.
- The node appears on the coordinator's `/api/nodes` (and the admin console).

## Update / uninstall

```powershell
# Update in place with fresh publish output (keeps your appsettings.json):
./update-service.ps1 -PublishDirectory "$publish"

# Remove the service (leaves the data directory / node id in place):
./uninstall-service.ps1
```

## Least-privilege note

Default `LocalSystem` is simplest. For tighter security, run under a virtual account and
rely on the data directory for writable state:

```powershell
./install-service.ps1 -BinaryPath "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" `
  -Account "NT SERVICE\InferHubNode" -DelayedStart
```

The node only makes outbound HTTP calls — SignalR to the coordinator and local Ollama
(`http://localhost:11434`). It needs no inbound ports and no GPU access of its own.

> **An alternative on Windows since v3.7:** if you already run Docker Desktop, the bundled image
> (`ghcr.io/dev-art-solutions/inferhub-node:ollama`) carries its own Ollama and reaches the card
> through WSL2 with `--gpus all` — no install, no service, one container. This service host is
> still the leaner path for a dedicated GPU box, and the one that does not depend on WSL2. Note
> that under WSL2 there are no `/dev/nvidia*` device nodes; InferHub detects the GPU by loading
> the driver rather than by looking for them, so a passed-through card is found either way.

## The Ollama supervisor and service privileges (v3.4+)

If you turn on `Ollama:Supervisor:Enabled`, the node restarts its local Ollama when it stops
answering. On a box where Ollama is installed **as a service**, that means calling
`sc.exe stop Ollama` / `sc.exe start Ollama` — and **this is exactly the deployment where the
account matters**, because a node service running under a restricted account cannot control a
machine-wide one.

The failure is reported as a single line naming the privilege and the service, not a stack
trace, so it is diagnosable from the Event Log:

```
Cannot start Ollama (Service 'Ollama'): the account this node runs as is not allowed to
control the service (…). Grant this account the right to control the service, or run the node
under one that has it — see deploy/windows/README.md.
```

Your options, in the order most people should try them:

1. **Run the node service as `LocalSystem`** (the installer's default). It can control the
   `Ollama` service; nothing further is needed.
2. **Grant the virtual account rights on the `Ollama` service specifically** — narrower than
   giving it administrative rights on the box. Read the current descriptor, add a start/stop
   ACE for your account, and write it back:

   ```powershell
   sc.exe sdshow Ollama          # copy the existing SDDL first
   # append an ACE granting RP (start), WP (stop) and LC/CC (query) to your account's SID:
   sc.exe sdset Ollama "D:(A;;CCLCSWRPWPDTLOCRRC;;;<SID>)(…existing ACEs…)S:(…)"
   ```

   Get the SID with `(New-Object System.Security.Principal.NTAccount('NT SERVICE\InferHubNode')).Translate([System.Security.Principal.SecurityIdentifier]).Value`.
   **Paste the existing descriptor back in** — `sdset` replaces it wholesale, and dropping the
   ACEs that were there is how the `Ollama` service becomes uncontrollable by anyone.
3. **Leave the supervisor off on this node.** It is off by default, and a fleet that installs
   and repairs Ollama by policy does not need it.

Where Ollama is installed as a **binary** rather than a service (the per-user Windows
installer puts it in `%LOCALAPPDATA%\Programs\Ollama`), the node spawns `ollama serve` itself
and needs only execute rights on that path — but note that a per-user install may not be
visible to the service account at all. Point `Ollama:Supervisor:ExecutablePath` at it
explicitly, or install Ollama machine-wide.

`Ollama:Supervisor:AutoInstall` almost certainly needs elevation too; on a managed Windows
fleet it is usually the wrong tool, and installing Ollama through your normal software
distribution is the right one.
