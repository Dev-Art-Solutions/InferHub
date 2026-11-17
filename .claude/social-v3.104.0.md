# InferHub v3.104.0 — social copy (unposted; Iliya posts by hand)

Blog: https://blog.devart.solutions/blog/inferhub-3-104-a-sealed-node-updates-through-its-coordinator
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.104.0
No image: the honest picture is two status lines that say "available: 3.104.0" and "up to date".

## Facebook

A sealed InferHub node talks to nothing outside its network except its coordinator. Until now that also meant it could not update itself: the list of new versions lives on GitHub, and GitHub is outside.

InferHub 3.104 fixes that. The coordinator now keeps node releases the way it already keeps models. An admin asks for a release once, the coordinator checks the setup against its published checksum, and sealed nodes update only from that copy. A new version on GitHub reaches a closed site only when its admin decides it should.

On the way we found a bug: the install plan the coordinator writes for a sealed Windows node used the installer's default updates from GitHub, so the node it described refused to start. That is fixed, and a test now checks the plan against the boot rules.

We checked it on the published images: the coordinator supplied 3.104.0 itself, and an unchanged 3.103.0 sealed node saw it as available.

Limits we know: we have not yet applied an update this way on an installed Windows service, and Docker nodes are still updated from your own registry.

https://blog.devart.solutions/blog/inferhub-3-104-a-sealed-node-updates-through-its-coordinator

## X

InferHub 3.104: a sealed node can now update itself, without ever reaching GitHub.

The coordinator keeps checked node releases. Sealed nodes update only from those, so a closed site picks its own version.

Also fixed: the install plan for a sealed Windows node described a node that would not boot.

https://blog.devart.solutions/blog/inferhub-3-104-a-sealed-node-updates-through-its-coordinator
