# InferHub v3.104.0 — a sealed node updates through its coordinator (phase 151)

A sealed node (3.82) reaches nothing outside its network except its coordinator. That kept models in, and it also kept
updates out: GitHub is outside, so the sealed boot check refused `Update:Check`, and a closed site updated its nodes by
hand or ran its own mirror of GitHub's release list. In 3.104 the coordinator's supply (3.84) holds node releases as well
as models. A sealed node reads them through the relay it already uses for models.

## What is new

- **`POST /api/admin/supply {"release": "latest"}`** (or `"v3.104.0"`) fetches a node release from `Relay:Releases`
  (GitHub's release list by default, or a mirror of its shape). The hub keeps the Windows setup only if it matches the
  SHA-256 published beside it. `latest` is the highest release that is not a draft or a pre-release and has its setup
  attached, which is what a node's own check would pick.
- **Offline:** `POST /api/admin/supply/import {"path": "releases"}` imports a directory that holds
  `InferHub-Node-Setup-X.Y.Z-win-x64.exe` and its `.sha256`, as downloaded from a release page. A setup without its
  checksum is skipped, and the job says so.
- **`Update:Source=coordinator`** on a sealed node. The node's update check asks `{hub}/node/relay/releases`, which
  answers in GitHub's shape, and downloads the setup from the same hub. It asks the hub it is connected to now, so after
  a failover it asks the new one. The relay signs these requests with the node's secret or certificate, the same as for
  models. The node checks the setup again before it runs it. Update with **Update** in the console's *Versions &
  updates* panel (`Update:AllowFromHub`), or automatically once idle (`Update:Auto`).
- **Releases come only from the supply.** The relay never fetches GitHub's list for a node, whether or not
  `Relay:Supply:Only` is set. A sealed fleet, including nodes with `Update:Auto`, moves only to a release an admin
  supplied. A release that was not supplied is a `404` with `X-InferHub-Relay-Supply: not-supplied`, and the message
  names the call that supplies it.
- **The site plan now installs a sealed node that boots.** From 3.89 to 3.103, the Windows command from
  `POST /api/admin/site/nodes` passed `/Sealed=yes` and no update choice. The silent setup then wrote its default
  (automatic, from GitHub), and the sealed boot check refused the node it had just installed. The command now passes
  `/Updates=request /UpdateSource=coordinator`, and the plan's settings carry `Update:Source=coordinator`,
  `Update:Check` and `Update:AllowFromHub`. The setup also writes `Update:Source=coordinator` whenever it is run with
  `/Sealed=yes` and no `/UpdateSource`.
- **Visible:** a *Supply a node release* box in the console's **Supplied models** panel, `relay.supply.releases` on
  `/api/status` (`relay.supply.models` no longer counts releases), and a recommended line `updates-supplied` on the site
  checklist.

## What does not change

A hub where nobody supplies a release behaves exactly as in 3.103. A node that is not sealed reads GitHub or its own
`Update:Source` URL as before. `Update:Source=coordinator` is refused at startup on a node that is not sealed. A sealed
node older than 3.104 can use the supply with `Update:Source=http://<hub>/node/relay/releases` when the hub has a local
name. Only a node installed by the Windows setup can apply an update. A Docker node reports what is available, and you
update it from your own registry. The checksum comes from the same release as the setup, so it catches a broken
download, not a hostile release. Zero new packages.

## Checked

- `dotnet test InferHub.sln`: 2 918 passed, 70 skipped (the gated suites), 0 failed.

- Tests: `ReleaseSupplyMeshTests` runs the hub against a fake GitHub release list that has a pre-release, a draft and a
  tag without a setup. `latest` was supplied once; then the fake GitHub was stopped, and the node's own release feed and
  downloader, behind the real sealed handler, found and fetched the release from the hub. Also covered: a named release
  and an imported one, listed newest first; a corrupt setup, never supplied; refusals for a release not supplied, a
  request with no secret, and another file name under a supplied tag; the supply off. `SealedModeTests` runs the site
  plan's settings and its Windows line through the sealed boot check, and confirms that the line before this release
  fails it.
- From source, against the real GitHub release list: the hub supplied **v3.103.0** (setup 35 616 087 bytes, SHA-256
  `7d2826ed…`, matching the published `.sha256`). A sealed node built as 3.102.0 with `Update:Source=coordinator` asked
  only its hub and reported `available: 3.103.0` on `/api/status`. The hub logged that it answered from the supply. The
  whole setup downloaded through the relay hashed to the published value, and `updates-supplied` read ok.
- The Windows setup script compiled with the new `/Sealed=yes` line (Inno Setup 6).

## Not established

- **An update applied from the supply on an installed Windows service.** From source the node has no applier
  (`canApply: false`), so the download path was checked by the tests and by hashing what the relay served.
- **The setup run with the new line.** The script was compiled, not run.
- **A hub behind a reverse proxy that rewrites the host.** The download URLs in the list come from the address of the
  node's request.

## Checked on the published images

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.104.0` and `inferhub-node:3.104.0` (revision `3fe760a`), plus
`inferhub-node:3.103.0`, on one Docker network, with the hub's supply on and `Only` set:

- **The published hub supplied this release from the real GitHub list.** `{"release": "latest"}` → `v3.104.0`, the
  setup the release workflow had attached minutes before (35 627 692 bytes). The setup served through the relay hashed
  to `0bde610f…`, the value in the release's own `.sha256`.
- **A 3.103.0 sealed node, unchanged**, with `Update:Source=http://hub:8080/node/relay/releases`: after a check from the
  hub it reported `available: 3.104.0`, with the release page from the supply record.
- **A 3.104.0 sealed node** with `Update:Source=coordinator`: `up-to-date`, from the same list.
- The site checklist read `updates-supplied` ok and `nodes-sealed` ok. Both containers reported `canApply: false`,
  because a container is updated by `docker pull`.

**Not established there:** the same as above. No update was applied, because that needs a Windows service installed by
the setup.

## Published

Blog: https://blog.devart.solutions/blog/inferhub-3-104-a-sealed-node-updates-through-its-coordinator (ID 6afc3678faff475a7f9bfe33). Docs: https://inferhub.devart.solutions/#idocs_sealed_updates (site commit bd23aaa). Social copy in `.claude/social-v3.104.0.md`, not posted.
