# v3.55.0 social copy (unposted — Iliya posts; blog is live, ID 6abd6e3b0bb5c39555cba334)

## Facebook

InferHub 3.55: a Bulgarian voice that runs on a CPU.

Until now InferHub's only Bulgarian voice (bg-tts-v5) needed its own 9 GB image and a GPU. Piper now has bg_BG-dimitar-medium, a 63 MB Bulgarian voice that runs on the CPU in the image you already use for speech. It read 5 seconds of Bulgarian in about a quarter of a second once loaded.

Getting it onto a node is one setting: Tools__Speech__Voices__0=bg_BG-dimitar-medium. The node downloads it from a pinned catalogue, checks both files' sha256 before installing anything, and starts offering the voice when it lands, with no restart. Nothing is downloaded unless you name it.

https://blog.devart.solutions/blog/inferhub-3-55-a-bulgarian-voice-on-a-cpu

## X

InferHub 3.55: a 63 MB Bulgarian voice that runs on a CPU. Name it in one setting and the node fetches it from a pinned, sha-checked catalogue, then offers it without a restart.

https://blog.devart.solutions/blog/inferhub-3-55-a-bulgarian-voice-on-a-cpu
