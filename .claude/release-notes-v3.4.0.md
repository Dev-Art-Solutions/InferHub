# InferHub v3.4.0 — the node heals its own Ollama

Ollama is a young server moving quickly, and sometimes it wedges. Not crashes — **wedges**: the
process is alive, the port still accepts connections, and nothing ever comes back. Until this
release that took the whole node out of the fleet and left it there. The node stayed connected, kept
heartbeating, kept reporting that it had no models, and waited for somebody to notice. On a GPU box
in a cupboard, somebody noticing can take a day.

The node now watches its own Ollama and puts it back.

```jsonc
"Ollama": {
  "Endpoint": "http://localhost:11434/",
  "Supervisor": {
    "Enabled": true,
    "AutoInstall": false
  }
}
```

Off by default, one key turns it on, and **zero new dependencies** — `Process`, `Socket` and
`HttpClient` all ship in the framework.

## Two symptoms, two cures

It probes `GET /api/version` every fifteen seconds and acts after **three consecutive** failures.
One slow answer is not a fault; a machine that reacts to a single missed probe spends its life
reacting. What it does next depends on what it found, and that distinction matters more than it
looks:

| State | How it looks | What it means | What the node does |
|---|---|---|---|
| healthy | answered inside `ProbeTimeout` | fine | nothing |
| unreachable | the socket never opened | not running | **start** it |
| wedged | socket opened, nothing came back (or a 5xx) | running but stuck | **stop**, then start |

Collapsing the last two gives you a log line that confidently reports a restart that never happened:
`start` on a wedged process fails on a port that is already bound.

> **The probe has its own `HttpClient`, and it is not redundant with the inference one.**
> `Ollama:RequestTimeout` is five minutes on purpose, because a cold 70B load takes that long.
> Probing over it would mean a wedged Ollama — the exact case this feature exists for — takes five
> minutes to fail one probe and a quarter of an hour to cross a three-probe threshold. If you ever
> find yourself merging the two clients, this feature stops working while every test still passes.

## Knowing when to stop trying

A supervisor that restarts a server every fifteen seconds forever has not fixed an outage, it has
manufactured a worse one: no model ever finishes loading, so the box never comes back even when the
underlying problem is gone.

So restarts are **budgeted** — three attempts in ten minutes, with a widening gap between them, and
up to two minutes of patience for the restarted server to answer, because a service that starts by
loading a model is slow rather than broken. Past the budget the node stops restarting, says so once
at error level, and **keeps probing**. That last part is the point: giving up on restarting is not
giving up on recovering. When a human fixes the driver, or the machine finishes whatever it was
choking on, the next probe succeeds and the node reports its models again on its own — immediately,
rather than waiting out the model-refresh interval.

## Two things it deliberately will not do

**It will not touch an Ollama that is not on the same machine.** If your endpoint points at a shared
Ollama serving four nodes, or at an OpenAI-compatible server like vLLM, the supervisor logs one line
at startup naming why and never probes again. A shared server restarted over one node's network
hiccup is a four-node outage caused by the node with the worst link, and somebody else's inference
server is not ours to bounce. The same rule covers containers for free.

**It will not install software unless you asked twice.** Turning the supervisor on consents to
restarting a process. Installing Ollama where it is missing is a separate switch, off by default,
**one attempt only**, fired only when discovery finds nothing at all (not installed — never "not
answering"), with the exact command written to the log before it runs and a configurable
`InstallUrl` so an air-gapped fleet points at its own mirror rather than at the internet.

A service manager also always wins over spawning: where the `Ollama` Windows service or an
`ollama.service` unit exists, the node restarts it through `sc.exe` / `systemctl` rather than running
`ollama serve` itself — two servers fighting over `:11434` is a worse outage than the one being
fixed. A node service running under a restricted account **cannot** control a machine-wide one, and
that is now reported as one line naming the privilege instead of an "Access is denied" stack trace.
See [deploy/windows/README.md](../deploy/windows/README.md).

## One honest limitation

**A restart kills whatever was streaming through that node.** There is no way around it, and waiting
for the work to drain first would be worse — a single stuck request would pin the node in a broken
state indefinitely, which is precisely the failure this feature exists to end. By the time a restart
happens, Ollama has not answered a trivial version check in three quarters of a minute; that stream
was not going to finish. The log line says how many requests were in flight, so the cost is recorded
rather than hidden.

## Found by using a real socket

The first implementation classified the two failure states from the exception `HttpClient` threw:
connection refused is an `HttpRequestException`, a wedge is a `TaskCanceledException`. That is wrong
on Windows, where a closed loopback port is silently dropped rather than refused — the connect hangs
to its timeout and produces *exactly the same* bare `TaskCanceledException` a wedged server does. A
stopped Ollama would have been treated as wedged and answered with a stop that had nothing to stop.

A stub HTTP handler cannot find that: it can only throw the exception the test author already
believed in. A real closed port and a real accept-but-never-answer listener can, and did. The probe
now performs the TCP connect itself and records whether a socket was actually established, so
"unreachable or wedged?" is a fact rather than an inference — and the two tests that caught it use
real sockets on purpose.

## What is not in this release

The coordinator does **not** learn "backend unhealthy" as a typed signal — there is no new heartbeat
field, no health column on `/api/status`, no console change. The fleet already stops routing to a
broken node through the empty model report, and that report now says *why* it is empty instead of
reading the same as "this box has nothing installed". Recorded here so the omission is a decision
rather than an oversight: if the logs turn out not to be enough, that is the next phase.

## Upgrading

Nothing to do. With `Ollama:Supervisor:Enabled` left at its default of `false`, a v3.4.0 node behaves
exactly as v3.3.0 did: no probe traffic, no new log lines, nothing registered.
