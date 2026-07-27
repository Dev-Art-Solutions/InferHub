# Running an InferHub node as a Windows service

These scripts install the `InferHub.Node.WindowsService` host
(`InferHub.Node.Service.exe`) as a native Windows service: auto-start on boot,
restart-on-failure recovery, and logging to the Windows Event Log. The service reuses the
node's exact composition root (`AddInferHubNode`), so it behaves identically to
`dotnet run --project src/InferHub.Node` — only the packaging differs. Dev/console and
Linux node paths are unchanged.

> Linux equivalent: the same host pattern with `builder.Services.AddSystemd()` and a
> `.service` unit file. Same composition root, different lifetime integration.

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
