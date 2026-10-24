# InferHub v3.77.0 — sharing on the owner's terms

Phase 116, plus InferHub Desktop 0.2.0 on its own tag. In 3.76 a node's owner could choose `always`, `idle` or
`paused`. In a network of friends' PCs, the next two questions are "use my GPU at night, not while I work" and "no
more than two hours a day". v3.77 adds both as settings, live, on the node:

```json
"Node": { "Sharing": {
  "Hours": ["mon-fri 18:00-08:00", "weekends all day"],
  "TimeZone": "Europe/Sofia",
  "DailyNetworkMinutes": 120 } }
```

## Sharing hours

- Each line is days, then hours. Days are `mon`…`sun`, ranges (`mon-fri`, `fri-mon`), lists (`sat,sun`), `daily`,
  `weekdays` or `weekends`. Hours are `HH:mm-HH:mm` (`24:00` for midnight at the end) or `all day`.
- **A window belongs to the day it starts on.** `fri 22:00-06:00` is Friday night into Saturday morning. Windows
  that touch or overlap merge, so `daily 22:00-24:00` plus `daily 00:00-07:00` does not close at midnight.
- The times are in `Node:Sharing:TimeZone` (an IANA or Windows id), or the machine's own zone if unset. A start
  that falls in a spring-forward gap opens at the first minute the clock shows.
- No lines means any hour, exactly as in 3.76. A line that does not parse stops the node from starting and is a
  400 from the control API, with the reason.

## A daily allowance

`Node:Sharing:DailyNetworkMinutes` is how many minutes of network jobs the node runs a day. Each job counts on its
own, so two at once use the minutes twice as fast. Your own local work never counts. The minutes renew at midnight
in the same zone. **Restarting the node does not reset them**: each finished network job writes the day's total to
`<Node:DataDirectory>/sharing-usage.json`, and a node that starts on the same day reads it back. `0` (the default)
means no allowance.

## How it shows

Outside its hours, or once the minutes are used, the node's sharing goes paused. This is the same pause as 113, with
a new reason and the time it lifts:

```json
"sharing": { "mode": "always", "paused": true,
  "reason": "outside its sharing hours; shares again Mon 09:00",
  "resumesAtUtc": "2026-10-26T07:00:00+00:00",
  "hours": ["mon-fri 09:00-17:00"], "timeZone": "Europe/Sofia", "zone": "Europe/Sofia",
  "allowance": { "minutes": 120, "usedSeconds": 60.7, "leftSeconds": 7139.3, "resetsAtUtc": "…" } }
```

- **The hub routes around it** and its console shows a `not sharing · until Mon 09:00` pill, with the reason on
  hover. Before 3.77 the console showed no sharing state at all.
- **A network job already running finishes.** Only new ones wait, as with every pause since 113.
- Set the terms with `PUT /api/control/sharing {"hours":[…],"timeZone":"…","dailyNetworkMinutes":120}`. Any of
  `mode`, `idleSeconds`, `hours`, `timeZone` and `dailyNetworkMinutes` can be sent; what is left out keeps its
  value. Or use the desktop app's Overview tab.
- **No hub change is needed for the pause.** A 3.76 hub already holds it, because it is the same report.
  `resumesAtUtc` is a new field, and older hubs ignore it.

## Fixed: the hub blamed the backend for an owner's pause

When every node holding a model was paused by its owner, the hub answered
`503 every node holding model 'x' reports an unhealthy inference backend`. That was wrong since 3.76, and with
sharing hours it would happen every night. The hub now names the cause:

```
503  Retry-After: 2716
every node holding model 'qwen2.5:0.5b' is not sharing now (live116-node: today's network allowance of 1 min is used; shares again tomorrow 00:00)
```

The `Retry-After` header is when the first of those nodes says it resumes, capped at an hour. If every holder is
unhealthy, the message is exactly 3.76's. A mix of causes is listed as a mix.

## InferHub Desktop 0.2.0

The Overview tab gets a **Sharing hours and a daily allowance** section: hours (one per line), time zone, minutes a
day, and a line saying what applies now and how much of today's allowance the network has used. It needs a 3.77
node.

## Found by running it

A real hub and node from source, the machine's Ollama, `qwen2.5:0.5b`:

- **Hours closed now** (`mon-fri 09:00-17:00`, on a Saturday night in Sofia): the node paused within a second, and
  the hub had `resumesAtUtc` 2026-10-26 07:00 UTC, which is Monday 09:00 *after* Sunday's change from summer time.
  The node re-reported the pause to a restarted hub at registration.
- **The 503 above was the first answer**, and it was the misleading backend message. That was fixed and checked
  again live before this release.
- **Allowance of 1 minute**: four concurrent network chats took 9.1, 15.3, 15.4 and 20.9 s. The node counted
  60.7 s, paused, and named midnight Sofia (21:00 UTC). A local chat of 2.7 s on the same node counted 0.
- **The node was killed and started again**: `sharing-usage.json` held `{"Day":"2026-10-24","Seconds":60.7}`, the
  new process came up paused, and the hub's `Retry-After` was 2716 s, the time to midnight.
- **The desktop app** attached to that node with a pasted token and rendered the new section (screenshot from the
  app's own renderer).

## Tests

`NodeControlTests` +18 (the hours table: parsing, open/closed and the next change, merging, a whole week, a DST
gap; the pause and its timer; the allowance with one and two jobs, local work, a restart and the next day; the
validator; the route and the status), `SharingMeshTests` +1 (a real hub hears the reason and the resume time, and
routes nothing), `BackendHealthRefusalTests` +2, `SharingTermsTests` (3, desktop). Full suite green.

## Not established, said out loud

- **The day's boundary was not crossed live.** The reset at midnight and a job running across midnight (only
  today's part counts) are covered by tests with a manual clock, not by a node left running overnight.
- **A DST change was not lived through.** The DST-gap rule and Monday's post-DST resume time were checked by test
  and by reading the live `resumesAtUtc`. No node was running across Sunday's change.
- **The console pill** was written against the status payload and its contract test. It was not looked at in a
  browser for this release.
- **The desktop app on Linux and macOS** is still built by CI only, as for 0.1.0.

## Upgrade

Nothing to do. With no `Hours` and no `DailyNetworkMinutes`, a node behaves as 3.76. Update the coordinator if you
want the corrected 503 and the console pill. The pause itself holds on a 3.76 hub.
