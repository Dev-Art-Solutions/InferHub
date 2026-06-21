// Management console for InferHub.
//
// The admin bearer key lives in this closure for the lifetime of the tab —
// never localStorage / sessionStorage.
(() => {
  let adminKey = null;
  const rowMessages = new Map();      // nodeId -> { text, isError }
  const pendingActions = new Set();   // `${nodeId}:${action}` while a request is in flight
  const draining = new Map();         // nodeId -> { startedAt }
  let pollHandle = null;

  const POLL_MS = 3000;

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

  // ---------------------------------------------------------------- HTTP

  const adminHeaders = () => {
    const h = { "Accept": "application/json" };
    if (adminKey) h["Authorization"] = `Bearer ${adminKey}`;
    return h;
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
      tbody.innerHTML = `<tr><td colspan="9" class="empty">No nodes connected.</td></tr>`;
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

  // ---------------------------------------------------------------- actions

  const setRowMessage = (nodeId, text, isError) => {
    if (!text) {
      rowMessages.delete(nodeId);
    } else {
      rowMessages.set(nodeId, { text, isError: Boolean(isError) });
    }
  };

  const runAction = async (nodeId, action) => {
    const key = `${nodeId}:${action}`;
    if (pendingActions.has(key)) return;
    pendingActions.add(key);

    setRowMessage(nodeId, `${action}…`, false);
    refreshRender(latestNodes, latestStatus);

    try {
      await callAdminAction(nodeId, action);
      setRowMessage(nodeId, null);
    } catch (err) {
      setRowMessage(nodeId, `${action} failed: ${err.message}`, true);
    } finally {
      pendingActions.delete(key);
      await tick();
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
    setRowMessage(nodeId, "cordoning…", false);
    refreshRender(latestNodes, latestStatus);

    try {
      await callAdminAction(nodeId, "cordon");
      setRowMessage(nodeId, "draining — waiting for in-flight jobs", false);
    } catch (err) {
      draining.delete(nodeId);
      setRowMessage(nodeId, `drain failed: ${err.message}`, true);
    } finally {
      pendingActions.delete(cordonKey);
      await tick();
    }
  };

  const evaluateDrains = (nodes) => {
    if (draining.size === 0) return;
    const byId = new Map(nodes.map(n => [n.nodeId, n]));
    for (const nodeId of [...draining.keys()]) {
      const node = byId.get(nodeId);
      if (!node) {
        // Node disappeared (deregistered elsewhere or evicted) — drain is moot.
        draining.delete(nodeId);
        setRowMessage(nodeId, null);
        continue;
      }
      if (node.cordoned && node.localInFlight === 0) {
        draining.delete(nodeId);
        setRowMessage(nodeId, "drained — node is idle and cordoned", false);
      }
    }
  };

  // ---------------------------------------------------------------- poll loop

  let latestNodes = [];
  let latestStatus = null;

  const refreshRender = (nodes, status) => {
    if (status) {
      document.getElementById("version").textContent = `v${status.coordinatorVersion}`;
      document.getElementById("uptime").textContent = fmtSeconds(status.uptimeSeconds);
      renderStats(status.metrics);
      renderModels(status.models);
    }
    renderNodes(nodes ?? []);
  };

  const tick = async () => {
    try {
      const [adminNodes, status] = await Promise.all([
        fetchAdminNodes(),
        fetchStatus()
      ]);
      if (adminNodes !== null) {
        latestNodes = adminNodes;
        evaluateDrains(latestNodes);
      }
      if (status) latestStatus = status;
      document.getElementById("refreshed").textContent = new Date().toLocaleTimeString();
      refreshRender(latestNodes, latestStatus);
    } catch (err) {
      document.getElementById("refreshed").textContent = `error: ${err.message}`;
    }
  };

  // ---------------------------------------------------------------- wiring

  document.getElementById("auth-set").addEventListener("click", () => {
    if (promptForKey()) tick();
  });
  document.getElementById("auth-clear").addEventListener("click", () => {
    setKey(null);
    tick();
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

  setKey(null);
  tick();
  pollHandle = setInterval(tick, POLL_MS);
})();
