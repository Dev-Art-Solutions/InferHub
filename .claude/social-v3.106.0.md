# InferHub 3.106 — social copy (unposted)

## Facebook

InferHub 3.106: a local model with a long memory, on a card with 24 GB.

We gave ornith (35B) two novels at once: 620,000 tokens. Reading them takes 34 minutes. The next question should not cost
that again, so the node now keeps a long context the way a processor keeps memory:
• in the engine's slot: instant
• in llama.cpp's RAM cache: a copy away
• in files on disk the node writes, restores and deletes by use: 620k tokens back in 5.5 seconds

What stays on disk is decided by how often it comes back. A pin keeps a book as permanent memory.

Three facts hidden at 10%, 50% and 90% of the text came back word for word. A question about the plot of the epilogue came
back wrong, so this is memory for finding and quoting, not a summariser.

https://blog.devart.solutions/blog/inferhub-3-106-a-long-context-kept-by-use

## X

InferHub 3.106: a long context on three tiers (slot, RAM, disk), kept by use.

ornith 35B on a 24 GB card: 620k tokens read in 34 min, restored from disk in 5.5 s. Three facts hidden deep in the text: 3/3 exact.

https://blog.devart.solutions/blog/inferhub-3-106-a-long-context-kept-by-use
