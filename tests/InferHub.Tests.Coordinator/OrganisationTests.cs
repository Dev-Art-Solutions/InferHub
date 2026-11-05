using System.Net.Http.Headers;
using System.Net.Http.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 130: organisations, the grants between them, and the one place every route finds its nodes.</summary>
public sealed class OrganisationTests
{
    private static OrganisationPolicy Policy(params OrganisationGrant[] grants)
        => new(Options.Create(new OrganisationOptions { Enabled = true, Home = "home", Grants = grants.ToList() }));

    [Fact]
    public void AClientReachesItsOwnOrganisationAndWhatAGrantNames()
    {
        var policy = Policy(
            new OrganisationGrant { Owner = "lab-b", To = "lab-a", Models = ["llama3", "qwen*"], Capabilities = ["chat"] },
            new OrganisationGrant { Owner = "shared", To = "*" });

        Assert.True(policy.Allows("lab-a", "lab-a", "anything", "embed"));
        Assert.True(policy.Allows("lab-a", "lab-b", "llama3", "chat"));
        Assert.True(policy.Allows("lab-a", "lab-b", "qwen2.5:0.5b", "chat"));
        Assert.False(policy.Allows("lab-a", "lab-b", "mistral", "chat"));
        Assert.False(policy.Allows("lab-a", "lab-b", "llama3", "embed"));
        Assert.False(policy.Allows("lab-b", "lab-a", "llama3", "chat"));
        Assert.True(policy.Allows("anyone", "shared", "mistral", "embed"));
        Assert.Equal("home", policy.OrganisationOf(null));
        Assert.Equal("lab-a", policy.OrganisationOf(" Lab-A "));
    }

    [Fact]
    public void TheRegistryShowsAClientOnlyTheNodesItsOrganisationMayUse()
    {
        var registry = Registry(("conn-a", "node-a", "lab-a"), ("conn-b", "node-b", "lab-b"), ("conn-h", "node-h", null));
        var policy = Policy(new OrganisationGrant { Owner = "lab-b", To = "lab-a", Models = ["llama3"] });

        // No scope: an admin call or a background service sees every node.
        Assert.Equal(3, registry.FindNodesWithModel("llama3").Count);

        var scope = Run(policy, "lab-a", () => registry.FindNodesWithModel("llama3").Select(n => n.NodeId).ToArray());
        Assert.Equal(["node-a", "node-b"], scope.Result);
        Assert.True(scope.Denied);

        Assert.Equal(["node-a"], Run(policy, "lab-a", () => registry.FindNodesWithModel("qwen").Select(n => n.NodeId).ToArray()).Result);
        Assert.Equal(["node-h"], Run(policy, "home", () => registry.FindNodesWithModel("llama3").Select(n => n.NodeId).ToArray()).Result);

        // The model listing follows the same rule: lab-b granted llama3, not qwen.
        Assert.Equal(["llama3", "qwen"], Run(policy, "lab-a", () => registry.DistinctModels().Select(m => m.Name).ToArray()).Result);
        Assert.Equal(["llama3", "qwen"], Run(policy, "lab-b", () => registry.DistinctModels().Select(m => m.Name).ToArray()).Result);
        var registryB = Registry(("conn-b", "node-b", "lab-b"));
        Assert.Empty(Run(policy, "lab-a", () => registryB.DistinctModels().Where(m => m.Name == "qwen").ToArray()).Result);
    }

    [Fact]
    public async Task ThroughTheRealGuardsTheKeyDecidesTheOrganisationAndAdminsSeeEverything()
    {
        var registry = Registry(("conn-a", "node-a", "lab-a"), ("conn-b", "node-b", "lab-b"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<ApiKeyOptions>(options =>
        {
            options.RequireAuthForLoopback = true;
            options.AdminApiKeys = ["admin"];
            options.Clients =
            [
                new ClientConfig { Id = "alice", Key = "key-a", Organisation = "lab-a" },
                new ClientConfig { Id = "bob", Key = "key-b", Organisation = "lab-b" }
            ];
        });
        builder.Services.Configure<InferHub.Coordinator.Observability.MetricsOptions>(_ => { });
        builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
        builder.Services.AddSingleton(Options.Create(new OrganisationOptions { Enabled = true }));
        builder.Services.AddSingleton<OrganisationPolicy>();
        await using var app = builder.Build();
        app.UseMiddleware<AdminApiKeyMiddleware>();
        app.UseMiddleware<BearerApiKeyMiddleware>();
        app.UseMiddleware<OrganisationMiddleware>();
        app.MapGet("/v1/probe", () => registry.FindNodesWithModel("llama3").Select(n => n.NodeId).ToArray());
        app.MapGet("/api/admin/probe", () => registry.FindNodesWithModel("llama3").Select(n => n.NodeId).ToArray());
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal(["node-a"], await Get(http, "/v1/probe", "key-a"));
        Assert.Equal(["node-b"], await Get(http, "/v1/probe", "key-b"));
        Assert.Equal(["node-a", "node-b"], await Get(http, "/api/admin/probe", "admin"));
        Assert.Equal(2, app.Services.GetRequiredService<OrganisationPolicy>().Snapshot()!.Denied);
    }

    [Fact]
    public async Task TheAuditLineCarriesTheClientsOrganisationThoughItsMiddlewareRunsFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), "inferhub-org-audit-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<ApiKeyOptions>(options =>
        {
            options.RequireAuthForLoopback = true;
            options.Clients = [new ClientConfig { Id = "alice", Key = "key-a", Organisation = "lab-a" }];
        });
        builder.Services.Configure<InferHub.Coordinator.Observability.MetricsOptions>(_ => { });
        builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
        builder.Services.AddSingleton(Options.Create(new OrganisationOptions { Enabled = true }));
        builder.Services.AddSingleton<OrganisationPolicy>();
        builder.Services.AddSingleton(Options.Create(new AuditTrailOptions { Enabled = true, DataDirectory = directory }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuditTrail>();
        builder.Services.AddHostedService(services => services.GetRequiredService<AuditTrail>());
        await using var app = builder.Build();
        app.UseMiddleware<AuditMiddleware>();
        app.UseMiddleware<AdminApiKeyMiddleware>();
        app.UseMiddleware<BearerApiKeyMiddleware>();
        app.UseMiddleware<OrganisationMiddleware>();
        app.MapPost("/v1/probe", () => "ok");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/probe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key-a");
        (await http.SendAsync(request)).EnsureSuccessStatusCode();

        var trail = app.Services.GetRequiredService<AuditTrail>();
        for (var i = 0; i < 200 && trail.Snapshot()!.Written < 1; i++)
        {
            await Task.Delay(10);
        }

        var line = Assert.Single(await trail.QueryAsync(new AuditQuery(), CancellationToken.None));
        Assert.Equal("lab-a", line.Organisation);
        await app.StopAsync();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WithOrganisationsOnTheSiteChecklistRequiresTrustFromCertificates()
    {
        SiteReport Report(bool require)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Options.Create(new OrganisationOptions { Enabled = true }));
            services.AddSingleton<OrganisationPolicy>();
            services.AddSingleton(Options.Create(new NodeIdentityOptions { Enabled = require, Require = require, DataDirectory = Path.GetTempPath() }));
            return SiteEndpoints.Report(services.BuildServiceProvider(), DateTimeOffset.UtcNow);
        }

        var open = Report(require: false).Checks.Single(c => c.Id == "organisations-trusted");
        Assert.False(open.Ok);
        Assert.Equal(SiteCheck.Required, open.Severity);
        Assert.True(Report(require: true).Checks.Single(c => c.Id == "organisations-trusted").Ok);
        Assert.DoesNotContain(SiteEndpoints.Report(new ServiceCollection().BuildServiceProvider(), DateTimeOffset.UtcNow).Checks, c => c.Id == "organisations-trusted");
    }

    [Fact]
    public void OffThereAreNoOrganisations()
    {
        var policy = new OrganisationPolicy(Options.Create(new OrganisationOptions()));

        Assert.False(policy.Enabled);
        Assert.Null(policy.Snapshot());
    }

    [Fact]
    public void NamesAreCheckedAtStartup()
    {
        var validator = new OrganisationOptionsValidator();
        Assert.True(validator.Validate(null, new OrganisationOptions { Home = "Not Valid" }).Succeeded);
        Assert.Contains("Organisations:Home", validator.Validate(null, new OrganisationOptions { Enabled = true, Home = "Not Valid" }).FailureMessage);
        Assert.Contains("Grants:0:To", validator.Validate(null, new OrganisationOptions
        {
            Enabled = true,
            Grants = [new OrganisationGrant { Owner = "lab-b", To = "lab a" }]
        }).FailureMessage);

        var clients = new ApiKeyOptionsValidator().Validate(null, new ApiKeyOptions
        {
            Clients = [new ClientConfig { Id = "c", Key = "k", Organisation = "Lab A" }]
        });
        Assert.Contains("Organisation 'Lab A'", clients.FailureMessage);
    }

    private static NodeRegistry Registry(params (string Connection, string NodeId, string? Organisation)[] nodes)
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        foreach (var (connection, nodeId, organisation) in nodes)
        {
            registry.Upsert(connection, new NodeRegistration(nodeId, nodeId, "http://localhost:11434/", "3.90.0", Organisation: organisation), now);
            registry.ReportModels(connection, new NodeModels(nodeId, [new ModelInfo("llama3", "d", 1), new ModelInfo("qwen", "e", 1)], now), now);
        }

        return registry;
    }

    // A request's flow: the scope set, the work done in it, then whether a node was left out.
    private static (T Result, bool Denied) Run<T>(OrganisationPolicy policy, string organisation, Func<T> work)
    {
        T result = default!;
        var denied = false;
        var thread = new Thread(() =>
        {
            var scope = OrganisationScope.Begin(policy, organisation);
            result = work();
            denied = scope.Denied;
        });
        thread.Start();
        thread.Join();
        return (result, denied);
    }

    private static async Task<string[]> Get(HttpClient http, string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<string[]>())!;
    }
}
