# InferHub v3.66.0 — social copy (unposted)

## Facebook

InferHub 3.66: a Windows GPU box becomes an InferHub node with one setup.

It installs the node as a Windows service and asks how to run it: join a coordinator or run solo, the node's name, labels, job cap, VRAM budget, Ollama, and which account runs it. The answers live outside Program Files, readable by administrators only, so no update can overwrite them.

And the node keeps itself current. Choose one:
• automatically — it waits for running jobs, then installs the new release itself
• when an admin says — the coordinator's console shows it, and Update installs it
• report only — you update on the box

Measured on a real Windows 11 box: Update from the console took a node to the new version in about 10 seconds, settings kept. A download whose checksum didn't match was refused, and the service kept serving.

https://blog.devart.solutions/blog/inferhub-3-66-a-windows-node-that-keeps-itself-current

## X

InferHub 3.66: one Windows setup installs a node as a service and asks how to run it. Its settings live where no update can overwrite them, and the node keeps itself current — by itself, from the coordinator's console, or by hand.

https://blog.devart.solutions/blog/inferhub-3-66-a-windows-node-that-keeps-itself-current
