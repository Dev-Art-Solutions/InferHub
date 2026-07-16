# Social drafts — InferHub v2.1.0 (Windows service)

Brand voice "we", plain, no hype. Posted manually by Iliya.
Repo: https://github.com/Dev-Art-Solutions/InferHub
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v2.1.0
Blog: https://blog.devart.solutions/inferhub-v2-1-0-windows-service

---

## Facebook — facebook.com/DevArtSolutions

InferHub nodes can now run as a proper Windows service. Auto-start on boot,
restart-on-failure, logs in Event Viewer, and a single PowerShell script to install —
same node, cleaner packaging, with the dev and Linux paths untouched. The interesting
part is under the hood: both the console and the service compose one shared root, so the
two can't drift apart.

Write-up: https://blog.devart.solutions/inferhub-v2-1-0-windows-service
Code + download: https://github.com/Dev-Art-Solutions/InferHub

---

## X — @DevArtSolutions

**Primary (~216 chars incl. link):**

InferHub 2.1: run a node as a Windows service — auto-start, restart-on-failure, Event
Viewer logs, one PowerShell install script. Same node, cleaner packaging; dev + Linux
paths untouched. https://github.com/Dev-Art-Solutions/InferHub

**Optional follow-up (design angle, 235 chars):**

The design: instead of a one-line AddWindowsService() in the worker, the Windows host is a
separate thin project composing the node's shared AddInferHubNode root. The node stays
cross-platform — the Linux analog is just AddSystemd().
