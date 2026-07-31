# Social — v3.9.0

Post manually. Lead with the *rejected* alternative, not with the feature: "we did not embed Python,
and here is what would have happened" is the interesting half, and it is the half other people
building this are actually deciding right now.

## Facebook

InferHub 3.9: the node can run your Python — as a subprocess, on purpose.

A node used to do exactly one thing: run prompts against an inference backend. It can now also start, supervise and talk to child processes that do work that backend cannot — transcription, speech, OCR, whatever you write.

The part worth writing down is the thing we did NOT do.

The libraries people want first are Python: faster-whisper, piper. So the obvious move is Python.NET or CSnakes — call them in-process, no serialisation, no child to supervise. We declined, and the second of the three reasons is the one that would have hurt:

One bad import takes the node down. A segfault in a native extension loaded into your process is not an exception you catch. It is a process that vanishes mid-stream, taking every in-flight inference request with it. A GPU box that stops answering chat because somebody's audio library disagreed with a CUDA version is not a trade worth making. A child process that segfaults is a log line and a restart.

It would also pin the node to a CPython ABI, in a project whose entire dependency list is two packages. And it forecloses the general case: a tool that's a Go binary, an ffmpeg call or a vendor CLI is free in this design and impossible in the other one.

So the node knows how to start a process, write a line, read a line and kill it. It doesn't know what Python is.

Running code is opted into twice — Tools:Enabled for the feature, Tools:Allowed for the specific manifest — and the second is not redundant. In 3.11 a coordinator will be able to turn a node's capabilities on and off, and Tools:Allowed is the ceiling it can never raise. One switch would make "the operator enabled tools" and "the hub may run any tool on this box" the same consent, which is a compromised coordinator away from fleet-wide RCE.

And it is NOT a sandbox. We say that in those words, because implying safety by listing mitigations is how somebody ends up running a random whisper-plus-telemetry.py from a gist on a machine holding their fleet's enrollment secret. A worker runs as the node's user, with the node's filesystem and the node's network. What the node does do is refuse to hand over its own environment — a child normally inherits the parent's, so it is cleared and rebuilt, and Coordinator__EnrollmentSecret does not reach a worker. That is a real hole closed and the honest extent of it.

Every level has a deadline. Overrun the request timeout and you are killed and the JOB fails — the node keeps serving inference, which is what the suite asserts after every single failure mode. Fail to start three times in ten minutes and the pool gives up, withdraws its capabilities so the coordinator stops routing that work there, and keeps probing so a fix needs no restart.

3.9 ships the machinery and no tool at all. The suite drives a real child process that echoes what it is sent, so Whisper lands in 3.10 onto something already proven — putting a speech model on an unproven process manager means debugging two new things through each other, and every failure looks like a model problem.

One bug that only running it could find: a failed worker is killed on a background task so the next caller's queue budget isn't spent waiting for it. Every test passed. Then a build failed on a locked DLL held by a process nobody had a handle to — a shutdown had raced the kill and orphaned a child. No assertion would have caught that.

Off by default, zero new dependencies, eleventh phase running.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.9.0

## X / Twitter — single post (270/280; the link counts as 23 under t.co, not its real length)

InferHub 3.9: a node can run your Python — as a subprocess.

We declined the in-process binding. One bad import isn't an exception you catch, it's a process that vanishes mid-stream with every in-flight job.

A child that segfaults is a log line.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.9.0

## X / Twitter — thread (each tweet under 280; link only on 4/4)

**1/4** (217)

InferHub 3.9: a node can now start, supervise and talk to child processes that do work its inference backend can't — transcription, speech, whatever you write.

The interesting part is what we didn't do: embed Python.

**2/4** (276)

Python.NET / CSnakes is the obvious move. faster-whisper in-process, no serialisation, no child to supervise.

One bad import takes the node down. A segfault in a native extension isn't an exception you catch — it's a process that vanishes mid-stream with every in-flight job.

**3/4** (264)

So the node knows how to start a process, write a line, read a line, kill it. It doesn't know what Python is.

Which means a tool that's a Go binary, an ffmpeg call or a vendor CLI is free.

Opt in twice: Tools:Enabled, then Tools:Allowed. The second is a ceiling.

**4/4** (260 incl. the link)

It is NOT a sandbox and we say so in those words. A worker runs as the node's user with the node's network.

It does not inherit the node's environment — your enrollment secret doesn't reach it. That's the honest extent.

Zero new deps.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.9.0
