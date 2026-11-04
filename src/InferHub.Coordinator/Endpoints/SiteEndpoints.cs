using System.Reflection;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Endpoints;

/// <summary>One line of the site checklist (129 D1): what is checked, whether it holds, why it matters, and the key that fixes it.</summary>
public sealed record SiteCheck(string Id, bool Ok, string Severity, string Detail, string? Fix = null)
{
    public const string Required = "required";
    public const string Recommended = "recommended";
}

/// <summary>A node as the checklist sees it: sealed, holding a certificate, and what its own egress check said.</summary>
public sealed record SiteNode(string NodeId, string Name, bool Sealed, bool Certified, bool? EgressClosed, IReadOnlyList<string>? Open);

public sealed record SiteReport(bool Ready, int Required, int RequiredOk, IReadOnlyList<SiteCheck> Checks, IReadOnlyList<SiteNode> Nodes);

/// <summary>What an admin asks for to add a node to the site (129 D2).</summary>
public sealed record SiteNodeRequest(string? Name, string? CoordinatorUrl, bool? Sealed);

/// <summary>Phase 129. <c>/api/admin/site</c>: the checklist for a closed site, and a plan to add one node to it.</summary>
public static class SiteEndpoints
{
    public const string Path = "/api/admin/site";

    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (IServiceProvider services) => Results.Ok(Report(services, DateTimeOffset.UtcNow)));

        app.MapPost(Path + "/nodes", (HttpContext context, SiteNodeRequest? request, IServiceProvider services) =>
        {
            var authority = services.GetService<NodeCertificateAuthority>();

            if (authority is not { Enabled: true })
            {
                return Results.Conflict(new
                {
                    error = "a site plan gives each node its own certificate, and this coordinator issues none (Identity:Enabled=false)",
                    fix = "Identity__Enabled=true"
                });
            }

            var name = Clean(request?.Name);

            if (name is null || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '/' or '\\'))
            {
                return Results.BadRequest(new { error = "name is required: one word, no quotes or slashes, e.g. gpu-03" });
            }

            var coordinatorUrl = Clean(request?.CoordinatorUrl) ?? $"{context.Request.Scheme}://{context.Request.Host}/";

            if (!Uri.TryCreate(coordinatorUrl, UriKind.Absolute, out var coordinator) || coordinator.Scheme is not ("http" or "https"))
            {
                return Results.BadRequest(new { error = "coordinatorUrl must be the absolute http(s) address the node will dial" });
            }

            var by = context.Items.TryGetValue(AuditMiddleware.WhoItemKey, out var who) && who is string named ? named : "admin";
            var (token, expires) = authority.CreateToken(name, by + " (site plan)");
            return Results.Ok(Plan(name, coordinator.ToString(), token, expires, request?.Sealed ?? true, Version()));
        });

        return app;
    }

    /// <summary>
    /// The checklist (D1). Read-only, and every line names the key that fixes it. <c>Ready</c> is every required line;
    /// recommended lines never block it.
    /// </summary>
    internal static SiteReport Report(IServiceProvider services, DateTimeOffset now)
    {
        var identity = services.GetService<IOptions<NodeIdentityOptions>>()?.Value ?? new NodeIdentityOptions();
        var relay = services.GetService<IOptionsMonitor<RelayOptions>>()?.CurrentValue ?? new RelayOptions();
        var audit = services.GetService<IOptions<AuditTrailOptions>>()?.Value ?? new AuditTrailOptions();
        var priority = services.GetService<IOptions<PriorityOptions>>()?.Value ?? new PriorityOptions();
        var keys = services.GetService<IOptionsMonitor<ApiKeyOptions>>()?.CurrentValue ?? new ApiKeyOptions();
        var fallback = services.GetService<IOptions<FallbackOptions>>()?.Value ?? new FallbackOptions();
        var providers = services.GetService<IProviderRegistry>()?.Configured ?? [];
        var authority = services.GetService<NodeCertificateAuthority>();
        var egress = services.GetService<NodeEgressRegistry>();
        var registry = services.GetService<INodeRegistry>();

        var certified = authority is { Enabled: true }
            ? authority.Certificates().Where(c => c.State(now) == "active").Select(c => c.NodeId).ToHashSet(StringComparer.Ordinal)
            : [];
        var nodes = (registry?.Snapshot(now) ?? [])
            .Select(node =>
            {
                var state = egress?.Of(node.NodeId);
                return new SiteNode(
                    node.NodeId,
                    node.Name,
                    node.Sealed == true,
                    certified.Contains(node.NodeId),
                    state is { Probed.Count: > 0 } ? state.Closed : null,
                    state is { Open.Count: > 0 } ? state.Open : null);
            })
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var checks = new List<SiteCheck>
        {
            new("admin-key", keys.AdminApiKeys.Any(k => !string.IsNullOrWhiteSpace(k)), SiteCheck.Required,
                "the admin API has a key, so nobody on the network manages the fleet without one", "Auth__AdminApiKeys__0"),
            new("identity", identity.Enabled, SiteCheck.Required,
                "every node gets its own certificate and can be revoked on its own (3.87)", "Identity__Enabled=true"),
            new("identity-require", identity.Enabled && identity.Require, SiteCheck.Required,
                "a node without a certificate is refused, even with the shared secret", "Identity__Require=true"),
            new("relay", relay.Enabled, SiteCheck.Required,
                "sealed nodes download only through this coordinator, so it must relay for them (3.82)", "Relay__Enabled=true"),
            new("no-cloud", providers.Count == 0 && !fallback.Enabled, SiteCheck.Required,
                providers.Count == 0 && !fallback.Enabled
                    ? "no cloud provider is configured: no request leaves the site"
                    : $"cloud routing is configured ({string.Join(", ", providers.Select(p => p.Id).DefaultIfEmpty("Fallback:"))}); a request can leave the site",
                "remove Providers: and Fallback:Enabled"),
            new("nodes-sealed", nodes.Length > 0 && nodes.All(n => n.Sealed), SiteCheck.Required,
                nodes.Length == 0 ? "no node is connected yet" : Names("not sealed", nodes.Where(n => !n.Sealed)),
                "Node__Sealed=true on each node"),
            new("nodes-certified", nodes.Length > 0 && nodes.All(n => n.Certified), SiteCheck.Required,
                nodes.Length == 0 ? "no node is connected yet" : Names("without a certificate", nodes.Where(n => !n.Certified)),
                "POST /api/admin/site/nodes, then Coordinator__EnrollmentToken on the node"),
            new("nodes-egress", nodes.Length > 0 && nodes.All(n => n.EgressClosed == true), SiteCheck.Required,
                nodes.Length == 0
                    ? "no node is connected yet"
                    : Names("whose machine can reach outside, or has not checked", nodes.Where(n => n.EgressClosed != true)),
                "apply the rules from `InferHub.Node firewall` on each node (3.88)"),
            new("supply", relay.Supply.Enabled && relay.Supply.Only, SiteCheck.Recommended,
                "models come only from what an admin supplied, never fetched when a node asks (3.84)", "Relay__Supply__Enabled=true, Relay__Supply__Only=true"),
            new("audit", audit.Enabled, SiteCheck.Recommended,
                "who asked, which model, which node answered — on disk (3.85)", "Audit__Enabled=true"),
            new("priority", priority.Enabled, SiteCheck.Recommended,
                "essential work keeps running when nodes fail (3.86)", "Priority__Enabled=true and Auth__Clients__N__Priority"),
            new("loopback-auth", keys.RequireAuthForLoopback, SiteCheck.Recommended,
                "a key is required even from the coordinator's own machine", "Auth__RequireAuthForLoopback=true")
        };

        var required = checks.Where(c => c.Severity == SiteCheck.Required).ToArray();
        return new SiteReport(required.All(c => c.Ok), required.Length, required.Count(c => c.Ok), checks, nodes);
    }

    /// <summary>
    /// What one node needs (D2): the same answers in the three shapes a node is installed in — the Windows setup's
    /// silent parameters, a container, and the settings a Linux service reads — and what to do after it connects.
    /// </summary>
    internal static object Plan(string name, string coordinatorUrl, string token, DateTimeOffset expires, bool sealedNode, string version)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Coordinator:Url"] = coordinatorUrl,
            ["Coordinator:EnrollmentToken"] = token,
            ["Node:Name"] = name
        };

        if (sealedNode)
        {
            settings["Node:Sealed"] = "true";
        }

        var env = settings.Select(pair => $"{pair.Key.Replace(":", "__")}={pair.Value}").ToArray();
        var windows = $"InferHub-Node-Setup-{version}-win-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /Mode=mesh "
            + $"/CoordinatorUrl={coordinatorUrl} /EnrollmentToken={token} /NodeName={name}" + (sealedNode ? " /Sealed=yes" : "");
        var docker = $"docker run -d --name inferhub-node-{name} --restart unless-stopped -v inferhub-node-{name}:/data "
            + string.Join(" ", env.Select(line => $"-e {line}"))
            + $" ghcr.io/dev-art-solutions/inferhub-node:{version}";

        return new
        {
            name,
            token,
            expiresUtc = expires,
            settings,
            env,
            windows,
            docker,
            next = new[]
            {
                $"Install the node before {expires:u}: the token is spent on first use and expires then.",
                "When it connects it makes its own key and enrolls; GET /api/admin/identity lists its certificate.",
                sealedNode
                    ? "On the node: `InferHub.Node firewall` (or GET /api/control/firewall) prints the rules that let it reach only this coordinator; apply them as the machine's administrator."
                    : "This node is not sealed: it may download and reach the internet itself.",
                "GET /api/admin/site again: its line in nodes-sealed, nodes-certified and nodes-egress should turn ok."
            }
        };
    }

    private static string Names(string what, IEnumerable<SiteNode> nodes)
    {
        var names = nodes.Select(n => n.Name).ToArray();
        return names.Length == 0 ? "every connected node passes" : $"{what}: {string.Join(", ", names)}";
    }

    private static string Version()
    {
        var informational = typeof(SiteEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
