# Social copy — v3.33.0 (phase 65, providers become routable)

**Status: unposted.** No connector — posted by hand. **No image**: the visual would be a screenshot
of a response header, and the honest picture of this release is a config file.

Blog post: <https://blog.devart.solutions/blog/inferhub-3-33-none-of-them-was-a-choice>
(ID 6a8cb78087e8766c7ff7d0ce, EN-visible / BG-hidden, one create_post, no connector outage)
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.33.0>

---

## Facebook

InferHub 3.33 is out, and it fixes something that had been true for four releases without anybody
saying it plainly.

Since 3.29 the mesh can name cloud vendors — OpenAI, OpenRouter, Anthropic, Gemini, the last two
speaking their own hand-rolled dialects. Four vendors, four releases, and **not one of them was ever
a routing target.** The dispatch path asked the router first and the upstream second, which means a
cloud provider was, by construction, the thing that happens when your own fleet cannot help.

So an ordinary sentence — "serve this model from Anthropic *while* my own boxes stay busy with local
models" — was not something the configuration could say.

`Policy` is that sentence. It is the same field `Trigger` was, with two more values: `prefer` asks
the vendor first and keeps your fleet as the backstop, `only` asks it always and takes the name away
from a node that happens to hold a model called the same thing. `Trigger` still binds and still
means what it meant, so nothing existing changes. Writing both and making them disagree **fails
startup naming both** — which vendor receives somebody's prompt is not a thing this hub settles by
precedence.

Three decisions from this one worth repeating:

**The backstop belongs to the policy, not to the error.** `prefer` may quietly fall back to your own
fleet when a vendor call fails — falling back to hardware you own is not a second disclosure of the
prompt. `only` may not, and returns a 502 saying so. Answering from different weights than the
caller asked for, silently, is the one failure that looks like a success.

**A header that can only ever narrow.** One request can steer itself with `X-InferHub-Provider`.
Name a provider and it serves from that provider *if that provider already claims the model* —
otherwise it is a 400 and nothing leaves the hub. A header can never create a route your config does
not already contain. The other value is `node`, and it is the one that matters: it refuses every
vendor for that single request, including an `only` one. That is how somebody keeps one prompt off a
vendor's servers without an operator editing anything.

**A wrong steer gets one sentence.** An unknown id, a parked provider and a real provider that maps
something else are three different mistakes and one identical answer — so a client holding an
inference key cannot enumerate your vendor configuration by probing it.

And the listing has stopped lying. Until today the model listings reported the fleet's inventory
while the chat endpoint would serve a mapped cloud model no node held — a model you could not
discover and *could* call. Both listings now include them, with no digest and no size (those are
facts about a file on a box), `chat` as the only capability, and **no vendor named anywhere**: your
configuration is not a fact every client with a key should be able to read.

Said out loud: no test here calls a live provider, deliberately — a test needing somebody's API key
is one CI cannot run and everyone learns to skip. So whether a real vendor honours a preferred route
under load is not something this release measured. That is a day of its own at the end of this
track, with one real key per provider, driven by hand.

Self-hosted, MIT, zero new dependencies for the thirteenth release running.

---

## X / Twitter (5 posts)

*Counted: 185 / 260 / 268 / 251 / 220. No backticks — X does not render them and they cost characters.*

**1/**
InferHub 3.33 is out.

Four releases spent teaching a self-hosted mesh to speak OpenAI, OpenRouter, Anthropic and Gemini.

And not one of those vendors was ever a *routing target*. 🧵

**2/**
The bug was an order of operations.

Router asked first, upstream second — so a cloud provider was by construction "the thing that happens when your fleet fails".

"Serve this from Anthropic *while* my boxes stay busy" wasn't a sentence the config could say.

**3/**
Policy is that sentence. Same field Trigger was, two more values:

• prefer — vendor first, fleet as backstop
• only — vendor always, node never serves it

Set both and disagree → startup fails naming both. Precedence can't decide whose servers see a prompt.

**4/**
The header is the part I like.

X-InferHub-Provider: <id> works only if that provider already claims the model. Otherwise 400, nothing leaves the hub.

X-InferHub-Provider: node refuses every vendor for that one request.

A steer can only ever narrow.

**5/**
And a wrong steer gets ONE sentence — unknown id, parked provider, wrong provider, same answer.

Three mistakes, one reply, so nobody enumerates your vendors by probing.

Self-hosted, MIT, zero new deps for 13 releases.

---

## Single-post X variant

*262 characters.*

InferHub 3.33: four cloud providers over four releases, and none was ever a *choice* — the router was asked first, the vendor second.

Now: a policy per model, and a header that can only narrow. X-InferHub-Provider: node keeps one prompt off a vendor entirely.
