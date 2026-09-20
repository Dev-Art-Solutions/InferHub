# Social copy — v3.44.0 (phase 79: platform-keyed tool manifests)

Blog: https://blog.devart.solutions/blog/inferhub-3-44-the-manifest-didnt-know-about-windows
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.44.0

## X / Twitter

InferHub's tool runtime was already OS-agnostic. Every manifest's command wasn't — hardcoded to
Linux.

v3.44: command can be keyed by platform, so one manifest runs on Windows too.

https://blog.devart.solutions/blog/inferhub-3-44-the-manifest-didnt-know-about-windows

## Facebook / LinkedIn

**A tool manifest can now name a different command per platform.**

InferHub's tool workers have always been plain OS processes talking JSON over stdin/stdout — no
Python binding, no shell. Investigating "can this run on Windows" found the runtime already didn't
care what OS it was on; every shipped *manifest* did, because it had only ever been written for the
Linux `:tools` image.

v3.44: `command` (and `workdir`) can be an object keyed by platform, resolved against the node's own
OS at load time. One manifest, one id, both platforms — no more hand-maintaining a second copy of a
tool's config for a Windows box.

Proven with a real spawned process on a real Windows machine, not just a parsed JSON file. What's
still open, named rather than implied: no Windows build of the shipped Whisper/Piper tools yet, and
no Windows container image — this is the manifest mechanism, not a finished Windows tools story.

https://blog.devart.solutions/blog/inferhub-3-44-the-manifest-didnt-know-about-windows
