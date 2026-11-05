using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using InferHub.Coordinator.Auth;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 130. <c>Organisations:</c> — machines and clients from more than one organisation on one coordinator, and the grants
/// that say who may use whose nodes. Off by default: with it off there are no organisations and every client reaches every
/// node, as before.
/// </summary>
public sealed class OrganisationOptions
{
    public const string SectionName = "Organisations";

    public bool Enabled { get; set; }

    /// <summary>The coordinator's own organisation: every node and client that names none belongs to it.</summary>
    public string Home { get; set; } = "home";

    /// <summary>What one organisation lets another use of its nodes (D2). Same-organisation use needs no grant.</summary>
    public List<OrganisationGrant> Grants { get; set; } = [];
}

/// <summary>
/// <c>Owner</c>'s nodes may serve <c>To</c>'s clients (<c>*</c>: every organisation), for the <c>Models</c> listed (exact
/// names or a trailing-<c>*</c> prefix; empty: all) and the <c>Capabilities</c> listed (empty: all).
/// </summary>
public sealed class OrganisationGrant
{
    public string Owner { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public List<string> Models { get; set; } = [];

    public List<string> Capabilities { get; set; } = [];
}

public sealed partial class OrganisationOptionsValidator : IValidateOptions<OrganisationOptions>
{
    public ValidateOptionsResult Validate(string? name, OrganisationOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!OrganisationPolicy.IsName(options.Home))
        {
            failures.Add($"Organisations:Home '{options.Home}' is not an organisation name: lowercase letters, digits and '-', up to 64.");
        }

        for (var i = 0; i < options.Grants.Count; i++)
        {
            var grant = options.Grants[i];

            if (!OrganisationPolicy.IsName(grant.Owner))
            {
                failures.Add($"Organisations:Grants:{i}:Owner '{grant.Owner}' is not an organisation name.");
            }

            if (grant.To != "*" && !OrganisationPolicy.IsName(grant.To))
            {
                failures.Add($"Organisations:Grants:{i}:To '{grant.To}' is not an organisation name or '*'.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}

/// <summary>What <c>/api/status</c> says while organisations are on: the home, the grants, and refusals since start.</summary>
public sealed record OrganisationSnapshot(string Home, IReadOnlyList<OrganisationGrant> Grants, long Denied);

/// <summary>
/// Phase 130. The rule (D1, D2): a client reaches a node of its own organisation, or of one that granted it the model and the
/// capability. Applied where every route finds its nodes (<see cref="NodeRegistry.FindNodesWithModel"/> and the model
/// listings), through the request's <see cref="OrganisationScope"/>.
/// </summary>
public sealed partial class OrganisationPolicy(IOptions<OrganisationOptions> options)
{
    private readonly ConcurrentDictionary<string, string> sources = new(StringComparer.Ordinal);
    private long denied;

    public bool Enabled => options.Value.Enabled;

    public string Home => options.Value.Home;

    public static bool IsName(string? name) => name is not null && NamePattern().IsMatch(name);

    public string OrganisationOf(string? named) => string.IsNullOrWhiteSpace(named) ? Home : named.Trim().ToLowerInvariant();

    /// <summary>Whether a client of <paramref name="clientOrganisation"/> may use a node of <paramref name="nodeOrganisation"/> for this work.</summary>
    public bool Allows(string clientOrganisation, string nodeOrganisation, string? model, string? capability)
    {
        if (string.Equals(clientOrganisation, nodeOrganisation, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var grant in options.Value.Grants)
        {
            if (!string.Equals(grant.Owner, nodeOrganisation, StringComparison.OrdinalIgnoreCase)
                || (grant.To != "*" && !string.Equals(grant.To, clientOrganisation, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var modelOk = grant.Models.Count == 0 || model is null || grant.Models.Any(pattern => Matches(pattern, model));
            var capabilityOk = grant.Capabilities.Count == 0 || capability is null
                || grant.Capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));

            if (modelOk && capabilityOk)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a node's organisation came from: <c>certificate</c> (recorded at enrollment) or <c>declared</c> (its own config).</summary>
    public void RecordSource(string nodeId, string source) => sources[nodeId] = source;

    public string? SourceOf(string nodeId) => sources.GetValueOrDefault(nodeId);

    public void CountDenied() => Interlocked.Increment(ref denied);

    public OrganisationSnapshot? Snapshot()
        => Enabled ? new OrganisationSnapshot(Home, options.Value.Grants, Interlocked.Read(ref denied)) : null;

    private static bool Matches(string pattern, string model)
    {
        var p = pattern.Trim();
        return p.EndsWith('*')
            ? model.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : NodeRegistry.ModelNamesMatch(p, model);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// Phase 130 D3. The organisation of the client a request runs for, carried by an <see cref="AsyncLocal{T}"/> so the registry
/// can filter without every endpoint passing it. No scope — an admin call, a background service, the fleet view — sees
/// every node, as before.
/// </summary>
public sealed class OrganisationScope
{
    private static readonly AsyncLocal<OrganisationScope?> current = new();

    private OrganisationScope(OrganisationPolicy policy, string organisation)
    {
        Policy = policy;
        Organisation = organisation;
    }

    public static OrganisationScope? Current => current.Value;

    public OrganisationPolicy Policy { get; }

    public string Organisation { get; }

    /// <summary>Some node was left out for this request: counted once, when the request ends.</summary>
    public bool Denied { get; private set; }

    internal static OrganisationScope Begin(OrganisationPolicy policy, string organisation)
    {
        var scope = new OrganisationScope(policy, organisation);
        current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Runs work later under a scope captured earlier, then puts back what was there: the image and video job pump routes a
    /// job from whatever flow runs it, never the submitter's (found reading the pump in this phase).
    /// </summary>
    public static IDisposable Enter(OrganisationScope? scope)
    {
        var previous = current.Value;
        current.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore(OrganisationScope? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }

    public bool Allows(string? nodeOrganisation, string? model, string? capability)
    {
        var allowed = Policy.Allows(Organisation, Policy.OrganisationOf(nodeOrganisation), model, capability);
        Denied |= !allowed;
        return allowed;
    }
}

/// <summary>
/// Phase 130. After the key guards: a client request runs in its client's organisation. Admin routes, the fleet view and
/// the node routes are not client work and get no scope.
/// </summary>
public sealed class OrganisationMiddleware(RequestDelegate next, OrganisationPolicy policy)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (!policy.Enabled
            || path.StartsWithSegments(AdminApiKeyMiddleware.AdminPathPrefix)
            || path.StartsWithSegments("/api/status")
            || !(path.StartsWithSegments("/api") || path.StartsWithSegments(BearerApiKeyMiddleware.OpenAiPathPrefix)))
        {
            await next(context);
            return;
        }

        var client = BearerApiKeyMiddleware.ClientOf(context);
        var scope = OrganisationScope.Begin(policy, policy.OrganisationOf(client.Organisation));

        try
        {
            await next(context);
        }
        finally
        {
            if (scope.Denied)
            {
                policy.CountDenied();
            }
        }
    }
}
