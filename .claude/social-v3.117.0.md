# v3.117.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-117-tools-without-a-language-engine

## Facebook

InferHub 3.117: a node with no language engine still offers its tools. A speech or picture node joined to a coordinator without an Ollama beside it used to register with an empty list of what it could do, and it was never sent a single request.

Now the node reports its tools even when its language engine does not answer, and the coordinator sends them work while that engine is down. Measured on the published images: the same request for speech got a 404 on 3.116 and returns a WAV file on 3.117.

https://blog.devart.solutions/blog/inferhub-3-117-tools-without-a-language-engine

## X

InferHub 3.117: a speech or picture node with no Ollama now tells the coordinator what it can do, and gets the work. On 3.116 the same speech request got a 404. On 3.117 it returns a WAV.

https://blog.devart.solutions/blog/inferhub-3-117-tools-without-a-language-engine
