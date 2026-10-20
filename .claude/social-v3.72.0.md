# v3.72.0 social copy (unposted)

## Facebook

InferHub 3.72: a free slot for the model.

Ollama runs one request per model at a time unless you set OLLAMA_NUM_PARALLEL. Six chats sent at once to one model finished one after another: 0.9, 1.4, 2.0, 2.5, 3.0 and 3.5 seconds.

The coordinator didn't know that. It counted jobs per node, so a node already busy with a model looked as good as a node busy with a different one, and every other new chat waited inside Ollama while another node's copy of the model sat idle.

Now each node says how many requests one model runs at once, the coordinator counts its jobs per model, and a request goes first to a node that can start it now. It never refuses anything: when every node is full, the request goes where it always went.

https://blog.devart.solutions/blog/inferhub-3-72-a-free-slot-for-the-model

## X

InferHub 3.72: Ollama runs one request per model at a time by default. The coordinator now knows that, and sends a chat to a node with a free slot for its model instead of one where it would wait behind the same model.

https://blog.devart.solutions/blog/inferhub-3-72-a-free-slot-for-the-model
