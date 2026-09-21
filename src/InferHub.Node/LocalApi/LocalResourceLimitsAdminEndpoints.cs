using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Configuration;
using InferHub.Node.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The local configuring UI phase 82 asked for (D7): a page served by the node itself, over
/// <c>LocalApi</c>, for editing this box's own <c>Node:ResourceLimits</c> without hand-editing JSON
/// or restarting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Under <c>/api/</c> on purpose, and mapped unconditionally within <c>LocalApi</c>.</b>
/// <see cref="LocalApiAuthMiddleware"/> guards every path under that prefix already —
/// loopback-only by default, a bearer token otherwise — so this page inherits the exact protection
/// every other route on this host has, rather than needing a second auth story invented for it (the
/// trap phase-21 D2 and phase-37 D4 both exist because of). It is mapped whether or not a cap is
/// configured, the same "the route exists, a 503 or an honest empty state answers it" posture the
/// tool and image routes use. <b>The trade-off worth stating:</b> <c>LocalApi</c> itself is off by
/// default (phase 37) and this page rides on it, so a purely meshed node reaches it only after
/// turning <c>LocalApi:Enabled</c> on — loopback-only by default, so doing so for this alone is safe.
/// A meshed-only deployment that never wants a local API edits <c>node.local.json</c> by hand
/// instead; the file format is identical either way.
/// </para>
/// <para>
/// <b>Writes go to <c>node.local.json</c>, never to <c>appsettings.json</c>.</b> The shipped file is
/// hand-authored prose with a "//Section" comment beside every key (see the top of this project's
/// own <c>appsettings.json</c>); rewriting it programmatically would either destroy those comments or
/// require parsing past them, and either way an operator's next manual edit and this page's last
/// write would fight over the same file. A second, optional, reload-on-change JSON file — merged in
/// after it, so it wins — is the same shape phase-67 D3 already uses for one section overriding
/// another it also binds.
/// </para>
/// <para>
/// <b>Only the soft cap is live.</b> <see cref="NodeOptions.ResourceLimits"/> is read fresh off an
/// <see cref="IOptionsMonitor{T}"/> on every poll (see <c>ResourceMonitor</c>), so a change here
/// reaches the governor within one <c>PollInterval</c> with no restart. <c>HardCpuCapPercent</c> is
/// deliberately left off this form: <c>HardCpuCap.Configure</c> runs once and is not idempotent-safe
/// to re-run with a different rate on a job object already handed out to running child processes, so
/// changing it is an <c>appsettings.json</c> edit and a restart, stated as such in the page.
/// </para>
/// </remarks>
internal static class LocalResourceLimitsAdminEndpoints
{
    private const string OverrideFileName = "node.local.json";

    public static IEndpointRouteBuilder MapLocalResourceLimitsAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/resource-limits", () => Results.Content(Page, "text/html; charset=utf-8"));
        app.MapGet("/api/admin/resource-limits/state", HandleStateAsync);
        app.MapPost("/api/admin/resource-limits", HandleSaveAsync);

        return app;
    }

    private static IResult HandleStateAsync(
        IOptionsMonitor<NodeOptions> nodeOptions,
        IResourceGovernor governor)
    {
        var limits = nodeOptions.CurrentValue.ResourceLimits;

        return Results.Json(
            new
            {
                config = new
                {
                    maxCpuPercent = limits.MaxCpuPercent,
                    maxGpuPercent = limits.MaxGpuPercent,
                    pollIntervalSeconds = limits.PollInterval.TotalSeconds,
                    sustainedPolls = limits.SustainedPolls,
                    recoverPolls = limits.RecoverPolls,
                    hardCpuCapPercent = limits.HardCpuCapPercent
                },
                live = new
                {
                    cpuPercent = governor.Current.CpuPercent,
                    gpuPercent = governor.Current.GpuPercent,
                    throttled = governor.IsThrottled,
                    reason = governor.Reason
                }
            },
            LocalApiEndpoints.JsonOptions);
    }

    private static async Task<IResult> HandleSaveAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ResourceLimitsRequest request;

        try
        {
            var rawJson = await LocalApiEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);
            request = LocalApiEndpoints.Deserialize<ResourceLimitsRequest>(rawJson);
        }
        catch (BadHttpRequestException ex)
        {
            return Results.Json(new { error = ex.Message }, LocalApiEndpoints.JsonOptions, statusCode: StatusCodes.Status400BadRequest);
        }

        foreach (var (name, value) in new[] { ("maxCpuPercent", request.MaxCpuPercent), ("maxGpuPercent", request.MaxGpuPercent) })
        {
            if (value is { } percent && (percent < 1 || percent > 100))
            {
                return Results.Json(
                    new { error = $"'{name}' must be between 1 and 100, or null to turn the cap off (got {percent})." },
                    LocalApiEndpoints.JsonOptions,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, OverrideFileName);
        var root = File.Exists(path)
            ? (JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? new JsonObject())
            : new JsonObject();

        var node = (root["Node"] as JsonObject) ?? new JsonObject();
        var limits = new JsonObject
        {
            ["MaxCpuPercent"] = request.MaxCpuPercent,
            ["MaxGpuPercent"] = request.MaxGpuPercent
        };

        if (request.SustainedPolls is { } sustained)
        {
            limits["SustainedPolls"] = sustained;
        }

        if (request.RecoverPolls is { } recover)
        {
            limits["RecoverPolls"] = recover;
        }

        node["ResourceLimits"] = limits;
        root["Node"] = node;

        await File.WriteAllTextAsync(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        return Results.Json(
            new
            {
                saved = true,
                path,
                note = "Applied within one Node:ResourceLimits:PollInterval — no restart needed. HardCpuCapPercent is not editable here; set it in appsettings.json and restart."
            },
            LocalApiEndpoints.JsonOptions);
    }

    private sealed record ResourceLimitsRequest(
        int? MaxCpuPercent,
        int? MaxGpuPercent,
        int? SustainedPolls,
        int? RecoverPolls);

    private const string Page = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>InferHub node — resource limits</title>
        <style>
          body { font-family: system-ui, sans-serif; max-width: 640px; margin: 2rem auto; color: #1a1a1a; }
          label { display: block; margin-top: 1rem; font-weight: 600; }
          input { width: 100%; padding: 0.4rem; margin-top: 0.25rem; box-sizing: border-box; }
          button { margin-top: 1.5rem; padding: 0.5rem 1.25rem; }
          #live, #note { margin-top: 1.5rem; padding: 0.75rem; background: #f4f4f4; border-radius: 4px; font-size: 0.9rem; }
          .hint { color: #666; font-size: 0.85rem; font-weight: 400; }
        </style>
        </head>
        <body>
        <h1>This node's own resource cap</h1>
        <p>An optional ceiling on how much of this machine's CPU/GPU this node lets itself be routed to
        consume. It overrides everything else — no coordinator profile can raise or clear it. Leave a
        field blank to turn that cap off.</p>
        <form id="f">
          <label>Max CPU % <span class="hint">(1-100, blank = off)</span>
            <input type="number" id="cpu" min="1" max="100"></label>
          <label>Max GPU % <span class="hint">(1-100, blank = off; NVIDIA/Linux only today)</span>
            <input type="number" id="gpu" min="1" max="100"></label>
          <label>Sustained over-cap polls before this node stops taking new work <span class="hint">(default 3)</span>
            <input type="number" id="sustained" min="1"></label>
          <label>Clean polls before this node is routable again <span class="hint">(default 2)</span>
            <input type="number" id="recover" min="1"></label>
          <button type="submit">Save</button>
        </form>
        <div id="live">loading current readings…</div>
        <div id="note" style="display:none"></div>
        <script>
          const q = (id) => document.getElementById(id);
          async function refresh() {
            const r = await fetch('/api/admin/resource-limits/state');
            const s = await r.json();
            q('cpu').value = s.config.maxCpuPercent ?? '';
            q('gpu').value = s.config.maxGpuPercent ?? '';
            q('sustained').value = s.config.sustainedPolls;
            q('recover').value = s.config.recoverPolls;
            q('live').textContent =
              'Live: CPU ' + (s.live.cpuPercent?.toFixed(0) ?? '—') + '% · GPU ' + (s.live.gpuPercent?.toFixed(0) ?? '—') +
              '% · ' + (s.live.throttled ? ('THROTTLED — ' + s.live.reason) : 'routable') +
              (s.config.hardCpuCapPercent ? ' · hard CPU cap ' + s.config.hardCpuCapPercent + '% (appsettings.json only, restart to change)' : '');
          }
          q('f').addEventListener('submit', async (e) => {
            e.preventDefault();
            const body = {
              maxCpuPercent: q('cpu').value === '' ? null : Number(q('cpu').value),
              maxGpuPercent: q('gpu').value === '' ? null : Number(q('gpu').value),
              sustainedPolls: q('sustained').value === '' ? null : Number(q('sustained').value),
              recoverPolls: q('recover').value === '' ? null : Number(q('recover').value)
            };
            const r = await fetch('/api/admin/resource-limits', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            const j = await r.json();
            const note = q('note');
            note.style.display = 'block';
            note.textContent = r.ok ? j.note : ('Error: ' + j.error);
            refresh();
          });
          refresh();
          setInterval(refresh, 5000);
        </script>
        </body>
        </html>
        """;
}
