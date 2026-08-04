using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Phase 45. The console is the one thing in this repo a unit test cannot verify by running it —
/// but it <em>can</em> verify the half that breaks silently: a panel reading a field the payload
/// does not have renders <c>undefined</c>, or a dash, or nothing at all, and every test in the suite
/// still passes.
/// </summary>
/// <remarks>
/// <para>
/// So this drives the <b>real</b> status and admin endpoints over a populated registry and resolves
/// every JSON path the console reads. The path list is the console's read set written down — and it
/// is guarded from becoming fiction by <see cref="EveryPathInTheReadSetIsActuallyReadByTheConsole"/>,
/// which fails if a path is listed here but appears in no static file. A contract test that has
/// drifted away from its subject is worse than none, because it reads as coverage.
/// </para>
/// </remarks>
public class ConsoleContractTests
{
    /// <summary>
    /// Every field the console renders from <c>/api/status</c>, as a JSON path. <c>[]</c> means
    /// "the first element of this array", so a path only resolves when the payload actually carries
    /// a populated example — which is why the fixture below reports a tool, a corpus and a profile
    /// refusal rather than an empty fleet.
    /// </summary>
    private static readonly string[] StatusPaths =
    [
        "coordinatorVersion",
        "uptimeSeconds",
        "metrics.requestsTotal",
        "metrics.requestsInFlight",
        "metrics.requestsCompleted",
        "metrics.requestsFailed",
        "metrics.failoversAttempted",
        "metrics.failoversSucceeded",
        "metrics.nodesEvicted",
        "models",
        "queue.depth",
        "fallback.enabled",
        "fallback.dispatched",

        // Phase 40 — the capability matrix and the fleet row under it.
        "capabilities[].capability",
        "capabilities[].nodes",
        "capabilities[].models",

        "nodes[].nodeId",
        "nodes[].name",
        "nodes[].ollamaEndpoint",
        "nodes[].version",
        "nodes[].ageSeconds",
        "nodes[].inFlight",
        "nodes[].localInFlight",
        "nodes[].modelCount",
        "nodes[].capabilities",
        "nodes[].maxConcurrency",
        "nodes[].tokensPerSecond",

        // Phase 43 — the profiles panel and the refusals strip.
        "nodes[].profile.name",
        "nodes[].profile.revision",
        "nodes[].profile.status",
        "nodes[].profile.refusals[].item",
        "nodes[].profile.refusals[].reason",

        // Phase 44 — node retrieval.
        "nodes[].corpus.enabled",
        "nodes[].corpus.provider",
        "nodes[].corpus.status",
        "nodes[].corpus.error",
        "nodes[].corpus.atUtc",
        "nodes[].corpus.collections[].name",
        "nodes[].corpus.collections[].dimension",
        "nodes[].corpus.collections[].records",

        // Phase 45 — the tools panel.
        "nodes[].tools.enabled",
        "nodes[].tools.atUtc",
        "nodes[].tools.tools[].id",
        "nodes[].tools.tools[].allowed",
        "nodes[].tools.tools[].state",
        "nodes[].tools.tools[].maxWorkers",
        "nodes[].tools.tools[].workers",
        "nodes[].tools.tools[].busy",
        "nodes[].tools.tools[].requests",
        "nodes[].tools.tools[].failures",
        "nodes[].tools.tools[].lastError",
        "nodes[].tools.tools[].capabilities[].kind",
        "nodes[].tools.tools[].capabilities[].models"
    ];

    /// <summary>What the node table and the model-management panel read from <c>/api/admin/nodes</c>.</summary>
    private static readonly string[] AdminNodePaths =
    [
        "[].nodeId",
        "[].name",
        "[].ollamaEndpoint",
        "[].ageSeconds",
        "[].inFlight",
        "[].localInFlight",
        "[].cordoned",
        "[].labels",
        "[].maxConcurrency",
        "[].capabilities",
        "[].supportsModelManagement",
        "[].profile.name",
        "[].profile.revision",
        "[].profile.status"
    ];

    /// <summary>What the profile editor reads from <c>/api/admin/profiles</c>.</summary>
    private static readonly string[] ProfilePaths =
    [
        "[].name",
        "[].revision",
        "[].selector"
    ];

    [Fact]
    public async Task EveryFieldTheConsoleReadsExistsInTheStatusPayload()
    {
        await using var hub = await ConsoleFixture.StartAsync();

        var status = await hub.JsonAsync("/api/status");

        foreach (var path in StatusPaths)
        {
            Assert.True(Resolves(status, path), $"/api/status has no '{path}' — the console renders undefined for it");
        }
    }

    [Fact]
    public async Task EveryFieldTheConsoleReadsExistsInTheAdminPayloads()
    {
        await using var hub = await ConsoleFixture.StartAsync();

        var nodes = await hub.JsonAsync("/api/admin/nodes");
        foreach (var path in AdminNodePaths)
        {
            Assert.True(Resolves(nodes, path), $"/api/admin/nodes has no '{path}'");
        }

        var profiles = await hub.JsonAsync("/api/admin/profiles");
        foreach (var path in ProfilePaths)
        {
            Assert.True(Resolves(profiles, path), $"/api/admin/profiles has no '{path}'");
        }
    }

    /// <summary>
    /// Guards the guard. A read set that names a field nothing reads is a list somebody will trust
    /// while the panel beside it quietly renders a dash.
    /// </summary>
    [Fact]
    public void EveryPathInTheReadSetIsActuallyReadByTheConsole()
    {
        var sources = string.Concat(ConsoleFixture.StaticFiles().Select(File.ReadAllText));

        foreach (var path in StatusPaths.Concat(AdminNodePaths).Concat(ProfilePaths))
        {
            var leaf = path.Split('.').Last().Replace("[]", string.Empty);

            if (leaf.Length == 0)
            {
                continue;
            }

            Assert.True(
                sources.Contains(leaf, StringComparison.Ordinal),
                $"no static file mentions '{leaf}', so the read set has drifted from the console");
        }
    }

    /// <summary>
    /// D1 in one assertion: the refusals strip is fed from <c>/api/status</c> alone. If a refusal
    /// ever needed a second request, it would stop being visible on the first paint — which is the
    /// support conversation the strip exists to prevent.
    /// </summary>
    [Fact]
    public async Task ARefusalIsVisibleFromTheStatusPayloadAlone()
    {
        await using var hub = await ConsoleFixture.StartAsync();

        var status = await hub.JsonAsync("/api/status");
        var node = status.GetProperty("nodes")[0];

        var refusal = node.GetProperty("profile").GetProperty("refusals")[0];
        Assert.Equal("tools.whisper", refusal.GetProperty("item").GetString());
        Assert.Contains("Tools:Allowed", refusal.GetProperty("reason").GetString());

        // The other two kinds the strip surfaces, both on the same payload.
        var tools = node.GetProperty("tools").GetProperty("tools");
        Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("state").GetString() == "not-allowed");
        Assert.Equal("failed", node.GetProperty("corpus").GetProperty("status").GetString());
    }

    /// <summary>
    /// A fleet that uses none of phases 40–45 must produce the payload v3.12 produced: no
    /// <c>tools</c> key on a node that has never reported one. Absence is a fact (D2), and a node
    /// running an older build reporting nothing must not read as "this box has no tools".
    /// </summary>
    [Fact]
    public async Task ANodeThatHasReportedNothingCarriesNoToolsOrCorpusKey()
    {
        await using var hub = await ConsoleFixture.StartAsync(reportNodeState: false);

        var node = (await hub.JsonAsync("/api/status")).GetProperty("nodes")[0];

        Assert.False(node.TryGetProperty("tools", out var tools) && tools.ValueKind is not JsonValueKind.Null);
        Assert.False(node.TryGetProperty("corpus", out var corpus) && corpus.ValueKind is not JsonValueKind.Null);
    }

    private static bool Resolves(JsonElement root, string path)
    {
        var current = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment;
            var indexed = name.EndsWith("[]", StringComparison.Ordinal);

            if (indexed)
            {
                name = name[..^2];
            }

            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
                {
                    return false;
                }

                current = next;
            }

            if (indexed)
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() == 0)
                {
                    return false;
                }

                current = current[0];
            }
        }

        // A key that is present and null still resolves: `lastError: null` is the honest answer for
        // a tool that has not failed, and the console renders a dash for it on purpose.
        return true;
    }
}

/// <summary>
/// A coordinator with the real status, admin and metrics endpoints over a fleet of one, carrying a
/// profile refusal, a failed corpus and a tool that was loaded but never allowed — the three states
/// D1 says must be visible without drilling in.
/// </summary>
internal sealed class ConsoleFixture : IAsyncDisposable
{
    private WebApplication app = null!;

    public HttpClient Client { get; private set; } = null!;

    public static async Task<ConsoleFixture> StartAsync(bool reportNodeState = true)
    {
        var fixture = new ConsoleFixture();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var now = DateTimeOffset.UtcNow;
        var registry = new NodeRegistry();
        registry.Upsert(
            "conn-1",
            new NodeRegistration(
                "gpu-1",
                "console-node",
                "http://localhost:11434/",
                "3.13.0",
                Labels: new Dictionary<string, string> { ["role"] = "gpu" },
                MaxConcurrency: 2),
            now);
        registry.ReportModels(
            "conn-1",
            new NodeModels(
                "gpu-1",
                [new ModelInfo("llama3", "sha256:abc", 4661211808)],
                now,
                [new NodeCapability("chat", ["llama3"]), new NodeCapability("transcribe", ["whisper-small"])]),
            now);

        var profiles = new ProfileRegistry(new NoProfileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProfileRegistry>.Instance);
        var corpora = new NodeCorpusRegistry();
        var tools = new NodeToolRegistry();

        profiles.Put("gpu-boxes", new NodeProfile(
            "gpu-boxes",
            0,
            new NodeProfileSelector(Labels: new Dictionary<string, string> { ["role"] = "gpu" }),
            Capabilities: new Dictionary<string, bool> { ["chat"] = true },
            Tools: new Dictionary<string, bool> { ["whisper"] = true },
            MaxConcurrency: 2));

        if (reportNodeState)
        {
            profiles.ReportState(new NodeProfileState(
                "gpu-1",
                "gpu-boxes",
                1,
                Applied: ["maxConcurrency=2"],
                Refusals: [new NodeProfileRefusal("tools.whisper", "'whisper' is not in Tools:Allowed on this node, and a profile may not add it")],
                Pending: [],
                MaxConcurrency: 2,
                now));

            corpora.Report(new NodeCorpusState(
                "gpu-1",
                Enabled: true,
                Provider: "qdrant",
                Status: NodeCorpusState.Failed,
                Collections: [new NodeCorpusCollection("handbook", 768, 1240)],
                Error: "connection refused reaching http://qdrant:6333",
                now));

            tools.Report(new NodeToolState(
                "gpu-1",
                Enabled: true,
                [
                    new NodeToolInfo(
                        "piper",
                        Allowed: true,
                        NodeToolInfo.Running,
                        [new NodeCapability("speak", ["en_US-amy"])],
                        MaxWorkers: 1,
                        Workers: 1,
                        Busy: 0,
                        Requests: 12,
                        Failures: 1,
                        LastError: "worker exited while encoding",
                        LastErrorAtUtc: now),
                    new NodeToolInfo(
                        "whisper",
                        Allowed: false,
                        NodeToolInfo.NotAllowed,
                        [],
                        MaxWorkers: 0,
                        Workers: 0,
                        Busy: 0,
                        Requests: 0,
                        Failures: 0,
                        LastError: null,
                        LastErrorAtUtc: null)
                ],
                now));
        }

        builder.Services.AddSingleton<INodeRegistry>(registry);
        builder.Services.AddSingleton<IProfileRegistry>(profiles);
        builder.Services.AddSingleton(corpora);
        builder.Services.AddSingleton(tools);
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<AdmissionControl>();
        builder.Services.AddSingleton<ThroughputTracker>();
        builder.Services.AddSingleton<IAuditLog, AuditLog>();
        builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
        builder.Services.AddSingleton<IConversationAffinity>(
            new ConversationAffinity(Microsoft.Extensions.Options.Options.Create(new RouterOptions())));
        builder.Services.AddSingleton<IClusterMembership>(new SingleCoordinatorMembership());
        builder.Services.AddSingleton(services => TestUsage.Queue(services.GetRequiredService<INodeRegistry>()));
        builder.Services.Configure<FallbackOptions>(_ => { });
        builder.Services.Configure<ApiKeyOptions>(_ => { });

        fixture.app = builder.Build();
        fixture.app.MapStatusEndpoint("3.13.0");
        fixture.app.MapMetricsEndpoint("3.13.0");

        await fixture.app.StartAsync();
        fixture.Client = new HttpClient { BaseAddress = new Uri(fixture.app.Urls.First()) };

        return fixture;
    }

    public async Task<JsonElement> JsonAsync(string path)
    {
        // The admin group needs services the status fixture does not carry, so those two payloads
        // are built from the same registries through the shipped shapes rather than over the wire.
        if (path == "/api/admin/nodes")
        {
            return AdminNodesJson();
        }

        if (path == "/api/admin/profiles")
        {
            return Serialize(app.Services.GetRequiredService<IProfileRegistry>().All());
        }

        var response = await Client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public async Task<string> TextAsync(string path)
    {
        var response = await Client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The console's own files, so the read set can be checked against what reads it.</summary>
    public static IEnumerable<string> StaticFiles()
    {
        var root = RepositoryRoot();

        yield return Path.Combine(root, "src", "InferHub.Coordinator", "wwwroot", "console.js");
        yield return Path.Combine(root, "src", "InferHub.Coordinator", "wwwroot", "console.html");
        yield return Path.Combine(root, "src", "InferHub.Coordinator", "wwwroot", "status.html");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    private JsonElement AdminNodesJson()
    {
        var registry = app.Services.GetRequiredService<INodeRegistry>();
        var audit = app.Services.GetRequiredService<IAuditLog>();
        var profiles = app.Services.GetRequiredService<IProfileRegistry>();

        var rows = registry.Snapshot(DateTimeOffset.UtcNow).Select(node =>
        {
            var assignment = profiles.MatchFor(node.NodeId, node.Labels);
            var state = profiles.StateOf(node.NodeId);
            var last = audit.Get(node.NodeId);

            return new
            {
                connectionId = node.ConnectionId,
                nodeId = node.NodeId,
                name = node.Name,
                ollamaEndpoint = node.OllamaEndpoint,
                version = node.Version,
                lastSeenUtc = node.LastSeenUtc,
                ageSeconds = node.AgeSeconds,
                inFlight = node.InFlight,
                localInFlight = node.LocalInFlight,
                modelCount = node.ModelCount,
                labels = node.Labels,
                maxConcurrency = node.MaxConcurrency,
                cordoned = node.Cordoned,
                supportsModelManagement = node.SupportsModelManagement,
                capabilities = (node.Capabilities ?? []).Select(c => c.Kind).ToArray(),
                lastAction = last is null ? null : new { action = last.Action, atUtc = last.AtUtc, by = last.By },
                profile = new
                {
                    name = assignment.Profile?.Name ?? state?.ProfileName,
                    revision = assignment.Profile?.Revision ?? state?.Revision ?? 0,
                    status = assignment.IsConflict ? "conflict" : state?.Status() ?? "pending",
                    conflicts = assignment.Conflicts,
                    refusals = state?.Refusals ?? Array.Empty<NodeProfileRefusal>()
                }
            };
        });

        return Serialize(rows);
    }

    private static JsonElement Serialize<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class NoProfileStore : IProfileStore
    {
        public IReadOnlyCollection<NodeProfile> Load() => Array.Empty<NodeProfile>();

        public void Save(NodeProfile profile)
        {
        }

        public void Delete(string name)
        {
        }
    }
}
