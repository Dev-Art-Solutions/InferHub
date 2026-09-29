# InferHub v3.54.0 — one dispatch deadline per capability

When the hub sends a job to a node, it waits a limited time for the answer. Until now that limit
was a single number, `Dispatcher:TimeoutSeconds`, and it applied to every kind of job: a chat
answer, an embedding, a transcription, a picture, a five-second video clip. The default is 300
seconds. v3.28 measured a real clip at ~143 s of cold model load plus ~330 s of generation, so at the
default a video dies while its model is still loading. The advice since then has been "raise it to
1800", which also gives a stuck chat request half an hour.

v3.54 lets you set the limit for each capability separately.

## What's new

```json
"Dispatcher": {
  "TimeoutSeconds": 300,
  "Deadlines": { "video": 3600, "image": 900 }
}
```

or, in a container: `Dispatcher__Deadlines__video=3600`.

- **One deadline per capability.** The key is the capability the job was routed on: `chat`,
  `embed`, `transcribe`, `speak`, `image`, `image-edit`, `video`, `rerank`, or a custom tool's own
  capability name. `chat` also covers `/api/generate`. Keys are case-insensitive. A capability you
  do not name keeps `TimeoutSeconds`.
- **Nothing changes unless you set it.** `Deadlines` is empty in the shipped config, so a hub that
  upgrades without touching config times every job exactly as v3.53 did. We did not ship
  `video: 3600` as a default, even though it is the right number, because that would change an
  upgraded hub without you reading this. The `appsettings.json` comment gives the figures.
- **A timeout now says which clock ran out.** Before, a job that ran out of time ended with
  `The operation has timed out.`, at `progress: 0`, with nothing to say which of the three possible
  deadlines it was. Now the message names the capability, the seconds and the config key:

  ```
  the hub's dispatch deadline for video (300 s, Dispatcher:TimeoutSeconds) expired before the node answered
  ```

  A failed video or image job carries it in `error.message`. The hub also logs one warning line
  with the same three facts.
- **A bad value stops the hub at startup.** `Dispatcher:Deadlines:video=0` is refused, and the error
  names the key. (`TimeoutSeconds` is still read as 1 if it is below 1, as it always was.)
- **The node's own time limits are separate.** `Ollama:RequestTimeout`, `Upstream:TimeoutSeconds` and
  a tool manifest's `requestTimeoutSeconds` still apply. A hub deadline longer than the node's gains
  nothing, because the node gives up first. For video, the shipped diffusion manifest already allows
  3600.

## Two bugs found while writing the tests, fixed here

No test in the solution had ever let a dispatch deadline actually run out. The first ones that did
found two bugs that had been there since the tool routes existed:

- **`/api/tools/*` and `/v1/audio/*` answered a timeout with a bodyless `500`.** Chat has answered
  `504` since v1.x, but nothing on the tool routes caught the timeout. They now answer **`504`**
  with the message above: in the native `{"error": ...}` shape on `/api/tools/*`, and in OpenAI's
  envelope with `code: "deadline_exceeded"` on `/v1/audio/*`. A stream that has already started is
  not affected, because its `200` has already been sent.
- **A blocking job the hub gave up on was never counted as failed.** When a blocking job timed out,
  the caller disconnected, or the send failed, the hub's routing count was corrected but its
  metrics were not. So `inferhub_requests_in_flight` went up by one for every such job and never
  came back down. The streaming paths always counted it. Blocking jobs are now counted as failed
  exactly once, and a late answer from the node finds nothing to count.

## How it is tested

- `DispatchDeadlineTests` (Coordinator): the lookup, config binding (including a key written in
  upper case), the validator, the shipped `appsettings.json` read from disk, and a real `Dispatcher`
  whose clock runs out. The tests cover blocking and streaming, inference and tool jobs, a stream
  that never starts and a stream that started. They also check that the node is sent `CancelJob`,
  and that another capability on the same hub keeps its longer deadline.
- Mesh tests through a real coordinator, SignalR connection, node and child process:
  `/api/tools/echo` past its deadline answers `504` naming the key, and within its deadline it
  answers `200`. `/v1/audio/speech` answers `504` with `deadline_exceeded`, while a transcription
  on the same hub is unaffected. A `/v1/videos` job fails on its own `video` deadline and names it.
  The echo worker gained `--audio-delay-ms` so the audio routes can be made slow.
- Mutation checks: with the audio filter removed, the speech test fails with `500`. With the
  metrics fix removed, the in-flight test fails with `1`.
- `dotnet test InferHub.sln`: **1717 passed, 53 skipped** (Shared 202, Coordinator 820, Node 230,
  Mesh 465).

Zero new `PackageReference`, and `InferHub.Shared.csproj` is still empty.

## Checked on the published image

(to be filled in after the tag)

## Not established, said out loud

- **A real clip under a real `video: 3600`.** The deadline is checked with the echo worker on a
  fixture clock. The v3.28 measurement is the reason for the figure, but this release did not render
  a clip on a card.
- **`inferhub_requests_in_flight` on a long-running hub.** The leak is fixed and tested at the
  `Metrics` level. Nobody has watched a production hub's gauge fall back to zero.
