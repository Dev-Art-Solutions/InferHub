# InferHub v3.48.0 — bubblewrap sandboxing for tool workers, the deferred half of phase-41 D7

Since phase 41, a tool worker has run as a plain child process with the node's own filesystem and
network — process isolation, stated plainly rather than implied safer (D7), with "a container per
tool, seccomp, a user namespace" named as deferred. This release implements the first of those:
an optional, per-manifest `bwrap` sandbox.

## What's new

```jsonc
{
  "id": "my-tool",
  "command": ["/opt/inferhub/venv/bin/python", "-u", "/opt/inferhub/tools/worker.py"],
  "sandbox": { "mode": "bubblewrap", "network": false }
}
```

A worker under `sandbox.mode: bubblewrap` gets read-only access to its own venv/script tree
(derived from its own `command`/`workdir` — a venv's `pyvenv.cfg` is detected so the whole venv is
reachable) plus the base OS, read-**write** access to exactly `Tools:ScratchDirectory` and nothing
else, and no network namespace at all unless `network: true` is set.

**Absent `sandbox` is `ToolSandboxMode.None` — every manifest shipped before this release, unedited,
behaves byte-identically.** None of `whisper.json`/`piper.json`/`rerank.json`/`diffusion.json` were
changed; the mechanism is proven, the shipped tools are not sandboxed.

**Linux-only, and there is no unsandboxed fallback.** A manifest naming `bubblewrap` on Windows or
macOS is refused at load, by name, the same "loaded, logged, skipped" shape phase-79's platform-keyed
`command` already uses — silently running unsandboxed what an operator explicitly asked to sandbox is
the one failure mode this exists to make impossible.

**Zero new `PackageReference`.** `bwrap` is `apt-get install bubblewrap` in `Dockerfile.tools` and
`Dockerfile.diffusion`, the same category of dependency as `ffmpeg`/`curl` (rule 5). The node builds
`bwrap ... -- <argv>` through `ProcessStartInfo.ArgumentList`, same as it always has — no shell, ever.

**Docker-socket wrapping was considered and rejected.** Mounting `docker.sock` into the node so it
could `docker run` each tool would trade "an untrusted tool has the node's filesystem" for "an
untrusted tool has the *host's* filesystem" — the socket is root-equivalent. `bwrap` needs no daemon
and no socket.

**Running a sandboxed manifest needs two container capabilities Docker does not grant by default:**

```bash
docker run --cap-add SYS_ADMIN --cap-add NET_ADMIN ... ghcr.io/dev-art-solutions/inferhub-node:tools
```

`bwrap` needs `CAP_SYS_ADMIN` to build its own mount namespace and `CAP_NET_ADMIN` for
`--unshare-net`'s loopback setup. Without them a sandboxed tool fails to start and the log names
`bwrap`'s own "Operation not permitted" — never a silent unsandboxed fallback.

## Three bugs found by actually running this, not by reading the bwrap manual

1. **`/bin`, `/lib`, `/lib64`, `/sbin` are symlinks into `/usr`** on every merged-usr distribution
   (Debian/Ubuntu since ~2017, which is what this project's own images are built from).
   `--ro-bind /bin /bin` against a symlink does not recreate the symlink in bwrap's new root, so the
   dynamic loader came back `ENOENT` even with `/usr` itself bound and readable. Fixed with
   `--symlink <resolved target> /bin` (and the other three) instead of `--ro-bind`.
2. **Bind order matters.** A scratch directory under `/tmp` (every test fixture in this suite uses
   one) was shadowed by a later `--tmpfs /tmp`. The read-write scratch bind is now added last.
3. **`--die-with-parent` is NOT in the shipped argv — a deviation from the original plan, found by
   running a real child under it, not by design.** Bubblewrap implements it with
   `PR_SET_PDEATHSIG`, which Linux ties to the specific OS *thread* that called `fork()`, not the
   process. `System.Diagnostics.Process.Start` can fork from a .NET thread-pool thread that gets
   recycled moments later, and when it does, the kernel SIGKILLs the sandboxed worker before it can
   even send `hello` — reproduced **reliably**, not intermittently, in this project's own Mesh test
   suite run for real inside a Linux container. Bubblewrap's own documented answer for a
   multi-threaded parent is `--sync-fd` (hold a pipe open; bwrap exits on its EOF), which has no
   portable path through `ProcessStartInfo` without native interop this project does not otherwise
   carry — the same bar rule 5 sets for a package, applied to a P/Invoke surface instead.
   **What this leaves uncovered:** a node that exits uncleanly (a crash, `kill -9`) rather than
   through its own shutdown path can leave an orphaned sandboxed worker running. The ordinary
   shutdown path (`ToolWorkerProcess.StopAsync`/`TerminateAsync`) is unaffected — it still
   `Process.Kill(entireProcessTree: true)`s the whole tree (phase-41 D6). This is a narrow,
   named gap, not a fixed one.

## Also new

- `NodeToolInfo` gains `Sandboxed` (`/api/status`, `/api/admin/tools`) — whether a running pool's
  manifest names `bubblewrap`. The console UI itself was **not** touched to render it; the field is
  there for anyone reading the API or a future console change to pick up.
- `ProcessToolRuntime` warns at startup when a pool is sandboxed with `network: false` **and**
  `Tools:AllowModelDownload=true` — a worker that unshares its network cannot honour a download the
  node otherwise told it to attempt, and the failure would otherwise surface as an opaque DNS error
  three layers down.
- The echo worker (`tools/InferHub.ToolWorker.Echo`) gained a `read` behaviour: the worker opens a
  named path **itself**, proving the OS-level sandbox rather than the existing `escape` behaviour,
  which proves the phase-41 *application-level* scratch-directory check.

## Verified

**Full regression green on Windows:** `dotnet test InferHub.sln` — 193 (Shared) + 829 (Coordinator)
+ 205 (Node) + 448 (Mesh, 1 skip — the sandbox test skips gracefully off Linux) = 1875 tests, 0
failures.

**The sandbox test run for real, inside a real Linux container, not skipped:** built
`mcr.microsoft.com/dotnet/sdk:10.0`, installed `bubblewrap` via `apt-get`, ran
`dotnet test tests/InferHub.Tests.Mesh` with `--cap-add SYS_ADMIN --cap-add NET_ADMIN` granted to the
container — 447 passed, 1 skipped (an unrelated Python-worker gate), 0 failed, including
`ASandboxedWorkerCannotReadAFileOutsideItsDeclaredBinds` exercised for real: a real
`inferhub-echo-worker` under `sandbox.mode: bubblewrap` could not read a marker file written into the
node's own working directory, and the same setup with the sandbox off could — the control that makes
the result attributable to the sandbox rather than a bad path. This same container run is also where
the three bugs above were found and fixed; the code in this release is the fixed version.

**Against the published `:tools` image, after the tag's GHCR build finished — the non-negotiable
check, run for real:**

Pulled `ghcr.io/dev-art-solutions/inferhub-node:3.48.0-tools` (digest
`sha256:d47814c4f9a174607bf318023e2c2c2b7bbd744cf918c2676cbec700f86f283c`). Confirmed
`bwrap --version` → `bubblewrap 0.9.0` is on the image. Ran the container for real
(`--cap-add SYS_ADMIN --cap-add NET_ADMIN`, `LocalApi:Enabled=true`) with two extra manifests bind-
mounted in (`sandbox.mode: bubblewrap` with `network: false` and `network: true`) and a small
self-contained worker script that opens a path or reaches a URL on request, then drove it over the
real `POST /api/tools/echo` HTTP endpoint:

- **Filesystem, outside the declared binds:** reading `/marker-outside.txt` (a file created directly
  in the container's own filesystem, unrelated to any bind) came back
  `FileNotFoundError: [Errno 2] No such file or directory` — not a permission error, an *absence*,
  because the path does not exist in the sandbox's mount namespace at all.
- **Filesystem, inside the declared bind:** the same worker reading its own script,
  `/opt/inferhub/tools/verify_worker.py`, came back with the file's real content — proving the
  sandbox is restricting, not simply failing every read.
- **Network, `sandbox.network: false`:** a request to `https://api.github.com` came back
  `URLError: [Errno -3] Temporary failure in name resolution` — `--unshare-net` is a real network
  namespace with no resolver reachable, not an application-layer block.
- **Network, `sandbox.network: true`:** the same request from the same worker script under a
  manifest naming `network: true` came back `{"reached": true, "status": 200}` — the escape hatch
  works, and the two manifests prove the field actually gates the behaviour rather than both being
  silently sandboxed the same way.
- **The honesty tie, seen live in the container's own log, unprompted:** `Tool 'verify' is sandboxed
  with network off, but Tools:AllowModelDownload is true.` — the warning added this phase fired
  exactly as designed against the real image, the first time this exact manifest combination was
  ever loaded by a running node.
- Also observed for free: `Tool runtime is on: 5 of 6 manifest(s) started` — `diffusion` correctly
  stayed unstarted (not in `Tools:Allowed`), confirming this phase's two new manifests loaded
  alongside the four shipped ones without disturbing them.
- **A fourth real finding, from the first attempt:** a verification worker script that did
  `from inferhub_worker import Worker` (the reference protocol library baked into every `:tools`
  image at `/opt/inferhub/inferhub_worker/`) failed to start under the sandbox with
  `ModuleNotFoundError: No module named 'inferhub_worker'` — because that directory is not on the
  manifest's own `command`/`workdir` paths, `ToolSandboxing`'s "derived, not guessed" binds correctly
  left it out. Not a bug: a manifest whose worker imports the reference library needs that
  directory on its own `command`/`workdir` tree (or `/opt/inferhub` bound explicitly) to use it
  sandboxed. The verification worker was rewritten self-contained (no imports outside the standard
  library) rather than changing the sandbox to guess a wider default.

## What is still not established

- **No shipped manifest runs sandboxed.** `whisper.json`/`piper.json`/`rerank.json`/`diffusion.json`
  are unedited. Their real filesystem needs — `HF_HOME` under `/data/tools/hf`, Piper voices under
  `/data/tools/voices`, both outside `Tools:ScratchDirectory` — have not been audited; turning
  `sandbox` on for any of them today would very likely break model caching until an extra read-write
  bind is added for those directories, which this release does not do.
- **Seccomp syscall filtering and UID-namespace remapping remain out of scope**, named explicitly
  rather than implied covered. A sandboxed worker still runs as the node's own uid and can make any
  syscall the kernel allows — what changed is what it can *see and reach*, not what it can *do*.
- **The unclean-exit orphan gap (`--die-with-parent` dropped)** is real and unfixed; see above.
- **The console UI was not updated** to show the new `Sandboxed` field — it is on the wire, not on
  the page.

## Site and blog copy (left for the next session — MCP connectors needed)

- `inferhub.devart.solutions` changelog row: "v3.48.0 — optional bubblewrap sandboxing for tool
  workers (bwrap, Linux-only, opt-in per manifest); no shipped manifest uses it yet."
- Blog angle: "we said tool workers were not a sandbox and meant it — now they can be, if you ask,
  and here's the .NET/bwrap threading bug we found running it for real before shipping it."
