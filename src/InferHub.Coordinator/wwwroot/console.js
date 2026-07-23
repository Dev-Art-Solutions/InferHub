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
      tbody.innerHTML = `<tr><td colspan="10" class="empty">No nodes connected.</td></tr>`;
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
    if (promptForKey()) restartStream();
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
    const badge = document.getElementById("documents-key-state");
    if (badge) badge.style.display = clientKey ? "" : "none";
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

  setKey(null);
  pollStatusNow();
  statusPollHandle = setInterval(pollStatusNow, STATUS_POLL_MS);
  ensureNodesPolling();
  streamLoop();
})();
