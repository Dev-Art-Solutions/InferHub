// Management console for InferHub.
//
// The admin bearer key lives in this closure for the lifetime of the tab —
// never localStorage / sessionStorage.
(() => {
  let adminKey = null;
  const rowMessages = new Map();      // nodeId -> { text, isError }
  const pendingActions = new Set();   // `${nodeId}:${action}` while a request is in flight
  const draining = new Map();         // nodeId -> { startedAt }

  const STATUS_POLL_MS = 5000;        // /api/status (metrics, uptime, models)
  const NODES_POLL_MS = 3000;         // /api/admin/nodes fallback when stream is down
  const STREAM_RECONNECT_MIN_MS = 1000;
  const STREAM_RECONNECT_MAX_MS = 15000;

  let streamAbort = null;
  let streamReconnectDelay = STREAM_RECONNECT_MIN_MS;
  let streamState = "connecting"; // 'connecting' | 'live' | 'polling' | 'offline'
  let nodesPollHandle = null;
  let statusPollHandle = null;

  let latestNodes = [];
  let latestStatus = null;
  let collectionsPollHandle = null;

  // The documents panel talks to the *client*-scoped API (Auth:ApiKeys), so it holds its own key.
  let clientKey = null;
  let documentsCollection = null;
  let documentsCollectionsSignature = "";

  const VECTOR_FEED_MAX = 40;
  const vectorFeed = [];    // newest first

  const modelCommands = new Map();  // commandId -> latest ModelCommandProgress frame (newest render on top)

  // ---------------------------------------------------------------- formatting

  const fmtSeconds = (s) => {
    if (s == null) return "—";
    if (s < 60) return `${s.toFixed(0)}s`;
    const m = Math.floor(s / 60), r = Math.floor(s % 60);
    if (m < 60) return `${m}m ${r}s`;
    const h = Math.floor(m / 60);
    return `${h}h ${m % 60}m`;
  };

  const fmtBytes = (b) => {
    if (b == null) return "—";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let i = 0, v = Number(b);
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
  };

  const fmtRelativeAge = (iso) => {
    if (!iso) return "—";
    const then = Date.parse(iso);
    if (Number.isNaN(then)) return "—";
    const sec = Math.max(0, (Date.now() - then) / 1000);
    return `${fmtSeconds(sec)} ago`;
  };

  const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");

  const statePill = (node) => {
    const pills = [];
    if (node.ageSeconds < 15) pills.push(`<span class="pill pill-ok">online</span>`);
    else if (node.ageSeconds < 30) pills.push(`<span class="pill pill-warn">stale</span>`);
    else pills.push(`<span class="pill pill-err">missing</span>`);

    if (draining.has(node.nodeId)) {
      pills.push(`<span class="pill pill-warn">draining</span>`);
    } else if (node.cordoned) {
      pills.push(`<span class="pill pill-warn">cordoned</span>`);
    }
    return pills.join(" ");
  };

  const labelChips = (labels) => {
    if (!labels) return "—";
    const entries = Object.entries(labels);
    if (entries.length === 0) return "—";
    return `<div class="labels">${entries.map(([k, v]) =>
      `<span class="label-chip">${escapeHtml(k)}=${escapeHtml(v)}</span>`).join("")}</div>`;
  };

  // Phase 40. What this node is routed for. "—" is a real state and it means the node holds no
  // models, or its operator turned every kind off — either way, nothing will be sent to it.
  const capabilityChips = (capabilities) => {
    if (!capabilities || capabilities.length === 0) return "—";
    return `<div class="labels">${capabilities.map(c =>
      `<span class="label-chip">${escapeHtml(c)}</span>`).join("")}</div>`;
  };

  // Phase 43. Which profile this node is under and whether it took. "conflict" is the hub's own
  // answer — two profiles match and neither was sent — and a refusal is shown with its reason,
  // because "I wrote a profile and that box still does what it did before" is the whole question.
  const profileCell = (node) => {
    const p = node.profile;
    if (!p) return "—";

    const title = p.status === "conflict"
      ? `matched by: ${(p.conflicts || []).join(", ")}`
      : (p.refusals || []).map(r => `${r.item}: ${r.reason}`).join("\n");

    const name = p.name ? `${escapeHtml(p.name)}@${p.revision}` : "—";
    return `<div class="last-action" title="${escapeHtml(title)}"><strong>${name}</strong><br>` +
      `<span class="profile-${escapeHtml(p.status)}">${escapeHtml(p.status)}</span>` +
      `${p.refusals && p.refusals.length ? ` (${p.refusals.length} refused)` : ""}</div>`;
  };

  const lastActionCell = (node) => {
    if (!node.lastAction) return "—";
    const by = node.lastAction.by ? ` by ${escapeHtml(node.lastAction.by)}` : "";
    return `<div class="last-action"><strong>${escapeHtml(node.lastAction.action)}</strong>${by}<br>${escapeHtml(fmtRelativeAge(node.lastAction.atUtc))}</div>`;
  };

  // ---------------------------------------------------------------- auth state

  const setAuthState = (state) => {
    const el = document.getElementById("auth-state");
    const bar = document.getElementById("auth-bar");
    const clearBtn = document.getElementById("auth-clear");
    el.className = `auth-state ${state.kind ?? ""}`.trim();
    el.textContent = state.text;
    bar.classList.toggle("warn", state.kind === "missing");
    clearBtn.disabled = !adminKey;
  };

  const setKey = (value) => {
    adminKey = value && value.trim().length > 0 ? value.trim() : null;
    setAuthState(adminKey
      ? { kind: "ok", text: "set for this tab" }
      : { kind: "missing", text: "not set (read-only)" });
  };

  const promptForKey = (reason) => {
    const message = reason
      ? `${reason}\n\nEnter admin bearer key:`
      : "Enter admin bearer key:";
    const value = window.prompt(message, "");
    if (value === null) return false;
    setKey(value);
    return adminKey !== null;
  };

  // ---------------------------------------------------------------- toasts

  const toast = (title, body, kind) => {
    const container = document.getElementById("toasts");
    if (!container) return;
    const el = document.createElement("div");
    el.className = `toast ${kind ?? ""}`.trim();
    const titleHtml = title ? `<div class="toast-title">${escapeHtml(title)}</div>` : "";
    const bodyHtml = body ? `<div class="toast-body">${escapeHtml(body)}</div>` : "";
    el.innerHTML = titleHtml + bodyHtml;
    container.appendChild(el);
    const dwell = kind === "err" ? 8000 : 3500;
    setTimeout(() => {
      el.style.transition = "opacity 0.2s";
      el.style.opacity = "0";
      setTimeout(() => el.remove(), 220);
    }, dwell);
  };

  // ---------------------------------------------------------------- stream state

  const setStreamState = (state) => {
    streamState = state;
    const el = document.getElementById("stream-state");
    if (!el) return;
    const labels = {
      connecting: ["polling", "connecting…"],
      live: ["live", "live"],
      polling: ["polling", "polling (no stream)"],
      offline: ["offline", "offline"]
    };
    const [css, label] = labels[state] ?? labels.offline;
    el.className = `stream-pill ${css}`;
    el.textContent = label;
  };

  // ---------------------------------------------------------------- HTTP

  const adminHeaders = (extra) => {
    const h = { "Accept": "application/json" };
    if (adminKey) h["Authorization"] = `Bearer ${adminKey}`;
    return Object.assign(h, extra ?? {});
  };

  const fetchAdminNodes = async () => {
    const res = await fetch("/api/admin/nodes", { headers: adminHeaders() });
    if (res.status === 401) {
      const reprompted = promptForKey("Admin key required or invalid.");
      if (!reprompted) return null;
      return fetchAdminNodes();
    }
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
  };

  const fetchStatus = async () => {
    const res = await fetch("/api/status", { headers: { "Accept": "application/json" } });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
  };

  const callAdminAction = async (nodeId, action) => {
    if (!adminKey && !promptForKey("Admin key required for this action.")) {
      throw new Error("admin key not provided");
    }
    const res = await fetch(`/api/admin/nodes/${encodeURIComponent(nodeId)}/${action}`, {
      method: "POST",
      headers: adminHeaders()
    });
    if (res.status === 401) {
      const reprompted = promptForKey("Admin key rejected.");
      if (!reprompted) throw new Error("admin key rejected");
      return callAdminAction(nodeId, action);
    }
    if (!res.ok) {
      let detail = `HTTP ${res.status}`;
      try {
        const body = await res.json();
        if (body && body.error) detail = body.error;
      } catch { /* ignore */ }
      throw new Error(detail);
    }
    return res.json().catch(() => ({}));
  };

  // ---------------------------------------------------------------- rendering

  const renderStats = (snapshot) => {
    const cards = [
      ["Requests total", snapshot.requestsTotal ?? 0],
      ["In-flight", snapshot.requestsInFlight ?? 0],
      ["Completed", snapshot.requestsCompleted ?? 0],
      ["Failed", snapshot.requestsFailed ?? 0],
      ["Failovers", `${snapshot.failoversSucceeded ?? 0} / ${snapshot.failoversAttempted ?? 0}`],
      ["Nodes evicted", snapshot.nodesEvicted ?? 0],
    ];
    document.getElementById("stats").innerHTML = cards.map(([label, value]) =>
      `<div class="card"><div class="stat-label">${label}</div><div class="stat">${value}</div></div>`
    ).join("");
  };

  const isPending = (nodeId, action) => pendingActions.has(`${nodeId}:${action}`);
  const anyPending = (nodeId) =>
    isPending(nodeId, "cordon")
      || isPending(nodeId, "uncordon")
      || isPending(nodeId, "drain")
      || isPending(nodeId, "deregister");

  const actionButtons = (node) => {
    const drainingNow = draining.has(node.nodeId);
    const pending = anyPending(node.nodeId);
    const safeId = encodeURIComponent(node.nodeId);

    const btns = [];
    if (node.cordoned) {
      btns.push(`<button class="primary" data-action="uncordon" data-node="${safeId}" ${pending ? "disabled" : ""}>Uncordon</button>`);
    } else {
      btns.push(`<button data-action="cordon" data-node="${safeId}" ${pending || drainingNow ? "disabled" : ""}>Cordon</button>`);
    }
    btns.push(`<button data-action="drain" data-node="${safeId}" ${pending || drainingNow ? "disabled" : ""}>${drainingNow ? "Draining…" : "Drain"}</button>`);
    btns.push(`<button class="danger" data-action="deregister" data-node="${safeId}" ${pending ? "disabled" : ""}>Deregister</button>`);
    return btns.join(" ");
  };

  const renderNodes = (nodes) => {
    const tbody = document.getElementById("nodes");
    if (!nodes || nodes.length === 0) {
      tbody.innerHTML = `<tr><td colspan="12" class="empty">No nodes connected.</td></tr>`;
      return;
    }

    tbody.innerHTML = nodes.map(n => {
      const msg = rowMessages.get(n.nodeId);
      const msgHtml = msg
        ? `<div class="row-msg ${msg.isError ? "" : "info"}">${escapeHtml(msg.text)}</div>`
        : "";
      const max = n.maxConcurrency == null ? "—" : String(n.maxConcurrency);
      return `
        <tr class="${msg && msg.isError ? "row-error" : ""}">
          <td>${escapeHtml(n.name)}${msgHtml}</td>
          <td><code>${escapeHtml(n.nodeId)}</code></td>
          <td>${escapeHtml(n.ollamaEndpoint)}</td>
          <td>${statePill(n)}</td>
          <td>${n.localInFlight} / ${n.inFlight}</td>
          <td>${max}</td>
          <td>${capabilityChips(n.capabilities)}</td>
          <td>${profileCell(n)}</td>
          <td>${labelChips(n.labels)}</td>
          <td>${fmtSeconds(n.ageSeconds)} ago</td>
          <td>${lastActionCell(n)}</td>
          <td><div class="actions">${actionButtons(n)}</div></td>
        </tr>`;
    }).join("");
  };

  const renderModels = (models) => {
    const tbody = document.getElementById("models");
    if (!models || models.length === 0) {
      tbody.innerHTML = `<tr><td colspan="3" class="empty">No models reported yet.</td></tr>`;
      return;
    }
    tbody.innerHTML = models.map(m => `
      <tr>
        <td><code>${escapeHtml(m.name)}</code></td>
        <td><code>${escapeHtml(m.digest ?? "—")}</code></td>
        <td>${fmtBytes(m.size)}</td>
      </tr>
    `).join("");
  };

  const renderCollections = (vector) => {
    const tbody = document.getElementById("collections");
    if (!tbody) return;

    const provider = vector?.provider ?? "local";
    const isExternal = provider === "postgres" || provider === "qdrant";
    const badge = document.getElementById("vector-provider");
    if (badge) {
      badge.style.display = vector ? "" : "none";
      badge.textContent = provider;
      badge.className = "pill " + (isExternal ? "pill-ok" : "");
    }

    const items = vector?.collections ?? [];
    if (items.length === 0) {
      tbody.innerHTML = `<tr><td colspan="7" class="empty">Vector store disabled or no collections yet.</td></tr>`;
      return;
    }

    tbody.innerHTML = items.map(c => {
      // An external provider (postgres, qdrant) owns durability and has no node replicas — the
      // replica/placement columns and the Rebuild action don't apply, so we show em-dashes and
      // disable the button with a reason.
      const replicaCell = isExternal
        ? `<span class="empty" style="padding:0">—</span>`
        : `${c.liveReplicas} / ${c.targetReplicas} ${c.underReplicated
            ? `<span class="pill pill-warn">under-replicated</span>`
            : `<span class="pill pill-ok">at target</span>`}`;
      const chips = isExternal
        ? `<span class="empty" style="padding:0">— ${escapeHtml(provider)}-backed</span>`
        : (c.replicaNodes && c.replicaNodes.length > 0)
          ? `<div class="replica-list">${c.replicaNodes.map(n =>
              `<span class="replica-chip">${escapeHtml(n)}</span>`).join("")}</div>`
          : `<span class="empty" style="padding:0">— hub-local only</span>`;
      const safeName = encodeURIComponent(c.name);
      const rebuildBtn = isExternal
        ? `<button data-vaction="rebuild" data-collection="${safeName}" disabled title="Not applicable when VectorStore:Provider=${provider} — the store owns durability">Rebuild</button>`
        : `<button data-vaction="rebuild" data-collection="${safeName}">Rebuild</button>`;
      return `
        <tr>
          <td><code>${escapeHtml(c.name)}</code></td>
          <td>${c.dimension}</td>
          <td>${escapeHtml(c.distance)}</td>
          <td>${c.recordCount}</td>
          <td>${replicaCell}</td>
          <td>${chips}</td>
          <td><div class="actions">
            ${rebuildBtn}
          </div></td>
        </tr>`;
    }).join("");
  };

  const kindClass = (kind) => {
    if (kind === "vector.replica.lost" || kind === "vector.heal.started") return "warn";
    if (kind === "vector.collection.dropped") return "err";
    return "ok";
  };

  const summarizeVectorEvent = (ev) => {
    const collection = ev.collection ? `<code>${escapeHtml(ev.collection)}</code>` : "";
    const d = ev.data ?? {};
    switch (ev.kind) {
      case "vector.collection.created":
        return `${collection} created (dim=${d.dimension ?? "?"}, ${escapeHtml(d.distance ?? "?")})`;
      case "vector.collection.dropped":
        return `${collection} dropped`;
      case "vector.replica.assigned":
        return `${collection} replica assigned to <code>${escapeHtml(d.nodeId ?? d.connectionId ?? "?")}</code> · ${d.records ?? 0} records`;
      case "vector.replica.lost":
        return `${collection} replica lost on <code>${escapeHtml(d.connectionId ?? "?")}</code>${d.reason ? ` (${escapeHtml(d.reason)})` : ""}`;
      case "vector.heal.started":
        return `${collection} heal started · reason=${escapeHtml(d.reason ?? "under-target")}`;
      case "vector.heal.completed":
        return `${collection} heal complete · ${d.before ?? 0}→${d.after ?? 0} replicas`;
      default:
        return `${collection} ${escapeHtml(ev.kind)}`;
    }
  };

  const renderVectorFeed = () => {
    const el = document.getElementById("vector-feed");
    if (!el) return;
    if (vectorFeed.length === 0) {
      el.innerHTML = `<div class="empty">No vector activity yet.</div>`;
      return;
    }
    el.innerHTML = vectorFeed.map(ev => `
      <div class="feed-row">
        <span class="feed-time">${escapeHtml(new Date(ev.atUtc).toLocaleTimeString())}</span>
        <span class="feed-kind ${kindClass(ev.kind)}">${escapeHtml(ev.kind.replace(/^vector\./, ""))}</span>
        <span class="feed-body">${summarizeVectorEvent(ev)}</span>
      </div>
    `).join("");
  };

  const pushVectorEvent = (ev) => {
    vectorFeed.unshift(ev);
    if (vectorFeed.length > VECTOR_FEED_MAX) vectorFeed.length = VECTOR_FEED_MAX;
    renderVectorFeed();
  };

  // ------------------------------------------------- capabilities, tools, corpora (phases 40–45)
  //
  // All three read /api/status, which the console already polls, and none of them asks the fleet
  // anything: what a node reports is what is shown, and the "reported" column is how stale it is.

  const emptyRow = (id, columns, text) => {
    document.getElementById(id).innerHTML =
      `<tr><td colspan="${columns}" class="empty">${escapeHtml(text)}</td></tr>`;
  };

  // Node × capability. The routed truth, not the declared one: these are the resolved capabilities
  // /api/status reports, so a node that declared nothing shows chat + embed (phase-40 D1).
  const renderCapabilityMatrix = (status) => {
    const host = document.getElementById("capability-matrix");
    if (!host) return;

    const nodes = status?.nodes ?? [];
    const kinds = [...new Set([
      ...(status?.capabilities ?? []).map(c => c.capability),
      ...nodes.flatMap(n => n.capabilities ?? [])
    ])].sort();

    if (nodes.length === 0 || kinds.length === 0) {
      host.innerHTML = `<div class="empty">No node declares a capability yet.</div>`;
      return;
    }

    const models = new Map((status?.capabilities ?? []).map(c => [c.capability, c.models ?? []]));

    const head = `<tr><th>Node</th>${kinds.map(k =>
      `<th class="matrix-cell">${escapeHtml(k)}</th>`).join("")}</tr>`;

    const body = nodes.map(n => {
      const has = new Set(n.capabilities ?? []);
      return `<tr><td>${escapeHtml(n.name)} <code>${escapeHtml(n.nodeId)}</code></td>${kinds.map(k =>
        `<td class="matrix-cell ${has.has(k) ? "matrix-yes" : "matrix-no"}">${has.has(k) ? "●" : "·"}</td>`
      ).join("")}</tr>`;
    }).join("");

    // The fleet row is the answer to "will this request find a node at all" — a capability with
    // zero nodes is a 503 with a Retry-After (phase-40 D4), not a 404, and this is where you see it.
    const totals = `<tr><td class="meta">fleet · models</td>${kinds.map(k => {
      const list = models.get(k) ?? [];
      const count = nodes.filter(n => (n.capabilities ?? []).includes(k)).length;
      return `<td class="matrix-cell" title="${escapeHtml(list.join(", "))}">${count} node${count === 1 ? "" : "s"}` +
        `<br><span class="meta">${list.length} model${list.length === 1 ? "" : "s"}</span></td>`;
    }).join("")}</tr>`;

    host.innerHTML = `<table><thead>${head}</thead><tbody>${body}${totals}</tbody></table>`;
  };

  // A pool inside its restart budget is still "running" — it has not given up, and saying it had
  // would be wrong. But it holds no worker and the most recent thing that happened to it was a
  // failure, so it will fail every request it is declared for: a green pill there is the lie this
  // panel exists to stop telling.
  const toolIsDegraded = (tool) => tool.state === "running" && Boolean(tool.lastError) && tool.workers === 0;

  const toolStatePill = (tool) => {
    const cls = tool.state === "running" ? (toolIsDegraded(tool) ? "pill-warn" : "pill-ok")
      : tool.state === "suspended" ? "pill-warn"
        : tool.state === "not-allowed" ? "pill-muted" : "pill-err";
    const label = toolIsDegraded(tool) ? "running · no worker" : tool.state;
    return `<span class="pill ${cls}">${escapeHtml(label)}</span>`;
  };

  const renderTools = (status) => {
    const tbody = document.getElementById("tools");
    if (!tbody) return;

    const rows = (status?.nodes ?? [])
      .filter(n => n.tools && n.tools.enabled)
      .flatMap(n => (n.tools.tools ?? []).map(t => ({ node: n, tool: t, atUtc: n.tools.atUtc })));

    if (rows.length === 0) {
      emptyRow("tools", 9, "No node reports a tool runtime. Tools:Enabled defaults to false.");
      return;
    }

    tbody.innerHTML = rows.map(({ node, tool, atUtc }) => {
      const caps = (tool.capabilities ?? []).map(c =>
        `<span class="label-chip">${escapeHtml(c.kind)}${c.models && c.models.length ? ` ·&nbsp;${c.models.length}` : ""}</span>`).join("");
      // Not allowed is not broken. Saying so in the row is the difference between "add the id to
      // Tools:Allowed" and an afternoon reading node logs (phase-41 D2).
      const why = tool.state === "not-allowed"
        ? `<span class="why">loaded from the manifest directory, not named in <code>Tools:Allowed</code></span>`
        : tool.lastError
          ? `<span class="why">${escapeHtml(tool.lastError)}</span>`
          : `<span class="matrix-no">—</span>`;
      return `
        <tr class="${tool.state === "stopped" ? "row-error" : ""}">
          <td>${escapeHtml(node.name)}</td>
          <td><code>${escapeHtml(tool.id)}</code></td>
          <td>${tool.allowed ? `<span class="matrix-yes">yes</span>` : `<span class="matrix-no">no</span>`}</td>
          <td>${toolStatePill(tool)}</td>
          <td>${caps || `<span class="matrix-no">—</span>`}</td>
          <td>${tool.busy} busy / ${tool.workers} warm / ${tool.maxWorkers} max</td>
          <td>${tool.requests}${tool.failures ? ` <span class="pill pill-err">${tool.failures} failed</span>` : ""}</td>
          <td>${why}</td>
          <td>${escapeHtml(fmtRelativeAge(atUtc))}</td>
        </tr>`;
    }).join("");
  };

  const corpusStatePill = (corpus) => {
    if (!corpus.enabled) return `<span class="pill pill-muted">off</span>`;
    const cls = corpus.status === "running" ? "pill-ok" : corpus.status === "failed" ? "pill-err" : "pill-warn";
    return `<span class="pill ${cls}">${escapeHtml(corpus.status)}</span>`;
  };

  const renderCorpora = (status) => {
    const tbody = document.getElementById("corpora");
    if (!tbody) return;

    const rows = (status?.nodes ?? []).filter(n => n.corpus).map(n => ({ node: n, corpus: n.corpus }));

    if (rows.length === 0) {
      emptyRow("corpora", 7, "No node hosts a corpus. Assign one with a profile's retrieval block.");
      return;
    }

    tbody.innerHTML = rows.map(({ node, corpus }) => {
      const collections = (corpus.collections ?? []);
      const names = collections.length
        ? `<div class="replica-list">${collections.map(c =>
            `<span class="replica-chip" title="dim ${c.dimension}">${escapeHtml(c.name)}</span>`).join("")}</div>`
        : `<span class="matrix-no">—</span>`;
      const records = collections.reduce((sum, c) => sum + (c.records ?? 0), 0);
      return `
        <tr class="${corpus.status === "failed" ? "row-error" : ""}">
          <td>${escapeHtml(node.name)} <code>${escapeHtml(node.nodeId)}</code></td>
          <td>${corpusStatePill(corpus)}</td>
          <td>${escapeHtml(corpus.provider ?? "—")}</td>
          <td>${names}</td>
          <td>${collections.length ? records : `<span class="matrix-no">—</span>`}</td>
          <td>${corpus.error ? `<span class="why">${escapeHtml(corpus.error)}</span>` : `<span class="matrix-no">—</span>`}</td>
          <td>${escapeHtml(fmtRelativeAge(corpus.atUtc))}</td>
        </tr>`;
    }).join("");
  };

  // Phase 45, D1. Everything that is *not* doing what it was told, in one strip, above the fold.
  // Desired and effective are both on the row, because "it did not take" without "and here is what
  // stopped it" is the support conversation this panel exists to prevent.
  const renderRefusals = (status) => {
    const section = document.getElementById("refusals-section");
    const host = document.getElementById("refusals");
    if (!section || !host) return;

    const items = [];

    for (const node of status?.nodes ?? []) {
      const label = `${node.name} (${node.nodeId})`;

      const profile = node.profile;
      if (profile?.status === "conflict") {
        items.push({
          kind: "profile", where: label,
          what: `matched by ${(profile.conflicts ?? []).join(", ")}`,
          why: "two profiles select this node, so the hub sent neither and it kept what it had (phase-43 D4)"
        });
      }
      for (const refusal of profile?.refusals ?? []) {
        items.push({
          kind: "profile", where: label,
          what: `${profile.name ?? "?"}@${profile.revision}: ${refusal.item}`,
          why: refusal.reason
        });
      }

      for (const tool of node.tools?.tools ?? []) {
        if (tool.state === "not-allowed") {
          items.push({
            kind: "tool", where: label, what: tool.id,
            why: "the manifest is on the box but Tools:Allowed does not name it, so it was never started"
          });
        } else if (tool.state === "stopped") {
          items.push({ kind: "tool", where: label, what: tool.id, why: tool.lastError ?? "the pool gave up starting it" });
        } else if (tool.state === "suspended") {
          items.push({ kind: "tool", where: label, what: tool.id, why: "switched off by this node's profile" });
        } else if (toolIsDegraded(tool)) {
          // Still inside its restart budget, so not "stopped" — but it is declared for work it
          // currently cannot do, and that is the strip's whole subject.
          items.push({ kind: "tool", where: label, what: tool.id, why: tool.lastError });
        }
      }

      if (node.corpus?.enabled && node.corpus.status === "failed") {
        items.push({ kind: "corpus", where: label, what: node.corpus.provider, why: node.corpus.error ?? "the corpus did not start" });
      }

      // Phase 51. A recipe the node holds and will not offer is invisible everywhere else: it is
      // simply absent from the capability list, which reads exactly like a model nobody installed.
      // `not-ready` is deliberately NOT on the strip — weights that are still downloading are a
      // fleet working correctly, and a strip that cried about every cold start would be a strip
      // people learn to close.
      for (const recipe of node.tools?.images ?? []) {
        if (recipe.offered || recipe.reason === "not-ready") continue;

        items.push({
          kind: "image", where: label, what: recipe.id,
          why: recipe.reason === "unlicensed"
            ? `licence '${recipe.licenseId}' is not permissive and is not in Tools:Image:AcceptedLicenses`
            : recipe.reason === "over-budget"
              ? `wants ${recipe.vramMiB} MiB and does not fit this node's declared VRAM budget minus its reserve`
              : "switched off on this node by a coordinator profile"
        });
      }
    }

    if (items.length === 0) {
      section.style.display = "none";
      host.innerHTML = "";
      return;
    }

    section.style.display = "";
    host.innerHTML = `<table><thead><tr><th>What</th><th>Node</th><th>Item</th><th>Why</th></tr></thead><tbody>` +
      items.map(i => `
        <tr class="refusal-row">
          <td><span class="pill pill-warn">${escapeHtml(i.kind)}</span></td>
          <td>${escapeHtml(i.where)}</td>
          <td><code>${escapeHtml(i.what ?? "—")}</code></td>
          <td><span class="why">${escapeHtml(i.why ?? "")}</span></td>
        </tr>`).join("") + `</tbody></table>`;
  };

  // Desired (the profile the hub matched) beside effective (what the node reports running), which
  // is the whole of D1 in one table.
  const renderProfileNodes = (status) => {
    const tbody = document.getElementById("profile-nodes");
    if (!tbody) return;

    const rows = (status?.nodes ?? []).filter(n => n.profile);

    if (rows.length === 0) {
      emptyRow("profile-nodes", 6, "No profile matches a connected node.");
      return;
    }

    tbody.innerHTML = rows.map(n => {
      const p = n.profile;
      const detail = p.status === "conflict"
        ? `<span class="why">${escapeHtml((p.conflicts ?? []).join(", "))}</span>`
        : (p.refusals ?? []).length
          ? `<span class="why">${(p.refusals ?? []).map(r =>
              `<code>${escapeHtml(r.item)}</code> — ${escapeHtml(r.reason)}`).join("<br>")}</span>`
          : `<span class="matrix-no">—</span>`;
      return `
        <tr class="${p.status === "refused" || p.status === "conflict" ? "row-error" : ""}">
          <td>${escapeHtml(n.name)} <code>${escapeHtml(n.nodeId)}</code></td>
          <td>${escapeHtml(p.name ?? "—")}</td>
          <td>${p.revision}</td>
          <td><span class="profile-${escapeHtml(p.status)}">${escapeHtml(p.status)}</span></td>
          <td>${capabilityChips(n.capabilities)}${n.maxConcurrency == null ? "" : `<span class="label-chip">max ${n.maxConcurrency}</span>`}</td>
          <td>${detail}</td>
        </tr>`;
    }).join("");
  };

  // ---------------------------------------------------------------- actions

  const setRowMessage = (nodeId, text, isError) => {
    if (!text) {
      rowMessages.delete(nodeId);
    } else {
      rowMessages.set(nodeId, { text, isError: Boolean(isError) });
    }
  };

  const refreshRender = () => {
    if (latestStatus) {
      document.getElementById("version").textContent = `v${latestStatus.coordinatorVersion}`;
      document.getElementById("uptime").textContent = fmtSeconds(latestStatus.uptimeSeconds);
      renderStats(latestStatus.metrics);
      renderModels(latestStatus.models);
      renderCollections(latestStatus.vector);
      syncDocumentCollections(latestStatus.vector);
      renderCapabilityMatrix(latestStatus);
      renderTools(latestStatus);
      renderCorpora(latestStatus);
      renderProfileNodes(latestStatus);
      renderImageRecipes(latestStatus);
      renderImageVram(latestStatus);
      renderRefusals(latestStatus);
    }
    renderNodes(latestNodes ?? []);
    renderVectorFeed();
    renderModelNodeSelect();
  };

  // ---------------------------------------------------------------- model management

  const selectedModelNode = () =>
    (latestNodes ?? []).find(n => n.nodeId === document.getElementById("mm-node")?.value) ?? null;

  const renderModelNodeSelect = () => {
    const sel = document.getElementById("mm-node");
    if (!sel) return;
    const nodes = latestNodes ?? [];
    const prev = sel.value;
    sel.innerHTML = nodes.length === 0
      ? `<option value="">No nodes connected</option>`
      : nodes.map(n => `<option value="${escapeHtml(n.nodeId)}">${escapeHtml(n.name)} (${escapeHtml(n.nodeId.slice(0, 8))})</option>`).join("");
    if (nodes.some(n => n.nodeId === prev)) sel.value = prev;

    const node = selectedModelNode();
    const canManage = Boolean(node && node.supportsModelManagement);
    const note = document.getElementById("mm-note");
    for (const id of ["mm-pull", "mm-warm", "mm-delete", "mm-model"]) {
      const el = document.getElementById(id);
      if (!el) continue;
      el.disabled = !canManage;
      el.title = canManage ? "" : "This node's backend cannot manage models (e.g. an OpenAI/vLLM upstream — its model is fixed at launch).";
    }
    if (note) {
      note.textContent = !node
        ? "Connect a node to manage its models."
        : canManage
          ? `${node.name} runs a backend that can pull, delete and warm models.`
          : `${node.name} runs a backend that cannot manage models — controls disabled.`;
    }
  };

  const postModelCommand = async (kind, nodeId, model) => {
    const enc = encodeURIComponent(model);
    const base = `/api/admin/nodes/${encodeURIComponent(nodeId)}/models/${enc}`;
    const url = kind === "pull" ? `${base}/pull` : kind === "warm" ? `${base}/warm` : base;
    const method = kind === "delete" ? "DELETE" : "POST";
    try {
      const res = await fetch(url, { method, headers: adminHeaders() });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) {
        pushModelNote(`${kind} '${model}' failed: ${body.error ?? ("HTTP " + res.status)}`, true);
      } else if (body.reused) {
        pushModelNote(`${kind} '${model}' already running (command ${String(body.commandId).slice(0, 8)}).`, false);
      }
    } catch (err) {
      pushModelNote(`${kind} '${model}' failed: ${err.message}`, true);
    }
  };

  const pushModelNote = (text, isError) => {
    const note = document.getElementById("mm-note");
    if (note) {
      note.textContent = text;
      note.style.color = isError ? "var(--danger, #ff6b6b)" : "";
    }
  };

  const handleModelProgress = (frame) => {
    if (!frame || !frame.commandId) return;
    modelCommands.set(frame.commandId, { ...frame, at: Date.now() });
    renderModelProgress();
    // A completed pull/delete changes what each node holds — refresh the matrix.
    if (frame.done) queueMatrixRefresh();
  };

  const renderModelProgress = () => {
    const el = document.getElementById("mm-progress");
    if (!el) return;
    const rows = [...modelCommands.values()].sort((a, b) => b.at - a.at).slice(0, 12);
    if (rows.length === 0) {
      el.innerHTML = `<div class="empty">No model commands yet.</div>`;
      return;
    }
    el.innerHTML = rows.map(f => {
      const pct = typeof f.percent === "number" ? Math.round(f.percent) : null;
      const state = f.error ? "err" : f.done ? "ok" : "warn";
      const bar = pct === null
        ? ""
        : `<div style="height:6px;background:var(--panel-border);border-radius:3px;overflow:hidden;margin-top:4px">
             <div style="height:100%;width:${pct}%;background:${f.error ? "#ff6b6b" : "#00d4ff"}"></div></div>`;
      const detail = f.error ? escapeHtml(f.error) : `${escapeHtml(f.status)}${pct === null ? "" : ` · ${pct}%`}`;
      return `
        <div class="feed-row">
          <span class="feed-kind ${state}">${escapeHtml(f.kind)}</span>
          <span class="feed-body"><code>${escapeHtml(f.modelName)}</code> <span class="empty" style="padding:0">on ${escapeHtml(f.nodeId.slice(0, 8))}</span><br>${detail}${bar}</span>
        </div>`;
    }).join("");
  };

  let modelMatrix = null;
  let matrixFetchQueued = false;

  const fetchModelMatrix = async () => {
    if (!adminKey) return;
    try {
      const res = await fetch("/api/admin/models", { headers: adminHeaders() });
      if (!res.ok) return;
      modelMatrix = await res.json();
      renderModelMatrix();
    } catch { /* transient; next trigger refetches */ }
  };

  const queueMatrixRefresh = () => {
    if (matrixFetchQueued) return;
    matrixFetchQueued = true;
    setTimeout(() => { matrixFetchQueued = false; fetchModelMatrix(); }, 500);
  };

  const renderModelMatrix = () => {
    const el = document.getElementById("model-matrix");
    if (!el) return;
    const m = modelMatrix;
    if (!m || !m.nodes || m.nodes.length === 0) {
      el.innerHTML = `<div class="empty" style="padding:12px">No nodes connected.</div>`;
      return;
    }
    if (!m.models || m.models.length === 0) {
      el.innerHTML = `<div class="empty" style="padding:12px">No models reported by the fleet yet.</div>`;
      return;
    }
    const nodeHead = m.nodes.map(n =>
      `<th title="${escapeHtml(n.nodeId)}${n.supportsModelManagement ? "" : " — backend cannot manage models"}">${escapeHtml(n.name)}${n.cordoned ? " 🚫" : ""}${n.supportsModelManagement ? "" : " 🔒"}</th>`).join("");
    const rows = m.models.map(model => {
      const held = new Set(model.nodes);
      const cells = m.nodes.map(n => {
        const enc = encodeURIComponent(model.name);
        const nid = encodeURIComponent(n.nodeId);
        if (held.has(n.nodeId)) {
          const del = n.supportsModelManagement
            ? ` <button data-mm="delete" data-node="${nid}" data-model="${enc}" title="Delete from this node" style="padding:1px 6px">×</button>`
            : "";
          return `<td style="text-align:center;color:#4ade80">✓${del}</td>`;
        }
        if (n.cordoned || !n.supportsModelManagement) return `<td style="text-align:center" class="empty">—</td>`;
        return `<td style="text-align:center"><button data-mm="pull" data-node="${nid}" data-model="${enc}" style="padding:1px 8px">pull</button></td>`;
      }).join("");
      return `<tr><td><code>${escapeHtml(model.name)}</code></td><td>${fmtBytes(model.sizeBytes)}</td>${cells}</tr>`;
    }).join("");
    el.innerHTML = `
      <table>
        <thead><tr><th>Model</th><th>Size</th>${nodeHead}</tr></thead>
        <tbody>${rows}</tbody>
      </table>`;
  };

  const ensureModel = async (model, replicas) => {
    try {
      const res = await fetch(`/api/admin/models/${encodeURIComponent(model)}/ensure?replicas=${replicas}`,
        { method: "POST", headers: adminHeaders() });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) { pushModelNote(`ensure '${model}' failed: ${body.error ?? ("HTTP " + res.status)}`, true); return; }
      const pulling = (body.pulling ?? []).length;
      const present = (body.alreadyPresent ?? []).length;
      pushModelNote(
        `ensure '${model}' ×${replicas}: ${present} already present, pulling onto ${pulling}` +
        (body.satisfied ? "" : ` — short by ${body.decision?.shortfall ?? "?"} (${body.decision?.note ?? ""})`),
        !body.satisfied);
      queueMatrixRefresh();
    } catch (err) {
      pushModelNote(`ensure '${model}' failed: ${err.message}`, true);
    }
  };

  const applyAdminNodes = (nodes) => {
    latestNodes = nodes;
    evaluateDrains(latestNodes);
    document.getElementById("refreshed").textContent = new Date().toLocaleTimeString();
    refreshRender();
    queueMatrixRefresh();
  };

  const runAction = async (nodeId, action) => {
    const key = `${nodeId}:${action}`;
    if (pendingActions.has(key)) return;
    pendingActions.add(key);

    refreshRender();

    try {
      await callAdminAction(nodeId, action);
      toast(`${labelForAction(action)} succeeded`, `node ${nodeId}`, "ok");
    } catch (err) {
      toast(`${labelForAction(action)} failed`, `${nodeId}: ${err.message}`, "err");
    } finally {
      pendingActions.delete(key);
      await pollAdminNodesNow();
    }
  };

  const labelForAction = (action) => {
    switch (action) {
      case "cordon": return "Cordon";
      case "uncordon": return "Uncordon";
      case "drain": return "Drain";
      case "deregister": return "Deregister";
      default: return action;
    }
  };

  const confirmAndRun = (nodeId, action, prompt) => {
    if (!window.confirm(prompt)) return;
    runAction(nodeId, action);
  };

  const startDrain = async (nodeId) => {
    if (draining.has(nodeId)) return;
    if (!window.confirm(
      `Drain node "${nodeId}"?\n\nIt will be cordoned and remain connected until in-flight jobs finish.`)) {
      return;
    }

    const cordonKey = `${nodeId}:drain`;
    if (pendingActions.has(cordonKey)) return;
    pendingActions.add(cordonKey);
    draining.set(nodeId, { startedAt: Date.now() });
    setRowMessage(nodeId, "draining — waiting for in-flight jobs", false);
    refreshRender();

    try {
      await callAdminAction(nodeId, "cordon");
      toast("Drain started", `node ${nodeId} is cordoned; waiting for in-flight jobs`, "ok");
    } catch (err) {
      draining.delete(nodeId);
      setRowMessage(nodeId, null);
      toast("Drain failed", `${nodeId}: ${err.message}`, "err");
    } finally {
      pendingActions.delete(cordonKey);
      await pollAdminNodesNow();
    }
  };

  const evaluateDrains = (nodes) => {
    if (draining.size === 0) return;
    const byId = new Map(nodes.map(n => [n.nodeId, n]));
    for (const nodeId of [...draining.keys()]) {
      const node = byId.get(nodeId);
      if (!node) {
        draining.delete(nodeId);
        setRowMessage(nodeId, null);
        continue;
      }
      if (node.cordoned && node.localInFlight === 0) {
        draining.delete(nodeId);
        setRowMessage(nodeId, null);
        toast("Drain complete", `${nodeId} is idle and cordoned`, "ok");
      }
    }
  };

  // ---------------------------------------------------------------- streaming

  const parseSseBuffer = (buffer) => {
    const events = [];
    let i = 0;
    while (true) {
      // Per the SSE spec, an event terminates with a blank line — accept LF or CRLF.
      const sepLf = buffer.indexOf("\n\n", i);
      const sepCrLf = buffer.indexOf("\r\n\r\n", i);
      let sep, advance;
      if (sepLf === -1 && sepCrLf === -1) break;
      if (sepLf === -1) { sep = sepCrLf; advance = 4; }
      else if (sepCrLf === -1) { sep = sepLf; advance = 2; }
      else if (sepCrLf < sepLf) { sep = sepCrLf; advance = 4; }
      else { sep = sepLf; advance = 2; }

      const block = buffer.slice(i, sep);
      i = sep + advance;

      const ev = { event: "message", data: "" };
      for (const rawLine of block.split(/\r?\n/)) {
        if (!rawLine || rawLine.startsWith(":")) continue;
        const colon = rawLine.indexOf(":");
        const field = colon === -1 ? rawLine : rawLine.slice(0, colon);
        let value = colon === -1 ? "" : rawLine.slice(colon + 1);
        if (value.startsWith(" ")) value = value.slice(1);
        if (field === "data") {
          ev.data = ev.data ? `${ev.data}\n${value}` : value;
        } else if (field === "event") {
          ev.event = value;
        }
      }
      if (ev.data || ev.event !== "message") {
        events.push(ev);
      }
    }
    return { events, remainder: buffer.slice(i) };
  };

  const handleStreamEvent = (event) => {
    if (event.event === "snapshot") {
      try {
        const payload = JSON.parse(event.data);
        if (payload && Array.isArray(payload.nodes)) {
          applyAdminNodes(payload.nodes);
        }
      } catch (err) {
        // Malformed payload — log to console and let the next event recover.
        console.warn("admin stream: failed to parse snapshot", err);
      }
      return;
    }

    if (event.event === "model-progress") {
      try {
        handleModelProgress(JSON.parse(event.data));
      } catch (err) {
        console.warn("admin stream: failed to parse model progress", err);
      }
      return;
    }

    if (event.event && event.event.startsWith("vector.")) {
      try {
        const payload = JSON.parse(event.data);
        pushVectorEvent(payload);
        // Any vector-lifecycle event may change collection counts/placement — pull
        // a fresh status snapshot so the collections table stays honest without
        // waiting for the next 5s status poll.
        pollStatusNow();
      } catch (err) {
        console.warn("admin stream: failed to parse vector event", err);
      }
    }
  };

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  const streamLoop = async () => {
    while (true) {
      if (!adminKey) {
        setStreamState("offline");
        ensureNodesPolling();
        await sleep(2000);
        continue;
      }

      let controller;
      try {
        controller = new AbortController();
        streamAbort = controller;
        setStreamState("connecting");

        const res = await fetch("/api/admin/stream", {
          headers: adminHeaders({ "Accept": "text/event-stream" }),
          cache: "no-store",
          signal: controller.signal
        });

        if (res.status === 401) {
          setStreamState("offline");
          ensureNodesPolling();
          const reprompted = promptForKey("Admin key required for live updates.");
          if (!reprompted) {
            await sleep(STREAM_RECONNECT_MAX_MS);
          }
          continue;
        }

        if (!res.ok || !res.body) {
          throw new Error(`HTTP ${res.status}`);
        }

        setStreamState("live");
        stopNodesPolling();
        streamReconnectDelay = STREAM_RECONNECT_MIN_MS;

        const reader = res.body.getReader();
        const decoder = new TextDecoder("utf-8");
        let buffer = "";

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          const parsed = parseSseBuffer(buffer);
          buffer = parsed.remainder;
          for (const ev of parsed.events) {
            handleStreamEvent(ev);
          }
        }

        // Normal close (server ended the stream) — fall through to reconnect.
      } catch (err) {
        if (err && err.name === "AbortError") {
          // User-triggered restart (e.g. key changed) — skip backoff, retry immediately.
          streamReconnectDelay = STREAM_RECONNECT_MIN_MS;
          if (streamAbort === controller) streamAbort = null;
          continue;
        }
        // Network drop or HTTP error — drop into polling fallback and retry.
      } finally {
        if (streamAbort === controller) {
          streamAbort = null;
        }
      }

      setStreamState("polling");
      ensureNodesPolling();
      await sleep(streamReconnectDelay);
      streamReconnectDelay = Math.min(streamReconnectDelay * 2, STREAM_RECONNECT_MAX_MS);
    }
  };

  const restartStream = () => {
    if (streamAbort) {
      streamAbort.abort();
      streamAbort = null;
    }
    streamReconnectDelay = STREAM_RECONNECT_MIN_MS;
    // streamLoop is already running; the abort triggers a fresh iteration.
  };

  // ---------------------------------------------------------------- poll loop

  const pollAdminNodesNow = async () => {
    try {
      const nodes = await fetchAdminNodes();
      if (nodes !== null) applyAdminNodes(nodes);
    } catch (err) {
      document.getElementById("refreshed").textContent = `error: ${err.message}`;
    }
  };

  const ensureNodesPolling = () => {
    if (nodesPollHandle) return;
    pollAdminNodesNow();
    nodesPollHandle = setInterval(pollAdminNodesNow, NODES_POLL_MS);
  };

  const stopNodesPolling = () => {
    if (nodesPollHandle) {
      clearInterval(nodesPollHandle);
      nodesPollHandle = null;
    }
  };

  const pollStatusNow = async () => {
    try {
      const status = await fetchStatus();
      if (status) {
        latestStatus = status;
        refreshRender();
      }
    } catch {
      // The status page is unauthenticated; if it fails we leave the previous snapshot up.
    }
  };

  // ---------------------------------------------------------------- wiring

  document.getElementById("auth-set").addEventListener("click", () => {
    if (promptForKey()) {
      restartStream();
      // The profile book is admin-scoped, so it could not be read before the key existed.
      refreshProfiles();
    }
  });
  document.getElementById("auth-clear").addEventListener("click", () => {
    setKey(null);
    restartStream();
  });

  const rebuildCollection = async (collection) => {
    if (!adminKey && !promptForKey("Admin key required for this action.")) return;
    if (!window.confirm(`Rebuild replicas of "${collection}" from the raw store?`)) return;
    try {
      const res = await fetch(`/api/admin/vector/collections/${encodeURIComponent(collection)}/rebuild`, {
        method: "POST",
        headers: adminHeaders()
      });
      if (res.status === 401) {
        promptForKey("Admin key rejected.");
        return;
      }
      if (!res.ok) {
        let detail = `HTTP ${res.status}`;
        try { const body = await res.json(); if (body?.error) detail = body.error; } catch { }
        throw new Error(detail);
      }
      toast("Rebuild started", `collection ${collection}`, "ok");
      pollStatusNow();
    } catch (err) {
      toast("Rebuild failed", `${collection}: ${err.message}`, "err");
    }
  };

  const collectionsBody = document.getElementById("collections");
  if (collectionsBody) {
    collectionsBody.addEventListener("click", (event) => {
      const button = event.target.closest("button[data-vaction]");
      if (!button) return;
      const collection = decodeURIComponent(button.dataset.collection);
      const action = button.dataset.vaction;
      if (action === "rebuild") rebuildCollection(collection);
    });
  }

  // ---------------------------------------------------------------- documents (phase 23)
  //
  // Ingestion is a *client* action, not an admin one, so it is guarded by Auth:ApiKeys rather
  // than Auth:AdminApiKeys. That means this panel needs its own key: the admin key the rest of
  // the console holds will not open it. On loopback with the default config neither is required
  // and both prompts stay out of the way.

  const clientHeaders = (extra) => {
    const h = { "Accept": "application/json" };
    if (clientKey) h["Authorization"] = `Bearer ${clientKey}`;
    return Object.assign(h, extra ?? {});
  };

  const setClientKey = (value) => {
    clientKey = value && value.trim().length > 0 ? value.trim() : null;

    // One client key, two panels that need one (phase 49 added the second). Both badges, or the
    // 360° viewer would keep saying "no key" after the documents panel has just been given one.
    for (const id of ["documents-key-state", "pano-key-state", "images-key-state"]) {
      const badge = document.getElementById(id);
      if (badge) badge.style.display = clientKey ? "" : "none";
    }
  };

  const promptForClientKey = (reason) => {
    const value = window.prompt(`${reason}\n\nEnter client bearer key (Auth:ApiKeys — not the admin key):`, "");
    if (value === null) return false;
    setClientKey(value);
    return clientKey !== null;
  };

  // One place that turns a documents-API response into either data or a thrown, readable error —
  // so the four callers below cannot each invent their own idea of what a 401 or a 404 means.
  const documentsFetch = async (path, init, retryOn401 = true) => {
    const res = await fetch(`/api/collections/${encodeURIComponent(documentsCollection)}/documents${path}`, {
      ...init,
      headers: clientHeaders(init?.headers)
    });

    if (res.status === 401 && retryOn401) {
      if (!promptForClientKey("Client key required or invalid.")) return null;
      return documentsFetch(path, init, false);
    }
    if (res.status === 204) return {};
    if (!res.ok) {
      let detail = `HTTP ${res.status}`;
      try { const body = await res.json(); if (body?.error) detail = body.error; } catch { }
      throw new Error(detail);
    }
    return res.json();
  };

  const renderDocuments = (documents) => {
    const tbody = document.getElementById("documents");
    const summary = document.getElementById("documents-summary");
    if (!tbody) return;

    if (!documents || documents.length === 0) {
      tbody.innerHTML = `<tr><td colspan="7" class="empty">No documents in this collection yet.</td></tr>`;
      if (summary) summary.textContent = "0 documents";
      return;
    }

    const chunks = documents.reduce((sum, d) => sum + (d.chunks ?? 0), 0);
    if (summary) {
      summary.textContent = `${documents.length} document${documents.length === 1 ? "" : "s"} · ${chunks} chunk${chunks === 1 ? "" : "s"}`;
    }

    tbody.innerHTML = documents.map(d => {
      const partial = d.status === "partial";
      const id = encodeURIComponent(d.id);
      return `
        <tr${partial ? ` class="row-error"` : ""}>
          <td><code>${escapeHtml(d.id)}</code>${d.source && d.source !== d.id ? `<div class="meta">${escapeHtml(d.source)}</div>` : ""}</td>
          <td>${d.chunks ?? 0}</td>
          <td>${fmtBytes(d.bytes ?? 0)}</td>
          <td>${escapeHtml(d.mediaType ?? "—")}</td>
          <td class="meta">${d.ingestedAt ? new Date(d.ingestedAt).toLocaleString() : "—"}</td>
          <td>${partial
            ? `<span class="pill pill-warn">partial</span>`
            : `<span class="pill pill-ok">complete</span>`}</td>
          <td><div class="actions">
            <button data-daction="preview" data-doc="${id}">Preview</button>
            <button class="danger" data-daction="delete" data-doc="${id}">Delete</button>
          </div></td>
        </tr>
        <tr id="preview-${id}" style="display:none"><td colspan="7" style="padding:0"><div class="chunk-list"></div></td></tr>`;
    }).join("");
  };

  const refreshDocuments = async () => {
    if (!documentsCollection) {
      renderDocuments([]);
      return;
    }
    try {
      const body = await documentsFetch("");
      if (body) renderDocuments(body.documents ?? []);
    } catch (err) {
      toast("Could not list documents", `${documentsCollection}: ${err.message}`, "err");
    }
  };

  const previewDocument = async (documentId) => {
    const row = document.getElementById(`preview-${encodeURIComponent(documentId)}`);
    if (!row) return;

    if (row.style.display !== "none") {
      row.style.display = "none";
      return;
    }

    try {
      const body = await documentsFetch(`/${encodeURIComponent(documentId)}/chunks`);
      if (!body) return;

      const list = row.querySelector(".chunk-list");
      list.innerHTML = (body.chunks ?? []).map(c => `
        <div class="chunk-preview">
          <div class="chunk-head">chunk ${escapeHtml(c.index ?? "?")}${c.page ? ` · page ${escapeHtml(c.page)}` : ""} · <code>${escapeHtml((c.id ?? "").slice(0, 12))}…</code></div>
          <div class="chunk-text">${escapeHtml(c.text ?? "")}</div>
        </div>`).join("");
      row.style.display = "";
    } catch (err) {
      toast("Could not read chunks", `${documentId}: ${err.message}`, "err");
    }
  };

  const deleteDocument = async (documentId) => {
    if (!window.confirm(`Delete "${documentId}" from "${documentsCollection}"?\n\nEvery chunk of it is removed from the vector store.`)) return;
    try {
      const body = await documentsFetch(`/${encodeURIComponent(documentId)}`, { method: "DELETE" });
      if (!body) return;
      toast("Document deleted", `${documentId} · ${body.chunks ?? 0} chunks removed`, "ok");
      await refreshDocuments();
      pollStatusNow();
    } catch (err) {
      toast("Delete failed", `${documentId}: ${err.message}`, "err");
    }
  };

  const uploadDocument = async (file) => {
    if (!documentsCollection) {
      toast("No collection selected", "Create a vector collection first.", "warn");
      return;
    }

    const zone = document.getElementById("documents-drop");
    zone?.classList.add("busy");
    const form = new FormData();
    form.append("file", file);

    try {
      // A 500 with status=partial is the honest outcome of a run that embedded some chunks and
      // then lost the fleet — the document is really there, in part, and saying "uploaded" would
      // be the lie this whole feature is written to avoid.
      const res = await fetch(`/api/collections/${encodeURIComponent(documentsCollection)}/documents`, {
        method: "POST",
        headers: clientKey ? { "Authorization": `Bearer ${clientKey}` } : {},
        body: form
      });

      if (res.status === 401) {
        if (promptForClientKey("Client key required or invalid.")) await uploadDocument(file);
        return;
      }

      const body = await res.json().catch(() => null);

      if (res.ok && body?.status === "unchanged") {
        toast("Already ingested", `${body.documentId} — identical bytes, no work done`, "ok");
      } else if (res.ok) {
        toast("Document ingested", `${body.documentId} · ${body.chunks} chunks embedded`, "ok");
      } else if (body?.status === "partial") {
        toast("Partially ingested",
          `${body.documentId} · ${body.chunksEmbedded}/${body.chunks} chunks — ${body.error ?? "embedding failed"}`, "err");
      } else {
        throw new Error(body?.error ?? `HTTP ${res.status}`);
      }

      await refreshDocuments();
      pollStatusNow();
    } catch (err) {
      toast("Ingest failed", `${file.name}: ${err.message}`, "err");
    } finally {
      zone?.classList.remove("busy");
    }
  };

  // Collections come from the status poll; keep the picker in step with them without stamping
  // on whatever the operator has currently selected.
  const syncDocumentCollections = (vector) => {
    const section = document.getElementById("documents-section");
    const select = document.getElementById("documents-collection");
    if (!section || !select) return;

    const names = (vector?.collections ?? []).map(c => c.name);
    section.style.display = names.length > 0 ? "" : "none";
    const playground = document.getElementById("playground-section");
    if (playground) playground.style.display = names.length > 0 ? "" : "none";
    if (names.length === 0) {
      documentsCollection = null;
      return;
    }

    const current = select.value;
    const signature = names.join(" ");
    if (signature !== documentsCollectionsSignature) {
      documentsCollectionsSignature = signature;
      select.innerHTML = names.map(n => `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`).join("");
      select.value = names.includes(current) ? current : names[0];
    }

    if (select.value !== documentsCollection) {
      documentsCollection = select.value;
      refreshDocuments();
    }
  };

  document.getElementById("documents-collection")?.addEventListener("change", (event) => {
    documentsCollection = event.target.value;
    refreshDocuments();
  });

  document.getElementById("documents-refresh")?.addEventListener("click", refreshDocuments);

  const dropzone = document.getElementById("documents-drop");
  const fileInput = document.getElementById("documents-file");
  if (dropzone && fileInput) {
    dropzone.addEventListener("click", () => fileInput.click());
    fileInput.addEventListener("change", () => {
      if (fileInput.files?.length) uploadDocument(fileInput.files[0]);
      fileInput.value = "";
    });
    dropzone.addEventListener("dragover", (event) => {
      event.preventDefault();
      dropzone.classList.add("over");
    });
    dropzone.addEventListener("dragleave", () => dropzone.classList.remove("over"));
    dropzone.addEventListener("drop", (event) => {
      event.preventDefault();
      dropzone.classList.remove("over");
      const file = event.dataTransfer?.files?.[0];
      if (file) uploadDocument(file);
    });
  }

  document.getElementById("documents")?.addEventListener("click", (event) => {
    const button = event.target.closest("button[data-daction]");
    if (!button) return;
    const documentId = decodeURIComponent(button.dataset.doc);
    if (button.dataset.daction === "preview") previewDocument(documentId);
    if (button.dataset.daction === "delete") deleteDocument(documentId);
  });

  document.getElementById("nodes").addEventListener("click", (event) => {
    const button = event.target.closest("button[data-action]");
    if (!button) return;
    const nodeId = decodeURIComponent(button.dataset.node);
    const action = button.dataset.action;

    switch (action) {
      case "cordon":
        runAction(nodeId, "cordon");
        break;
      case "uncordon":
        runAction(nodeId, "uncordon");
        break;
      case "drain":
        startDrain(nodeId);
        break;
      case "deregister":
        confirmAndRun(nodeId, "deregister",
          `Deregister node "${nodeId}"?\n\nThis force-disconnects the node. It will re-register on reconnect.`);
        break;
    }
  });

  // --- Retrieval playground (phase 24) -----------------------------------------------------
  // Runs the same query in each mode against POST /api/collections/{c}/search and shows the ranked
  // chunks side by side. Client-scoped like the documents panel, so it reuses the same client key.
  const pgModes = [
    { label: "vector", mode: "vector", rerank: false },
    { label: "keyword", mode: "keyword", rerank: false },
    { label: "hybrid", mode: "hybrid", rerank: false },
    { label: "hybrid + rerank", mode: "hybrid", rerank: true },
  ];

  const pgColumn = (label, hits, error) => {
    let inner;
    if (error) {
      inner = `<div class="meta">${escapeHtml(error)}</div>`;
    } else if (!hits || hits.length === 0) {
      inner = `<div class="meta">no matches</div>`;
    } else {
      inner = hits.map((h, i) => {
        const cite = h.documentId
          ? `${escapeHtml(h.documentId)}${h.page ? ` · p${h.page}` : ""}`
          : `<code>${escapeHtml(h.id)}</code>`;
        return `<div class="pg-hit">
          <div><span class="pg-rank">#${i + 1}</span> ${cite} <span class="meta">${h.score.toFixed(3)}</span></div>
          <div class="pg-snippet">${escapeHtml(h.text ?? "")}</div>
        </div>`;
      }).join("");
    }
    return `<div class="card pg-col"><h3>${escapeHtml(label)}</h3>${inner}</div>`;
  };

  const pgSearchOne = async (query, spec, k) => {
    try {
      const res = await fetch(`/api/collections/${encodeURIComponent(documentsCollection)}/search`, {
        method: "POST",
        headers: clientHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ query, mode: spec.mode, k, rerank: spec.rerank })
      });
      if (res.status === 401) {
        promptForClientKey("Client key required for the retrieval playground.");
        return pgColumn(spec.label, null, "unauthorized");
      }
      if (!res.ok) {
        let detail = `HTTP ${res.status}`;
        try { const body = await res.json(); if (body?.error) detail = body.error; } catch { }
        return pgColumn(spec.label, null, detail);
      }
      const body = await res.json();
      return pgColumn(spec.label, body.hits, null);
    } catch (err) {
      return pgColumn(spec.label, null, err.message);
    }
  };

  const runPlayground = async () => {
    if (!documentsCollection) { toast("No collection", "Select a collection in the Documents panel first.", "err"); return; }
    const query = document.getElementById("pg-query")?.value.trim();
    if (!query) return;
    const k = Math.max(1, Math.min(50, parseInt(document.getElementById("pg-k")?.value, 10) || 5));
    const results = document.getElementById("pg-results");
    if (!results) return;
    results.innerHTML = pgModes.map(m => pgColumn(m.label, [], "searching…")).join("");
    const columns = await Promise.all(pgModes.map(m => pgSearchOne(query, m, k)));
    results.innerHTML = columns.join("");
  };

  document.getElementById("pg-run")?.addEventListener("click", runPlayground);
  document.getElementById("pg-query")?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") runPlayground();
  });

  // --- Clients & usage (phase 25) -----------------------------------------------------------
  // Admin-scoped. The rows are aggregates of (client, model, counts) — the ledger holds
  // counts, never text, so there is nothing more detailed to show.
  let usageRows = [];

  const fmtInt = (n) => (n ?? 0).toLocaleString("en-US");

  const limitChips = (limits) => {
    if (!limits) return `<span class="meta">unlimited</span>`;
    const parts = [];
    if (limits.maxConcurrent != null) parts.push(`concurrent ≤ ${limits.maxConcurrent}`);
    if (limits.requestsPerMinute != null) parts.push(`${limits.requestsPerMinute} req/min`);
    if (limits.tokensPerMinute != null) parts.push(`${fmtInt(limits.tokensPerMinute)} tok/min`);
    if (limits.tokensPerDay != null) parts.push(`${fmtInt(limits.tokensPerDay)} tok/day`);
    if (limits.allowedModels?.length) parts.push(`models: ${limits.allowedModels.join(", ")}`);
    if (parts.length === 0) return `<span class="meta">unlimited</span>`;
    return `<div class="labels">${parts.map(p => `<span class="label-chip">${escapeHtml(p)}</span>`).join("")}</div>`;
  };

  const renderClients = (clients) => {
    const tbody = document.getElementById("clients");
    if (!tbody) return;
    if (!clients || clients.length === 0) {
      tbody.innerHTML = `<tr><td colspan="6" class="empty">No named clients configured — every key is anonymous and unlimited (Auth:Clients).</td></tr>`;
      return;
    }
    tbody.innerHTML = clients.map(c => `<tr>
      <td><code>${escapeHtml(c.id)}</code></td>
      <td>${limitChips(c.limits)}</td>
      <td>${fmtInt(c.live?.inFlight)}</td>
      <td>${fmtInt(c.live?.requestsLastMinute)}</td>
      <td>${fmtInt(c.live?.tokensLastMinute)}</td>
      <td>${fmtInt(c.live?.tokensToday)}</td>
    </tr>`).join("");
  };

  const renderUsage = (rows) => {
    const tbody = document.getElementById("usage");
    if (!tbody) return;
    if (!rows || rows.length === 0) {
      tbody.innerHTML = `<tr><td colspan="7" class="empty">No usage recorded for this window.</td></tr>`;
      return;
    }
    tbody.innerHTML = rows.map(r => `<tr>
      <td><code>${escapeHtml(r.clientId)}</code></td>
      <td>${escapeHtml(r.model)}</td>
      <td>${fmtInt(r.requests)}</td>
      <td>${fmtInt(r.promptTokens)}</td>
      <td>${fmtInt(r.completionTokens)}</td>
      <td>${fmtInt(r.totalTokens)}</td>
      <td>${fmtInt(r.fallbackRequests)}</td>
    </tr>`).join("");
  };

  const usageQueryString = () => {
    const params = new URLSearchParams();
    const from = document.getElementById("usage-from")?.value;
    const to = document.getElementById("usage-to")?.value;
    const client = document.getElementById("usage-client")?.value.trim();
    const model = document.getElementById("usage-model")?.value.trim();
    // Date inputs are day-granular; the ledger is UTC, so the window is [from 00:00Z, to+1d 00:00Z).
    if (from) params.set("from", `${from}T00:00:00Z`);
    if (to) {
      const end = new Date(`${to}T00:00:00Z`);
      end.setUTCDate(end.getUTCDate() + 1);
      params.set("to", end.toISOString());
    }
    if (client) params.set("clientId", client);
    if (model) params.set("model", model);
    const qs = params.toString();
    return qs ? `?${qs}` : "";
  };

  const refreshUsage = async () => {
    if (!adminKey && !promptForKey("Admin key required for usage data.")) return;
    const summary = document.getElementById("usage-summary");
    try {
      const [usageRes, clientsRes] = await Promise.all([
        fetch(`/api/admin/usage${usageQueryString()}`, { headers: adminHeaders() }),
        fetch("/api/admin/clients", { headers: adminHeaders() })
      ]);
      if (usageRes.status === 401 || clientsRes.status === 401) {
        if (promptForKey("Admin key required or invalid.")) return refreshUsage();
        return;
      }
      if (!usageRes.ok) throw new Error(`HTTP ${usageRes.status}`);
      if (!clientsRes.ok) throw new Error(`HTTP ${clientsRes.status}`);
      const usageBody = await usageRes.json();
      usageRows = usageBody.rows ?? [];
      renderUsage(usageRows);
      renderClients(await clientsRes.json());
      const totalTokens = usageRows.reduce((sum, r) => sum + (r.totalTokens ?? 0), 0);
      if (summary) summary.textContent =
        `${usageRows.length} row${usageRows.length === 1 ? "" : "s"} · ${fmtInt(totalTokens)} tokens`;
      const csvBtn = document.getElementById("usage-csv");
      if (csvBtn) csvBtn.disabled = usageRows.length === 0;
    } catch (err) {
      if (summary) summary.textContent = err.message;
      toast("Usage refresh failed", err.message, "err");
    }
  };

  const exportUsageCsv = () => {
    if (usageRows.length === 0) return;
    const esc = (v) => {
      const s = String(v ?? "");
      return /[",\n]/.test(s) ? `"${s.replaceAll('"', '""')}"` : s;
    };
    const header = "clientId,model,requests,promptTokens,completionTokens,totalTokens,fallbackRequests";
    const lines = usageRows.map(r =>
      [r.clientId, r.model, r.requests, r.promptTokens, r.completionTokens, r.totalTokens, r.fallbackRequests]
        .map(esc).join(","));
    const blob = new Blob([header + "\n" + lines.join("\n") + "\n"], { type: "text/csv" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = `inferhub-usage-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    URL.revokeObjectURL(link.href);
  };

  document.getElementById("usage-refresh")?.addEventListener("click", refreshUsage);
  document.getElementById("usage-csv")?.addEventListener("click", exportUsageCsv);

  document.getElementById("mm-node")?.addEventListener("change", renderModelNodeSelect);
  const runModelCommand = (kind) => {
    const node = selectedModelNode();
    const model = document.getElementById("mm-model")?.value.trim();
    if (!node) { pushModelNote("Select a node first.", true); return; }
    if (!model) { pushModelNote("Enter a model name.", true); return; }
    postModelCommand(kind, node.nodeId, model);
  };
  document.getElementById("mm-pull")?.addEventListener("click", () => runModelCommand("pull"));
  document.getElementById("mm-warm")?.addEventListener("click", () => runModelCommand("warm"));
  document.getElementById("mm-delete")?.addEventListener("click", () => runModelCommand("delete"));
  document.getElementById("mm-ensure")?.addEventListener("click", () => {
    const model = document.getElementById("mm-model")?.value.trim();
    const replicas = Math.max(1, parseInt(document.getElementById("mm-replicas")?.value, 10) || 1);
    if (!model) { pushModelNote("Enter a model name to ensure.", true); return; }
    ensureModel(model, replicas);
  });
  document.getElementById("model-matrix")?.addEventListener("click", (event) => {
    const btn = event.target.closest("button[data-mm]");
    if (!btn) return;
    postModelCommand(btn.dataset.mm, decodeURIComponent(btn.dataset.node), decodeURIComponent(btn.dataset.model));
  });

  // --- Node profiles (phase 43) --------------------------------------------------------------
  //
  // Admin-scoped CRUD over /api/admin/profiles. The editor is a textarea over the profile's own
  // JSON rather than a form of checkboxes: a profile is a small document with an open-ended
  // capability map and a retrieval block, and a form would have to be rewritten for every field a
  // later phase adds — while what the operator wants to paste into a ticket is the JSON anyway.

  let profileNames = [];
  let profileNamesSignature = "";

  const profileNote = (text, isError) => {
    const el = document.getElementById("profile-note");
    if (!el) return;
    el.style.display = text ? "" : "none";
    el.className = `row-msg ${isError ? "" : "info"}`;
    el.textContent = text ?? "";
  };

  const profileSummary = (text) => {
    const el = document.getElementById("profile-summary");
    if (el) el.textContent = text;
  };

  const NEW_PROFILE = {
    name: "gpu-boxes",
    selector: { labels: { role: "gpu" } },
    capabilities: { chat: true },
    maxConcurrency: 2
  };

  const showProfile = (profile) => {
    const body = document.getElementById("profile-body");
    if (body) body.value = JSON.stringify(profile, null, 2);
    profileNote(null);
  };

  const refreshProfiles = async (selectName) => {
    const select = document.getElementById("profile-pick");
    if (!select) return;

    let profiles;
    try {
      const res = await fetch("/api/admin/profiles", { headers: adminHeaders() });
      if (res.status === 401) {
        profileSummary("admin key required");
        return;
      }
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      profiles = await res.json();
    } catch (err) {
      profileSummary(`could not load profiles: ${err.message}`);
      return;
    }

    profileNames = profiles.map(p => p.name);
    const signature = profileNames.join(" ");

    if (signature !== profileNamesSignature) {
      profileNamesSignature = signature;
      select.innerHTML = profileNames.length === 0
        ? `<option value="">(none yet)</option>`
        : profileNames.map(n => `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`).join("");
    }

    const wanted = selectName ?? select.value;
    const chosen = profiles.find(p => p.name === wanted) ?? profiles[0];

    if (chosen) {
      select.value = chosen.name;
      showProfile(chosen);
      profileSummary(`${profiles.length} profile${profiles.length === 1 ? "" : "s"} · ${chosen.name}@${chosen.revision}`);
    } else {
      profileSummary("no profiles yet — New… then Apply");
    }
  };

  const applyProfile = async () => {
    if (!adminKey && !promptForKey("Admin key required to write a profile.")) return;

    let profile;
    try {
      profile = JSON.parse(document.getElementById("profile-body").value);
    } catch (err) {
      profileNote(`that is not valid JSON: ${err.message}`, true);
      return;
    }

    const name = (profile.name ?? "").trim();
    if (!name) {
      profileNote("the profile needs a name", true);
      return;
    }

    try {
      const res = await fetch(`/api/admin/profiles/${encodeURIComponent(name)}`, {
        method: "PUT",
        headers: adminHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify(profile)
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(body.error ?? `HTTP ${res.status}`);

      // What a write actually did, said out loud: how many boxes took it, and which ones two
      // profiles now select. A silent 200 is how somebody believes a fleet changed when it did not.
      const applied = body.applied ?? [];
      const conflicts = body.conflicts ?? [];
      toast(
        `${name}@${body.profile?.revision} written`,
        `${applied.length} node(s) matched${conflicts.length ? `, ${conflicts.length} in conflict` : ""}`,
        conflicts.length ? "warn" : "ok");
      profileNote(
        conflicts.length
          ? `conflict: ${conflicts.map(c => `${c.nodeId} is matched by ${(c.profiles ?? []).join(", ")}`).join("; ")}`
          : `applied to ${applied.length === 0 ? "no connected node yet" : applied.join(", ")}`,
        conflicts.length > 0);

      await refreshProfiles(name);
      pollStatusNow();
    } catch (err) {
      profileNote(err.message, true);
      toast("Profile not written", err.message, "err");
    }
  };

  const deleteProfile = async () => {
    const name = document.getElementById("profile-pick")?.value;
    if (!name) return;
    if (!adminKey && !promptForKey("Admin key required to delete a profile.")) return;
    // Deleting is not "stop configuring these boxes", it is "revert them to their own config", and
    // the confirm says so because those read the same and are not (phase-43 D2).
    if (!window.confirm(`Delete profile "${name}"? Every node it matched reverts to its own configuration.`)) return;

    try {
      const res = await fetch(`/api/admin/profiles/${encodeURIComponent(name)}`, {
        method: "DELETE",
        headers: adminHeaders()
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(body.error ?? `HTTP ${res.status}`);

      const reverted = body.reverted ?? [];
      toast(`${name} deleted`, `${reverted.length} node(s) reverted to their own configuration`, "ok");
      profileNamesSignature = "";
      await refreshProfiles();
      pollStatusNow();
    } catch (err) {
      toast("Profile not deleted", err.message, "err");
    }
  };

  document.getElementById("profile-pick")?.addEventListener("change", () => refreshProfiles());
  document.getElementById("profile-new")?.addEventListener("click", () => {
    showProfile(NEW_PROFILE);
    profileSummary("editing a new profile — Apply writes it");
  });
  document.getElementById("profile-apply")?.addEventListener("click", applyProfile);
  document.getElementById("profile-delete")?.addEventListener("click", deleteProfile);

  // ---------------------------------------------------------------- Images (phase 51, D1)
  //
  // Job-centric, because "it is running and I cannot tell how far along" is the state this track
  // produces that nothing else in the console answers. Everything here comes from routes 46–50
  // already expose plus the one listing phase 49 deferred to this phase; the recipe and card tables
  // come off /api/status, which the node already reports into.
  //
  // D3: the gallery is the browser's. Object URLs in this tab, revoked when the panel re-renders,
  // gone on reload. Fetching a result CONSUMES it — that is the store's read-once rule, not a
  // console decision — so a thumbnail here is the only copy that still exists.

  let imageJobs = [];
  const imageGallery = [];

  const imageNote = (message, kind) => {
    const note = document.getElementById("image-note");
    if (!note) return;
    note.textContent = message;
    note.className = kind === "err" ? "row-msg" : "row-msg info";
  };

  const progressCell = (job) => {
    if (job.state === "queued") {
      return `<span class="progress-text">${job.queuePosition ? `#${job.queuePosition} in line` : "queued"}</span>`;
    }

    // A worker that sends no progress frames is a worker written against 3.14, and it is not
    // broken — so the cell says what it knows rather than showing a bar stuck at zero.
    if (job.step == null || !job.totalSteps) {
      return `<span class="progress-text">${escapeHtml(job.state)}</span>`;
    }

    const percent = Math.max(0, Math.min(100, Math.round((job.step / job.totalSteps) * 100)));
    return `<div class="progress">
        <div class="progress-track"><div class="progress-fill" style="width:${percent}%"></div></div>
        <span class="progress-text">${job.step}/${job.totalSteps}</span>
      </div>`;
  };

  const imageElapsed = (job) => {
    const started = Date.parse(job.createdAt);
    if (!Number.isFinite(started)) return "—";
    const ended = job.completedAt ? Date.parse(job.completedAt) : Date.now();
    return fmtSeconds(Math.max(0, (ended - started) / 1000));
  };

  const renderImageJobs = () => {
    const tbody = document.getElementById("image-jobs");
    if (!tbody) return;

    if (imageJobs.length === 0) {
      tbody.innerHTML = emptyRow("image-jobs", 9, "No image jobs. Submit one above, or POST /api/images/jobs.");
      return;
    }

    tbody.innerHTML = imageJobs.map(job => {
      const ready = job.state === "succeeded" && (job.images ?? []).length > 0;
      const warnings = (job.warnings ?? []).length > 0
        ? ` <span class="pill pill-warn">${escapeHtml(job.warnings.join(", "))}</span>` : "";

      const result = ready
        ? `${job.images.length} image(s), ${fmtBytes(job.images.reduce((sum, i) => sum + (i.bytes ?? 0), 0))}${warnings}`
        : job.error
          ? `<span class="why">${escapeHtml(job.errorCode ? `${job.errorCode}: ${job.error}` : job.error)}</span>`
          : job.reason === "delivered" ? "collected" : "—";

      const actions = [];
      if (ready) actions.push(`<button data-image-fetch="${escapeHtml(job.id)}">Fetch</button>`);
      if (!["succeeded", "failed", "cancelled", "expired"].includes(job.state)) {
        actions.push(`<button data-image-cancel="${escapeHtml(job.id)}">Cancel</button>`);
      }

      return `<tr>
          <td><code title="${escapeHtml(job.id)}">${escapeHtml(job.id.slice(0, 8))}</code></td>
          <td><code>${escapeHtml(job.model)}</code></td>
          <td>${imageStatePill(job)}</td>
          <td>${progressCell(job)}</td>
          <td>${escapeHtml(job.node ?? "—")}</td>
          <td>${imageElapsed(job)}</td>
          <td>${job.megapixelSteps ? job.megapixelSteps.toFixed(1) : "—"}</td>
          <td>${result}</td>
          <td>${actions.join(" ") || "—"}</td>
        </tr>`;
    }).join("");
  };

  const imageStatePill = (job) => {
    const kind = job.state === "succeeded" ? "pill-ok"
      : job.state === "failed" ? "pill-bad"
        : job.state === "running" ? "pill-ok"
          : "pill-warn";
    return `<span class="pill ${kind}">${escapeHtml(job.state)}</span>`;
  };

  const renderImageGallery = () => {
    const host = document.getElementById("image-gallery");
    if (!host) return;

    host.innerHTML = imageGallery.map(item => `
      <figure>
        <img src="${item.url}" alt="${escapeHtml(item.caption)}" data-image-open="${escapeHtml(item.job)}">
        <figcaption>${escapeHtml(item.caption)}</figcaption>
      </figure>`).join("");
  };

  // The three-column answer to "why can I not use that model": what the node holds, what it
  // offers, and — for everything it does not — the reason, which is the whole of phase 51 D1.
  const renderImageRecipes = (status) => {
    const tbody = document.getElementById("image-recipes");
    if (!tbody) return;

    const rows = (status?.nodes ?? []).flatMap(node =>
      (node.tools?.images ?? []).map(recipe => ({ node, recipe })));

    if (rows.length === 0) {
      tbody.innerHTML = emptyRow("image-recipes", 6, "No node reports image recipes. Run the :diffusion image on a box with a card.");
      return;
    }

    tbody.innerHTML = rows.map(({ node, recipe }) => {
      const kinds = (recipe.kinds ?? []).map(k =>
        `<span class="pill pill-ok">${k === "image-edit" ? "edit" : "generate"}</span>`).join(" ");

      const why = recipe.offered ? "" : imageRecipeWhy(recipe);

      return `<tr${recipe.offered ? "" : ' class="refusal-row"'}>
          <td>${escapeHtml(node.name)}</td>
          <td><code>${escapeHtml(recipe.id)}</code></td>
          <td>${kinds || "—"}</td>
          <td><span class="why">${why}</span></td>
          <td>${recipe.vramMiB ? `${recipe.vramMiB} MiB` : "—"}</td>
          <td><code>${escapeHtml(recipe.licenseId ?? "—")}</code></td>
        </tr>`;
    }).join("");
  };

  // Each reason names the fix, because the four of them have four different ones and a bare
  // "not offered" sends everybody to the same wrong place first.
  const imageRecipeWhy = (recipe) => {
    switch (recipe.reason) {
      case "unlicensed":
        return `its licence <code>${escapeHtml(recipe.licenseId ?? "?")}</code> is not permissive and is not in Tools:Image:AcceptedLicenses` +
          (recipe.licenseUrl ? ` — <a href="${escapeHtml(recipe.licenseUrl)}" target="_blank" rel="noopener">read it</a>` : "");
      case "over-budget":
        return `it wants ${recipe.vramMiB} MiB and does not fit this node's declared Node:Vram:BudgetMiB minus its reserve`;
      case "narrowed":
        return "a coordinator profile switched it off on this node";
      case "not-ready":
        return "no worker offers it: weights still fetching, a fetch that failed, not cpuViable on a CPU-only box, or a pool that is not running — the node's log says which";
      default:
        return "";
    }
  };

  const renderImageVram = (status) => {
    const tbody = document.getElementById("image-vram");
    if (!tbody) return;

    const rows = (status?.nodes ?? []).filter(n => n.tools?.vram);

    if (rows.length === 0) {
      // Not a zero. A node with no declared budget has not measured anything and has no gate, and
      // "0 MiB" would read as "this box has no VRAM" (phase-48 D1, phase-28 D5).
      tbody.innerHTML = emptyRow("image-vram", 6, "No node declares Node:Vram:BudgetMiB. Undeclared is not zero — there is simply no gate on those boxes.");
      return;
    }

    tbody.innerHTML = rows.map(node => {
      const vram = node.tools.vram;
      const resident = (vram.resident ?? []);
      const usedMiB = resident.reduce((sum, r) => sum + (r.vramMiB ?? 0), 0);
      const free = Math.max(0, vram.budgetMiB - vram.reserveMiB - usedMiB);

      const chips = resident.map(r =>
        `<span class="pill ${r.inUse ? "pill-ok" : ""}">${escapeHtml(r.model)} ${r.vramMiB} MiB${r.inUse ? " · in use" : ""}</span>`).join(" ");

      // The worker's own reading beside the declared one, never instead of it — a disagreement is
      // the thing worth seeing, and adopting the measurement would be detecting VRAM after all.
      const measured = vram.measuredMiB
        ? `${vram.measuredMiB} MiB${Math.abs(vram.measuredMiB - vram.budgetMiB) > vram.budgetMiB * 0.1
          ? ' <span class="pill pill-warn">differs</span>' : ""}`
        : "—";

      return `<tr>
          <td>${escapeHtml(node.name)}</td>
          <td>${vram.budgetMiB} MiB</td>
          <td>${vram.reserveMiB} MiB</td>
          <td>${chips || "—"}</td>
          <td>${free} MiB</td>
          <td>${measured}</td>
        </tr>`;
    }).join("");
  };

  const imageFetch = async (path, init, retryOn401 = true) => {
    const res = await fetch(path, { ...init, headers: clientHeaders(init?.headers) });

    if (res.status === 401 && retryOn401) {
      if (!promptForClientKey("Client key required: image jobs are guarded by Auth:ApiKeys.")) return null;
      return imageFetch(path, init, false);
    }

    return res;
  };

  const refreshImageJobs = async () => {
    const res = await imageFetch("/api/images/jobs");
    if (!res) return;

    if (!res.ok) {
      imageNote(`Could not list image jobs: HTTP ${res.status}`, "err");
      return;
    }

    const body = await res.json();
    imageJobs = body.jobs ?? [];
    renderImageJobs();

    // The queue's own numbers rather than a count of the rows above: the rows are this client's,
    // and "3 waiting" is a fact about the fleet. `retainedBytes` is the one worth having on screen
    // — it is the memory the hub is holding on your behalf, and the sentence next to it is why it
    // will not grow forever.
    imageNote(
      `${body.active ?? 0} active, ${body.queued ?? 0} waiting · ` +
      `${fmtBytes(body.retainedBytes ?? 0)} held in memory, dropped on delivery or after ` +
      `${fmtSeconds(body.retentionSeconds ?? 0)}. Thumbnails below live in this tab and vanish on reload.`,
      "info");

    scheduleImagePoll();
  };

  document.getElementById("image-refresh")?.addEventListener("click", refreshImageJobs);

  document.getElementById("image-submit")?.addEventListener("click", async () => {
    const model = (document.getElementById("image-model")?.value ?? "").trim();
    const prompt = (document.getElementById("image-prompt")?.value ?? "").trim();
    const size = (document.getElementById("image-size")?.value ?? "").trim();

    if (!model || !prompt) {
      imageNote("A model and a prompt, at least. The model is a recipe id — sdxl, not a repo id.", "err");
      return;
    }

    const body = { model, prompt };
    if (size) body.size = size;

    const res = await imageFetch("/api/images/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });

    if (!res) return;

    const answer = await res.json().catch(() => ({}));

    if (!res.ok) {
      // The edge's own sentence, which names what is possible — a size outside the recipe's
      // buckets, an unknown header value, a capability nobody provides. Showing our own summary
      // instead would throw away the one message written to be acted on.
      imageNote(answer?.error?.message ?? `HTTP ${res.status}`, "err");
      return;
    }

    imageNote(`Submitted ${answer.id}. It is job-shaped: watch the row, or cancel it.`, "info");
    await refreshImageJobs();
  });

  document.getElementById("image-jobs")?.addEventListener("click", async (event) => {
    const cancelId = event.target?.getAttribute?.("data-image-cancel");
    const fetchId = event.target?.getAttribute?.("data-image-fetch");

    if (cancelId) {
      const res = await imageFetch(`/api/images/jobs/${cancelId}`, { method: "DELETE" });
      if (!res) return;

      // Best-effort, and the UI says so rather than pretending: a job cancelled at step 27 of 28
      // may still succeed, and if it does you get the image.
      imageNote(res.ok
        ? "Cancel asked for. It is cooperative — a job near the end may still finish, and you get the picture."
        : `Could not cancel: HTTP ${res.status}`, res.ok ? "info" : "err");

      await refreshImageJobs();
      return;
    }

    if (fetchId) {
      const res = await imageFetch(`/api/images/jobs/${fetchId}/content/0`);
      if (!res) return;

      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        imageNote(body?.error?.message ?? `HTTP ${res.status}`, "err");
        await refreshImageJobs();
        return;
      }

      const projection = res.headers.get("X-InferHub-Image-Projection") ?? "flat";
      const url = URL.createObjectURL(await res.blob());

      imageGallery.unshift({ job: fetchId, url, caption: `${fetchId.slice(0, 8)} · ${projection}` });

      // Bounded, and the bound is the tab's memory rather than a policy: past a handful the object
      // URLs are revoked so a long console session does not hold every picture it ever fetched.
      while (imageGallery.length > 8) URL.revokeObjectURL(imageGallery.pop().url);

      renderImageGallery();
      imageNote(projection === "equirectangular"
        ? "Fetched. It is equirectangular — put the job id in the 360° viewer below to look around it."
        : "Fetched. It is in this tab only: the hub dropped its copy on delivery.", "info");

      await refreshImageJobs();
    }
  });

  // Clicking a thumbnail sends it to the viewer, which picks its renderer from the projection the
  // worker declared rather than from the aspect ratio.
  document.getElementById("image-gallery")?.addEventListener("click", (event) => {
    const job = event.target?.getAttribute?.("data-image-open");
    if (!job) return;
    const input = document.getElementById("pano-job");
    if (input) input.value = job;
    document.getElementById("pano-load")?.click();
  });

  // A job with a step counter changes several times a second on a fast recipe, and once every few
  // seconds on a slow one — so the panel polls only while something is actually in flight, and
  // stops when the list goes quiet. It is deliberately NOT wired into the status poll: that runs
  // whether or not anybody has given the console a client key, and a background 401 prompt every
  // few seconds would be unusable.
  let imagePoll = null;

  const scheduleImagePoll = () => {
    const busy = imageJobs.some(job => !["succeeded", "failed", "cancelled", "expired"].includes(job.state));

    if (!busy) {
      if (imagePoll) { clearTimeout(imagePoll); imagePoll = null; }
      return;
    }

    if (imagePoll) return;

    imagePoll = setTimeout(async () => {
      imagePoll = null;
      await refreshImageJobs();
    }, 1500);
  };

  // ---------------------------------------------------------------- 360° viewer (phase 49)
  //
  // The one rule here: the renderer is chosen from `X-InferHub-Image-Projection`, never from the
  // aspect ratio. A 2048×1024 photograph and a 2048×1024 panorama are indistinguishable as pixels,
  // and guessing gets one of them wrong every time — which is exactly why the worker declares it
  // and the header carries it to the one request that has no JSON to read it from.

  let pano = null;
  let panoObjectUrl = null;

  const panoNote = (message, kind) => {
    const note = document.getElementById("pano-note");
    if (!note) return;
    note.textContent = message;
    note.className = kind === "err" ? "row-msg" : "row-msg info";
  };

  const panoViewer = () => {
    if (!pano) {
      const canvas = document.getElementById("pano-canvas");
      if (!canvas || !window.InferHubPano) return null;
      pano = window.InferHubPano.mount(canvas);
      if (pano.reason) panoNote(pano.reason, "err");
    }
    return pano;
  };

  const panoLoad = async () => {
    const viewer = panoViewer();
    if (!viewer) return;

    const raw = (document.getElementById("pano-job")?.value ?? "").trim();
    const index = Math.max(0, Number(document.getElementById("pano-index")?.value ?? 0) | 0);

    if (!raw) {
      panoNote("Give it a job id, or a URL to any equirectangular image.", "err");
      return;
    }

    // A bare id addresses this hub's own job; anything else is taken at face value, so a panorama
    // hosted anywhere can be checked in the same viewer without a round trip through the fleet.
    const isJobId = /^[0-9a-f-]{36}$/i.test(raw);
    const url = isJobId ? `/api/images/jobs/${raw}/content/${index}` : raw;

    try {
      let projection = "flat";
      let source = url;

      if (isJobId) {
        const res = await fetch(url, { headers: clientHeaders() });

        if (res.status === 401) {
          if (!promptForClientKey("Client key required: image jobs are guarded by Auth:ApiKeys.")) return;
          return panoLoad();
        }

        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body?.error?.message ?? `HTTP ${res.status}`);
        }

        projection = res.headers.get("X-InferHub-Image-Projection") ?? "flat";

        if (panoObjectUrl) URL.revokeObjectURL(panoObjectUrl);
        panoObjectUrl = URL.createObjectURL(await res.blob());
        source = panoObjectUrl;
      } else {
        projection = "equirectangular";
      }

      const size = await viewer.load(source);
      const equirectangular = projection === "equirectangular";
      viewer.setFlat(!equirectangular);

      const dimensions = size ? `${size.width}×${size.height}` : "loaded";

      panoNote(
        equirectangular
          ? `${dimensions}, equirectangular — drag to look, scroll to zoom, arrow keys when focused.`
          : `${dimensions}, projection "${projection}" — shown flat, because it is not a panorama.`,
        "info");
    } catch (err) {
      panoNote(err.message, "err");
    }
  };

  document.getElementById("pano-load")?.addEventListener("click", panoLoad);
  document.getElementById("pano-job")?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") panoLoad();
  });
  document.getElementById("pano-reset")?.addEventListener("click", () => panoViewer()?.reset());
  document.getElementById("pano-flat")?.addEventListener("click", (event) => {
    const viewer = panoViewer();
    if (!viewer) return;
    const flat = event.currentTarget.dataset.on !== "1";
    event.currentTarget.dataset.on = flat ? "1" : "0";
    event.currentTarget.classList.toggle("primary", flat);
    viewer.setFlat(flat);
  });

  setKey(null);
  refreshProfiles();
  pollStatusNow();
  statusPollHandle = setInterval(pollStatusNow, STATUS_POLL_MS);
  ensureNodesPolling();
  streamLoop();
})();
