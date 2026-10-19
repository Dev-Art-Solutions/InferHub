# v3.71.0 social copy (unposted)

## Facebook

InferHub 3.71 started as one small change and found three older bugs.

The change: a node now runs the jobs the coordinator sends it at the same time. Before, SignalR handed them over one by one, and a second chat waited for the first, even with room on the GPU.

What that found:
• A cancelled chat, picture or video was never stopped on the node. The coordinator marked it cancelled and the node kept going to the end. Now the node is told every time.
• A cancelled picture freed the node at once, and the next picture went to a worker that was still stopping. Now the coordinator waits for the node's answer.
• Streams from an Ollama node arrived all at once at the end. The first line came at 6.3 seconds of a 6.3-second answer. Now it comes at 0.016 seconds.

https://blog.devart.solutions/blog/inferhub-3-71-jobs-side-by-side-and-a-cancel-that-arrives

## X

InferHub 3.71: a node runs the coordinator's jobs side by side instead of one after another. Getting there found that cancelled jobs never stopped on the node, and that Ollama streams arrived in one piece at the end. All fixed.

https://blog.devart.solutions/blog/inferhub-3-71-jobs-side-by-side-and-a-cancel-that-arrives
