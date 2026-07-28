# InferHub v3.5.0 — sometimes you just want the node

InferHub has had exactly one shape since it started: a coordinator somewhere always-on, and nodes on
GPU machines dialling out to it. That shape is right when you have a fleet. It is pure overhead when
you have one machine — and one machine is how most people start, and how a lot of people stay.

To put an OpenAI-compatible endpoint in front of a local Ollama you had to stand up a coordinator,
give it a URL the node could reach, invent an enrollment secret, and keep two processes alive so that
one client could talk to one backend. In 3.5 the node serves the API itself.

```python
# before — the fleet
client = OpenAI(base_url="http://hub.example:5080/v1", api_key=KEY)

# after — just the node. No coordinator, no secret, no internet.
client = OpenAI(base_url="http://localhost:5081/v1", api_key=KEY)
```

That is the entire migration, and **the point of the release is that there is no second change
hiding behind it.** Same request bodies, same responses, same streaming, same error envelope, same
headers — both the OpenAI dialect at `/v1` and the Ollama one at `/api`, blocking and streaming,
including tool calls and vision.

```jsonc
// src/InferHub.Node/appsettings.json
{
  "Coordinator": { "Enabled": false },
  "LocalApi": { "Enabled": true, "Urls": "http://localhost:5081" }
}
```

Off by default, and **zero new dependencies**: ASP.NET Core is a `FrameworkReference`, not a package,
and the node image already finalled on `aspnet`, so nothing grew. The release in fact *removes* a
package reference that became redundant.

## Solo mode is the hub with the middle deleted

The coordinator does this with an inference request:

```
HTTP → translate → [ admit → route → queue → dispatch → SignalR ] → node → executor → Ollama
                     everything in brackets needs a fleet
```

Solo mode is the same line with the bracket gone. Both ends were already shared code — the
translators have lived in `InferHub.Shared` since 2.4, and the node's executor already consumes an
Ollama-shaped job. That last part is the whole reason this release is small rather than large: the
mesh's internal protocol has always been Ollama JSON whatever dialect the client spoke, a decision
that has looked pedantic more than once and quietly paid for itself here.

The alternative — running the coordinator in-process as an "embedded hub" with a loopback self-node
— was considered and rejected. It reuses more code and inverts the project dependency, dragging a
Postgres driver and a PDF parser into an image deliberately free of both, to serve a fleet of one.

## Keeping the two from drifting

Two hosts formatting the same two dialects is exactly how a `finish_reason` quietly diverges and a
client that worked against the hub starts failing against a node. So the line is drawn precisely:
**everything that turns a result into text is shared** — the SSE frame body, the NDJSON line, the
error envelope, the error unwrapping — and the ten lines that write that text to a response are
duplicated per host, because a divergent `WriteAsync` is not a bug users hit.

`SoloParityTests` drives identical requests through a real Kestrel hub and a real Kestrel solo node
and compares what a client actually receives, normalising only the ids and timestamps that are minted
per request. Not what the handlers return — what comes down the socket. We have been bitten before by
a bug living below the layer every test stubbed.

It was verified by breaking the node's SSE terminator on purpose: four parity tests went red.

## Three things it deliberately will not do

**No retrieval.** RAG lives on the vector store in the coordinator, and pulling that into the node
would mean pulling a Postgres driver and a PDF parser with it. An `X-InferHub-Retrieve` header
against a solo node returns a clean **501** naming the limitation — and that refusal is the feature.
A developer moving a working RAG app onto one machine and getting confident, fluent, silently
**ungrounded** answers files a bug three weeks later that begins "the model got worse".

**No admin API and no console.** There is one node and you are sitting at it. Model management stays
hub-driven; in solo mode it is `ollama pull` in the terminal you already have open.

**It is not a second kind of coordinator.** A solo node serves its own clients and nothing else.

## The one place it refuses to boot

Off by default, loopback when on. If you bind it to a LAN, it **refuses to start** without API keys
unless you explicitly set `AllowAnonymous`, which it then warns about on every boot.

That is stricter than InferHub usually is about somebody else's network — a remote Qdrant with no API
key only warns. The asymmetry is intentional. There the exposure is data the operator already chose
to store, and refusing to boot would be us overruling them about their own network. Here it is
arbitrary compute on somebody's GPU, the default is safe, and the first sign of trouble is a bill or
a melted card.

This bites in a container by design: the image binds a wildcard, so a containerised solo node needs
a key.

```bash
docker run -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
           -e LocalApi__ApiKeys__0=your-key \
           -e Ollama__Endpoint=http://host.docker.internal:11434/ \
           -p 5081:8080 ghcr.io/dev-art-solutions/inferhub-node:3.5.0
```

## One key changes meaning, on purpose

`Node:MaxConcurrency` has always been *advisory* — a number the coordinator's router respects. In
solo mode nobody is respecting it, so it is enforced locally: over the cap a request waits, then gets
`503` + `Retry-After` — the same status and header as the hub's queue, so existing client retry logic
behaves identically. One key with two behaviours is normally a smell; it is right here because the
key's meaning ("this many at once is what this box can take") is unchanged and only the enforcer
moved, from the hub that is no longer there to the node that is.

## Nothing about the fleet changes

The coordinator gained no behaviour. Meshed nodes behave exactly as before. The outbound-only rule
that lets a GPU box sit behind NAT with no inbound firewall rule is untouched — what solo mode adds
is a surface for *your own* clients, which is precisely why it works with no coordinator at all.

`Coordinator:Enabled` and `LocalApi:Enabled` are independent, so a fleet node can also serve its own
API while you debug it. Both off is a startup failure naming both keys: a node that neither joins a
mesh nor serves anyone is a typo.

## Upgrading

Nothing to do. With `LocalApi:Enabled` left at its default of `false`, a v3.5.0 node behaves exactly
as v3.4.0 did — verified on a running process, not just in a test: it opens no listening socket at
all.
