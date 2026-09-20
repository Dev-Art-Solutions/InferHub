# Social copy — v3.44.0 (phase 79: platform-keyed tool manifests)

Blog: not posted (blog connector unavailable this session)
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.44.0

## X / Twitter

InferHub's tool workers (transcription, TTS, anything you wire up) have always been plain child
processes — no shell, no native binding. Turns out the runtime was already OS-agnostic C#.

What wasn't: every manifest's command was a hardcoded Linux path.

v3.44 lets one manifest's `command` be keyed by platform — `{"linux": [...], "windows": [...]}` —
so the same tool id runs unedited on a bare-metal Windows node and the Linux `:tools` image.

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
