using System.Reflection;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ApiKeyOptions>, ApiKeyOptionsValidator>();
builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
// The message cap is derived from Tools:MaxAttachmentBytes, not left at SignalR's 32 KB default —
// see NodeHubLimits. Exceeding that default does not fail a message, it kills the connection, and
// a synthesised sentence is ~300 KB.
builder.Services.AddSignalR(options =>
    options.MaximumReceiveMessageSize = NodeHubLimits.ReceiveSizeFor(
        builder.Configuration
            .GetSection(ToolEdgeOptions.SectionName)
            .GetValue("MaxAttachmentBytes", ToolAttachmentLimits.DefaultMaxBytes)));
builder.Services.AddSingleton<NodeAuthFilter>();
builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection("Dispatcher"));
builder.Services.Configure<RouterOptions>(builder.Configuration.GetSection("Router"));
builder.Services.AddSingleton<Metrics>();
builder.Services.Configure<MetricsOptions>(builder.Configuration.GetSection(MetricsOptions.SectionName));
builder.Services.AddSingleton<INodeRegistry, NodeRegistry>();
builder.Services.AddSingleton<IAuditLog, AuditLog>();

// Conversation affinity (phase 30). Keyed on the stable node id so a node reconnecting keeps its
// warm conversations. Persistence is opt-in and off by default: `none` keeps the map in-memory
// (byte-identical to v2.11), `file` writes a derived cache of routing hints to disk.
builder.Services.Configure<AffinityOptions>(builder.Configuration.GetSection(AffinityOptions.SectionName));
var affinityPersistence = builder.Configuration
    .GetSection(AffinityOptions.SectionName)
    .GetValue<string>(nameof(AffinityOptions.Persistence)) ?? AffinityOptions.PersistenceNone;

if (string.Equals(affinityPersistence.Trim(), AffinityOptions.PersistenceFile, StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAffinityStore, FileAffinityStore>();
}
else
{
    builder.Services.AddSingleton<IAffinityStore, NoAffinityStore>();
}

builder.Services.AddSingleton<IConversationAffinity>(sp => new ConversationAffinity(
    sp.GetRequiredService<IOptions<RouterOptions>>(),
    sp.GetRequiredService<IAffinityStore>(),
    TimeProvider.System));
builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
builder.Services.Configure<InferHub.Coordinator.Endpoints.ToolEdgeOptions>(
    builder.Configuration.GetSection(InferHub.Coordinator.Endpoints.ToolEdgeOptions.SectionName));
builder.Services.AddOptions<InferHub.Shared.Images.ImageEdgeOptions>()
    .Bind(builder.Configuration.GetSection(InferHub.Shared.Images.ImageEdgeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<InferHub.Shared.Images.ImageEdgeOptions>,
    InferHub.Coordinator.Services.ImageEdgeOptionsValidator>();
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
// Phase 41: the same instance, a second capability. One job registry, one failover path.
builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
builder.Services.AddSingleton<IEmbeddingDispatcher, EmbeddingDispatcher>();
builder.Services.AddSingleton<ModelCommandCoordinator>();

// Node profiles (phase 43). The registry is always present so nothing branches on the feature
// existing; with no profile written it matches nothing and every node runs its own configuration,
// which is byte-identical to v3.10. Persistence is the opt-in half — see ProfileOptions for why a
// profile is rule 4's third recorded exception.
builder.Services.AddOptions<FleetOptions>()
    .Bind(builder.Configuration.GetSection(FleetOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<FleetOptions>, FleetOptionsValidator>();

var profilePersistence = (builder.Configuration
    .GetSection(FleetOptions.SectionName)
    .GetSection(nameof(FleetOptions.Profiles))
    .GetValue<string>(nameof(ProfileOptions.Persistence)) ?? ProfileOptions.PersistenceNone).Trim();

if (string.Equals(profilePersistence, ProfileOptions.PersistenceFile, StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IProfileStore, FileProfileStore>();
}
else if (string.Equals(profilePersistence, ProfileOptions.PersistencePostgres, StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IProfileStore, PostgresProfileStore>();
}
else
{
    builder.Services.AddSingleton<IProfileStore, NoProfileStore>();
}

builder.Services.AddSingleton<IProfileRegistry, ProfileRegistry>();
builder.Services.AddSingleton<NodeProfileCoordinator>();

// Phase 44. Who is the authority for a collection name (D1), what each node says about the corpus it
// hosts (D6), and how work for a node-owned collection reaches its owner (D5). All three are
// registered unconditionally and are inert for a fleet that assigns no corpus: ownership is empty, so
// every name is the hub's and every code path is the one v3.11 took.
builder.Services.AddSingleton<CollectionOwnership>();
builder.Services.AddSingleton<NodeCorpusRegistry>();
builder.Services.AddSingleton<NodeCorpusDispatcher>();

// Phase 45. The same mailbox for what a node's tool runtime is doing. Empty and harmless on a fleet
// with Tools:Enabled=false, which is the default and therefore almost every deployment.
builder.Services.AddSingleton<NodeToolRegistry>();

// Phase 47. The async image-job surface. Registered unconditionally and inert on a fleet with no
// image capability: the store holds nothing, the pump reads an empty queue, and the sweeper ticks
// every five seconds over zero jobs.
//
// Phase 56 made durability an option and rule 4's fourth recorded exception: with
// Images:Jobs:Persistence=none (the default) nothing is created, opened or listed and a restart
// forgets in-flight AND completed jobs exactly as v3.23 did; with `file` a finished job survives for
// its retention window and not one second longer, and one that was in flight comes back `failed`
// with `hub_restarted` rather than as a 404 that reads like a bug.
builder.Services.AddSingleton<InferHub.Shared.Images.IImageJobArchive>(sp =>
    InferHub.Shared.Images.ImageJobArchives.Create(
        sp.GetRequiredService<IOptions<InferHub.Shared.Images.ImageEdgeOptions>>().Value.Jobs,
        (message, ex) => sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("InferHub.Images.Archive")
            .LogWarning(ex, "{Message}", message)));
builder.Services.AddSingleton<ImageJobRegistry>();
builder.Services.AddHostedService<ImageJobSweeper>();
builder.Services.AddSingleton<ThroughputTracker>();
builder.Services.AddHostedService<NodeReaper>();

// Clients, quotas & usage (phase 25). All of it is inert for a config without Auth:Clients:
// every key resolves anonymous-unlimited, admission is a dictionary miss, and the ledger
// records what the responses already carried.
builder.Services.AddHttpContextAccessor();
builder.Services.AddOptions<QueueOptions>()
    .Bind(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddOptions<UsageOptions>()
    .Bind(builder.Configuration.GetSection(UsageOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<UsageOptions>, UsageOptionsValidator>();
builder.Services.AddSingleton<AdmissionControl>();
builder.Services.AddSingleton<UsageMeter>();
builder.Services.AddSingleton<IRequestQueue, RequestQueue>();

var usagePersistence = builder.Configuration
    .GetSection(UsageOptions.SectionName)
    .GetValue<string>(nameof(UsageOptions.Persistence)) ?? UsageOptions.PersistenceNone;

if (string.Equals(usagePersistence.Trim(), UsageOptions.PersistencePostgres, StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IUsageLedger, PostgresUsageLedger>();
}
else
{
    builder.Services.AddSingleton<IUsageLedger, InMemoryUsageLedger>();
}

// Cloud burst. Registered always, disabled unless Fallback:Enabled — with it off, ShouldServe
// is a single `false` and every existing behaviour is byte-for-byte unchanged.
builder.Services.Configure<FallbackOptions>(builder.Configuration.GetSection(FallbackOptions.SectionName));
builder.Services.AddHttpClient(FallbackDispatcher.HttpClientName);
builder.Services.AddSingleton<IFallbackDispatcher, FallbackDispatcher>();

// High availability (phase 32). Off by default and inert when off: no lease, no Postgres
// connection, and SingleCoordinatorMembership reports Enabled=false so the role header, the
// standby 503 and the status block never appear — byte-identical to v2.13.
builder.Services.AddOptions<ClusterOptions>()
    .Bind(builder.Configuration.GetSection(ClusterOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ClusterOptions>, ClusterOptionsValidator>();

var clusterEnabled = builder.Configuration
    .GetSection(ClusterOptions.SectionName)
    .GetValue<bool>(nameof(ClusterOptions.Enabled));

if (clusterEnabled)
{
    var instanceId = builder.Configuration
        .GetSection(ClusterOptions.SectionName)
        .GetValue<string>(nameof(ClusterOptions.InstanceId)) ?? Environment.MachineName;

    builder.Services.AddSingleton(new ClusterMembership(instanceId));
    builder.Services.AddSingleton<IClusterMembership>(sp => sp.GetRequiredService<ClusterMembership>());
    builder.Services.AddSingleton<IClusterLease, PostgresClusterLease>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHostedService<ClusterLeaseService>();
}
else
{
    builder.Services.AddSingleton<IClusterMembership, SingleCoordinatorMembership>();
}

builder.Services.AddInferHubVectorStore(builder.Configuration);
var vectorSection = builder.Configuration.GetSection(VectorStoreOptions.SectionName);
var vectorStoreEnabled = vectorSection.GetValue<bool>(nameof(VectorStoreOptions.Enabled));
var vectorProvider = vectorSection.GetValue<string>(nameof(VectorStoreOptions.Provider)) ?? VectorStoreProviderExtensions.Local;
// Replication / self-healing / node-served reads only exist under the local provider; an external
// provider (postgres, qdrant) owns its own durability, so the rebuild endpoint is not applicable there.
var vectorSupportsReplication = vectorStoreEnabled && !VectorStoreProviderExtensions.IsExternal(vectorProvider);

var app = builder.Build();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

app.UseMiddleware<AdminApiKeyMiddleware>();
app.UseMiddleware<BearerApiKeyMiddleware>();

// After the two auth guards, before anything routes: a standby refuses work but still answers
// health, status and /metrics, so an operator can see what the standby thinks it is.
app.UseMiddleware<ClusterRoleMiddleware>();

// Status page is read-only; serve it from wwwroot/ and surface /status as an alias.
var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("status.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapGet("/status", () => Results.Redirect("/status.html"));
app.MapGet("/console", () => Results.Redirect("/console.html"));

// Intentionally open, and intentionally still 200 on a standby: a standby *is* healthy, it just
// is not leading. A load balancer drains it on the role field (or on the 503 the inference routes
// return); reporting a standby as unhealthy would have an orchestrator restart-loop the very
// instance that is supposed to be waiting quietly.
app.MapGet("/health", (IClusterMembership membership, ILogger<Program> logger) =>
{
    logger.LogInformation("Health check requested");

    return membership.Enabled
        ? Results.Ok(new
        {
            status = "ok",
            version,
            role = membership.IsActive ? ClusterRoleMiddleware.ActiveRole : ClusterRoleMiddleware.StandbyRole,
            instance = membership.InstanceId
        })
        : Results.Ok(new { status = "ok", version });
});

app.MapGet("/api/nodes", (INodeRegistry registry) =>
{
    return Results.Ok(registry.Snapshot(DateTimeOffset.UtcNow));
});

app.MapStatusEndpoint(version);
app.MapMetricsEndpoint(version);
app.MapInferenceEndpoints();
app.MapOpenAiEndpoints();
app.MapToolEndpoints();

// Phase 42. Beside /api/tools/{capability} rather than replacing it: an operator who writes their
// own tool needs a call InferHub did not have to know about in advance, and a client with an OpenAI
// SDK needs the route that SDK already calls.
app.MapAudioEndpoints();

// Phase 46. The same reasoning one modality over: /api/tools/image works and is generic, and a
// client holding an OpenAI SDK calls /v1/images/generations.
app.MapImageEndpoints();

// Phase 47. The async surface, beside the synchronous one rather than instead of it: OpenAI has no
// asynchronous Images API to adopt, so work with no existing shape travels as its own contract
// under /api (D1). /v1/images/generations is unchanged for anyone who never reads this.
app.MapImageJobEndpoints();
app.MapAdminEndpoints();

if (vectorStoreEnabled)
{
    app.MapVectorEndpoints(vectorSupportsReplication);
    app.MapIngestionEndpoints();
    app.MapSearchEndpoints();
}

app.MapHub<NodeHub>("/hubs/node");

app.Run();

public partial class Program;
