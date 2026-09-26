# Social copy — v3.51.0 (phase 86: the hub routes to the node whose card is already warm)

Blog: https://blog.devart.solutions/blog/inferhub-3-51-the-hub-sends-work-to-the-warm-card
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.51.0

## X / Twitter

InferHub v3.51: with several on-demand GPU boxes, a chat no longer lands on the card busy with diffusion. Each node reports who holds its card, and the router picks warm, then free, then cold. A preference, never a refusal.

https://blog.devart.solutions/blog/inferhub-3-51-the-hub-sends-work-to-the-warm-card

## Facebook / LinkedIn

**The hub now sends the work to the card that is already warm.**

Last release, InferHub learned to share one GPU between services: the chat model, speech, images and video take the card in turn, and it is freed between jobs. That worked on one box. With two such boxes behind one coordinator, there was a gap: the coordinator did not know which service each card was holding. A chat could land on the box that was busy rendering an image, wait for the image model to unload and the chat model to load, and then make the next image job wait for the switch back. Meanwhile the other box had the chat model ready.

In v3.51 each node tells the coordinator who holds its card, on every heartbeat and again the moment it changes. When several nodes could answer, the router prefers one whose card already holds the right service, then one whose card is free, and only then one that would have to switch.

It is a preference, never a refusal. If the only node with the model is busy with something else, the request still goes there and waits, as before. A fleet without on-demand nodes routes exactly as it did, and there is no new setting.

We checked it on the published images: with one box's card held by a tool, three chats in a row all went to the other box, and the first box never waited for its card. Still open: a real GPU worker switching on a card; the tool in our run was a real process, but not a GPU one.

https://blog.devart.solutions/blog/inferhub-3-51-the-hub-sends-work-to-the-warm-card
