using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Configuration;
using InferHub.Node.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 112 (the control API) and 113 (sharing modes): the pure parts as tables, the routes over a real solo host.
/// </summary>
public sealed partial class NodeControlTests
{
    [Fact]
    public void LocalSettingsAreSetAndRemovedByConfigurationKeyKeepingTheFilesOwnSpelling()
    {
        var file = (JsonObject)JsonNode.Parse("""{"node": {"name": "box", "Sharing": {"Mode": "always"}}}""")!;

        NodeLocalSettings.Set(file, "Node:Sharing:Mode", JsonValue.Create("idle"));
        NodeLocalSettings.Set(file, "Coordinator:Url", JsonValue.Create("http://hub/"));

        Assert.Equal("idle", file["node"]!["Sharing"]!["Mode"]!.GetValue<string>());
        Assert.Equal("http://hub/", file["Coordinator"]!["Url"]!.GetValue<string>());

        Assert.True(NodeLocalSettings.Remove(file, "Coordinator:Url"));
        Assert.Null(file["Coordinator"]);
        Assert.False(NodeLocalSettings.Remove(file, "Nothing:There"));
    }

    [Fact]
    public void AWriteIsAtomicAndKeepsThePreviousFile()
    {
        var dir = Directory.CreateTempSubdirectory("inferhub-local-").FullName;
        var path = Path.Combine(dir, "node.local.json");

        NodeLocalSettings.Write(new JsonObject { ["a"] = 1 }, path);
        NodeLocalSettings.Write(new JsonObject { ["a"] = 2 }, path);

        Assert.Equal(2, NodeLocalSettings.Read(path)["a"]!.GetValue<int>());
        Assert.Equal(1, NodeLocalSettings.Read(path + ".bak")["a"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("Node:Sharing:Mode", "\"IDLE\"", "\"idle\"")]
    [InlineData("Node:MaxConcurrency", "3", "3")]
    [InlineData("Node:MaxConcurrency", "\"4\"", "4")]
    [InlineData("Coordinator:Enabled", "true", "true")]
    public void AValueIsCoercedToItsKeysType(string key, string json, string expected)
    {
        var coerced = NodeSettingsSchema.Coerce(NodeSettingsSchema.Find(key)!, JsonNode.Parse(json), out var error);

        Assert.Null(error);
        Assert.Equal(expected, coerced!.ToJsonString());
    }

    [Theory]
    [InlineData("Node:Sharing:Mode", "\"sometimes\"", "one of always, idle, paused")]
    [InlineData("Node:MaxConcurrency", "\"many\"", "whole number")]
    [InlineData("Backend:Engines", "\"x\"", "JSON object")]
    public void AValueThatDoesNotFitIsRefusedNamingTheKey(string key, string json, string expected)
    {
        NodeSettingsSchema.Coerce(NodeSettingsSchema.Find(key)!, JsonNode.Parse(json), out var error);

        Assert.Contains(key, error);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("Coordinator:EnrollmentSecret", true)]
    [InlineData("LocalApi:ApiKeys", true)]
    [InlineData("Control:Token", true)]
    [InlineData("Upstream:ApiKey", true)]
    [InlineData("Node:Name", false)]
    public void SecretsAreRecognisedWhereverTheyAre(string key, bool secret) => Assert.Equal(secret, NodeSettingsSchema.IsSecret(key));

    [Fact]
    public void ActivitySaysWhoseJobItIsAndNeverWhatItSaid()
    {
        var activity = new NodeActivity();

        using (activity.Begin("chat", "qwen"))
        {
            using (NodeActivity.FromMesh())
            using (var mesh = activity.Begin("embed", "nomic"))
            {
                mesh.Fail("failed");
                Assert.Equal(1, activity.RunningCount(NodeActivity.Mesh));
                Assert.Equal(1, activity.RunningCount(NodeActivity.Local));
            }
        }

        Assert.Empty(activity.Running);
        // Newest first: the hub's job ended before the local one that was running around it.
        Assert.Equal(["chat", "embed"], activity.Recent.Select(e => e.Kind));
        Assert.Equal(["ok", "failed"], activity.Recent.Select(e => e.Outcome));
        Assert.Equal([NodeActivity.Local, NodeActivity.Mesh], activity.Recent.Select(e => e.Source));
        Assert.Equal(["job.started", "job.started", "job.finished", "job.finished"], activity.EventsAfter(0).Select(e => e.Type));
    }

    [Theory]
    [InlineData("""{"model":"qwen2.5:0.5b","messages":[{"role":"user","content":"model"}]}""", "qwen2.5:0.5b")]
    [InlineData("""{"messages":[{"model":"nested"}],"model":"later"}""", "later")]
    [InlineData("""{"prompt":"hi"}""", null)]
    [InlineData("not json", null)]
    public void TheModelIsReadFromTheTopLevelOnly(string json, string? expected) => Assert.Equal(expected, NodeActivity.ModelOf(json));

    [Theory]
    [InlineData("INFERHUB_SUPERVISOR", "desktop", true, "desktop")]
    [InlineData("INVOCATION_ID", "abc", true, "systemd")]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER", "true", true, "container")]
    [InlineData("NOTHING", "x", false, "none")]
    public void ARestartIsOfferedOnlyWhereSomethingStartsTheNodeAgain(string variable, string value, bool supported, string supervisor)
    {
        var support = NodeRestarter.Detect(name => name == variable ? value : null);

        Assert.Equal(supported, support.Supported);
        Assert.Equal(supervisor, support.Supervisor);
    }

    private static (NodeSharing Sharing, NodeActivity Activity, Monitor Options, ManualTime Time) Sharing(string mode, int idleSeconds = 60)
    {
        var options = new Monitor(new NodeOptions { Sharing = new SharingOptions { Mode = mode, IdleSeconds = idleSeconds } });
        var time = new ManualTime();
        var activity = new NodeActivity(time);
        return (new NodeSharing(options, activity, NullLogger<NodeSharing>.Instance, time), activity, options, time);
    }

    [Fact]
    public void AlwaysSharesWhateverThisMachineIsDoing()
    {
        var (sharing, activity, _, _) = Sharing(SharingOptions.Always);

        using (activity.Begin("chat", "m"))
        {
            Assert.False(sharing.Paused);
        }
    }

    [Fact]
    public void IdlePausesBeforeTheFirstLocalJobRunsAndResumesAfterTheIdleTime()
    {
        var (sharing, activity, _, time) = Sharing(SharingOptions.Idle, idleSeconds: 60);
        var changes = new List<bool>();
        sharing.Changed += state => changes.Add(state.Paused);

        Assert.False(sharing.Paused);

        using (activity.Begin("chat", "m"))
        {
            Assert.True(sharing.Paused);
            Assert.Contains("1 local job", sharing.State("n").Reason);
        }

        Assert.True(sharing.Paused);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(sharing.Paused);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.False(sharing.Paused);
        Assert.Equal([true, false], changes.Distinct().ToList());
    }

    [Fact]
    public void AHubJobNeverPausesSharing()
    {
        var (sharing, activity, _, _) = Sharing(SharingOptions.Idle);

        using (NodeActivity.FromMesh())
        using (activity.Begin("chat", "m"))
        {
            Assert.False(sharing.Paused);
        }
    }

    [Fact]
    public void PausedIsAModeAndTakesEffectLive()
    {
        var (sharing, _, options, _) = Sharing(SharingOptions.Always);

        options.Set(new NodeOptions { Sharing = new SharingOptions { Mode = "paused" } });

        Assert.True(sharing.Paused);
        Assert.Equal("paused by its owner", sharing.State("n").Reason);
    }

    // ---- the routes, over a real solo host ----

    private static async Task<(SoloHost Host, HttpClient Client, string Dir)> ControlHostAsync(params string[] settings)
    {
        var dir = Directory.CreateTempSubdirectory("inferhub-control-").FullName;
        var host = await SoloHost.StartAsync(null, [$"--Node:LocalSettingsFile={Path.Combine(dir, "node.local.json")}", .. settings]);
        var token = host.Services.GetRequiredService<ControlToken>();
        var client = new HttpClient { BaseAddress = new Uri(host.Url) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(token.FilePath).Trim());
        return (host, client, dir);
    }

    [Fact]
    public async Task EveryControlRouteNeedsTheTokenOnLoopbackToo()
    {
        await using var host = await SoloHost.StartAsync();

        foreach (var path in new[] { "/api/control/status", "/api/control/settings", "/api/control/logs" })
        {
            var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/control/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsync("/api/control/restart", null)).StatusCode);
    }

    [Fact]
    public async Task TheStatusSaysWhatTheNodeIsAndWhetherARestartWouldComeBack()
    {
        var (host, client, _) = await ControlHostAsync();
        await using var owned = host;

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/control/status"));
        var root = status.RootElement;

        Assert.Equal("solo-test", root.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal("disabled", root.GetProperty("network").GetProperty("state").GetString());
        Assert.Equal("always", root.GetProperty("sharing").GetProperty("mode").GetString());
        Assert.Equal(0, root.GetProperty("jobs").GetProperty("running").GetInt32());
        Assert.True(root.GetProperty("restart").TryGetProperty("supported", out _));
    }

    [Fact]
    public async Task ASettingIsWrittenToTheLocalFileValidatedFirstAndSaysWhetherARestartIsNeeded()
    {
        var (host, client, dir) = await ControlHostAsync();
        await using var owned = host;

        var saved = await client.PutAsJsonAsync("/api/control/settings", new
        {
            set = new Dictionary<string, object> { ["Node:Sharing:Mode"] = "idle", ["Coordinator:Url"] = "http://hub.example/" }
        });

        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(["Coordinator:Url"], body.RootElement.GetProperty("restartFor").EnumerateArray().Select(e => e.GetString()));

        var file = NodeLocalSettings.Read(Path.Combine(dir, "node.local.json"));
        Assert.Equal("idle", file["Node"]!["Sharing"]!["Mode"]!.GetValue<string>());

        // A key nobody reads, and a value the node would refuse at boot: neither reaches the file.
        var unknown = await client.PutAsJsonAsync("/api/control/settings", new { set = new Dictionary<string, object> { ["Node:Nmae"] = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Node:Nmae", await unknown.Content.ReadAsStringAsync());

        var refused = await client.PutAsJsonAsync("/api/control/settings", new { set = new Dictionary<string, object> { ["Node:MaxConcurrency"] = 0 } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("MaxConcurrency", await refused.Content.ReadAsStringAsync());
        Assert.Null(NodeLocalSettings.Read(Path.Combine(dir, "node.local.json"))["Node"]!["MaxConcurrency"]);
    }

    [Fact]
    public async Task ASecretIsWrittenAndNeverReadBack()
    {
        var (host, client, _) = await ControlHostAsync();
        await using var owned = host;

        await client.PutAsJsonAsync("/api/control/settings", new { set = new Dictionary<string, object> { ["Coordinator:EnrollmentSecret"] = "s3cret-value" } });

        var settings = await client.GetStringAsync("/api/control/settings");
        var file = await client.GetStringAsync("/api/control/settings/file");

        Assert.DoesNotContain("s3cret-value", settings);
        Assert.DoesNotContain("s3cret-value", file);
        Assert.Contains("(set)", file);
    }

    [Fact]
    public async Task SharingIsSetLiveThroughItsOwnRoute()
    {
        var (host, client, _) = await ControlHostAsync();
        await using var owned = host;

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/control/sharing", new { mode = "paused" })).StatusCode);

        var sharing = host.Services.GetRequiredService<NodeSharing>();
        await WaitUntilAsync(() => sharing.Paused);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/control/sharing", new { mode = "later" })).StatusCode);
    }

    [Fact]
    public async Task ALocalJobIsCountedAsLocalAndTheLogKeepsTheNodesLines()
    {
        var (host, client, _) = await ControlHostAsync();
        await using var owned = host;

        var chat = await host.Client.PostAsJsonAsync("/api/chat", new { model = "llama3", stream = false, messages = new[] { new { role = "user", content = "the secret word is plum" } } });
        Assert.True(chat.IsSuccessStatusCode, await chat.Content.ReadAsStringAsync());

        var activity = await client.GetStringAsync("/api/control/activity");
        using var doc = JsonDocument.Parse(activity);
        var recent = doc.RootElement.GetProperty("recent").EnumerateArray().First();
        Assert.Equal("local", recent.GetProperty("source").GetString());
        Assert.Equal("chat", recent.GetProperty("kind").GetString());
        Assert.Equal("llama3", recent.GetProperty("model").GetString());
        Assert.DoesNotContain("plum", activity);
    }

    [Fact]
    public async Task OnABareConsoleARestartIsRefusedWithTheReason()
    {
        var (host, client, _) = await ControlHostAsync();
        await using var owned = host;

        if (host.Services.GetRequiredService<INodeRestarter>().Support.Supported)
        {
            return; // run under systemd or in a container: there is a supervisor, and this test would end the host.
        }

        var refused = await client.PostAsync("/api/control/restart", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("console", await refused.Content.ReadAsStringAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private sealed class Monitor(NodeOptions value) : IOptionsMonitor<NodeOptions>
    {
        private Action<NodeOptions, string?>? listener;

        public NodeOptions CurrentValue { get; private set; } = value;

        public NodeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NodeOptions, string?> listener)
        {
            this.listener = listener;
            return null;
        }

        public void Set(NodeOptions options)
        {
            CurrentValue = options;
            listener?.Invoke(options, null);
        }
    }

    private sealed class ManualTime(DateTimeOffset? start = null) : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TimerCallback Callback, object? State)> timers = [];
        private DateTimeOffset now = start ?? DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by)
        {
            now += by;

            foreach (var timer in timers.Where(t => t.Due <= now).ToList())
            {
                timers.Remove(timer);
                timer.Callback(timer.State);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var entry = (now + dueTime, callback, state);
            timers.Add(entry);
            return new Timer(() => timers.Remove(entry));
        }

        private sealed class Timer(Action remove) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() => remove();

            public ValueTask DisposeAsync()
            {
                remove();
                return ValueTask.CompletedTask;
            }
        }
    }
}
