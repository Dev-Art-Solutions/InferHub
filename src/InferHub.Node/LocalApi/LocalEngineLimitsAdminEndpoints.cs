using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Configuration;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The box's own runtime limits, on the box (phase 102): a page beside phase 82's, under the same
/// loopback-guarded <c>/api/</c>, that shows every runtime's ceiling, what the coordinator asked for,
/// what is in force and what it uses — and edits the ceiling. Writes go to <c>node.local.json</c>
/// (82 D7), and the limiter reads them on its next poll: no restart.
/// </summary>
/// <remarks>
/// The coordinator's column is read-only here for the same reason the node's is read-only at the hub:
/// each side writes its own half, and the smaller wins.
/// </remarks>
internal static class LocalEngineLimitsAdminEndpoints
{

    public static IEndpointRouteBuilder MapLocalEngineLimitsAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/engine-limits", () => Results.Content(Page, "text/html; charset=utf-8"));
        app.MapGet("/api/admin/engine-limits/state", (IEngineLimiter limiter, INodeIdentity identity) =>
            Results.Json(limiter.State(identity.GetOrCreateNodeId()), LocalApiEndpoints.JsonOptions));
        app.MapPost("/api/admin/engine-limits", HandleSaveAsync);

        return app;
    }

    private static async Task<IResult> HandleSaveAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        Dictionary<string, EngineLimitRequest?> request;

        try
        {
            var rawJson = await LocalApiEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);
            request = LocalApiEndpoints.Deserialize<Dictionary<string, EngineLimitRequest?>>(rawJson);
        }
        catch (BadHttpRequestException ex)
        {
            return Error(ex.Message);
        }

        var engines = new JsonObject();

        foreach (var (key, limits) in request)
        {
            if (EngineLimitGroups.Normalize(key) is not { } group)
            {
                return Error($"'{key}' names no runtime; use one of {string.Join(", ", EngineLimitGroups.All)}.");
            }

            // Every field written, null included: node.local.json is layered over appsettings.json, and
            // a key left out there would let the appsettings value show through a limit the operator cleared.
            var options = new EngineLimitOptions
            {
                CpuPercent = limits?.CpuPercent,
                Cores = limits?.Cores,
                MemoryMiB = limits?.MemoryMiB,
                VramMiB = limits?.VramMiB,
                Gpus = string.IsNullOrWhiteSpace(limits?.Gpus) ? null : limits.Gpus.Trim()
            };

            // The validator's own checks, before the file: a reload that fails validation would throw out
            // of every IOptionsMonitor read on the box, which is worse than a 400 here.
            if (options.CpuPercent is < 1 or > 100)
            {
                return Error($"{group}: cpuPercent must be 1–100, or empty for no limit (got {options.CpuPercent}).");
            }

            foreach (var (field, value) in new[] { ("cores", options.Cores), ("memoryMiB", options.MemoryMiB), ("vramMiB", options.VramMiB) })
            {
                if (value is < 1)
                {
                    return Error($"{group}: {field} must be at least 1, or empty for no limit (got {value}).");
                }
            }

            if (!EngineLimitOptions.TryParseGpus(options.Gpus, out _))
            {
                return Error($"{group}: gpus must be indexes separated by commas (\"0,1\"), \"none\", or empty for no limit (got '{options.Gpus}').");
            }

            engines[group] = new JsonObject
            {
                ["CpuPercent"] = options.CpuPercent,
                ["Cores"] = options.Cores,
                ["MemoryMiB"] = options.MemoryMiB,
                ["VramMiB"] = options.VramMiB,
                ["Gpus"] = options.Gpus
            };
        }

        var path = httpContext.RequestServices.GetRequiredService<LocalSettingsFile>().Path;
        var root = File.Exists(path)
            ? (JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? new JsonObject())
            : new JsonObject();

        var node = root["Node"] as JsonObject ?? new JsonObject();
        var resourceLimits = node["ResourceLimits"] as JsonObject ?? new JsonObject();
        var existing = resourceLimits["Engines"] as JsonObject ?? new JsonObject();

        foreach (var (group, value) in engines)
        {
            existing[group] = value?.DeepClone();
        }

        resourceLimits["Engines"] = existing;
        node["ResourceLimits"] = resourceLimits;
        root["Node"] = node;

        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        return Results.Json(
            new
            {
                saved = true,
                path,
                note = "In force within one Node:ResourceLimits:PollInterval, no restart. A coordinator can only go lower than these."
            },
            LocalApiEndpoints.JsonOptions);
    }

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: StatusCodes.Status400BadRequest);

    private sealed record EngineLimitRequest(int? CpuPercent, int? Cores, int? MemoryMiB, int? VramMiB, string? Gpus);

    private const string Page = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>InferHub node — runtime limits</title>
        <style>
          body { font-family: system-ui, sans-serif; max-width: 980px; margin: 2rem auto; padding: 0 1rem; color: #1a1a1a; }
          table { border-collapse: collapse; width: 100%; margin-top: 1rem; font-size: 0.9rem; }
          th, td { border-bottom: 1px solid #ddd; padding: 0.4rem; text-align: left; vertical-align: top; }
          input { width: 5.5rem; padding: 0.25rem; }
          .hub { color: #555; font-size: 0.8rem; }
          .how { color: #666; font-size: 0.75rem; }
          .breach { color: #b00020; font-size: 0.8rem; }
          button { margin-top: 1rem; padding: 0.5rem 1.25rem; }
          #note { margin-top: 1rem; padding: 0.75rem; background: #f4f4f4; border-radius: 4px; display: none; }
          .wrap { overflow-x: auto; }
        </style>
        </head>
        <body>
        <h1>Runtime limits on this node</h1>
        <p>Hard limits on what each runtime may use — every process of it together. These are this box's
        ceiling: a coordinator sees them, may set lower ones of its own, and can never raise or clear them.
        Empty = no limit. GPUs as <code>0,1</code> or <code>none</code>.</p>
        <div class="wrap">
        <table>
          <thead><tr><th>Runtime</th><th>CPU %</th><th>Cores</th><th>Memory MiB</th><th>VRAM MiB</th><th>GPUs</th><th>Uses now</th></tr></thead>
          <tbody id="rows"></tbody>
        </table>
        </div>
        <button id="save">Save</button>
        <div id="note"></div>
        <p><a href="/api/admin/resource-limits">The whole-box soft cap (phase 82)</a></p>
        <script>
          const groups = ['ollama', 'llamacpp', 'colibri', 'strata', 'tools'];
          const fields = [['cpuPercent', 'CPU'], ['cores', 'cores'], ['memoryMiB', 'memory'], ['vramMiB', 'VRAM'], ['gpus', 'GPUs']];
          const q = (id) => document.getElementById(id);
          const fmt = (v) => v === null || v === undefined ? '' : Array.isArray(v) ? (v.length ? v.join(',') : 'none') : String(v);
          let editing = false;

          function render(state) {
            const byGroup = Object.fromEntries((state.groups || []).map(g => [g.group, g]));
            const rows = groups.map(name => {
              const g = byGroup[name] || {};
              const cells = fields.map(([f]) => {
                const mine = fmt(g.node ? g.node[f] : null);
                const hub = g.hub && g.hub[f] !== null && g.hub[f] !== undefined ? fmt(g.hub[f]) : null;
                const eff = g.effective ? fmt(g.effective[f]) : '';
                const how = g.enforcement && g.enforcement[f] ? g.enforcement[f] : '';
                return '<td><input data-g="' + name + '" data-f="' + f + '" value="' + mine + '">' +
                  (hub !== null ? '<div class="hub">hub: ' + hub + ' → in force: ' + eff + '</div>' : '') +
                  (how ? '<div class="how">' + how + '</div>' : '') + '</td>';
              }).join('');
              const uses = (g.processes ? g.processes + ' process(es)' : 'not running') +
                (g.cpuPercent !== null && g.cpuPercent !== undefined ? ' · CPU ' + g.cpuPercent + '%' : '') +
                (g.memoryMiB !== null && g.memoryMiB !== undefined ? ' · ' + g.memoryMiB + ' MiB' : '') +
                (g.vramMiB !== null && g.vramMiB !== undefined ? ' · VRAM ' + g.vramMiB + ' MiB' : '') +
                (g.told && g.told.length ? '<div class="how">told: ' + g.told.join(' · ') + '</div>' : '') +
                (g.breaches ? '<div class="breach">stopped ' + g.breaches + '×: ' + g.lastBreach + '</div>' : '');
              return '<tr><td><b>' + name + '</b></td>' + cells + '<td>' + uses + '</td></tr>';
            });
            q('rows').innerHTML = rows.join('');
            document.querySelectorAll('input').forEach(i => i.addEventListener('input', () => editing = true));
          }

          async function refresh() {
            if (editing) return;
            const r = await fetch('/api/admin/engine-limits/state');
            render(await r.json());
          }

          q('save').addEventListener('click', async () => {
            const body = {};
            groups.forEach(name => {
              const one = {};
              fields.forEach(([f]) => {
                const v = document.querySelector('input[data-g="' + name + '"][data-f="' + f + '"]').value.trim();
                one[f] = v === '' ? null : (f === 'gpus' ? v : Number(v));
              });
              body[name] = one;
            });
            const r = await fetch('/api/admin/engine-limits', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            const j = await r.json();
            const note = q('note');
            note.style.display = 'block';
            note.textContent = r.ok ? j.note : ('Error: ' + j.error);
            if (r.ok) { editing = false; setTimeout(refresh, 1500); }
          });

          refresh();
          setInterval(refresh, 5000);
        </script>
        </body>
        </html>
        """;
}
