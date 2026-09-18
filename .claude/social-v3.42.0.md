# Social copy — v3.42.0 (phase 77: node-corpus standby replication)

Blog: https://blog.devart.solutions/blog/inferhub-3-42-a-collection-outlives-its-node
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.42.0

## X / Twitter

A node-owned collection in InferHub used to live on exactly one disk. Lose that box for good
and the collection was gone, silently.

v3.42 gives it a standby: relayed live, and the hub never holds a copy itself — same guarantee
node ownership always had, just no longer fragile.

https://blog.devart.solutions/blog/inferhub-3-42-a-collection-outlives-its-node

## Facebook / LinkedIn

**A node-owned collection can now survive the node that owned it.**

InferHub lets a GPU node run its own vector store — deliberately never copied to the hub, so the
node is the one authority for its own data. That was safe as long as the box stayed up. If it
didn't (disk failure, a decommissioned machine, a VM that never comes back), the collection was
gone for good.

v3.42 adds a standby: assign one, and the hub relays the primary's snapshot and every live write
to it — without ever storing a copy itself. If the primary is confirmed permanently gone, the
standby is promoted through the exact same mechanism an admin's own collection assignment uses —
no second, undocumented way for a node to end up owning a name, and no automatic failback if the
old primary ever comes back.

Off by default on both ends. Verified over a real two-node mesh: assign, write, disconnect the
primary, promote, and query the promoted standby's own corpus for the record written before the
primary went away.

Full writeup: https://blog.devart.solutions/blog/inferhub-3-42-a-collection-outlives-its-node
