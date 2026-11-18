// Fleet page for InferHub (phase 152): nodes grouped, the last hour of traffic, and what needs a hand.
//
// Reads /api/admin/fleet, /api/admin/traffic and /api/admin/groups; acts through /api/admin/groups/{name}[/cordon|/uncordon]
// and the per-node /api/admin/nodes/{id}/cordon|uncordon the console already uses. The admin key lives in this closure
// for the lifetime of the tab — never localStorage / sessionStorage (the console's rule).
(() => {
  const POLL_MS = 5000;
  const PALETTE = ["#4ea1ff", "#4ade80", "#f59e0b", "#c084fc", "#f472b6", "#22d3ee", "#a3e635", "#fb7185"];
  const FLEET_COLOR = "#e4e7ee";
  const MAX_SERIES = PALETTE.length;

  let adminKey = null;
  let connected = false;
  let timer = null;
  let minutes = 15;
  let metric = "rate";
  let groupBy = "group";
  let report = null;
  let traffic = null;
  let definitions = [];
  let selectedGroup = null;          // a group key the node table is narrowed to
  let sort = { key: "name", dir: 1 };
  let editing = null;                // the group name being edited, or "" for a new one
  const hidden = new Set();          // series keys switched off in the legend
  const selectedNodes = new Set();
  const busy = new Set();

  const $ = id => document.getElementById(id);
  const text = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmt = (n, digits = 1) => n == null ? "—" : Number(n).toLocaleString(undefined, { maximumFractionDigits: digits });
  const pct = r => r == null ? "—" : `${(r * 100).toFixed(r < 0.1 && r > 0 ? 1 : 0)}%`;

  async function call(method, path, body) {
    const headers = { "Content-Type": "application/json" };
    if (adminKey) headers["Authorization"] = `Bearer ${adminKey}`;
    const response = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : undefined });
    if (response.status === 204) return {};
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || `${response.status} ${response.statusText}`);
    return data;
  }

  function toast(message, bad) {
    const el = $("toast");
    el.textContent = message;
    el.className = `toast show${bad ? " bad" : ""}`;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => { el.className = "toast"; }, 4000);
  }

  // ---- loading -------------------------------------------------------------------------------------------------

  async function refresh(silent) {
    try {
      const query = `groupBy=${encodeURIComponent(groupBy)}&minutes=${minutes}`;
      const [fleet, history, groups] = await Promise.all([
        call("GET", `/api/admin/fleet?${query}`),
        call("GET", `/api/admin/traffic?${query}`),
        call("GET", "/api/admin/groups")
      ]);
      report = fleet;
      traffic = history;
      definitions = groups.groups || [];
      connected = true;
      $("error").textContent = "";
      $("refreshed").textContent = `refreshed ${new Date().toLocaleTimeString()}`;
      render();
    } catch (e) {
      if (!silent) $("error").textContent = e.message;
      $("refreshed").textContent = connected ? "refresh failed" : "not connected";
    }
  }

  function schedule() {
    clearInterval(timer);
    if ($("auto").checked && connected) timer = setInterval(refresh, POLL_MS);
  }

  // ---- rendering -----------------------------------------------------------------------------------------------

  function render() {
    renderGroupBy();
    renderTiles();
    renderChart();
    renderGroups();
    renderNodes();
  }

  function renderGroupBy() {
    const select = $("group-by");
    const options = [["group", "groups (defined here)"], ["organisation", "organisation"], ["version", "version"], ["none", "nothing (whole fleet)"]]
      .concat((report.labelKeys || []).map(k => [`label:${k}`, `label: ${k}`]));
    if (!options.some(([value]) => value === groupBy)) options.push([groupBy, groupBy]);
    const html = options.map(([value, label]) => `<option value="${text(value)}"${value === groupBy ? " selected" : ""}>${text(label)}</option>`).join("");
    if (select.innerHTML !== html) select.innerHTML = html;
  }

  function tile(label, value, sub, tone) {
    return `<div class="tile${tone ? " " + tone : ""}"><div class="label">${text(label)}</div><div class="value">${value}</div><div class="sub">${sub}</div></div>`;
  }

  function renderTiles() {
    const t = report.totals;
    const capacity = t.capacity ? ` of ${fmt(t.capacity, 0)} declared` : " · no cap declared by every node";
    const failTone = t.failureRate >= 0.2 ? "err" : t.failureRate >= 0.05 ? "warn" : "";
    $("tiles").innerHTML =
      tile("Nodes", `${t.routable}<span class="meta"> / ${t.nodes}</span>`, `taking work · ${t.cordoned} cordoned`)
      + tile("In flight", fmt(t.inFlight, 0), capacity, t.capacity && t.inFlight >= t.capacity ? "warn" : "")
      + tile("Requests / min", fmt(t.perMinute), `${fmt(t.completed + t.failed, 0)} in the last ${report.minutes} min`)
      + tile("Failed", pct(t.failureRate), `${fmt(t.failed, 0)} in the last ${report.minutes} min`, failTone)
      + tile("Needs attention", fmt(t.attention, 0), t.attention ? "nodes with a warning, members absent" : "nothing to look at", t.attention ? "warn" : "");
  }

  function valuesOf(series) {
    const perMinute = traffic.stepSeconds / 60;
    if (metric === "inflight") return series.inFlight;
    if (metric === "failed") return series.failed.map(v => v / perMinute);
    return series.completed.map((v, i) => (v + series.failed[i]) / perMinute);
  }

  function chartSeries() {
    const list = [{ key: "fleet", label: "whole fleet", color: FLEET_COLOR, dashed: true, values: valuesOf(traffic.fleet) }];
    if (traffic.groupBy !== "none") {
      traffic.series.slice(0, MAX_SERIES).forEach((s, i) =>
        list.push({ key: s.key, label: s.key, color: PALETTE[i % PALETTE.length], values: valuesOf(s) }));
    }
    return list;
  }

  function colorOf(key) {
    const index = (traffic?.series || []).findIndex(s => s.key === key);
    return index >= 0 && index < MAX_SERIES ? PALETTE[index] : "var(--muted)";
  }

  function renderChart() {
    const svg = $("chart");
    const W = 1000, H = 240, L = 44, R = 10, T = 12, B = 24;
    const series = chartSeries();
    const visible = series.filter(s => !hidden.has(s.key));
    const points = traffic.points.map(p => new Date(p));
    const n = points.length;
    const max = Math.max(1, ...visible.flatMap(s => s.values));
    const niceMax = niceCeil(max);
    const x = i => L + (n <= 1 ? 0 : (i / (n - 1)) * (W - L - R));
    const y = v => T + (1 - v / niceMax) * (H - T - B);

    let html = "";
    const mantissa = niceMax / Math.pow(10, Math.floor(Math.log10(niceMax)));
    const divisions = mantissa === 2.5 || mantissa === 5 ? 5 : 4;   // so every gridline is a round number
    for (let k = 0; k <= divisions; k++) {
      const v = (niceMax / divisions) * k;
      html += `<line x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}" stroke="#232936" stroke-width="1" vector-effect="non-scaling-stroke"/>`;
      html += `<text x="${L - 6}" y="${y(v) + 4}" text-anchor="end">${fmt(v, v < 10 ? 1 : 0)}</text>`;
    }
    const ticks = Math.min(6, n);
    for (let k = 0; k < ticks; k++) {
      const i = Math.round((k / Math.max(1, ticks - 1)) * (n - 1));
      if (!points[i]) continue;
      html += `<text x="${x(i)}" y="${H - 6}" text-anchor="${k === 0 ? "start" : k === ticks - 1 ? "end" : "middle"}">${points[i].toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</text>`;
    }
    for (const s of visible.slice().reverse()) {
      // A bucket no sample fell in is drawn as a gap, never as a quiet zero.
      const d = s.values.map((v, i) => traffic.covered[i] ? `${i && traffic.covered[i - 1] ? "L" : "M"}${x(i).toFixed(1)},${y(v).toFixed(1)}` : "").join("");
      html += `<path d="${d}" fill="none" stroke="${s.color}" stroke-width="${s.dashed ? 2 : 1.6}" ${s.dashed ? 'stroke-dasharray="5 4"' : ""} vector-effect="non-scaling-stroke" stroke-linejoin="round"/>`;
    }
    html += `<line id="cursor" x1="0" x2="0" y1="${T}" y2="${H - B}" stroke="#8b94a7" stroke-width="1" vector-effect="non-scaling-stroke" visibility="hidden"/>`;
    html += `<rect x="${L}" y="${T}" width="${W - L - R}" height="${H - T - B}" fill="transparent" id="hover"/>`;
    svg.innerHTML = html;

    const unit = metric === "inflight" ? "jobs in flight (busiest sample)" : metric === "failed" ? "failed requests per minute" : "requests per minute";
    const empty = traffic.fleet.completed.every(v => v === 0) && traffic.fleet.failed.every(v => v === 0) && traffic.fleet.inFlight.every(v => v === 0);
    $("chart-note").textContent = `${unit} · a point every ${traffic.stepSeconds} s${empty ? " · no traffic in this window yet" : ""}`;

    $("legend").innerHTML = series.map(s =>
      `<span data-key="${text(s.key)}" class="${hidden.has(s.key) ? "off" : ""}"><i class="swatch" style="background:${s.color}"></i>${text(s.label)}</span>`).join("")
      + (traffic.series.length > MAX_SERIES ? `<span class="meta">+${traffic.series.length - MAX_SERIES} more not drawn</span>` : "");

    const hover = $("hover");
    const readout = $("readout");
    const cursor = $("cursor");
    hover.addEventListener("mousemove", event => {
      const box = svg.getBoundingClientRect();
      const px = ((event.clientX - box.left) / box.width) * W;
      const i = Math.max(0, Math.min(n - 1, Math.round(((px - L) / (W - L - R)) * (n - 1))));
      cursor.setAttribute("x1", x(i));
      cursor.setAttribute("x2", x(i));
      cursor.setAttribute("visibility", "visible");
      readout.innerHTML = `<div class="meta">${points[i].toLocaleTimeString()}</div>`
        + (traffic.covered[i]
          ? visible.map(s => `<div><i class="swatch" style="background:${s.color}"></i> ${text(s.label)}: <b>${fmt(s.values[i])}</b></div>`).join("")
          : '<div class="meta">no sample: the hub was not sampling yet</div>');
      readout.style.display = "block";
      const left = (x(i) / W) * box.width;
      readout.style.left = `${left > box.width / 2 ? left - readout.offsetWidth - 12 : left + 12}px`;
    });
    hover.addEventListener("mouseleave", () => { readout.style.display = "none"; cursor.setAttribute("visibility", "hidden"); });
  }

  function niceCeil(v) {
    const p = Math.pow(10, Math.floor(Math.log10(v)));
    const m = v / p;
    return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * p;
  }

  function spark(values, color) {
    const n = values.length;
    if (!n) return "";
    const max = Math.max(1, ...values);
    const d = values.map((v, i) => `${i ? "L" : "M"}${((i / Math.max(1, n - 1)) * 100).toFixed(2)},${(32 - (v / max) * 30).toFixed(2)}`).join("");
    return `<svg class="spark" viewBox="0 0 100 34" preserveAspectRatio="none"><path d="${d} L100,34 L0,34 Z" fill="${color}" opacity="0.12"/><path d="${d}" fill="none" stroke="${color}" stroke-width="1.5" vector-effect="non-scaling-stroke"/></svg>`;
  }

  function meter(used, cap) {
    if (!cap) return `<div class="meta">${fmt(used, 0)} in flight · no cap declared</div>`;
    const ratio = Math.min(1, used / cap);
    return `<div class="meter${ratio >= 1 ? " full" : ratio >= 0.75 ? " hot" : ""}"><div style="width:${(ratio * 100).toFixed(0)}%"></div></div>`
      + `<div class="meta">${fmt(used, 0)} of ${fmt(cap, 0)} in flight</div>`;
  }

  function renderGroups() {
    const byGroup = groupBy === "group";
    $("groups-title").textContent = byGroup ? "Groups" : `By ${groupBy.replace("label:", "label ")}`;
    $("groups-note").textContent = byGroup
      ? (report.groupsPersistence === "file" ? "kept on disk (Fleet:Groups:Persistence=file)" : "held in memory until this coordinator restarts — set Fleet:Groups:Persistence=file to keep them")
      : "computed from what each node reports";
    $("new-group").style.display = byGroup ? "" : "none";

    if (!report.groups.length) {
      $("groups").innerHTML = `<div class="empty">${byGroup ? "No group yet, and no node is connected. Make one with <b>New group</b>." : "No node is connected."}</div>`;
      return;
    }

    $("groups").innerHTML = report.groups.map(g => {
      const series = traffic.series.find(s => s.key === g.key);
      const values = series ? series.completed.map((v, i) => v + series.failed[i]).filter((_, i) => traffic.covered[i]) : [];
      const tone = g.attention ? '<span class="pill warn">' + g.attention + " to look at</span>" : '<span class="pill ok">ok</span>';
      const absent = g.absent.length ? `<div class="att warn">absent: ${g.absent.map(text).join(", ")}</div>` : "";
      const actions = g.defined
        ? `<div class="actions">
            <button class="small" data-act="cordon" data-group="${text(g.key)}" ${busy.has("g:" + g.key) ? "disabled" : ""}>Cordon all</button>
            <button class="small" data-act="uncordon" data-group="${text(g.key)}" ${busy.has("g:" + g.key) ? "disabled" : ""}>Uncordon all</button>
            <button class="small" data-act="edit" data-group="${text(g.key)}">Edit</button>
            <button class="small danger" data-act="delete" data-group="${text(g.key)}">Delete</button>
          </div>`
        : "";
      return `<div class="group${selectedGroup === g.key ? " selected" : ""}">
        <div class="head"><span class="swatch" style="background:${colorOf(g.key)}"></span>
          <span class="name" data-act="select" data-group="${text(g.key)}" title="Show only these nodes">${text(g.key)}</span>
          ${g.defined ? '<span class="pill accent">defined</span>' : ""}<span style="flex:1"></span>${tone}</div>
        ${g.description ? `<div class="meta">${text(g.description)}</div>` : ""}
        <div class="stats">
          <div><b>${g.routable}<span class="meta">/${g.nodes.length}</span></b><span>taking work</span></div>
          <div><b>${fmt(g.perMinute)}</b><span>req / min</span></div>
          <div><b>${pct(g.failureRate)}</b><span>failed</span></div>
          <div><b>${g.cordoned}</b><span>cordoned</span></div>
        </div>
        ${meter(g.inFlight, g.capacity)}
        ${spark(values, colorOf(g.key))}
        ${absent}
        ${actions}
      </div>`;
    }).join("");
  }

  function stateTone(state) {
    return { idle: "ok", busy: "accent", full: "warn", paused: "neutral", cordoned: "neutral", throttled: "warn", unhealthy: "err", silent: "err" }[state] || "neutral";
  }

  function visibleNodes() {
    const filter = $("filter").value.trim().toLowerCase();
    const onlyAttention = $("only-attention").checked;
    return report.nodes.filter(n => {
      if (selectedGroup && !n.keys.includes(selectedGroup)) return false;
      if (onlyAttention && !n.attention.some(a => a.level === "warn")) return false;
      if (!filter) return true;
      const hay = [n.name, n.nodeId, n.version, n.organisation, n.state, ...n.groups, ...Object.entries(n.labels).map(([k, v]) => `${k}=${v}`)].join(" ").toLowerCase();
      return hay.includes(filter);
    });
  }

  function sortValue(n, key) {
    switch (key) {
      case "state": return n.state;
      case "groups": return n.groups.join(",");
      case "inFlight": return n.inFlight;
      case "rate": return n.recent.perMinute;
      case "failure": return n.recent.failureRate ?? -1;
      case "total": return n.completedTotal + n.failedTotal;
      case "version": return n.version;
      default: return n.name.toLowerCase();
    }
  }

  function renderNodes() {
    const nodes = visibleNodes().sort((a, b) => {
      const va = sortValue(a, sort.key), vb = sortValue(b, sort.key);
      return (va < vb ? -1 : va > vb ? 1 : 0) * sort.dir;
    });
    $("nodes-note").innerHTML = selectedGroup
      ? `in <b>${text(selectedGroup)}</b> · <a href="#" id="clear-group" style="color:var(--accent)">show all</a>`
      : `${report.nodes.length} connected`;
    document.querySelectorAll("th[data-sort]").forEach(th => {
      th.textContent = th.textContent.replace(/ [▲▼]$/, "") + (th.dataset.sort === sort.key ? (sort.dir > 0 ? " ▲" : " ▼") : "");
    });

    if (!nodes.length) {
      $("nodes").innerHTML = `<tr><td colspan="11" class="empty">${report.nodes.length ? "No node matches." : "No node is connected to this coordinator."}</td></tr>`;
      updateSelection();
      return;
    }

    $("nodes").innerHTML = nodes.map(n => {
      const labels = Object.entries(n.labels).map(([k, v]) => `<span class="pill neutral">${text(k)}=${text(v)}</span>`).join("");
      const groups = n.groups.map(g => `<span class="pill accent">${text(g)}</span>`).join("");
      const attention = n.attention.map(a => `<div class="att ${a.level}">${a.level === "warn" ? "⚠ " : ""}${text(a.text)}</div>`).join("");
      const cap = n.maxConcurrency ? ` / ${n.maxConcurrency}` : "";
      const act = n.cordoned ? "uncordon" : "cordon";
      const extras = [n.backend && n.backend !== "healthy" ? `backend ${n.backend}` : null, n.sealed ? "sealed" : null, n.sharing ? `sharing ${n.sharing}` : null, n.profile ? `profile ${n.profile}` : null]
        .filter(Boolean).map(s => `<span class="pill neutral">${text(s)}</span>`).join("");
      return `<tr>
        <td><input type="checkbox" data-node="${text(n.nodeId)}" ${selectedNodes.has(n.nodeId) ? "checked" : ""}></td>
        <td><b>${text(n.name)}</b><div><code>${text(n.nodeId)}</code></div>${n.organisation ? `<div class="meta">org ${text(n.organisation)}</div>` : ""}</td>
        <td><span class="pill ${stateTone(n.state)}">${text(n.state)}</span><div class="meta">seen ${fmt(n.ageSeconds, 0)} s ago</div>${extras}</td>
        <td>${groups || '<span class="meta">no group</span>'}<div>${labels}</div></td>
        <td class="num">${n.inFlight}${cap}<div class="meta">${n.models} model${n.models === 1 ? "" : "s"}</div></td>
        <td class="num">${fmt(n.recent.perMinute)}</td>
        <td class="num">${pct(n.recent.failureRate)}<div class="meta">${n.recent.failed} of ${n.recent.completed + n.recent.failed}</div></td>
        <td class="num">${fmt(n.completedTotal, 0)}<div class="meta">${fmt(n.failedTotal, 0)} failed</div></td>
        <td><code title="${text(n.version)}">${text(String(n.version).split("+")[0])}</code></td>
        <td>${attention || '<span class="meta">—</span>'}</td>
        <td><button class="small" data-act="${act}" data-node="${text(n.nodeId)}" ${busy.has("n:" + n.nodeId) ? "disabled" : ""}>${act === "cordon" ? "Cordon" : "Uncordon"}</button></td>
      </tr>`;
    }).join("");
    updateSelection();
  }

  function updateSelection() {
    for (const id of [...selectedNodes]) if (!report.nodes.some(n => n.nodeId === id)) selectedNodes.delete(id);
    $("group-selected").disabled = selectedNodes.size === 0;
    $("group-selected").textContent = selectedNodes.size ? `Group ${selectedNodes.size} selected…` : "Group selected…";
  }

  // ---- the group editor ----------------------------------------------------------------------------------------

  function openEditor(name, preselect) {
    const existing = name ? definitions.find(g => g.name.toLowerCase() === name.toLowerCase()) : null;
    editing = existing ? existing.name : "";
    $("ed-name").value = existing ? existing.name : "";
    $("ed-name").disabled = !!existing;
    $("ed-desc").value = existing?.description || "";
    $("ed-labels").value = Object.entries(existing?.labels || {}).map(([k, v]) => `${k}=${v}`).join("\n");
    const ids = new Set((existing?.nodes || []).map(id => id.toLowerCase()));
    (preselect || []).forEach(id => ids.add(id.toLowerCase()));
    const live = report.nodes.map(n => n.nodeId.toLowerCase());
    $("ed-extra").value = [...ids].filter(id => !live.includes(id)).join("\n");
    $("ed-members").innerHTML = report.nodes.length
      ? report.nodes.map(n => `<label><input type="checkbox" value="${text(n.nodeId)}" ${ids.has(n.nodeId.toLowerCase()) ? "checked" : ""}> ${text(n.name)} <code>${text(n.nodeId)}</code></label>`).join("")
      : '<div class="meta">No node is connected; name ids below or use labels.</div>';
    $("ed-error").textContent = "";
    $("editor").classList.add("open");
    (existing ? $("ed-desc") : $("ed-name")).focus();
  }

  async function saveEditor() {
    const name = $("ed-name").value.trim();
    const labels = {};
    for (const line of $("ed-labels").value.split("\n").map(l => l.trim()).filter(Boolean)) {
      const eq = line.indexOf("=");
      if (eq <= 0) { $("ed-error").textContent = `"${line}" is not key=value`; return; }
      labels[line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
    }
    const nodes = [...document.querySelectorAll("#ed-members input:checked")].map(i => i.value)
      .concat($("ed-extra").value.split("\n").map(l => l.trim()).filter(Boolean));
    try {
      await call("PUT", `/api/admin/groups/${encodeURIComponent(name)}`, { description: $("ed-desc").value.trim() || null, nodes, labels });
      $("editor").classList.remove("open");
      selectedNodes.clear();
      toast(`Group ${name} saved.`);
      if (groupBy !== "group") { groupBy = "group"; }
      await refresh();
    } catch (e) {
      $("ed-error").textContent = e.message;
    }
  }

  // ---- actions -------------------------------------------------------------------------------------------------

  async function groupAction(act, name) {
    if (act === "edit") return openEditor(name);
    if (act === "select") { selectedGroup = selectedGroup === name ? null : name; renderGroups(); renderNodes(); return; }
    if (act === "delete") {
      if (!confirm(`Delete the group ${name}? Its nodes are not touched.`)) return;
      try { await call("DELETE", `/api/admin/groups/${encodeURIComponent(name)}`); toast(`Group ${name} deleted.`); if (selectedGroup === name) selectedGroup = null; }
      catch (e) { toast(e.message, true); }
      return refresh();
    }
    if (act === "cordon" && !confirm(`Cordon every connected node in ${name}? They finish what they run and take no new work.`)) return;
    busy.add("g:" + name);
    renderGroups();
    try {
      const result = await call("POST", `/api/admin/groups/${encodeURIComponent(name)}/${act}`);
      toast(`${act === "cordon" ? "Cordoned" : "Uncordoned"} ${result.nodes.length} node(s) in ${name}${result.absent.length ? ` · ${result.absent.length} absent` : ""}.`);
    } catch (e) {
      toast(e.message, true);
    } finally {
      busy.delete("g:" + name);
      await refresh();
    }
  }

  async function nodeAction(act, nodeId) {
    busy.add("n:" + nodeId);
    renderNodes();
    try {
      await call("POST", `/api/admin/nodes/${encodeURIComponent(nodeId)}/${act}`);
    } catch (e) {
      toast(e.message, true);
    } finally {
      busy.delete("n:" + nodeId);
      await refresh();
    }
  }

  // ---- wiring --------------------------------------------------------------------------------------------------

  $("connect").addEventListener("click", async () => {
    adminKey = $("key").value.trim() || null;
    await refresh();
    schedule();
  });
  $("key").addEventListener("keydown", e => { if (e.key === "Enter") $("connect").click(); });
  $("auto").addEventListener("change", schedule);
  $("group-by").addEventListener("change", e => { groupBy = e.target.value; selectedGroup = null; hidden.clear(); refresh(); });
  $("window").addEventListener("click", e => {
    const min = e.target.dataset?.min;
    if (!min) return;
    minutes = Number(min);
    document.querySelectorAll("#window button").forEach(b => b.classList.toggle("on", b === e.target));
    if (connected) refresh();
  });
  $("metric").addEventListener("click", e => {
    const value = e.target.dataset?.metric;
    if (!value) return;
    metric = value;
    document.querySelectorAll("#metric button").forEach(b => b.classList.toggle("on", b === e.target));
    if (traffic) renderChart();
  });
  $("legend").addEventListener("click", e => {
    const key = e.target.closest("span[data-key]")?.dataset.key;
    if (!key) return;
    hidden.has(key) ? hidden.delete(key) : hidden.add(key);
    renderChart();
  });
  $("groups").addEventListener("click", e => {
    const el = e.target.closest("[data-act]");
    if (el) groupAction(el.dataset.act, el.dataset.group);
  });
  $("nodes").addEventListener("click", e => {
    const el = e.target.closest("button[data-act]");
    if (el) nodeAction(el.dataset.act, el.dataset.node);
  });
  $("nodes").addEventListener("change", e => {
    const id = e.target.dataset?.node;
    if (!id) return;
    e.target.checked ? selectedNodes.add(id) : selectedNodes.delete(id);
    updateSelection();
  });
  $("select-all").addEventListener("change", e => {
    visibleNodes().forEach(n => e.target.checked ? selectedNodes.add(n.nodeId) : selectedNodes.delete(n.nodeId));
    renderNodes();
  });
  document.querySelector("thead").addEventListener("click", e => {
    const key = e.target.closest("th[data-sort]")?.dataset.sort;
    if (!key) return;
    sort = { key, dir: sort.key === key ? -sort.dir : 1 };
    renderNodes();
  });
  $("nodes-note").addEventListener("click", e => {
    if (e.target.id === "clear-group") { e.preventDefault(); selectedGroup = null; renderGroups(); renderNodes(); }
  });
  $("filter").addEventListener("input", () => report && renderNodes());
  $("only-attention").addEventListener("change", () => report && renderNodes());
  $("new-group").addEventListener("click", () => report ? openEditor(null) : toast("Connect first.", true));
  $("group-selected").addEventListener("click", () => openEditor(null, [...selectedNodes]));
  $("ed-cancel").addEventListener("click", () => $("editor").classList.remove("open"));
  $("ed-save").addEventListener("click", saveEditor);

  // An unguarded loopback hub answers without a key, so try once on load.
  refresh(true).then(schedule);
})();
