# v3.83.0 social copy (unposted)

## Facebook

InferHub 3.83: the hub keeps what it relayed.

Since 3.82, a sealed node downloads everything through its coordinator, and the next node downloads it all again over the same uplink. Turn on Relay:Cache, and the coordinator keeps each blob it passed on, so the second node's pull comes from the hub's disk.

It keeps a blob only by the sha256 the upstream named: an Ollama blob's digest, or the hash Hugging Face sends on the resolve. The upstream is still asked every time, with the node's own token, so a gated repo still says no to a node without access, and "main" is always today's. Only the bytes behind the redirect come from the cache. Every blob is hashed as it is written and kept only if it matches.

Ollama asks for every layer in parts, so on a miss the hub relays the parts as before and fetches the whole blob once, beside them.

Live: a sealed re-pull went from 16 seconds to 4, and a 105 MB Hugging Face file from 139 seconds to 0.63.

https://blog.devart.solutions/blog/inferhub-3-83-the-hub-keeps-what-it-relayed

## X

InferHub 3.83: the coordinator keeps what it relayed for sealed nodes, by the sha256 the upstream named, and still asks the upstream every time, so a gated repo still says no. A sealed re-pull went from 16 s to 4 s.

https://blog.devart.solutions/blog/inferhub-3-83-the-hub-keeps-what-it-relayed
