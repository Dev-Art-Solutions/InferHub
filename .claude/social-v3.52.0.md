# Social copy — v3.52.0 (phase 87: inferhub-node:all, one image for a one-card box)

Blog: https://blog.devart.solutions/blog/inferhub-3-52-one-image-for-a-one-card-box
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.52.0

## X / Twitter

InferHub v3.52: one image, one GPU, and chat, speech, images and video take turns on it. Its first run on an empty disk found our on-demand mode had been killing the image model download. Fixed, and checked on a real 3090 Ti.

https://blog.devart.solutions/blog/inferhub-3-52-one-image-for-a-one-card-box

## Facebook / LinkedIn

**One image for a machine with one graphics card.**

Two releases ago InferHub learned to share one GPU between services: the chat model, speech, images and video take the card in turn, and it is free between jobs. But no single image held all of those services. You had to run two containers, and two containers on one card are two programs, each sure the card is its own.

v3.52 adds inferhub-node:all: everything in one node, with turn-taking switched on. One Python environment instead of two, so it is smaller than the two images it replaces.

Its first run on an empty disk found a real bug. The image worker downloads model weights in the background, and turn-taking stopped that worker right after it started, killing the download. An on-demand node on a fresh disk never offered a single image model. Workers can now say "I am still downloading", and the node waits for them to finish.

We checked it on the published image with a real RTX 3090 Ti: a chat, an image, a transcription and a chat again, all on the GPU, each waiting for the previous one to let go. The card's memory followed along, 3.7 GB, 6.1 GB, 3.0 GB, 3.7 GB. Not yet checked: the image behind a coordinator, and other cards.

https://blog.devart.solutions/blog/inferhub-3-52-one-image-for-a-one-card-box
