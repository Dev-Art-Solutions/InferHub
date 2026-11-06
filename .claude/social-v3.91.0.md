# v3.91.0 social copy (unposted)

## Facebook

InferHub 3.91: the coordinator that takes over now lets in every machine the old one certified.

With two coordinators sharing one database, the certificates, tokens, revocations and organisations of every machine now live in that database, not in one coordinator's files. Before this, a failover at a site that required certificates handed the lead to a coordinator that knew none of its machines. You can now also name the coordinator that should lead: after a failover, the lead returns to it once it has been back long enough. Tested live: hub-a stopped, hub-b let the machine in with hub-a's certificate; hub-a came back and got the lead again 20 seconds later.

https://blog.devart.solutions/blog/inferhub-3-91-one-identity-across-the-cluster

## X

InferHub 3.91: machine certificates now survive a coordinator failover, and the lead can return on its own to the coordinator that should have it.

https://blog.devart.solutions/blog/inferhub-3-91-one-identity-across-the-cluster
