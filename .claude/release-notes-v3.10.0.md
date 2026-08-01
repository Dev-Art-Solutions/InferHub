# InferHub v3.10.0 — speech in, speech out, on your own box

v3.8 taught the fleet to route on **what a node can do**. v3.9 gave a node the machinery to run child
processes that do work its inference backend cannot — and shipped **no tool**, deliberately, so the
process manager could be proven on its own. This is the tool.

Two endpoints, on OpenAI's audio API exactly, against your own hardware:

```bash
curl http://localhost:5080/v1/audio/transcriptions \
  -H "Authorization: Bearer $KEY" \
  -F file=@meeting.m4a -F model=whisper-small -F response_format=verbose_json

curl http://localhost:5080/v1/audio/speech \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"en_US-amy-medium","input":"InferHub can talk now.","response_format":"wav"}' \
  --output out.wav
```

Every SDK in every language already speaks this, so pointing an existing app at your own GPU is a
base-URL change. The same two routes are served by a **standalone node with no coordinator at all**,
and by one container:

```bash
docker run -d --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$KEY" -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:tools
```

Leave `--gpus` off and it runs on the CPU — roughly real time for `whisper-small` on a modern core —
and the worker's first log line says which one it got. That line exists because the alternative is
four gigabytes of CUDA runtime, a dropped flag, two tokens a second, and an afternoon spent blaming
the model.

## Zero new dependencies, again

`faster-whisper` and `piper` are **Python in a Dockerfile**, not packages anything compiles against.
No `.csproj` changed, `InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`,
and the node still knows how to start a process, write a line, read a line and kill it — and nothing
else about what a worker is written in. That is v3.9's decision paying for itself: the workers landed
on a runtime that was already proven, and every failure during the phase was a tool problem rather
than a plumbing problem.

## A third image, and the other three are untouched

| Tag | Size | Arch | What is in it |
|---|---|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | The hub |
| `inferhub-node` | ~340 MB | amd64 + arm64 | A node. No inference engine |
| `inferhub-node:ollama` | ~4 GB | amd64 | The same node with Ollama inside it |
| `inferhub-node:tools` | ~6 GB | amd64 | The same again, plus Python, `faster-whisper` and `piper` |

The first three are **unchanged by this release**. The Python is ~1.5 GB and it is in a layer whether
a flag is on or off, so a flag would have grown every existing coordinator+node stack for a feature
it does not use. An operator on the plain image can still run these workers by installing Python
themselves and pointing a manifest at it — `python/requirements-tools.txt` is exactly what the image
installs, and the runtime does not care where the interpreter came from.

## Formats, and nothing silently substituted

| | |
|---|---|
| Transcription | `json` (default), `text`, `srt`, `vtt`, `verbose_json` |
| Speech | `wav`, `pcm` natively; `mp3`, `opus`, `flac` where the worker has `ffmpeg` (the `:tools` image does) |

A worker always answers with the verbose shape — text, segments, duration — and **the edge formats**.
`srt` and `vtt` are string formatting on the hub, which means no worker author ever writes a subtitle
timestamp, in any language, and two workers cannot disagree about where the comma goes.

**A format that cannot be produced is a `400` naming the ones that can.** Not a substitution: a
caller who asked for mp3 and got a wav has a corrupted file with a confident content type and finds
out in a media player three days later. A worker with no encoder says *which kind* of failure it was
as a field, and the edge renders the status from that — never from reading the message.

## No weights in the image, and a third opt-in for fetching them

Whisper downloads its weights on first use, into `/data/tools/hf` on the volume, so it happens once
rather than once per `docker run`. That download is a reach onto the internet from a GPU box, so it
sits behind `Tools:AllowModelDownload` — **default `false`, `true` in the `:tools` image**, because
choosing that image *is* the consent, exactly as choosing `:ollama` is the consent to run an Ollama.

Three opt-ins now, and none of them is redundant with the others:

| Key | Consents to |
|---|---|
| `Tools:Enabled` | running tools at all |
| `Tools:Allowed` | running *these* tools — and it is the ceiling a coordinator will never be able to raise |
| `Tools:AllowModelDownload` | one of them reaching the internet from a box you may have air-gapped |

With the third off, a worker that needs missing weights fails the **job** with the exact pre-fetch
command in the message, and the node keeps serving everything else including chat.

**Voices are not fetched at all**, because no default voice is right for everyone and a confident
answer in the wrong language is worse than a refusal. Drop a Piper `.onnx` + `.onnx.json` pair into
`/data/tools/voices`; the model name is the file's stem.

## None of it is kept

Design rule 7 at its most literal: a transcription request is a recording of somebody's voice, and
the answer is what they said.

- The hub buffers the upload for the dispatch and drops it. No temp file, no cache.
- The node writes it into a per-request scratch directory deleted in a `finally` — after success and
  after failure.
- **Nothing containing audio bytes or transcript text is logged, at any level.** The line for a
  transcription carries the model, the duration and the outcome. Not even the filename you chose:
  `board-meeting.m4a` is metadata about somebody's day and is not needed to run a fleet.
- The usage ledger gains a **duration**, never a transcript.

`AudioPrivacyTests` runs a transcription through a real mesh with a capturing logger at `Trace` and
fails if a known phrase from the fixture appears anywhere in the log or the ledger.

## Metered in the unit the work is actually in

Audio has no token count, so metering it as tokens would mean inventing a number. A usage row now
carries `units` and a `unitKind` — `tokens`, `audio_seconds` or `characters`. The token fields are
untouched, the Postgres migration is additive and rewrites nothing, and a row written by v3.9 still
means exactly what it meant.

```jsonc
"Limits": { "AudioSecondsPerDay": 3600, "CharactersPerDay": 200000 }
```

Separate budgets, each consuming only its own unit — a client whose only limit is `TokensPerDay`
could otherwise transcribe a library for free. Over one is the same `402` + `Retry-After` to UTC
midnight as the token budget, in the OpenAI envelope on `/v1`.

## A busy transcription does not slow your chat

`maxWorkers` defaults to 1: a second Whisper process on the same card is two copies of the weights
and an out-of-memory error at the worst possible moment. But because routing has been per
`(capability, model)` since v3.8, **a node busy transcribing is still a candidate for chat**. That is
v3.8 paying for itself, and it is worth stating because nobody can see it working — the failure it
prevents is "my chat got slow when someone uploaded a podcast".

## Deliberately not in this release

- **Streaming TTS.** Chunked audio needs a concatenable format and a client-side contract the OpenAI
  dialect only recently grew. v3.10 returns complete audio.
- **Diarization, alignment, voice cloning.** `verbose_json` carries the segments and timestamps
  Whisper produces anyway. Speaker labels it does not produce are not invented.
- **`/v1/audio/translations`.** One flag on the same worker; it can land whenever somebody asks.
  Shipping an untested surface to look complete is how a feature list starts lying.

## Upgrading

Nothing to do. `Tools:*` is off by default, the audio routes answer a `503` naming the capability
until a worker provides it, and a deployment that changes no config behaves identically to v3.9.

`dotnet test`: **936 passed, 0 failed, 46 skipped** — up from 876 at v3.9.0, every pre-existing suite
untouched.
