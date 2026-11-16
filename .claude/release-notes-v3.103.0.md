# InferHub v3.103.0 — a long answer is a job you can follow (phase 150)

A chat that takes minutes no longer has to be one HTTP connection held open. Submit it to `POST /api/jobs`: the
coordinator answers `202` with an id, runs the request the way `/api/chat` would, and keeps a status that the caller,
the node running it and the coordinator all read and update. It works for a model split across nodes too, where the
status also says how far each member's load is.

## What is new

- **`/api/jobs`** — `POST` (submit a `chat` or a `generate`), `GET /api/jobs` (this client's jobs),
  `GET /api/jobs/{id}` (status), `GET /api/jobs/{id}/events` (the status as server-sent events, on every change),
  `GET /api/jobs/{id}/result` (the answer `/api/chat` would have given; `202` with the text so far while it runs),
  `DELETE /api/jobs/{id}` (cancel a running job — the node is told to stop — or drop a finished one).
- **The status**: `queued` · `running` · `succeeded` · `failed` · `cancelled`, with a stage (`waiting`, `sent`,
  `preparing`, `generating`, `done`), a sentence when it is waiting for something, the node, pieces so far, tokens and
  tokens per second, and the error with its status when it failed.
- **The node reports on the job it runs.** When it gets the job, when its engine's first piece arrives, and every two
  seconds: a stage and a count, never the text. `preparing` in a status is the node's own word, and
  `node.reportedAt` going stale means the node went quiet rather than the model. The node's owner sees the same jobs
  at `GET /api/control/jobs`.
- **For a split model** (`ShardGroups`) the status carries the group — ready or why not, and for each member its
  layers and `sentMiB` of `expectMiB` — and the head's reports say the same in one sentence.
- **A job waits out a `503`.** A full fleet, a group reassembling after a member left, owners not sharing now: the
  blocking route answers these with `Retry-After`; a job stays `queued` with the reason and goes when it can, for up
  to `Jobs:QueueSeconds`.
- **Its own deadline**, `Jobs:DeadlineSeconds` (3600), in place of `Dispatcher:TimeoutSeconds` (300).
- **`GET /api/admin/jobs`** and a **Jobs** panel in the console: every client's jobs, with no request and no answer.
- **`Jobs:`** — `Enabled` (true), `MaxJobs` (256), `MaxActivePerClient` (16), `RetentionSeconds` (600),
  `QueueSeconds` (600), `DeadlineSeconds` (3600).

## What does not change

Every existing route answers as in 3.102. The job runs through the same dispatch path as `/api/chat` (admission, the
client's concurrency lease, routing, queues, priority classes, organisations, providers, metering). Nothing is written
to disk: jobs and their answers are in memory, a finished one goes after `Jobs:RetentionSeconds` or on `DELETE`, and a
coordinator restart loses every job. A node older than 3.103 runs a job as before and simply does not report on it; a
coordinator older than 3.103 never asks a newer node to. Zero new packages.

## Checked

- `dotnet test InferHub.sln`: 2 908 passed, 70 skipped (the gated suites), 0 failed.
- From source, a real coordinator and a real node over a real Ollama (`qwen2.5:0.5b`, 300 tokens): `202` with the
  id; `running`/`preparing` with the node's report while the model loaded cold; the node's own `/api/control/jobs`
  showing the job; `succeeded` after 17 s with `tokens: 300`; the answer on `/result` with
  `X-InferHub-Served-By: node`; the job on `/api/admin/jobs` under client `anonymous`.

## Not established

- **A coordinator restart mid-job** loses the job by design; nothing was run to show what a caller polling it sees
  beyond the `404`.
- **A cluster of coordinators** (active-active): a job lives on the coordinator that accepted it; asking another one
  for it is a `404`. Not run.
- **A solo node** has no `/api/jobs`; `/v1` has no job routes; a retrieval header on a submit is refused, not applied.
- No `/metrics` series for jobs.

## Checked on the published images

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.103.0` (revision `88ca3d8`) and `inferhub-node:3.103.0`, in the shard
lab (`deploy/lab/shards/tunnel-lab.sh`): one coordinator, four node containers of 4 GiB, an 8B model split over three of
them with the fourth as the spare.

- **A job on a split model.** `POST /api/jobs` for the group's name: `202`; `running`/`generating` with the head as its
  node, the head's own report every two seconds (`node.reportedAt`, `node.pieces`), `tokensPerSecond` 6.6–7.0, and the
  `group` block naming the three members with their layers and `sentMiB` of `expectMiB`; `succeeded` after 10 s with
  `tokens: 64`; the answer on `/result` with `X-InferHub-Served-By: node`.
- **A member killed, and a job submitted in the same second.** The blocking route answers this moment with a `503`.
  The job stayed `queued`/`waiting` for 40 s with `detail` "group 'llama-8b-split' is starting on shard-a: starting",
  its `group.members[].sentMiB` climbing from 0 as the head loaded the reassembled group, then went `preparing`,
  `generating`, `succeeded` — and its answer was read from `/result`.
- `/api/admin/jobs` listed both with their client and no content.

**Not established there:** `sentMiB` goes on counting what the head sends a member after the load, so a long-running
group shows more sent than expected (seen: 1585 of 1389) — the clients clamp it when they work out a loaded fraction.
A node's own `/api/control/jobs` was read from source only (the node image has no `curl` to ask it from inside).
A 70B was not run for this release; the lab used the 8B.

## Published

Blog: https://blog.devart.solutions/blog/inferhub-3-103-a-long-answer-is-a-job-you-can-follow (ID 6afaf3596bbffea5b2464f6b). Docs: https://inferhub.devart.solutions/#idocs_jobs. Clients: python 1.1.1, js 1.1.1 and go 1.1.1 installed from their registries and run against these images; dotnet 1.8.1 reached NuGet later the same day (the repository's API key had expired; the workflow now uses Trusted Publishing) and was installed from there and run against a coordinator and node built from this release's source.
