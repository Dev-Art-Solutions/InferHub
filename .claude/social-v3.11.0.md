# Social — v3.11.0

Post manually. The angle is **not** "you can configure a fleet from one place" — everybody has that,
and nobody clicks it. The angle is the direction of trust: **the coordinator can only ever narrow a
node, and the check runs on the node.** Most systems in this space do it the other way round and
nobody says so out loud. That is the post.

Second-best hook, for the ops crowd: **a selector that names nothing matches nothing.** One line, and
everyone who has ever written a Kubernetes selector feels it.

Link to the release.

## Facebook

InferHub 3.11: configure the fleet, not the boxes.

Twenty GPU nodes has meant twenty appsettings.json files and twenty restarts. Now the coordinator writes one profile — which capabilities a node serves, which tools it runs, which models it should hold, what its concurrency cap is — and every node matching the selector converges on it.

That part is unremarkable. This is the part worth reading.

**A profile can only ever narrow a node, and the check that enforces it runs on the node.**

The hub can switch a capability off. It cannot re-open one the box's own config closed. It can stop a tool. It cannot introduce one — a tool id that isn't already in that node's Tools:Allowed is refused by name, and there is no field anywhere in a profile for a command, a path or an interpreter. It can lower a concurrency cap. Raising it is refused, because that number is a statement about hardware you own and the coordinator does not.

And the clamp lives on the node, not on the hub. That distinction is the whole feature. A clamp that runs on the hub is a clamp an attacker skips by not being the hub — the moment your coordinator is compromised or just misconfigured, it becomes remote code execution across every GPU box in the building. Putting the check at the far end means the worst a bad coordinator can do is switch things off.

The test suite for it is written adversarially: it hands the node profiles naming interpreters, relative paths, and a shell one-liner, and asserts that the running node is unchanged and each one comes back with a refusal naming the config key that stopped it. That's the acceptance criterion for the release.

Two smaller things that matter more than they sound:

**A profile is desired state, not a command.** A node asks for its profile every time it registers. So a box that was being rebuilt when you wrote one converges on the way back in — no operator action, and nothing for the hub to remember about who has what. Applying the same revision twice does nothing and says so, which is what makes that unconditional re-ask safe instead of a re-download of forty gigabytes of weights every reconnect.

**A selector that names nothing matches nothing**, and is refused with a 400 rather than quietly applying to every machine you own. Two profiles matching one node is a conflict the hub reports — not a merge, not first-one-wins. The node keeps what it last applied until you fix the selectors. Silent precedence is how a box ends up in a state that no single document explains.

Refusals are per item, so a profile asking for one impossible thing and four possible ones applies the four and reports the one. And a profile never restarts a node: switching a tool off stops its workers in place, switching it back on starts them again, and in-flight work is undisturbed. A node that reboots because the hub said so is a node you cannot keep up.

975 tests. A fleet that defines no profile behaves exactly as 3.10.

👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.11.0

## X / Twitter — single post (267/280; the link counts as 23 under t.co)

InferHub 3.11: the coordinator can now configure the fleet.

It can only ever NARROW a node — and the check that enforces that runs on the node, not the hub.

A clamp on the hub is a clamp an attacker skips by not being the hub.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.11.0

## X / Twitter — the selector post (single, 249/280)

New rule in our fleet config: a selector that names nothing matches NOTHING.

Not everything. It's a 400.

The one thing a fleet-configuration API must never do is apply to more boxes than the author meant.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.11.0

## X / Twitter — thread (each under 280; link only on 5/5)

**1/5** (238)

InferHub 3.11: one profile on the coordinator, and every matching node converges.

Capabilities on or off, tools on or off, models pulled or removed, concurrency lowered.

The interesting part isn't that. It's which end does the checking.

**2/5** (271)

A profile can only ever NARROW a node.

It can stop a tool. It can't introduce one — an id not already in that node's allow-list is refused by name, and there's no field in a profile for a command, a path or an interpreter.

It can lower a concurrency cap. Never raise it.

**3/5** (263)

And the clamp runs on the node, not the hub.

A clamp on the hub is a clamp an attacker skips by not being the hub. Compromise the coordinator and you have fleet-wide RCE.

Put the check at the far end and the worst a bad hub can do is switch things off.

**4/5** (256)

Desired state, not commands.

A node asks for its profile at registration. So a box rebuilt while you were writing one converges on the way back in — nothing for the hub to remember.

Same revision twice = no-op. Otherwise every reconnect re-pulls 40GB of weights.

**5/5** (244 incl. link)

A selector naming nothing matches nothing — 400, not "everything".

Two profiles matching one node is a reported conflict, not a merge. The node keeps what it had until you fix it.

Silent precedence is how a box ends up in a state no document explains.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.11.0

## LinkedIn (if used)

Same as Facebook. Lead harder on the RCE framing — that audience has an opinion about control planes
and will argue with it, which is fine.
