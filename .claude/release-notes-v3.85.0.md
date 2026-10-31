# InferHub v3.85.0 — an audit trail

Phase 125. A hub already knew who called it: every request is authenticated and metered per client. But the usage
ledger is counts per client and model, and the admin actions it remembered were one line per node, in memory. Neither
could answer the questions an operator of a closed cluster is asked: *who used this key at 03:12, from where, and which
machine ran it? Who cordoned gpu-2? Which box tried to join with the wrong secret?* `Audit:Enabled` writes the answer
down: one JSON line per request and per node at the door, never what was said.

```bash
# on the coordinator
Audit__Enabled=true          # off by default
Audit__RetentionDays=90      # day files older than this are deleted
```

```json
{"at":"2026-10-31T19:45:57Z","event":"request","who":"alice","remote":"::1","method":"POST","path":"/api/chat",
 "status":200,"ms":20637,"model":"qwen2.5:0.5b","capability":"chat","nodes":["ace6b0a5-…","050dfd9f-…"],"servedBy":"node"}
```

```http
GET /api/admin/audit?from=2026-10-31T00:00:00Z&node=<id>&who=alice&event=node.refused&status=401&limit=100
```

## What it does

- **One line per request.** When; **who**: the client id the key resolved to, `admin:` with the first 8 hex digits of
  the admin key's SHA-256, `loopback`, `node` for the relay, or `-` for a key that resolved to nobody. Then the remote
  address, the method, the **path (never the query string)**, the status, the milliseconds (a stream to its last byte),
  the model asked for and the kind of work, **every node a job of the request was sent to, in order**, and
  `X-InferHub-Served-By`. A failover appends, so the last node in the list answered it.
- **Never what was said.** No body, prompt, answer, query string, header value, key, or a hash of any of them. The
  tests send a prompt, a query value and both kinds of key through the real guards and grep the files for them.
- **Reads are not actions.** `/api`, `/v1`, the sealed nodes' relay and ChatGPT pairing are recorded. Reads of
  `/api/status` and of the admin API are not, because the console polls them every five seconds. Two exceptions: a
  **refused** request is recorded on every method (a wrong key is the line an auditor wants first), and so is a read of
  the trail itself. The middleware runs before the key guards, so what they turn away is on the trail.
- **The fleet's door.** `node.registered` (node id and name), `node.refused` (a wrong or missing enrollment secret,
  with its address) and `node.disconnected`.
- **Off the request path.** One background writer appends to `audit-yyyy-MM-dd.jsonl` (UTC). A full queue (10 000
  lines) or an unwritable directory drops a line and counts it; it never slows or fails a request.
- **Read it.** `GET /api/admin/audit`, behind the admin key: newest first, filters on time, who, node (any node a
  failover tried), model, event and status, up to 1 000 lines. Or read the files; they are plain JSONL.
- **Visible.** `/api/status` gains `audit` (written, dropped, days, bytes) and `/metrics` gains
  `inferhub_audit_records_total`, `inferhub_audit_dropped_total`, `inferhub_audit_days`, `inferhub_audit_bytes`, only
  while the trail is on.
- **Rule 4's eighth exception.** Off by default. Under `Audit:DataDirectory` (`./data/audit`; the image sets
  `/data/audit`). Day files older than `RetentionDays` (1–3660) are deleted at start and at each midnight; nothing else
  in the directory is touched.

## Upgrading

Nothing changes unless you set `Audit:Enabled`. With it off the middleware passes straight through, no directory is
created, `/api/status` has no `audit` key, `/metrics` has no `inferhub_audit_*` series, and `GET /api/admin/audit`
answers `{"enabled":false}` naming the key. No node change, no wire change, no new package.

## Found while building it

- **A `DropWrite` channel lies about drops.** The writer's queue was first `BoundedChannelFullMode.DropWrite`, under
  which `TryWrite` answers `true` for a line it threw away — so a full queue would have lost lines without the counter
  moving. It is `Wait` now: `TryWrite` still never waits, and a `false` is counted.
- **A reader must share the file.** The first test that read today's file while the hub was writing it failed with a
  sharing violation on Windows: the writer holds it open for append. The hub's own reader opens it with shared
  read/write; a log shipper must too (said in the README).
- **A node with a stale secret writes a line every five seconds**, because it retries every five seconds. Seen live:
  the intruder in the rig wrote 30+ `node.refused` lines in three minutes. Kept, deliberately — each attempt is an
  attempt — and named in the README. It is about 2 MB a day per misconfigured node.

## Not established

- **Which node ran an async image or video job.** The submit's line is written when it answers 202; the background
  dispatch that runs later is not that request's line and is dropped rather than half-reported (125 D2).
- **Tamper-evidence.** No hash chain, no signing: a box's root can edit any file. Ship the files off the box if that
  matters.
- **A full disk.** The unwritable-directory path (the writer stops, lines are counted as dropped) is code, not a run.
- **The HA pair.** Each coordinator writes its own trail; not run with two.
- **A console panel.** There is none; the endpoint and the files are the interface.

## Verification

- Suite: 2 711 passed, 63 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 971, Node 728, Mesh 543). New: `AuditTrailTests` (27, Coordinator — the real key guards over a real socket, day files,
  the window, the filters, a torn last line, the keep/skip table, the fingerprint, the privacy grep, the scrape) and
  `AuditTrailMeshTests` (real SignalR: a node registers, runs a job through the real `Dispatcher`, a stranger with the
  wrong secret is refused, the node leaves; the trail names each, and the prompt is not on disk).
- Live, from source, on this box: a hub with `Audit:Enabled` and keys required even on loopback, nodes on the real
  Ollama 0.40 with `qwen2.5:0.5b`, and a node with the wrong secret.
  - Three chats and a stream from client `alice`: each line names the node that ran it; the stream's line has
    `ms: 51458`, its full length. A chat for a model nobody has: 404 with the model named, no nodes.
  - A stolen key (401, `who: "-"`), a guessed admin key on a read (401, kept), a cordon and an uncordon
    (`who: "admin:d2a23839"`), an admin read with the right key (not kept).
  - `node.registered` for both nodes with their names; `node.refused` every 5 s for the intruder; `node.disconnected`
    when a node was stopped through its own control API (`/api/control/restart?force=true`).
  - **A live failover:** a long story on gpu-d, gpu-d stopped mid-answer, the hub retried on gpu-c, the client got 200
    in 20.6 s, and the line says `"nodes":["<gpu-d>","<gpu-c>"]`.
  - `grep` of the audit directory for the prompts, the query value and both keys: nothing.
  - `/api/status` `audit` `{"written":37,"dropped":0,"days":1,"bytes":5924}` and the four series on `/metrics`.
