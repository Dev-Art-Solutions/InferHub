# InferHub 3.105 — social copy (unposted)

## Facebook

InferHub 3.105 adds a fleet page to the coordinator.

The console had a panel for every feature, and none of them answered the plain question: how is the fleet doing?

/fleet.html answers it:
• Groups you keep on the coordinator. Name a set of machines by id, by labels (site=sofia), or both, and see their numbers added up.
• The last hour of traffic per group (requests per minute, failures, work in flight), sampled from the coordinator's own counters. You don't need Prometheus.
• What needs a hand, one sentence per machine: no heartbeat, backend down, too many failures, a sealed node that can reach the internet, a group member that never connected.
• Cordon a whole group in one click, with an audit entry for each machine.

A group only changes what the page shows. Where requests go is still decided by the router.

https://blog.devart.solutions/blog/inferhub-3-105-the-fleet-page

## X

InferHub 3.105: a fleet page for the coordinator.

Groups by id or label, the last hour of traffic without Prometheus, one sentence per machine that needs a hand, and a cordon for a whole group. Groups change no routing.

https://blog.devart.solutions/blog/inferhub-3-105-the-fleet-page
