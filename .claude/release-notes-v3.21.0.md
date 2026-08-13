# InferHub v3.21.0 — a large upload streams *through* the hub instead of landing in it

Since v3.9, anything you uploaded to InferHub — a recording to transcribe, a picture to edit — was
read into the coordinator's memory in full, capped at 25 MB, and forwarded to a node as bytes on the
job. This release adds a second path for uploads past that cap: the node **pulls** them over the
connection it already opened, 64 KB at a time, straight into the file its worker is about to read.
The hub never holds the body.

**It is off by default.** With `Tools:MaxStreamedBytes` unset, v3.21 behaves byte for byte as v3.20
did — same 25 MB ceiling, same `413`, same key named in it.

## First, a correction we owe you

The design note behind the old ceiling said the uploaded bytes were held *"in memory … no temp file,
no cache"*. That was true of the code we wrote and **false underneath it**, and this release is
where we found out.

ASP.NET's `ReadFormAsync` buffers each file section through `FileBufferingReadStream`: under
`FormOptions.MemoryBufferThreshold` (**64 KB**) it stays in memory, and over it the section is
written to an `ASPNETCORE_*.tmp` file in the process's temp directory. Measured on .NET 10, against
a real Kestrel host:

| Upload | Temp file created |
|---|---|
| 32 KB | none |
| 3 MB | **one, 3 145 570 bytes** |

Which means that from v3.9 to v3.20, **every real audio and image upload did briefly touch the
coordinator's disk** — not through anything we wrote, but through the framework beneath it. Nothing
was retained past the request and nothing was logged, so no data outlived a request; but the claim
was wrong and it is corrected in the docs rather than quietly edited.

The streamed path added here does not do this, and the test suite asserts it: after a 40 MB upload,
no temp file the size of the body exists.

## The three ceilings, and why one key now moves all of them

Raising the attachment cap alone never worked, because two of the three limits between a client and
a worker are ASP.NET's:

| Limit | Value |
|---|---|
| `Tools:MaxAttachmentBytes` | 25 MB — ours |
| Kestrel `MaxRequestBodySize` | **30 000 000 bytes** — today's cap clears it by ~3.7 MB |
| `FormOptions.MultipartBodyLengthLimit` | 134 217 728 bytes |

`Tools:MaxStreamedBytes` now derives the other two, on the upload routes only. An operator who
raises our key and then meets a 413 with none of our text in it has been handed a puzzle by a design
that knew the answer. It is applied per route rather than globally, so `/api/chat`, `/v1/embeddings`
and the vector data plane keep the bound they have always had.

## What you get, and what it costs

```bash
curl -X POST http://hub:5080/v1/audio/transcriptions \
  -H "Authorization: Bearer $KEY" \
  -F model=whisper-small \
  -F file=@lecture.m4a          # 380 MB
```

Three things are worth knowing before turning it on, and all three are in the README:

- **A streamed job is not retried on another node.** The body has been read and a client's socket
  cannot be rewound, so a node lost mid-upload is a `502` naming `node_lost` and the caller decides.
  This is a real step down in reliability, on a path you opt into by size.
- **Send your form fields before the file part.** The request is routed before the bytes arrive, so
  a `model` that turns up after the file is a `400` that says so. `curl -F model=… -F file=@…` and
  the OpenAI SDKs already do this; a solo node has nothing to route and accepts any order.
- **A fleet with no capable node answers `503` naming that reason** — never a silent fall back to
  buffering, which would work brilliantly right up to the 25 MB it cannot do. Support is declared
  per node (`Tools:MaxStreamedBytes` on the node is the declaration), and a v3.20 node is read as
  "no", so a mixed fleet keeps working.

## Where it applies, and where it deliberately does not

`POST /api/tools/{capability}` and `POST /v1/audio/transcriptions`, on the coordinator and on a solo
node.

**The image routes keep the 25 MB cap**, and that is a decision rather than an omission. A body can
only stream while somebody is waiting for it, and an image job is answered *before* it runs:
`POST /api/images/jobs` returns `202` immediately, and `/v1/images/edits` stops waiting at
`Images:SyncMaxWaitSeconds` while the job keeps going. Either way the bytes would have to outlive
the request that carried them — which means storing them on the hub, which is the image archive this
project has now refused three times (no result URLs in v3.14, an in-memory expiring job store in
v3.15, no console gallery in v3.19). Lifting the ceiling for images needs the *result* direction
solved first, and that is not this release.

## Under the hood

The node pulls rather than the hub pushing, so the mesh's outbound-only property is untouched: the
stream is established by the node's own invocation on the connection it opened, exactly as its
profile pull is. The hub still never dials a node.

The node side turned out small, and the reason is a decision from v3.9: a worker has always been
handed a **path** into a per-request scratch directory rather than bytes. Writing that file from a
socket instead of from a `byte[]` changes nothing above it — **the worker protocol did not change by
one field**, and any worker written against v3.9 works unmodified.

## Honest notes

- **The published images have not been pulled and run at the time of writing.** Nothing about the
  artifact is claimed here; the numbers above come from the test suite and from a measured probe on
  a development host.
- **Peak memory is not measured.** The suite proves the hub writes no temp file the size of the
  upload and that the job carries no attachment — which is a weaker statement than a memory profile,
  and is stated as the weaker one.
- **Real SDK clients have not been pointed at it**, so the field-ordering claim above is verified
  against `curl`-shaped and .NET-shaped multipart bodies, not against `openai-python`.

**Tests: 1 221 passing, 48 skipped, 0 failing** (+22). Zero new `PackageReference`s, and
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.
