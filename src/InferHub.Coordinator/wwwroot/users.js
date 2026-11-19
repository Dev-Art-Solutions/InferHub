// Users page for InferHub (phase 154): people with a role and their own keys, and how to connect an MCP client.
//
// Reads /api/admin/users and /api/admin/roles; acts through POST/PATCH/DELETE on /api/admin/users[/{id}[/keys[/{keyId}]|/sessions]].
// The admin key lives in this closure for the lifetime of the tab — never localStorage / sessionStorage (the console's rule).
// A new key is shown once, in the page, and dropped from memory when another replaces it.
(() => {
  let adminKey = null;
  let roles = [];

  const $ = id => document.getElementById(id);
  const text = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  async function call(method, path, body) {
    const headers = { "Content-Type": "application/json" };
    if (adminKey) headers["Authorization"] = `Bearer ${adminKey}`;
    const response = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : undefined });
    if (response.status === 204) return {};
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || `${response.status} ${response.statusText}`);
    return data;
  }

  function fail(error) { $("error").textContent = error ? error.message || String(error) : ""; }

  function showSecret(title, key) {
    $("secret-title").textContent = title;
    $("secret-value").textContent = key;
    $("secret").style.display = "block";
  }

  async function load() {
    try {
      const [roleData, userData] = await Promise.all([call("GET", "/api/admin/roles"), call("GET", "/api/admin/users")]);
      roles = roleData.roles;
      renderRoles();
      renderUsers(userData);
      fail(null);
    } catch (error) {
      fail(error);
    }
  }

  function renderRoles() {
    $("new-role").innerHTML = roles.map(r => `<option ${r.name === "user" ? "selected" : ""}>${text(r.name)}</option>`).join("");
    $("roles").innerHTML = roles.map(r =>
      `<tr><td>${text(r.name)} ${r.builtIn ? '<span class="pill">built-in</span>' : ""}</td><td>${r.permissions.map(text).join(", ")}</td></tr>`).join("");
  }

  function renderUsers(data) {
    $("count").textContent = `${data.users.length} · kept: ${data.persistence}`;
    if (data.users.length === 0) {
      $("rows").innerHTML = '<tr><td colspan="7" class="meta">No users yet.</td></tr>';
      return;
    }

    $("rows").innerHTML = data.users.map(u => `
      <tr class="${u.disabled ? "off" : ""}" data-id="${text(u.id)}">
        <td><b>${text(u.id)}</b>${u.disabled ? ' <span class="pill">disabled</span>' : ""}</td>
        <td>${text(u.displayName || "")}</td>
        <td><select data-act="role">${roles.map(r => `<option ${r.name === u.role ? "selected" : ""}>${text(r.name)}</option>`).join("")}</select></td>
        <td>${text(u.organisation || "home")}</td>
        <td><div class="keys">${u.keys.map(k => `<span>${text(k.hint)} ${text(k.name || "")} <button class="small danger" data-act="del-key" data-key="${text(k.id)}">remove</button></span>`).join("") || '<span class="meta">none</span>'}</div></td>
        <td>${u.sessions} ${u.sessions ? '<button class="small" data-act="sessions">sign out</button>' : ""}</td>
        <td><div class="actions">
          <button class="small" data-act="key">new key</button>
          <button class="small" data-act="toggle">${u.disabled ? "enable" : "disable"}</button>
          <button class="small danger" data-act="delete">delete</button>
        </div></td>
      </tr>`).join("");
  }

  async function act(event) {
    const target = event.target.closest("[data-act]");
    const row = event.target.closest("tr[data-id]");
    if (!target || !row) return;
    const id = encodeURIComponent(row.dataset.id);
    const action = target.dataset.act;
    if (event.type === "click" && action === "role") return;
    if (event.type === "change" && action !== "role") return;

    try {
      if (action === "role") await call("PATCH", `/api/admin/users/${id}`, { role: target.value });
      if (action === "toggle") await call("PATCH", `/api/admin/users/${id}`, { disabled: target.textContent === "disable" });
      if (action === "sessions") await call("DELETE", `/api/admin/users/${id}/sessions`);
      if (action === "del-key" && confirm("Remove this key? Anything using it stops working, and MCP sign-ins made with it end.")) {
        await call("DELETE", `/api/admin/users/${id}/keys/${encodeURIComponent(target.dataset.key)}`);
      }
      if (action === "delete" && confirm(`Delete user ${row.dataset.id}? Their keys and sign-ins stop working.`)) {
        await call("DELETE", `/api/admin/users/${id}`);
      }
      if (action === "key") {
        const name = prompt("A name for the key (optional), e.g. laptop or claude.ai") ?? null;
        if (name === null) return;
        const made = await call("POST", `/api/admin/users/${id}/keys`, { name: name || null });
        showSecret(`New key for ${row.dataset.id}`, made.key);
      }
      await load();
    } catch (error) {
      fail(error);
    }
  }

  async function create() {
    try {
      const made = await call("POST", "/api/admin/users", {
        id: $("new-id").value.trim(),
        displayName: $("new-name").value.trim() || null,
        role: $("new-role").value,
        organisation: $("new-org").value.trim() || null
      });
      showSecret(`First key for ${made.user.id}`, made.key);
      $("new-id").value = ""; $("new-name").value = ""; $("new-org").value = "";
      await load();
    } catch (error) {
      fail(error);
    }
  }

  function snippets() {
    const url = `${location.origin}/mcp`;
    $("snip-claude-code").textContent =
      `claude mcp add --transport http inferhub ${url} \\\n  --header "Authorization: Bearer ihu_…"`;
    $("snip-connector").textContent =
      `Add a custom connector with the URL\n${url}\n\nNo client id or secret: the app registers itself, then\nopens this hub's sign-in page, where the person pastes\ntheir own ihu_… key. Needs Users:Enabled, a public https\naddress, and Mcp:PublicUrl set to it.`;
    $("snip-claude-api").textContent = JSON.stringify({
      model: "claude-opus-5-5",
      max_tokens: 4096,
      mcp_servers: [{ type: "url", url, name: "inferhub", authorization_token: "ihu_…" }],
      tools: [{ type: "mcp_toolset", mcp_server_name: "inferhub" }],
      messages: [{ role: "user", content: "Which models can I use on InferHub?" }]
    }, null, 2) + "\n\n// header  anthropic-beta: mcp-client-2025-11-20";
    $("snip-openai").textContent = JSON.stringify({
      tools: [{ type: "mcp", server_label: "inferhub", server_url: url, authorization: "ihu_…", require_approval: "never" }],
      input: "Which models can I use on InferHub?"
    }, null, 2) + "\n\n// POST /v1/responses with your model of choice";
  }

  $("connect").addEventListener("click", () => { adminKey = $("key").value.trim() || null; load(); });
  $("key").addEventListener("keydown", e => { if (e.key === "Enter") $("connect").click(); });
  $("create").addEventListener("click", create);
  $("copy").addEventListener("click", () => navigator.clipboard?.writeText($("secret-value").textContent));
  $("rows").addEventListener("click", act);
  $("rows").addEventListener("change", act);
  snippets();
  load(); // a loopback hub answers without a key
})();
