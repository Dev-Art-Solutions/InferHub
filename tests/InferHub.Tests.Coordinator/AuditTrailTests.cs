using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 125: the trail's file, its window, its question, and what the middleware keeps.</summary>
public sealed class AuditTrailTests : IDisposable
{
    private const string AdminKey = "admin-key-for-the-trail";
    private const string ClientKey = "client-key-for-the-trail";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OffItCreatesNothingAndSaysNothing()
    {
        var trail = Trail(enabled: false);
        await trail.StartAsync(CancellationToken.None);
        trail.Write(new AuditRecord(DateTimeOffset.UtcNow, AuditRecord.Request, "alice"));
        await trail.StopAsync(CancellationToken.None);

        Assert.False(Directory.Exists(directory));
        Assert.Null(trail.Snapshot());
    }

    [Fact]
    public async Task LinesAreWrittenByDayAndReadBackNewestFirstWithFilters()
    {
        var clock = new SettableClock(new DateTimeOffset(2026, 10, 30, 23, 59, 0, TimeSpan.Zero));
        var trail = Trail(clock: clock);
        await trail.StartAsync(CancellationToken.None);

        trail.Write(Request(clock.GetUtcNow(), "alice", "llama3", ["gpu-1"], 200));
        trail.Write(Request(clock.GetUtcNow().AddSeconds(30), "bob", "qwen3", ["gpu-2", "gpu-1"], 200));
        trail.Write(Request(clock.GetUtcNow().AddMinutes(2), "alice", "qwen3", null, 429));
        await WaitForWrittenAsync(trail, 3);

        Assert.True(File.Exists(Path.Combine(directory, "audit-2026-10-30.jsonl")));
        Assert.True(File.Exists(Path.Combine(directory, "audit-2026-10-31.jsonl")));

        var all = await trail.QueryAsync(new AuditQuery(), CancellationToken.None);
        Assert.Equal([429, 200, 200], all.Select(record => record.Status!.Value));
        Assert.Equal("bob", all[1].Who);

        // A node filter matches any node a failover tried, not only the last.
        Assert.Equal(2, (await trail.QueryAsync(new AuditQuery(Node: "gpu-1"), CancellationToken.None)).Count);
        Assert.Equal(2, (await trail.QueryAsync(new AuditQuery(Who: "ALICE"), CancellationToken.None)).Count);
        Assert.Single(await trail.QueryAsync(new AuditQuery(Status: 429), CancellationToken.None));
        Assert.Single(await trail.QueryAsync(new AuditQuery(Limit: 1), CancellationToken.None));
        Assert.Single(await trail.QueryAsync(
            new AuditQuery(FromUtc: new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)), CancellationToken.None));

        var snapshot = trail.Snapshot()!;
        Assert.Equal(3, snapshot.Written);
        Assert.Equal(2, snapshot.Days);
        Assert.True(snapshot.Bytes > 0);

        await trail.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ATornLastLineIsSkippedNotFatal()
    {
        var trail = Trail();
        Directory.CreateDirectory(directory);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        await File.WriteAllTextAsync(
            trail.PathFor(day),
            Encoding.UTF8.GetString(AuditTrail.Line(Request(DateTimeOffset.UtcNow, "alice", "m", null, 200))) + "{\"at\":\"2026-");

        var records = await trail.QueryAsync(new AuditQuery(), CancellationToken.None);

        Assert.Equal("alice", Assert.Single(records).Who);
    }

    [Fact]
    public async Task DaysOutsideTheWindowAreDeletedAndNothingElseIs()
    {
        Directory.CreateDirectory(directory);
        var clock = new SettableClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var trail = Trail(clock: clock, retentionDays: 2);

        foreach (var day in new[] { "2026-10-28", "2026-10-29", "2026-10-30", "2026-10-31" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"audit-{day}.jsonl"), "");
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "the operator's");
        await File.WriteAllTextAsync(Path.Combine(directory, "audit-latest.jsonl"), "not a day");

        trail.Sweep(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        Assert.Equal(
            ["audit-2026-10-30.jsonl", "audit-2026-10-31.jsonl", "audit-latest.jsonl", "notes.txt"],
            Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order().ToArray());
    }

    [Theory]
    [InlineData("POST", "/v1/chat/completions", 200, true)]
    [InlineData("GET", "/v1/models", 200, true)]
    [InlineData("GET", "/api/status", 200, false)]
    [InlineData("GET", "/api/status", 401, true)]
    [InlineData("GET", "/api/admin/nodes", 200, false)]
    [InlineData("GET", "/api/admin/nodes", 401, true)]
    [InlineData("POST", "/api/admin/nodes/gpu-1/cordon", 200, true)]
    [InlineData("DELETE", "/api/admin/supply/abc", 404, true)]
    [InlineData("GET", "/api/admin/audit", 200, true)]
    [InlineData("GET", "/node/relay/hf/api/models/x", 200, true)]
    public void ReadsAreNotActionsButARefusalAndTheTrailItselfAre(string method, string path, int status, bool kept)
        => Assert.Equal(kept, AuditMiddleware.Kept(method, new PathString(path), status));

    [Theory]
    [InlineData("/api/chat", true)]
    [InlineData("/v1/embeddings", true)]
    [InlineData("/node/relay/ollama-registry/v2/x", true)]
    [InlineData("/chatgpt-pairing/start", true)]
    [InlineData("/health", false)]
    [InlineData("/metrics", false)]
    [InlineData("/console.html", false)]
    [InlineData("/hubs/node", false)]
    [InlineData("/apikeys", false)]
    public void OnlyTheKeyedSurfacesAreAudited(string path, bool audited)
        => Assert.Equal(audited, AuditMiddleware.Audited(new PathString(path)));

    [Fact]
    public void AnAdminKeyIsNamedByAFingerprintThatDoesNotContainIt()
    {
        var fingerprint = AdminApiKeyMiddleware.Fingerprint(AdminKey);

        Assert.Matches("^admin:[0-9a-f]{8}$", fingerprint);
        Assert.Equal(fingerprint, AdminApiKeyMiddleware.Fingerprint(AdminKey));
        Assert.NotEqual(fingerprint, AdminApiKeyMiddleware.Fingerprint(AdminKey + "2"));
        Assert.DoesNotContain(AdminKey, fingerprint);
    }

    [Fact]
    public async Task ThroughTheRealGuardsALineSaysWhoWhatWhichNodeAndNeverWhatWasSaid()
    {
        await using var app = await PipelineAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // A client's request with a prompt in the body and text in the query string.
        using var chat = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions?note=the-query-secret")
        {
            Content = new StringContent("""{"model":"llama3","messages":[{"role":"user","content":"the body secret"}]}""", Encoding.UTF8, "application/json")
        };
        chat.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ClientKey);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(chat)).StatusCode);

        // A wrong key, a refused admin read, an admin write, an admin read that is not kept, and the trail's own read.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Post, "/v1/chat/completions", "wrong-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Get, "/api/admin/nodes", "wrong-key")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "/api/admin/nodes/gpu-1/cordon", AdminKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/admin/nodes", AdminKey)).StatusCode);

        var trail = app.Services.GetRequiredService<AuditTrail>();
        await WaitForWrittenAsync(trail, 4);

        var read = await Send(http, HttpMethod.Get, AuditEndpoints.Path + "?limit=10", AdminKey);
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());
        var records = body.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(4, records.Length);

        var cordon = records[0];
        Assert.Equal(AdminApiKeyMiddleware.Fingerprint(AdminKey), cordon.GetProperty("who").GetString());
        Assert.Equal("/api/admin/nodes/gpu-1/cordon", cordon.GetProperty("path").GetString());

        Assert.Equal("-", records[1].GetProperty("who").GetString());
        Assert.Equal(401, records[1].GetProperty("status").GetInt32());
        Assert.Equal("/api/admin/nodes", records[1].GetProperty("path").GetString());
        Assert.Equal(401, records[2].GetProperty("status").GetInt32());

        var request = records[3];
        Assert.Equal("trail-client", request.GetProperty("who").GetString());
        Assert.Equal("POST", request.GetProperty("method").GetString());
        Assert.Equal("/v1/chat/completions", request.GetProperty("path").GetString());
        Assert.Equal("llama3", request.GetProperty("model").GetString());
        Assert.Equal("chat", request.GetProperty("capability").GetString());
        Assert.Equal(["gpu-2", "gpu-1"], request.GetProperty("nodes").EnumerateArray().Select(node => node.GetString()).ToArray());
        Assert.Equal("node", request.GetProperty("servedBy").GetString());
        Assert.Equal("127.0.0.1", request.GetProperty("remote").GetString());

        // The trail's own read is a line too — written after it answered.
        await WaitForWrittenAsync(trail, 5);
        Assert.Equal(AuditEndpoints.Path, (await trail.QueryAsync(new AuditQuery(Limit: 1), CancellationToken.None))[0].Path);

        // Rule 7, on the disk itself.
        var everything = string.Concat(Directory.EnumerateFiles(directory).Select(Shared));
        Assert.DoesNotContain("the body secret", everything);
        Assert.DoesNotContain("the-query-secret", everything);
        Assert.DoesNotContain(ClientKey, everything);
        Assert.DoesNotContain(AdminKey, everything);

        // Status and scrape carry it while it is on.
        var scrape = PrometheusFormatter.Format(new PrometheusScrape(
            "test", new Metrics().Snapshot(DateTimeOffset.UtcNow), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], 0,
            Audit: trail.Snapshot()));
        Assert.Contains("inferhub_audit_records_total 5", scrape);
        Assert.Contains("inferhub_audit_dropped_total 0", scrape);
    }

    [Fact]
    public async Task TheTrailsEndpointSaysWhenThereIsNone()
    {
        await using var app = await PipelineAsync(enabled: false);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var read = await Send(http, HttpMethod.Get, AuditEndpoints.Path, AdminKey);
        Assert.Contains("Audit:Enabled=false", await read.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void TheValidatorRefusesAWindowOfNothing()
    {
        var validator = new AuditTrailOptionsValidator();

        Assert.True(validator.Validate(null, new AuditTrailOptions { Enabled = false, RetentionDays = 0 }).Succeeded);
        Assert.Contains("Audit:RetentionDays", validator.Validate(null, new AuditTrailOptions { Enabled = true, RetentionDays = 0 }).FailureMessage);
        Assert.Contains("Audit:DataDirectory", validator.Validate(null, new AuditTrailOptions { Enabled = true, DataDirectory = " " }).FailureMessage);
    }

    private async Task<WebApplication> PipelineAsync(bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<ApiKeyOptions>(options =>
        {
            options.RequireAuthForLoopback = true;
            options.AdminApiKeys = [AdminKey];
            options.Clients = [new ClientConfig { Id = "trail-client", Key = ClientKey }];
        });
        builder.Services.Configure<MetricsOptions>(_ => { });
        builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
        builder.Services.AddSingleton(Options.Create(new AuditTrailOptions { Enabled = enabled, DataDirectory = directory }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuditTrail>();
        builder.Services.AddHostedService(services => services.GetRequiredService<AuditTrail>());

        var app = builder.Build();
        app.UseMiddleware<AuditMiddleware>();
        app.UseMiddleware<AdminApiKeyMiddleware>();
        app.UseMiddleware<BearerApiKeyMiddleware>();

        // Stand-ins for the dispatch path: what InferenceCore and the dispatcher tell the scope.
        app.MapPost("/v1/chat/completions", (HttpContext context) =>
        {
            AuditScope.Asked("llama3", "chat");
            AuditScope.Dispatched("gpu-2", "llama3", "chat");
            AuditScope.Dispatched("gpu-1", "llama3", "chat");
            context.Response.Headers[InferenceCore.ServedByHeader] = InferenceCore.ServedByNode;
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/admin/nodes", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/admin/nodes/{id}/cordon", (string id) => Results.Ok(new { id }));
        app.MapAuditEndpoints();

        await app.StartAsync();
        return app;
    }

    // The writer holds today's file open for append; a reader shares it, as a log shipper would.
    internal static string Shared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, string key)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await http.SendAsync(request);
    }

    private AuditTrail Trail(bool enabled = true, TimeProvider? clock = null, int retentionDays = 90)
        => new(
            Options.Create(new AuditTrailOptions { Enabled = enabled, DataDirectory = directory, RetentionDays = retentionDays }),
            clock ?? TimeProvider.System,
            NullLogger<AuditTrail>.Instance);

    private static AuditRecord Request(DateTimeOffset at, string who, string model, string[]? nodes, int status)
        => new(at, AuditRecord.Request, who, "10.0.0.5", Guid.NewGuid().ToString("N"), "POST", "/v1/chat/completions", status, 12, model, "chat", nodes, "node");

    private static async Task WaitForWrittenAsync(AuditTrail trail, long count)
    {
        for (var i = 0; i < 200 && trail.Snapshot()!.Written < count; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(count, trail.Snapshot()!.Written);
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

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
