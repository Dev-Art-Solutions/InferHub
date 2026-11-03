# InferHub v3.88.0 — firewall rules for a sealed node

Phase 128. Since 3.82 a sealed node keeps InferHub's own code off the internet: it starts with no door open, and it downloads
only through its coordinator. But that is a promise about one program. The machine's other programs, and an engine's own
downloader, are held by the operating system's firewall or not at all. Now the node writes the firewall rules that match it,
and checks, below InferHub, that the machine really reaches nothing outside — and tells its coordinator what it found.

```bash
InferHub.Node firewall powershell          # Windows Defender Firewall; run the output as Administrator
InferHub.Node firewall nftables            # Linux; nft -f the output as root
InferHub.Node firewall nftables --no-dns   # the coordinator is an IP or in /etc/hosts
```

```http
GET  /api/control/firewall?format=nftables&dns=false
GET  /api/control/egress
POST /api/control/egress/check
```

## What it does

- **Rules from the node's own config.** Outbound TCP to each coordinator endpoint, resolved when the rules are printed; DNS
  unless `--no-dns` (to the system's resolvers on Windows; port 53 on nftables, with a note to narrow it); loopback and
  replies kept; everything else dropped. The header names what was resolved and how to undo it. A name that does not resolve
  is a comment saying so, not a silently missing rule.
- **Printed, never applied.** The rules change the whole machine's network, so applying them is the machine owner's call.
- **A check below InferHub.** A sealed node tries a bare TCP connect (nothing is sent) to each `Node:Egress:Probes` address —
  by default `1.1.1.1:443`, `8.8.8.8:443` and `9.9.9.9:443` — at start and every `CheckMinutes` (15). Only IP:port entries
  are probed: a name would need DNS, and the DNS the rules allow would make a closed machine look open. Any probe that
  connects is an open door; the node logs it once and `/api/control/egress` shows it.
- **The coordinator is told.** `/api/status` gains `nodes[].egress` (`probed`, `open`, `closed`, `checkedUtc`), `/metrics`
  gains `inferhub_node_egress_open{node}` (0: sealed), and the coordinator logs a warning when a sealed node can reach
  outside. It does not refuse the node. The state is stored under the node id the connection registered as.

## Upgrading

A node that is not sealed probes nothing and reports nothing; a coordinator with no sealed nodes shows nothing new. No new
package. A coordinator older than 3.88 ignores the report; the node's log and control API still say it.

## Found while building it

- **The nftables rules a Windows node printed were rejected line by line.** .NET's `AppendLine` writes CRLF on Windows, and
  `nft -c` reads the CR as "unexpected junk" at the end of every line. Found by checking the generated file with `nft -c` in
  WSL, after the unit tests had passed on their text. The nftables output is now LF always, and a test asserts it.
- **A hostname in the probe list would have counted as refused**, making a machine look sealed when nothing was tried. Names
  are now left out of the check.

## Not established

- **The rules applied to a real machine, and the check then reading `closed`.** Applying them here would have cut this
  machine off. The closed verdict is tested against refused local ports; the rules were checked by PowerShell's parser and
  by `nft -c`, not enforced.
- **macOS.** No pf rules yet.
- **The coordinator's own host.** It is the one machine that may reach outside; no rules are generated for it.

## Verification

- Suite: 2 752 passed, 63 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 997, Node 738, Mesh 548). New: `SealedNetworkTests` (Node — a probe that connects and one refused, names never probed, the verdict,
  the event, both formats, IPv6, an unresolved host, no CR in nftables, the verb), `EgressCheckMeshTests` (real SignalR —
  the check reaches the hub on registration and a later check replaces it; the scrape).
- Live, from source: a coordinator and a sealed node on this machine, which has internet. The node's first check found all
  three probes open; its log said so, the coordinator logged *"Sealed node … can reach 1.1.1.1:443, 8.8.8.8:443,
  9.9.9.9:443 itself"*, `/api/status` showed `nodes[].egress.open` with the three, and `/metrics`
  `inferhub_node_egress_open{node=…} 3`.
- The rules from `/api/control/firewall`: the PowerShell script parsed with 0 errors (PowerShell's own parser; not run);
  the nftables file, regenerated with the fix through the `firewall` verb, has 0 CR bytes and `nft -c` in WSL Ubuntu accepts
  it, where a CRLF control file is rejected with the same error as before the fix.
