# v3.116.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-116-a-burst-waits-its-turn

## Facebook

InferHub 3.116: four requests at once, and a node with room for two. Since 3.74 the coordinator can hold a chat until a slot frees, but requests that arrived together all looked before any was counted, so all four went and two waited inside the engine.

Now a request that finds a free slot takes the same line as one that finds none. Measured: four at once, two running and two held; eight at once, never more than two running, none refused.

https://blog.devart.solutions/blog/inferhub-3-116-a-burst-waits-its-turn

## X

InferHub 3.116: four requests at once to a node with two slots now run two and the coordinator holds two. Before, all four looked before any was counted, and all four went.

https://blog.devart.solutions/blog/inferhub-3-116-a-burst-waits-its-turn
