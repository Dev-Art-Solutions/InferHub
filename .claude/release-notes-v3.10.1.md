# InferHub v3.10.1 — everything v3.10.0 promised, actually working

**Upgrade from v3.10.0.** Seven bugs, all found the same way: by pulling the published images and
running them on a real GPU box. Every unit test was green for every one of them.

Three of the seven made the feature unusable rather than awkward, so if you pulled `:3.10.0-tools`,
pull again.

## The three that mattered

**Nothing in the Python venv was importable.** It was built in a `debian:trixie-slim` stage — Python
3.13 — and copied into the runtime image, which has 3.12. A venv's packages live under
`lib/python3.13/`, which a 3.12 interpreter does not look in. Every manifest still loaded, the node
still started, `/api/status` still answered, and the first transcription would have died on
`from faster_whisper import WhisperModel`. The venv is now built **in the final image**, by the
interpreter that will run it, and the build asserts that it imports — so this fails `docker build`
rather than somebody's first request.

**Every real `/v1/audio/speech` through a coordinator returned a 500 and dropped the node.**
SignalR's default `MaximumReceiveMessageSize` is **32 KB**, and exceeding it does not fail a message
— it tears down the connection. A six-second synthesised wav is ~300 KB. v3.9 shipped attachments
and verified them across a real wire with a *16-byte* file, four orders of magnitude under the cap:
the test proved the plumbing and said nothing about the size. The wire cap is now **derived from
`Tools:MaxAttachmentBytes`**, so the two numbers cannot disagree, and the mesh suite now pushes
256 KB in both directions and then checks the node is still connected.

**Text to speech could never start.** A manifest may declare `"models": []` to mean "ask the
worker", which is how a TTS node discovers the voices on its volume — but nothing declared the
capability until a worker had reported, no worker started until a request was routed, and no request
was routed to a capability nobody declared. A node with a voice sitting right there answered *this
node does not provide 'speak'* forever. An open model set now starts one worker eagerly, because
promising to ask the worker means the asking cannot wait for somebody to need the answer.

## The other four

**GPU transcription failed with `Library libcublas.so.12 is not found`.** CTranslate2 needs the CUDA
*runtime*, not just the driver — and the runtime was already in the image, in Ollama's own bundle,
just not on the worker's loader path. The Whisper manifest now points `LD_LIBRARY_PATH` at it. And
because a card that cannot be used is the operator's problem while a failed job is everyone's, the
worker now **falls back to the CPU and says why**, loudly, instead of failing the request.

**Solo `/api/status` reported `capabilities: []`** on a tools-only node that was happily serving
transcriptions — it asked the backend's model list and never the tool runtime. That is the one page
an operator checks to find out why nothing is being routed to a node.

**`docker exec … curl -O` did not work**, because the image had no `curl` — and that is the exact
command the docs give for installing a voice, on the one step an operator has to do by hand.

**The `:tools` image could not be reconfigured through its environment.** `-e Tools__Enabled=false`
failed startup with *"Allowed names 2 tool(s)"*, and there is no way to *remove* an array element
that came from an image — `-e Tools__Allowed__1=` is the only lever `docker run` gives you, and a
blank was counted as a named tool. Blanks are now ignored, which is how you drop one tool or all of
them. Nothing is hidden by that: a manifest not in the list is still loaded and still logged by name
as not started.

Plus one piece of wording: a worker killed mid-request reported *"exited before answering (exit code
still running)"*, which is a contradiction the reader has to decode. SIGKILL closes stdout before
the OS reaps the process, so there is no exit code yet — and reporting nothing beats reporting a
non-answer as if it were one.

## Measured on the way through

On an RTX 3090 Ti (driver 591.86, Docker 27.3.1 / Docker Desktop WSL2), `whisper-small`, on **113
seconds** of audio, warm worker:

| | Wall time | Ratio to real time |
|---|---|---|
| GPU, `float16` | **3.4 s** | ~33× |
| CPU, `int8`, `--gpus` omitted | **13.5 s** | ~8× |

Both produce the same transcript to the character except for capitalisation, which is what float16
against int8 costs. First call on a fresh volume is 45 s including the 464 MB weight download; every
call after it on the same volume is 4 s.

And the claim v3.8 was built for, measured: a **1.5-second chat** answered on one node while a
113-second transcription ran on another. A busy transcription does not make a node ineligible for
chat.

## Upgrading

Nothing to configure. `Tools:*` is still off by default, and a deployment that changes no config
behaves identically to v3.9.

`dotnet test`: **946 passed, 0 failed, 46 skipped** — up from 936 at v3.10.0, with a regression test
for each of the seven.
