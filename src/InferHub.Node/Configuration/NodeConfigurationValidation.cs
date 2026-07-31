using InferHub.Shared.Contracts;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;
using InferHub.Node.Backends;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Configuration;

/// <remarks>
/// The configuration is optional so the validator can be unit-tested on its own; absent, nothing
/// has turned solo mode on, which is exactly what the both-off check below wants to know.
/// </remarks>
public sealed class CoordinatorOptionsValidator(IConfiguration? configuration = null)
    : IValidateOptions<CoordinatorOptions>
{
    public ValidateOptionsResult Validate(string? name, CoordinatorOptions options)
    {
        var failures = new List<string>();

        if (!options.Enabled)
        {
            // A node that neither joins a mesh nor serves its own clients does nothing at all, and
            // nothing is never what was meant. Read straight from configuration rather than through
            // IOptions<LocalApiOptions>: an options validator resolving another options monitor
            // during ValidateOnStart is how you get a cycle that only shows up at boot.
            var solo = configuration?
                .GetSection(LocalApiOptions.SectionName)
                .GetValue<bool>(nameof(LocalApiOptions.Enabled)) ?? false;

            if (!solo)
            {
                failures.Add(
                    $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.Enabled)} and {LocalApiOptions.SectionName}:{nameof(LocalApiOptions.Enabled)} are both false, so this node would neither join a mesh nor serve anyone. Turn one of them on.");
            }

            // Everything below is about reaching a coordinator. There isn't one.
            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        // Endpoints falls back to Url, so validating the resolved list covers both shapes and
        // cannot let a typo in an HA list boot a node that then silently only ever reaches one hub.
        var endpoints = options.Endpoints.Count > 0
            ? nameof(CoordinatorOptions.Endpoints)
            : nameof(CoordinatorOptions.Url);

        if (options.ResolvedEndpoints().Count == 0 || options.ResolvedEndpoints().Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{CoordinatorOptions.SectionName}:{endpoints} must be set.");
        }

        foreach (var endpoint in options.ResolvedEndpoints().Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add(
                    $"{CoordinatorOptions.SectionName}:{endpoints} must be absolute http(s) URLs (got '{endpoint}').");
            }
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.HeartbeatInterval)} must be positive (got {options.HeartbeatInterval}).");
        }

        if (options.ModelRefreshInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.ModelRefreshInterval)} must be positive (got {options.ModelRefreshInterval}).");
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            failures.Add(
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.RetryDelay)} must be positive (got {options.RetryDelay}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class NodeOptionsValidator : IValidateOptions<NodeOptions>
{
    public ValidateOptionsResult Validate(string? name, NodeOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Name))
        {
            failures.Add($"{NodeOptions.SectionName}:{nameof(NodeOptions.Name)} must be set.");
        }

        if (options.MaxConcurrency is { } cap && cap < 1)
        {
            failures.Add(
                $"{NodeOptions.SectionName}:{nameof(NodeOptions.MaxConcurrency)} must be >= 1 when set (got {cap}).");
        }

        // A typo here is silent by construction — capability kinds are open strings on the wire
        // (phase-40 D1), so "chatt" disables nothing and the box quietly keeps taking the traffic
        // the operator meant to move off it. Names are checked; the wire is still not.
        foreach (var disabled in options.Capabilities.Disabled)
        {
            if (!CapabilityKinds.IsWellKnown(disabled?.Trim()))
            {
                failures.Add(
                    $"{NodeOptions.SectionName}:Capabilities:Disabled contains '{disabled}', which is not a capability this release knows. Expected one of: {CapabilityKinds.Chat}, {CapabilityKinds.Embed}, {CapabilityKinds.Transcribe}, {CapabilityKinds.Speak}.");
            }
        }

        // Disabling both of the backend's kinds leaves a node that can be routed for nothing —
        // the phase-37 D10 shape. Note this is not the same as a node with no models (a
        // vector-store-only node, phase-39 D10): that one declares nothing because it holds
        // nothing, which is honest. This one holds models and refuses every use of them.
        if (options.Capabilities.IsDisabled(CapabilityKinds.Chat)
            && options.Capabilities.IsDisabled(CapabilityKinds.Embed))
        {
            failures.Add(
                $"{NodeOptions.SectionName}:Capabilities:Disabled turns off both '{CapabilityKinds.Chat}' and '{CapabilityKinds.Embed}', which is every kind of work this node's backend can do. Leave one on.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Guards the tool runtime's keys (phase 41). Everything here is inert while
/// <c>Tools:Enabled</c> is false, which is the default and therefore almost every deployment.
/// </summary>
public sealed class ToolOptionsValidator : IValidateOptions<ToolOptions>
{
    public ValidateOptionsResult Validate(string? name, ToolOptions options)
    {
        var failures = new List<string>();

        if (options.MaxAttachmentBytes < 1)
        {
            failures.Add(
                $"{ToolOptions.SectionName}:{nameof(ToolOptions.MaxAttachmentBytes)} must be positive (got {options.MaxAttachmentBytes}).");
        }

        if (options.QueueMaxWaitSeconds < 0)
        {
            failures.Add(
                $"{ToolOptions.SectionName}:{nameof(ToolOptions.QueueMaxWaitSeconds)} must be zero or more (got {options.QueueMaxWaitSeconds}).");
        }

        if (options.MaxStartAttempts < 1)
        {
            failures.Add(
                $"{ToolOptions.SectionName}:{nameof(ToolOptions.MaxStartAttempts)} must be >= 1 (got {options.MaxStartAttempts}).");
        }

        foreach (var (key, value) in new[]
                 {
                     (nameof(ToolOptions.RestartWindow), options.RestartWindow),
                     (nameof(ToolOptions.RestartBackoff), options.RestartBackoff),
                     (nameof(ToolOptions.RecoveryProbeInterval), options.RecoveryProbeInterval),
                     (nameof(ToolOptions.MaintenanceInterval), options.MaintenanceInterval)
                 })
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{ToolOptions.SectionName}:{key} must be positive (got {value}).");
            }
        }

        if (!options.Enabled)
        {
            // Allowing tools without enabling them is a common half-configuration, and it is
            // silent: the operator reads their own Allowed list back and concludes it is on.
            if (options.Allowed.Count > 0)
            {
                failures.Add(
                    $"{ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)} names {options.Allowed.Count} tool(s) but {ToolOptions.SectionName}:{nameof(ToolOptions.Enabled)} is false, so none of them can run. Turn it on, or remove the list.");
            }

            return Result(failures);
        }

        if (string.IsNullOrWhiteSpace(options.ManifestDirectory))
        {
            failures.Add($"{ToolOptions.SectionName}:{nameof(ToolOptions.ManifestDirectory)} must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.ScratchDirectory))
        {
            failures.Add($"{ToolOptions.SectionName}:{nameof(ToolOptions.ScratchDirectory)} must be set.");
        }

        if (options.Allowed.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add(
                $"{ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)} contains an empty entry. A blank id matches no manifest and hides a typo behind an index.");
        }

        // Deliberately NOT a failure: an enabled runtime with an empty Allowed list runs nothing,
        // and that is a legitimate way to stage a rollout — the runtime is registered, the
        // manifests on disk are read and reported, and the operator turns tools on one id at a
        // time. ProcessToolRuntime says so at startup rather than the host refusing to boot.
        return Result(failures);
    }

    private static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
}

/// <summary>
/// Only bites when <c>Backend:Type=openai</c> — an Ollama node has no upstream to configure.
/// A node that boots and then 500s on every job it is handed is worse than a node that refuses
/// to boot and says which key is missing.
/// </summary>
public sealed class OpenAiBackendOptionsValidator(IOptions<BackendOptions> backend)
    : IValidateOptions<OpenAiBackendOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenAiBackendOptions options)
    {
        if (backend.Value.Normalized() != BackendOptions.OpenAi)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.BaseUrl)} must be set when {BackendOptions.SectionName}:{nameof(BackendOptions.Type)}=openai.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.BaseUrl)} must be an absolute http(s) URL (got '{options.BaseUrl}').");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.TimeoutSeconds)} must be greater than zero (got {options.TimeoutSeconds}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return ValidateOptionsResult.Fail(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Endpoint)} must be set.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Endpoint)} must be an absolute http(s) URL (got '{options.Endpoint}').");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.RequestTimeout)} must be greater than zero (got '{options.RequestTimeout}').");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Only bites when solo mode is switched on, for the same reason the supervisor's validator does.
/// </summary>
public sealed class LocalApiOptionsValidator : IValidateOptions<LocalApiOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalApiOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        var addresses = options.SplitUrls();

        if (addresses.Count == 0)
        {
            failures.Add(
                $"{LocalApiOptions.SectionName}:{nameof(LocalApiOptions.Urls)} must be set when solo mode is on.");
        }

        foreach (var address in addresses)
        {
            // Through LocalApiOptions.TryParse, NOT Uri.TryCreate: Kestrel accepts `http://+:8080`
            // and `http://*:8080` and Uri does not. Validating with Uri alone refused to start the
            // shipped container, where exactly that form is the default — see the remarks on
            // TryParse. This check must stay Kestrel's idea of an address, not System.Uri's.
            if (!LocalApiOptions.TryParse(address, out _, out _))
            {
                failures.Add(
                    $"{LocalApiOptions.SectionName}:{nameof(LocalApiOptions.Urls)} must be absolute http(s) URLs (got '{address}').");
            }
        }

        // The one that matters. A keyless inference API reachable from a LAN hands arbitrary
        // compute on somebody's GPU to anyone who can reach the port, and the first sign of it is a
        // bill or a melted card. Deliberately stricter than phase-35 D4's warn-don't-refuse: there
        // the alternative was overruling an operator about their own network and the exposure was
        // data they had already chosen to store; here the default is safe and the dangerous
        // configuration has to be asked for by name.
        if (addresses.Count > 0
            && !options.BindsLoopbackOnly()
            && options.ApiKeys.Count(key => !string.IsNullOrWhiteSpace(key)) == 0
            && !options.AllowAnonymous)
        {
            failures.Add(
                $"{LocalApiOptions.SectionName}:{nameof(LocalApiOptions.Urls)} is not loopback ('{options.Urls}'), so the local API would serve inference to anything that can reach it. Set {LocalApiOptions.SectionName}:{nameof(LocalApiOptions.ApiKeys)}, or set {LocalApiOptions.SectionName}:{nameof(LocalApiOptions.AllowAnonymous)}=true if that network is genuinely trusted.");
        }

        if (options.MaxWaitSeconds <= 0)
        {
            failures.Add(
                $"{LocalApiOptions.SectionName}:{nameof(LocalApiOptions.MaxWaitSeconds)} must be greater than zero (got {options.MaxWaitSeconds}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Only bites when solo retrieval is switched on (phase 38), and the first thing it checks is the
/// one that is not about a value at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Retrieval in solo mode requires that there is no coordinator.</strong> Design rule 4:
/// <em>one source of truth per deployment, and node replicas are only ever derived from it — never
/// a second authority.</em> A meshed node already holds derived copies of the hub's collections
/// (phase-15 replicas, on disk, maintained by the hub pushing down). Give that same process an
/// <em>authoritative</em> store of its own and there are two vector authorities inside one node: a
/// locally ingested document is invisible to the fleet, a collection name that exists in both
/// places has two different sets of chunks under it, and the hub's replication will overwrite a
/// collection the operator believes they own.
/// </para>
/// <para>
/// It refuses rather than quietly disabling, which is the opposite of what phase-36 D1 does for the
/// supervisor — and deliberately so. A disabled supervisor costs an operational nicety; retrieval
/// silently switched off is <em>grounding</em> silently switched off, and the node then answers
/// confidently, fluently and ungrounded with no signal at all. That is the exact failure phase-31
/// D4 and phase-37 D8 exist to prevent.
/// </para>
/// </remarks>
public sealed class LocalRetrievalOptionsValidator(IConfiguration? configuration = null)
    : IValidateOptions<LocalRetrievalOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalRetrievalOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        // Read straight from configuration rather than through IOptions<CoordinatorOptions>, for the
        // reason CoordinatorOptionsValidator already documents: an options validator resolving
        // another options monitor during ValidateOnStart is a cycle that only shows up at boot.
        // Absent configuration means the default, and Coordinator:Enabled defaults to true.
        var meshed = configuration?
            .GetSection(CoordinatorOptions.SectionName)
            .GetValue<bool?>(nameof(CoordinatorOptions.Enabled)) ?? true;

        if (meshed)
        {
            failures.Add(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Enabled)} is true while {CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.Enabled)} is also true. A meshed node already holds vector replicas derived from its coordinator, and a second, authoritative store in the same process would be a second source of truth for the same collection names. Set {CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.Enabled)}=false to run this node standalone with its own corpus, or set {LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Enabled)}=false and ingest into the coordinator instead.");
        }

        var solo = configuration?
            .GetSection(LocalApiOptions.SectionName)
            .GetValue<bool>(nameof(LocalApiOptions.Enabled)) ?? false;

        if (!solo)
        {
            failures.Add(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Enabled)} is true but {LocalApiOptions.SectionName}:{nameof(LocalApiOptions.Enabled)} is false, so nothing would ever reach it — retrieval is served over the local API. Turn solo mode on.");
        }

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.DataDirectory)} must be set.");
        }

        if (!DistanceMetricExtensions.TryParse(options.Distance, out _))
        {
            failures.Add(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Distance)} must be one of 'cosine', 'dot', 'l2' (got '{options.Distance}').");
        }

        if (options.SnapshotEveryOps < 1)
        {
            failures.Add(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.SnapshotEveryOps)} must be >= 1 (got {options.SnapshotEveryOps}).");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultEmbeddingModel))
        {
            failures.Add(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.DefaultEmbeddingModel)} must be set.");
        }

        ValidateRetrieval(options.Retrieval, failures);
        ValidateIngestion(options.Ingestion, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // The same rules and the same wording as the coordinator's VectorStoreOptionsValidator, against
    // the same options class — only the key prefix differs. A value that is invalid on the hub must
    // not be quietly accepted here.
    private static void ValidateRetrieval(RetrievalOptions retrieval, List<string> failures)
    {
        const string prefix = LocalRetrievalOptions.SectionName + ":Retrieval:";

        if (retrieval.DefaultK < 1)
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.DefaultK)} must be >= 1 (got {retrieval.DefaultK}).");
        }

        if (retrieval.MaxRecords < retrieval.DefaultK)
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.MaxRecords)} must be >= DefaultK ({retrieval.DefaultK}, got {retrieval.MaxRecords}).");
        }

        if (retrieval.OnMissing is not "error" and not "passthrough")
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.OnMissing)} must be 'error' or 'passthrough' (got '{retrieval.OnMissing}').");
        }

        if (string.IsNullOrWhiteSpace(retrieval.Template))
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.Template)} must be set.");
        }
        else if (!retrieval.Template.Contains("{context}", StringComparison.Ordinal))
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.Template)} must contain the literal '{{context}}' placeholder.");
        }

        if (!RetrievalModes.TryParse(retrieval.Mode, out _))
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.Mode)} must be 'vector', 'keyword' or 'hybrid' (got '{retrieval.Mode}').");
        }

        if (retrieval.CandidatesPerBranch < 1)
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.CandidatesPerBranch)} must be >= 1 (got {retrieval.CandidatesPerBranch}).");
        }

        if (retrieval.Rerank is not "none" and not "llm")
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.Rerank)} must be 'none' or 'llm' (got '{retrieval.Rerank}').");
        }

        if (retrieval.RerankCandidates < 1)
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.RerankCandidates)} must be >= 1 (got {retrieval.RerankCandidates}).");
        }

        if (retrieval.RerankTimeoutSeconds < 1)
        {
            failures.Add($"{prefix}{nameof(RetrievalOptions.RerankTimeoutSeconds)} must be >= 1 (got {retrieval.RerankTimeoutSeconds}).");
        }
    }

    private static void ValidateIngestion(IngestionOptions ingestion, List<string> failures)
    {
        const string prefix = LocalRetrievalOptions.SectionName + ":Ingestion:";

        if (ingestion.MaxChars < 64)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.MaxChars)} must be >= 64 (got {ingestion.MaxChars}).");
        }

        // Overlap at or above the chunk size means chunk N+1 starts at or before chunk N did: the
        // chunker would never advance and a 1 MB document would spin forever.
        if (ingestion.OverlapChars < 0 || ingestion.OverlapChars >= ingestion.MaxChars)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.OverlapChars)} must be >= 0 and < MaxChars ({ingestion.MaxChars}, got {ingestion.OverlapChars}).");
        }

        if (ingestion.MaxDocumentBytes < 1)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.MaxDocumentBytes)} must be >= 1 (got {ingestion.MaxDocumentBytes}).");
        }

        if (ingestion.EmbeddingBatchSize < 1)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.EmbeddingBatchSize)} must be >= 1 (got {ingestion.EmbeddingBatchSize}).");
        }

        if (ingestion.MaxRetriesPerBatch < 1)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.MaxRetriesPerBatch)} must be >= 1 (got {ingestion.MaxRetriesPerBatch}).");
        }
    }
}

/// <summary>
/// Only bites when the supervisor is switched on. A node that leaves it off must boot exactly
/// as it did before the feature existed, including past a section somebody half-edited.
/// </summary>
public sealed class OllamaSupervisorOptionsValidator : IValidateOptions<OllamaSupervisorOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaSupervisorOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        Positive(options.ProbeInterval, nameof(OllamaSupervisorOptions.ProbeInterval));
        Positive(options.ProbeTimeout, nameof(OllamaSupervisorOptions.ProbeTimeout));
        Positive(options.ReadyTimeout, nameof(OllamaSupervisorOptions.ReadyTimeout));
        Positive(options.RestartWindow, nameof(OllamaSupervisorOptions.RestartWindow));
        Positive(options.RestartBackoff, nameof(OllamaSupervisorOptions.RestartBackoff));

        // A probe that outlives its own tick makes the consecutive-failure threshold meaningless:
        // the ticks would overlap and "three in a row" would stop meaning three intervals.
        if (options.ProbeTimeout >= options.ProbeInterval)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.ProbeTimeout)} must be shorter than {nameof(OllamaSupervisorOptions.ProbeInterval)} (got {options.ProbeTimeout} >= {options.ProbeInterval}).");
        }

        if (options.ReadyTimeout <= options.ProbeTimeout)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.ReadyTimeout)} must be longer than {nameof(OllamaSupervisorOptions.ProbeTimeout)} (got {options.ReadyTimeout} <= {options.ProbeTimeout}).");
        }

        if (options.UnhealthyThreshold < 1)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.UnhealthyThreshold)} must be >= 1 (got {options.UnhealthyThreshold}).");
        }

        if (options.MaxRestartAttempts < 1)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.MaxRestartAttempts)} must be >= 1 (got {options.MaxRestartAttempts}).");
        }

        if (!string.IsNullOrWhiteSpace(options.InstallUrl)
            && (!Uri.TryCreate(options.InstallUrl, UriKind.Absolute, out var installUri)
                || (installUri.Scheme != Uri.UriSchemeHttp && installUri.Scheme != Uri.UriSchemeHttps)))
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.InstallUrl)} must be an absolute http(s) URL when set (got '{options.InstallUrl}').");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

        void Positive(TimeSpan value, string key)
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{OllamaSupervisorOptions.SectionName}:{key} must be greater than zero (got {value}).");
            }
        }
    }
}
