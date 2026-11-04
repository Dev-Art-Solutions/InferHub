using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 129: the closed-site checklist, and a plan for one node whose token really enrolls.</summary>
public sealed class SiteTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-site-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ADefaultHubIsNotAClosedSiteAndEveryMissingLineNamesItsKey()
    {
        var report = SiteEndpoints.Report(Services(closed: false), DateTimeOffset.UtcNow);

        Assert.False(report.Ready);
        Assert.Equal(8, report.Required);
        Assert.All(report.Checks.Where(c => !c.Ok), c => Assert.False(string.IsNullOrWhiteSpace(c.Fix)));
        Assert.False(Check(report, "identity").Ok);
        Assert.Contains("no node is connected", Check(report, "nodes-sealed").Detail);
    }

    [Fact]
    public void AClosedHubWithOneSealedCertifiedClosedNodeIsReady()
    {
        var services = Services(closed: true);
        var nodeId = Connect(services, "gpu-1", sealedNode: true, certify: true, open: []);

        var report = SiteEndpoints.Report(services, DateTimeOffset.UtcNow);

        Assert.True(report.Ready, string.Join("; ", report.Checks.Where(c => !c.Ok && c.Severity == SiteCheck.Required).Select(c => c.Id)));
        var node = Assert.Single(report.Nodes);
        Assert.Equal((nodeId, true, true, (bool?)true), (node.NodeId, node.Sealed, node.Certified, node.EgressClosed));
    }

    [Fact]
    public void ANodeThatFailsALineIsNamedInIt()
    {
        var services = Services(closed: true);
        Connect(services, "gpu-1", sealedNode: true, certify: true, open: []);
        Connect(services, "laptop", sealedNode: false, certify: false, open: ["1.1.1.1:443"]);

        var report = SiteEndpoints.Report(services, DateTimeOffset.UtcNow);

        Assert.False(report.Ready);
        Assert.Equal("not sealed: laptop", Check(report, "nodes-sealed").Detail);
        Assert.Equal("without a certificate: laptop", Check(report, "nodes-certified").Detail);
        Assert.Contains("laptop", Check(report, "nodes-egress").Detail);
        Assert.DoesNotContain("gpu-1", Check(report, "nodes-egress").Detail);
    }

    [Fact]
    public void ACloudProviderMeansARequestCanLeaveTheSite()
    {
        var services = Services(closed: true, fallback: true);

        var check = Check(SiteEndpoints.Report(services, DateTimeOffset.UtcNow), "no-cloud");

        Assert.False(check.Ok);
        Assert.Contains("can leave the site", check.Detail);
    }

    [Fact]
    public void ThePlanGivesTheSameAnswersInEveryShape()
    {
        var plan = JsonSerializer.SerializeToElement(
            SiteEndpoints.Plan("gpu-3", "https://hub.lan:5080/", "ihn_abc", DateTimeOffset.UnixEpoch, sealedNode: true, "3.89.0"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var windows = plan.GetProperty("windows").GetString()!;
        Assert.StartsWith("InferHub-Node-Setup-3.89.0-win-x64.exe /VERYSILENT", windows);
        Assert.Contains("/CoordinatorUrl=https://hub.lan:5080/ /EnrollmentToken=ihn_abc /NodeName=gpu-3 /Sealed=yes", windows);

        var docker = plan.GetProperty("docker").GetString()!;
        Assert.Contains("-e Coordinator__EnrollmentToken=ihn_abc", docker);
        Assert.Contains("-e Node__Sealed=true", docker);
        Assert.EndsWith("ghcr.io/dev-art-solutions/inferhub-node:3.89.0", docker);

        Assert.Equal("true", plan.GetProperty("settings").GetProperty("Node:Sealed").GetString());

        var open = JsonSerializer.SerializeToElement(
            SiteEndpoints.Plan("gpu-4", "https://hub.lan/", "ihn_x", DateTimeOffset.UnixEpoch, sealedNode: false, "3.89.0"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(open.GetProperty("settings").TryGetProperty("Node:Sealed", out _));
        Assert.DoesNotContain("/Sealed", open.GetProperty("windows").GetString());
    }

    [Fact]
    public async Task ThroughHttpAPlannedTokenEnrollsAndBadRequestsAreRefused()
    {
        await using var app = await StartAsync(identity: true);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(SiteEndpoints.Path + "/nodes", new { name = "two words" })).StatusCode);

        var response = await http.PostAsJsonAsync(SiteEndpoints.Path + "/nodes", new { name = "gpu-3" });
        response.EnsureSuccessStatusCode();
        using var plan = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = plan.RootElement.GetProperty("token").GetString()!;

        // The coordinator URL defaults to the address the admin used.
        Assert.Contains($"/CoordinatorUrl={app.Urls.First()}/", plan.RootElement.GetProperty("windows").GetString());

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrolled = app.Services.GetRequiredService<NodeCertificateAuthority>()
            .Enroll(new NodeEnrollRequest(token, "node-3", null, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        Assert.False(string.IsNullOrEmpty(enrolled.Certificate));
        Assert.Equal("gpu-3", Assert.Single(app.Services.GetRequiredService<NodeCertificateAuthority>().Certificates()).Name);

        var report = await http.GetFromJsonAsync<JsonElement>(SiteEndpoints.Path);
        Assert.False(report.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task WithoutNodeCertificatesThereIsNoPlan()
    {
        await using var app = await StartAsync(identity: false);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var response = await http.PostAsJsonAsync(SiteEndpoints.Path + "/nodes", new { name = "gpu-3" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Identity__Enabled=true", await response.Content.ReadAsStringAsync());
    }

    private static SiteCheck Check(SiteReport report, string id) => report.Checks.Single(c => c.Id == id);

    private IServiceProvider Services(bool closed, bool fallback = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INodeRegistry, NodeRegistry>();
        services.AddSingleton<NodeEgressRegistry>();
        services.AddSingleton(Options.Create(new NodeIdentityOptions { Enabled = closed, Require = closed, DataDirectory = directory }));
        services.AddSingleton<NodeCertificateAuthority>();
        services.AddSingleton<IOptionsMonitor<RelayOptions>>(new Monitor<RelayOptions>(new RelayOptions { Enabled = closed }));
        services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(new Monitor<ApiKeyOptions>(new ApiKeyOptions { AdminApiKeys = closed ? ["admin"] : [] }));
        services.AddSingleton(Options.Create(new FallbackOptions { Enabled = fallback }));
        return services.BuildServiceProvider();
    }

    private static string Connect(IServiceProvider services, string name, bool sealedNode, bool certify, string[] open)
    {
        var nodeId = "id-" + name;
        var registry = (NodeRegistry)services.GetRequiredService<INodeRegistry>();
        registry.Upsert("conn-" + name, new NodeRegistration(nodeId, name, "http://localhost:11434/", "3.89.0", Sealed: sealedNode ? true : null), DateTimeOffset.UtcNow);

        if (certify)
        {
            var authority = services.GetRequiredService<NodeCertificateAuthority>();
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            authority.Enroll(new NodeEnrollRequest(authority.CreateToken(name, "test").Token, nodeId, name, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        }

        services.GetRequiredService<NodeEgressRegistry>().Report(nodeId, name, new NodeEgressState(nodeId, DateTimeOffset.UtcNow, ["1.1.1.1:443"], open));
        return nodeId;
    }

    private async Task<WebApplication> StartAsync(bool identity)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<INodeRegistry, NodeRegistry>();
        builder.Services.AddSingleton(Options.Create(new NodeIdentityOptions { Enabled = identity, DataDirectory = directory }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<NodeCertificateAuthority>();
        var app = builder.Build();
        app.MapSiteEndpoints();
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

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
