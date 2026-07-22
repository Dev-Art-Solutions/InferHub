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
builder.Services.AddSignalR();
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
builder.Services.AddSingleton<IDispatcher, Dispatcher>();
builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
builder.Services.AddSingleton<IEmbeddingDispatcher, EmbeddingDispatcher>();
builder.Services.AddSingleton<ModelCommandCoordinator>();
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
// Replication / self-healing / node-served reads only exist under the local provider; postgres
// owns its own durability, so the rebuild endpoint is not applicable there.
var vectorSupportsReplication = vectorStoreEnabled && !VectorStoreProviderExtensions.IsPostgres(vectorProvider);

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

app.MapGet("/api/tags", (INodeRegistry registry, ILogger<Program> logger) =>
{
    logger.LogInformation("Model tags requested");
    return Results.Ok(new OllamaTagsResponse(registry.DistinctModels().ToArray()));
});

app.MapGet("/api/nodes", (INodeRegistry registry) =>
{
    return Results.Ok(registry.Snapshot(DateTimeOffset.UtcNow));
});

app.MapStatusEndpoint(version);
app.MapMetricsEndpoint(version);
app.MapInferenceEndpoints();
app.MapOpenAiEndpoints();
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
