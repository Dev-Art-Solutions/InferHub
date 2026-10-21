# v3.73.0 social copy (unposted)

## Facebook

InferHub 3.73: slots the engine reports.

In 3.72 the coordinator learned that a model can only run so many requests at once, and started sending each request to a node with a free slot for it. But only Ollama said how many, and only from its configuration.

llama.cpp knows its own number and will tell you: on our test box it runs 4 requests per model at once by default. Now each node asks it, for every loaded model, and the coordinator uses that number first.

The catch: a llama.cpp router loads any model you ask about. So the node asks only about loaded models, with autoload=false, and never loads anything by asking.

https://blog.devart.solutions/blog/inferhub-3-73-slots-the-engine-reports

## X

InferHub 3.73: a llama.cpp model now tells the coordinator how many requests it runs at once (4 by default here, vs Ollama's 1), asked without loading anything. The coordinator routes by that number first.

https://blog.devart.solutions/blog/inferhub-3-73-slots-the-engine-reports
