# InferHub v3.90.0 — organisations and the grants between them

Phase 130. Since 3.76 a machine joins a network on its owner's terms. But a coordinator still treated everyone the same:
every client could reach every node. When machines from several organisations share one coordinator, each organisation has
to decide who may use its machines, and that decision has to hold whatever a machine claims about itself. Now organisations
are part of the coordinator: every node and client belongs to one, grants say which organisation's clients may use whose
nodes and models, and a certified node's organisation is the one its certificate was issued for.

```bash
Organisations__Enabled=true                        # off by default
Auth__Clients__0__Organisation=lab-a               # a client's organisation; unset is Organisations:Home ("home")
Organisations__Grants__0__Owner=lab-b              # lab-b's nodes...
Organisations__Grants__0__To=lab-a                 # ...may serve lab-a's clients ("*": everyone)
Organisations__Grants__0__Models__0=qwen2.5*       # ...for these models (empty: all); Capabilities likewise
```

```http
POST /api/admin/identity/tokens   {"label": "gpu-7", "organisation": "lab-b"}
POST /api/admin/site/nodes        {"name": "gpu-7", "organisation": "lab-b"}
```

## What it does

- **The rule.** A client reaches the nodes of its own organisation, and those of an organisation that granted it the model
  and the kind of work. Grants go one way and are not transitive.
- **Enforced where every route finds its nodes.** The registry lookup that every router, queue, priority and capability
  check uses, and the model listings, see only the nodes the client's organisation may use. A model only another
  organisation holds is a `404` and absent from `/v1/models` and `/api/tags`, as if it did not exist. Chat, embeddings,
  tools, speech, images and video all go through it. Admin routes and `/api/status` see every node.
- **Trust comes from the certificate.** An enrollment token can carry an organisation; the certificate records it and
  renewal keeps it. A node that proves that certificate belongs to that organisation, whatever its own `Node:Organisation`
  says. Without a certificate a node's organisation is its own word, marked `declared` on `/api/status`, and the site
  checklist (3.89) gains a required line, `organisations-trusted`, that asks for `Identity:Require`.
- **Visible.** `/api/status` gains `organisations` (home, grants, refusals) and `nodes[].organisation` with
  `organisationSource`; `/metrics` gains `inferhub_organisation_grants` and `inferhub_organisation_denied_total`; the audit
  trail's lines gain the client's `organisation`. Names are checked at startup and when a token is created.

## Upgrading

Nothing changes unless you set `Organisations:Enabled`. With it off there are no organisations and every client reaches
every node. Nodes before 3.90 send no organisation and belong to the home organisation. No new package.

## Found while building it

- **The audit line's organisation was empty for every request.** The audit middleware runs first, before the key guards,
  and the organisation scope is begun by a middleware after them; an `AsyncLocal` set further down a pipeline never flows
  back up. Found in the live run; the audit line now takes the organisation from the resolved client, and a test drives
  the real middleware order.
- **The image and video job queue routes outside the request.** Its pump runs from whichever flow happens to call it, so
  the submitter's organisation would have been missing or wrong. Found while checking that every route goes through the
  filter; each queued job now keeps its submitter's scope and is routed under it. The test is red without the fix.

## Not established

- **Video and image jobs live.** The job pump fix is covered by a unit test with the real router; no picture or clip was
  rendered across organisations.
- **Per-organisation admins.** There is still one set of admin keys for the coordinator.
- **Billing between organisations.** Usage is metered per client, as before.

## Verification

- Suite: 2 768 passed, 63 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 1 012, Node 738, Mesh 549). New:
  `OrganisationTests` (the grant table; the registry and listings per organisation, no scope sees everything — red with the
  filter removed; through the real guards the key decides and admins see everything; the audit line through the real
  middleware order; the checklist line; off; names), `OrganisationJobTests` (the job pump), `OrganisationMeshTests` (real
  SignalR: a node enrolled with a `lab-b` token is `lab-b`, source `certificate`).
- Live, from source: certificates required, organisations on, one grant (`lab-b` → `lab-a`, `qwen2.5*`), clients alice
  (`lab-a`), bob (`lab-b`), carol (`lab-c`), and two nodes on the real Ollama enrolled with tokens for `lab-a` and `lab-b`.
  - node-b declared `Node:Organisation=lab-a` in its own config; the coordinator recorded `lab-b`, source `certificate`.
  - A token for the organisation `Lab B` was refused naming the rule.
  - bob's three chats all ran on node-b. alice's `smollm:135m` ran only on node-a (not granted by lab-b).
  - With node-a cordoned, alice's `qwen2.5:0.5b` ran on lab-b's node-b (granted) and her `smollm:135m` was a 404.
  - carol saw no models in `/v1/models`, and her chat was a 404.
  - After the audit fix, the lines said `lab-a`, `lab-b`, `lab-c`; both nodes came back on their own after the coordinator
    restarted, with their certificates and organisations.
