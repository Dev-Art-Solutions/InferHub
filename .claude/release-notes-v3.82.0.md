# InferHub v3.82.0 — a sealed node: nothing outside its network but its coordinator

Phase 122. One setting for a node that must not reach the internet. `Node:Sealed=true` makes the node refuse to start
with anything that could reach outside: a cloud engine, its own API on a public address, a direct download. Once it
runs, its only way out is the coordinator. What it needs from outside (an Ollama pull, a Hugging Face file, the model
catalogue, *Check fit*), it fetches through the hub's new relay.

```bash
Node__Sealed=true        # on the node
Relay__Enabled=true      # on the coordinator: GET|HEAD /node/relay/{hf|ollama-registry|ollama-library}/…
```

## What it does

- **It refuses to start, and names every open door at once.** A `chatgpt`, `openrouter`, `anthropic` or `gemini`
  engine, or any engine at a public `BaseUrl`. An Ollama outside this network. `LocalApi:Urls` on a wildcard or a
  public address (loopback or one private address is fine). A corpus kept elsewhere. `HuggingFace:Enabled` with no
  coordinator and no local mirror. `Ollama:Supervisor:AutoInstall`. `Update:Check`, `Auto` or `AllowFromHub` from
  GitHub. `Tools:AllowModelDownload`. Any `CloudMedia` provider. The error lists all of them, so you fix the config
  in one edit.
- **Local** means loopback, private and link-local addresses, `100.64.0.0/10` (a Tailscale mesh), `fc00::/7`, a
  single-label name (a container, a LAN host), and `.local`, `.lan`, `.internal` and `.home.arpa`. A dotted public
  name counts as outside, even if it resolves to `10.x` today.
- **Every HTTP client of the node** goes through one handler. A request to a non-local host other than the
  coordinator fails before a socket opens. A request to the relay carries the node's enrollment secret.
- **Its Ollama stays home.** The Ollama and Python processes the node starts inherit `OLLAMA_NO_CLOUD=1` (Ollama's
  cloud models and web search off), `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1`. Ollama's cloud models (listed
  with a `remote_host`) are never reported to the hub, and a request for one is refused before Ollama sees it. If you
  started that Ollama yourself, the node reads its `/api/status` and logs an error until its cloud is off.
- **An Ollama pull goes through the hub.** For the length of one pull, the node opens a registry on its own
  loopback. Ollama pulls `127.0.0.1:port/library/model:tag` from it, and every request is answered from the hub's
  relay. Then the model is copied to the name you asked for, and the loopback name is deleted. The blobs are shared,
  so the copy is only a manifest. Progress shows under *Command progress* as before.
- **A Hugging Face download, the catalogue and *Check fit*** use the relay as their endpoint. The hub follows the
  CDN redirects itself and streams the body, ranges included. The node's own Hugging Face token reaches Hugging Face
  and never the CDN.
- **What downloads by itself is refused, with the way that works.** A llama.cpp router pull (llama-server fetches
  from Hugging Face itself) and a Strata install (its setup does) end in one error. For a GGUF, send a Hugging Face
  link instead: the store downloads it through the relay.
- **The hub:** `Relay` is off by default, and a sealed node asking gets a 403 naming the key. It serves three
  upstreams by name, never a URL from the request, and a path cannot move the host. Each request is logged with its
  path, never its query. The console shows a neutral `sealed` pill. `/api/status` and `/api/admin/nodes` carry
  `sealed: true`. A sealed node that connects while the relay is off gets a warning in the hub's log.
- **The app:** `Node:Sealed` is a settings key (network group), so it reaches the desktop app's settings tab with no
  app release. A restart applies it.

## Found before any C#, by a prototype

- **Ollama wants a blob from a *different host*.** A Python shim answering a blob GET with `200` failed with "no
  Location header". A redirect to the same host was followed by Ollama's client and failed the same way. Ollama 0.40
  takes a redirect only to another host. So the registry is `127.0.0.1` and the redirect goes to `localhost`, the same
  listener under a second name.
- **On Windows, Ollama dials `localhost` as `::1` only.** The first redirect to `localhost` was refused. The loopback
  registry now listens on IPv6 loopback too, on the same port.

## Upgrading

Nothing changes unless you set `Node:Sealed`. Unsealed, there is no handler, no environment change and no new check.
The hub's relay is off, and its route answers 401 or 403. A node before v3.82 reports no `sealed` field. A sealed
node needs a v3.82 coordinator with `Relay:Enabled=true` before it can download anything.

## Not established

- **A host whose firewall actually blocks the internet.** The live check ran on a box that has internet, and showed
  that the node never addressed an outside host: the hub's log has every fetch, and the node's has none. A
  firewalled run would show the same thing more strongly.
- **A container with IPv6 loopback off.** The registry then listens on 127.0.0.1 only, and the redirect to
  `localhost` depends on the container resolving it to 127.0.0.1, as Docker's `/etc/hosts` does.
- **An HA list.** The relay is the first coordinator in `Coordinator:Endpoints`. If that coordinator is a standby,
  the relay call fails.
- **A redirect inside a client's own handler**, from a local server to the outside, is not caught by the handler.
  The keys that would lead there are refused at boot.
- **An Ollama this node did not start** keeps whatever cloud setting its owner gave it. The node hides and refuses its
  cloud models, but cannot switch the cloud off for it.

## Verification

- Suite: 2 630 passed, 63 skipped (the gated ones, as before). New: `SealedModeTests` (41), `NodeRelayTests` (8),
  `SealedRelayMeshTests` (4, real Kestrel hub and upstreams).
- Live, from source, on this box. A hub with `Relay:Enabled` and a sealed node drove the real Ollama 0.40.12:
  - The node registered `sealed: true`. 24 of Ollama's 29 models were listed; the 5 cloud ones were hidden. The node
    logged the error about Ollama's cloud being on.
  - `POST /api/admin/nodes/{id}/models/smollm:135m/pull`: the manifest, 4 HEADs and 4 ranged GETs went through the
    relay (91 MB). The model landed as `smollm:135m`, the loopback name was gone, and a chat through the hub answered.
  - `glm-5.2:cloud` through the hub: `model not found` (never reported).
  - The catalogue (`source=ollama`) came through `ollama-library` (910 KB). The estimate for `qwen3:0.6b` read the
    manifest and the GGUF head through `ollama-registry`. A real Hugging Face API call and a ranged GGUF read worked
    through `hf`.
  - A sealed node with a `chatgpt` engine, `LocalApi:Urls=http://0.0.0.0:5391` and `Update:Check=true` refused to
    start, naming all three.
