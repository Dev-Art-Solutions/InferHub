using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 152: node groups kept on the hub, the traffic history, the fleet report and a cordon for a whole group.</summary>
public sealed class FleetTests : IDisposable
{
    // The registry ages a node against the clock it was given, so the report is asked at the real now.
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-fleet-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AGroupHoldsANodeByIdOrByEveryLabelAndAnEmptyOneIsRefused()
    {
        var groups = Groups();

        Assert.NotNull(groups.Put("empty", new NodeGroup("x")).Error);
        Assert.NotNull(groups.Put("a/b", new NodeGroup("x", Nodes: ["n1"])).Error);

        var (error, stored) = groups.Put(" rack-a ", new NodeGroup("ignored", " the rack ", ["n1", "N1", " "], new Dictionary<string, string> { ["site"] = "sofia", ["gpu"] = "4090" }));
        Assert.Null(error);
        Assert.Equal("rack-a", stored!.Name);
        Assert.Equal("the rack", stored.Description);
        Assert.Single(stored.Nodes!);

        Assert.True(stored.Contains("n1", null));
        Assert.True(stored.Contains("n2", Labels(("site", "Sofia"), ("gpu", "4090"), ("extra", "x"))));
        Assert.False(stored.Contains("n2", Labels(("site", "sofia"))));
        Assert.Equal(["rack-a"], groups.GroupsOf("n2", Labels(("site", "sofia"), ("gpu", "4090"))));
        Assert.Empty(groups.GroupsOf("n3", null));
    }

    [Fact]
    public void UnderFileTheGroupsOutliveTheHubAndUnderNoneNothingIsWritten()
    {
        var first = Groups(file: true);
        first.Put("rack-a", new NodeGroup("rack-a", Nodes: ["n1"]));
        first.Put("gone", new NodeGroup("gone", Nodes: ["n9"]));
        Assert.True(first.Delete("gone"));

        var second = Groups(file: true);
        Assert.Equal("file", second.Persistence);
        Assert.Equal(["rack-a"], second.All().Select(g => g.Name));

        var memory = Groups(file: false);
        memory.Put("x", new NodeGroup("x", Nodes: ["n1"]));
        Assert.Equal("none", memory.Persistence);
        Assert.False(File.Exists(Path.Combine(directory, "none", "groups.json")));
    }

    [Fact]
    public void AnUnknownGroupsPersistenceOrTrafficWindowFailsStartup()
    {
        var validator = new FleetOptionsValidator();

        Assert.True(validator.Validate(null, new FleetOptions()).Succeeded);
        Assert.True(validator.Validate(null, new FleetOptions { Groups = { Persistence = "fiel" } }).Failed);
        Assert.True(validator.Validate(null, new FleetOptions { Traffic = { SampleSeconds = 0 } }).Failed);
        Assert.True(validator.Validate(null, new FleetOptions { Traffic = { Minutes = 2000 } }).Failed);
    }

    [Fact]
    public void TheFirstSampleIsABaselineAndEachLaterOneTheDifference()
    {
        var (services, registry, metrics) = Services();
        Connect(registry, "a");
        Run(metrics, "id-a", completed: 7, failed: 0);   // before the hub's first sample: not a spike in the first ten seconds

        var history = services.GetRequiredService<TrafficHistory>();
        history.Sample(T0);
        Assert.Empty(history.Since(T0.AddHours(-1)));

        Run(metrics, "id-a", completed: 3, failed: 1);
        history.Sample(T0.AddSeconds(10));
        Run(metrics, "id-a", completed: 2, failed: 0);
        history.Sample(T0.AddSeconds(20));

        var samples = history.Since(T0);
        Assert.Equal(2, samples.Count);
        Assert.Equal((3L, 1L), (samples[0].Nodes["id-a"].Completed, samples[0].Nodes["id-a"].Failed));
        Assert.Equal((5L, 1L), history.Totals("id-a", T0));
    }

    [Fact]
    public void TheHistoryKeepsItsWindowAndANodeThatLeftQuietlyLeavesNoPoint()
    {
        var (services, registry, metrics) = Services(minutes: 1);
        Connect(registry, "a");
        var history = services.GetRequiredService<TrafficHistory>();

        for (var i = 0; i <= 20; i++)
        {
            history.Sample(T0.AddSeconds(i * 10));
        }

        Assert.Equal(6, history.Since(DateTimeOffset.MinValue).Count);

        registry.Remove("conn-a");
        history.Sample(T0.AddSeconds(300));
        Assert.Empty(history.Since(T0.AddSeconds(300)).Single().Nodes);

        // It still knows where the node belonged, so its history keeps summing into its group.
        Assert.Equal("a", history.Known("id-a")!.Name);
    }

    [Fact]
    public void TheReportGroupsByALabelAndSaysWhatNeedsAHand()
    {
        var (services, registry, metrics) = Services();
        Connect(registry, "a", maxConcurrency: 2, labels: Labels(("site", "sofia")));
        Connect(registry, "b", maxConcurrency: 2, labels: Labels(("site", "sofia")));
        Connect(registry, "c", labels: Labels(("site", "plovdiv")));
        Connect(registry, "d");
        registry.Cordon("id-b");

        var history = services.GetRequiredService<TrafficHistory>();
        history.Sample(T0.AddMinutes(-5));
        Run(metrics, "id-a", completed: 6, failed: 4);
        Run(metrics, "id-c", completed: 10, failed: 0);
        history.Sample(T0.AddMinutes(-4));

        var report = FleetEndpoints.Report(services, T0, "label:site", minutes: 15);

        Assert.Equal(["site"], report.LabelKeys);
        Assert.Equal(["(no site)", "plovdiv", "sofia"], report.Groups.Select(g => g.Key).Order(StringComparer.Ordinal));

        var sofia = report.Groups.Single(g => g.Key == "sofia");
        Assert.Equal((2, 1, 1, 4), (sofia.Nodes.Count, sofia.Routable, sofia.Cordoned, sofia.Capacity));
        Assert.Equal((6L, 4L, 0.4), (sofia.Completed, sofia.Failed, sofia.FailureRate));

        // One node with no declared cap makes a group's capacity unknown, not smaller.
        Assert.Null(report.Totals.Capacity);

        var a = report.Nodes.Single(n => n.NodeId == "id-a");
        Assert.Contains(a.Attention, x => x.Level == FleetAttention.Warn && x.Text.StartsWith("40% of 10 requests failed"));
        Assert.Equal("cordoned", report.Nodes.Single(n => n.NodeId == "id-b").State);
        Assert.Contains(report.Nodes.Single(n => n.NodeId == "id-b").Attention, x => x.Level == FleetAttention.Info && x.Text.StartsWith("cordoned"));
        Assert.DoesNotContain(report.Nodes.Single(n => n.NodeId == "id-c").Attention, x => x.Level == FleetAttention.Warn);
        Assert.Equal(1, report.Totals.Attention);
    }

    [Fact]
    public void UnderGroupsADefinedGroupNamesItsAbsentMembersAndTheRestAreUngrouped()
    {
        var (services, registry, _) = Services();
        Connect(registry, "a");
        Connect(registry, "b");
        var groups = services.GetRequiredService<NodeGroupRegistry>();
        groups.Put("rack-a", new NodeGroup("rack-a", "the rack", ["id-a", "id-gone"]));
        groups.Put("empty-now", new NodeGroup("empty-now", Labels: Labels(("site", "nowhere"))));

        var report = FleetEndpoints.Report(services, T0);

        Assert.Equal(["empty-now", "rack-a", FleetEndpoints.Ungrouped], report.Groups.Select(g => g.Key));
        var rack = report.Groups.Single(g => g.Key == "rack-a");
        Assert.True(rack.Defined);
        Assert.Equal(["id-a"], rack.Nodes);
        Assert.Equal(["id-gone"], rack.Absent);
        Assert.Equal(1, rack.Attention);
        Assert.Equal(1, report.Totals.Attention);
        Assert.Equal(["id-b"], report.Groups.Single(g => g.Key == FleetEndpoints.Ungrouped).Nodes);
        Assert.Equal(["rack-a"], report.Nodes.Single(n => n.NodeId == "id-a").Groups);
    }

    [Fact]
    public void TrafficIsBucketedPerGroupWithInFlightAsItsBusiestSample()
    {
        var (services, registry, metrics) = Services();
        Connect(registry, "a", labels: Labels(("site", "sofia")));
        Connect(registry, "b", labels: Labels(("site", "plovdiv")));
        var history = services.GetRequiredService<TrafficHistory>();

        history.Sample(T0.AddSeconds(-30));
        Run(metrics, "id-a", completed: 4, failed: 1);
        history.Sample(T0.AddSeconds(-20));
        Run(metrics, "id-b", completed: 2, failed: 0);
        metrics.RecordRequestStart("id-b");                  // one still running
        registry.Snapshot(T0);
        history.Sample(T0.AddSeconds(-10));

        var traffic = FleetEndpoints.Traffic(services, T0, "label:site", minutes: 60);

        Assert.Equal(60, traffic.StepSeconds);
        Assert.Equal(60, traffic.Points.Count);

        // Only the buckets a sample fell in are covered; the hour before the hub started is a gap, not a quiet hour.
        Assert.Equal(1, traffic.Covered.Count(c => c));
        Assert.False(traffic.Covered[0]);
        Assert.Equal(5L, traffic.Fleet.Completed.Sum() + traffic.Fleet.Failed.Sum() - 2);
        Assert.Equal(4L, traffic.Series.Single(s => s.Key == "sofia").Completed.Sum());
        Assert.Equal(1L, traffic.Series.Single(s => s.Key == "sofia").Failed.Sum());
        Assert.Equal(2L, traffic.Series.Single(s => s.Key == "plovdiv").Completed.Sum());
    }

    [Fact]
    public void GroupByIsOneOfFiveShapes()
    {
        Assert.Equal("group", FleetEndpoints.ParseGroupBy(null).GroupBy);
        Assert.Equal("label:site", FleetEndpoints.ParseGroupBy("label:site").GroupBy);
        Assert.Equal("organisation", FleetEndpoints.ParseGroupBy("Organisation").GroupBy);
        Assert.NotNull(FleetEndpoints.ParseGroupBy("label:").Error);
        Assert.NotNull(FleetEndpoints.ParseGroupBy("rack").Error);
    }

    [Fact]
    public async Task ThroughHttpAGroupIsKeptAndCordonedAsOneAndEachNodeKeepsItsOwnAuditLine()
    {
        await using var app = await StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var registry = app.Services.GetRequiredService<INodeRegistry>();
        Connect(registry, "a", labels: Labels(("site", "sofia")));
        Connect(registry, "b", labels: Labels(("site", "sofia")));
        Connect(registry, "c");

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/admin/groups/x", new { })).StatusCode);
        (await http.PutAsJsonAsync("/api/admin/groups/sofia", new { labels = new { site = "sofia" }, nodes = new[] { "id-gone" } })).EnsureSuccessStatusCode();

        var cordon = await http.PostAsync("/api/admin/groups/sofia/cordon", null);
        cordon.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await cordon.Content.ReadAsStringAsync());
        Assert.Equal(["id-a", "id-b"], body.RootElement.GetProperty("nodes").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal(["id-gone"], body.RootElement.GetProperty("absent").EnumerateArray().Select(e => e.GetString()));

        var cordoned = registry.Snapshot(DateTimeOffset.UtcNow).Where(n => n.Cordoned).Select(n => n.NodeId).Order().ToArray();
        Assert.Equal(["id-a", "id-b"], cordoned);
        Assert.Contains("group sofia", app.Services.GetRequiredService<IAuditLog>().Get("id-a")!.By);

        var fleet = await http.GetFromJsonAsync<JsonElement>("/api/admin/fleet?groupBy=group&minutes=5");
        Assert.Equal(2, fleet.GetProperty("totals").GetProperty("cordoned").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/admin/fleet?groupBy=rack")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/admin/traffic?groupBy=label:site")).StatusCode);

        (await http.PostAsync("/api/admin/groups/sofia/uncordon", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(registry.Snapshot(DateTimeOffset.UtcNow), n => n.Cordoned);

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync("/api/admin/groups/sofia")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/admin/groups/sofia/cordon", null)).StatusCode);
    }

    [Fact]
    public void ThePageReadsOnlyFieldsThePayloadsCarry()
    {
        // ConsoleContractTests' idea for the fleet page: a field fleet.js reads that the records do not carry is a blank column.
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "InferHub.Coordinator", "wwwroot", "fleet.js"));
        var fields = new[]
        {
            "labelKeys", "groupsPersistence", "totals", "routable", "cordoned", "inFlight", "capacity", "perMinute", "failureRate",
            "attention", "absent", "covered", "defined", "description", "stepSeconds", "points", "fleet", "series", "completed", "failed",
            "nodeId", "maxConcurrency", "models", "backend", "sealed", "sharing", "profile", "completedTotal", "failedTotal",
            "recent", "ageSeconds", "keys", "groups", "labels", "version", "organisation", "state", "level"
        };
        var json = JsonSerializer.Serialize(new object[]
        {
            FleetEndpoints.Report(Populated(), DateTimeOffset.UtcNow),
            FleetEndpoints.Traffic(Services().Services, T0),
            new FleetNode("n", "n", "v", null, new Dictionary<string, string>(), [], [], "idle", 0, false, 0, null, 0, null, null, null, null, null, 0, 0,
                new FleetNodeTraffic(0, 0, 0, null), [new FleetAttention("warn", "x")])
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var field in fields)
        {
            Assert.Contains($".{field}", script);
            Assert.Contains($"\"{field}\"", json);
        }
    }

    private IServiceProvider Populated()
    {
        var (services, registry, _) = Services();
        Connect(registry, "a", maxConcurrency: 1);
        services.GetRequiredService<NodeGroupRegistry>().Put("g", new NodeGroup("g", "d", ["id-a"]));
        return services;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InferHub.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("InferHub.sln not found above the test binaries");
    }

    private static IReadOnlyDictionary<string, string> Labels(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static void Connect(INodeRegistry registry, string name, int? maxConcurrency = null, IReadOnlyDictionary<string, string>? labels = null) =>
        registry.Upsert("conn-" + name, new NodeRegistration("id-" + name, name, "http://localhost:11434/", "3.105.0", labels, maxConcurrency), DateTimeOffset.UtcNow);

    private static void Run(Metrics metrics, string nodeId, int completed, int failed)
    {
        for (var i = 0; i < completed; i++)
        {
            metrics.RecordRequestStart(nodeId);
            metrics.RecordRequestComplete(nodeId);
        }

        for (var i = 0; i < failed; i++)
        {
            metrics.RecordRequestStart(nodeId);
            metrics.RecordRequestFail(nodeId);
        }
    }

    private NodeGroupRegistry Groups(bool file = false) =>
        new(Options.Create(new FleetOptions
        {
            Groups = { Persistence = file ? "file" : "none", DataDirectory = Path.Combine(directory, file ? "file" : "none") }
        }), NullLogger<NodeGroupRegistry>.Instance);

    private (IServiceProvider Services, INodeRegistry Registry, Metrics Metrics) Services(int minutes = 60)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INodeRegistry, NodeRegistry>();
        services.AddSingleton<Metrics>();
        services.AddSingleton(Options.Create(new FleetOptions { Traffic = { SampleSeconds = 10, Minutes = minutes } }));
        services.AddSingleton<NodeGroupRegistry>();
        services.AddSingleton<TrafficHistory>();
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<INodeRegistry>(), provider.GetRequiredService<Metrics>());
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<INodeRegistry, NodeRegistry>();
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<IAuditLog, AuditLog>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Options.Create(new FleetOptions()));
        builder.Services.AddSingleton<NodeGroupRegistry>();
        builder.Services.AddSingleton<TrafficHistory>();
        var app = builder.Build();
        app.MapFleetEndpoints();
        await app.StartAsync();
        return app;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
