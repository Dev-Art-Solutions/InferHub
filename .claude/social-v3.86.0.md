# v3.86.0 social copy (unposted)

## Facebook

InferHub 3.86: when capacity runs short, essential work first.

Give each client a class: essential, normal or background. The coordinator counts the free places for a model on the nodes it can reach now. Background work never takes the last of them, so when a node fails or every node is busy, background work waits and the essential work keeps running. It is paused, not refused, and goes as soon as there is room again. A request can lower its own class, never raise it.

Live: with one of two nodes stopped, background requests waited while essential ones were answered at once; when the node came back, the waiting requests went within seconds.

https://blog.devart.solutions/blog/inferhub-3-86-essential-work-first

## X

InferHub 3.86: priority classes. When nodes fail or fill up, background work waits and essential work keeps running. Paused, not refused.

https://blog.devart.solutions/blog/inferhub-3-86-essential-work-first
