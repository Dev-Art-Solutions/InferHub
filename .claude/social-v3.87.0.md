# v3.87.0 social copy (unposted)

## Facebook

InferHub 3.87: a certificate per node.

Nodes used to share one enrollment secret, so taking one machine out meant changing the secret everywhere. Now the coordinator issues each node its own certificate from a one-time token. The node's key never leaves the node, and every connection carries a fresh signature that cannot be replayed. Revoke one node and it is disconnected at once and kept out, even if it still has the old shared secret.

Live: a revoked node was gone from the fleet in 161 ms, and every retry was refused with the reason.

https://blog.devart.solutions/blog/inferhub-3-87-a-certificate-per-node

## X

InferHub 3.87: each node gets its own certificate from the coordinator. A signed proof on every connection, and one node revoked and dropped at once.

https://blog.devart.solutions/blog/inferhub-3-87-a-certificate-per-node
