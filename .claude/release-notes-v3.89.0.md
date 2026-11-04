# InferHub v3.89.0 — a closed site, checked and planned

Phase 129. Releases 3.82 to 3.88 each added one piece of a site that keeps its models and its traffic inside: sealed nodes,
the coordinator's relay, supplied models, a certificate per node, an audit trail, priority classes, firewall rules. Each is a
switch of its own, and nothing told an operator whether they were all in place, or how to add a node to such a site. Now the
coordinator does both: a checklist of what a closed site needs, naming the nodes that fail each line and the key that fixes
it, and a plan for each new node.

```http
GET  /api/admin/site                              the checklist and every node's state
POST /api/admin/site/nodes  {"name": "gpu-03"}    a plan for one node, with a one-time token
```

Or open `/site.html` on the coordinator.

## What it does

- **The checklist.** Eight required lines: an admin key; node certificates on (`Identity:Enabled`) and required
  (`Identity:Require`); the relay on; no cloud provider and no `Fallback:`; and every connected node sealed, holding an
  active certificate, and with its own egress check closed (3.88). Four recommended lines: supplied models only
  (`Relay:Supply:Only`), the audit trail, priority classes, and keys required on loopback. `ready` means every required
  line holds; a recommended line never blocks it. A failing line names the nodes that fail it and the key that fixes it.
  It reads; it changes nothing.
- **A plan per node.** One call creates a one-time enrollment token labelled with the node's name, and returns the node's
  settings in three shapes made from the same dictionary: the Windows setup's silent command line, a `docker run`, and
  environment lines for a Linux service. It ends with what to do next: the firewall rules on the node, then the checklist
  again. The coordinator URL defaults to the address the admin reached the coordinator on. It needs `Identity:Enabled`.
- **The Windows setup takes the plan.** Two silent parameters, `/EnrollmentToken=` and `/Sealed=yes|no`, written to the
  node's settings only when given, so an update keeps them. This also closes 3.87's open item: the setup can now carry a
  token.
- **The installed service prints its firewall rules.** 3.88's `firewall` verb had only reached the console host;
  `InferHub.Node.Service.exe firewall` now works too, reading the setup's settings file.

## Upgrading

Nothing changes for a coordinator that does not call the new routes. No new package, no new setting.

## Not established

- **A silent install with the new parameters.** The setup was compiled (Inno Setup 6) and the exact lines it now writes were
  fed to the service's `configure` verb, which wrote them to `node.settings.json`. The setup itself was not installed on this
  machine.
- **The page in a browser.** `/site.html` is served; it was not clicked.
- **A site that reads `ready`.** This machine has internet, so its node's egress line failed, as it should.

## Verification

- Suite: 2 759 passed, 63 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 1 004, Node 738, Mesh 548). New: `SiteTests` (7, Coordinator — the default coordinator's lines and their keys; a closed coordinator
  with one good node is ready; a failing node named in each line; a cloud fallback fails `no-cloud`; the plan's three shapes
  agree; through HTTP a planned token really enrolls, a bad name is a 400, no node certificates is a 409).
- Live, from source: a coordinator set up as a closed site (certificates required, relay, supply only, audit, priority,
  keys on loopback). With no node, the checklist said 5 of 8 required lines. A plan for `gpu-site-1`; a node started with
  exactly the plan's environment lines enrolled with its token, registered sealed and certified, and answered a chat. The
  checklist then said 7 of 8: the one failing line was `nodes-egress`, naming `gpu-site-1`, because this machine can reach
  the internet.
