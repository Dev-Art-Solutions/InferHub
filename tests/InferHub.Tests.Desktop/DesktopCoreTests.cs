using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InferHub.Desktop;

namespace InferHub.Tests;

/// <summary>
/// Phase 115: what the desktop app knows of a node — the control API's client, the event stream it follows, finding
/// a node, and the command line of its own node. Pure, or over a scripted HTTP handler; the window is not here.
/// </summary>
public sealed class DesktopCoreTests
{
    [Fact]
    public async Task TheEventStreamIsReadAsTheNodeWritesIt()
    {
        var text = ": keep-alive\n\nid: 7\nevent: job.started\ndata: {\"kind\":\"chat\"}\n\nid: 8\nevent: log\ndata: line one\ndata: line two\n\n";
        var events = new List<ServerEvent>();

        await foreach (var item in NodeControlClient.ReadEventsAsync(new MemoryStream(Encoding.UTF8.GetBytes(text))))
        {
            events.Add(item);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal((7L, "job.started", "chat"), (events[0].Id, events[0].Type, events[0].Data!["kind"]!.GetValue<string>()));
        Assert.Equal("line one\nline two", events[1].Data!.GetValue<string>());
    }

    [Fact]
    public async Task EveryCallCarriesTheControlTokenAndARefusalComesBackInTheNodesWords()
    {
        var handler = new Scripted(request =>
        {
            Assert.Equal("Bearer tok", request.Headers.Authorization!.ToString());
            return request.RequestUri!.AbsolutePath == "/api/control/restart"
                ? (HttpStatusCode.Conflict, """{"error":{"message":"not restarting: nothing would start the node again"}}""")
                : (HttpStatusCode.OK, """{"node":{"name":"box"}}""");
        });
        using var client = new NodeControlClient(new NodeEndpoint(new Uri("http://127.0.0.1:5081"), "tok", "test"), handler);

        Assert.Equal("box", (await client.StatusAsync())["node"]!["name"]!.GetValue<string>());

        var refused = await Assert.ThrowsAsync<NodeControlException>(() => client.RestartAsync());
        Assert.Equal(409, refused.Status);
        Assert.Equal("not restarting: nothing would start the node again", refused.Message);
    }

    [Fact]
    public async Task ASettingsRefusalListsWhatTheNodeWouldRefuse()
    {
        var handler = new Scripted(_ => (HttpStatusCode.BadRequest,
            """{"error":{"message":"the change was not saved: the node would refuse it","failures":["Node:MaxConcurrency must be >= 1 when set (got 0)."]}}"""));
        using var client = new NodeControlClient(new NodeEndpoint(new Uri("http://127.0.0.1:5081"), "tok", "test"), handler);

        var refused = await Assert.ThrowsAsync<NodeControlException>(() =>
            client.SaveSettingsAsync(new Dictionary<string, JsonNode?> { ["Node:MaxConcurrency"] = 0 }));

        Assert.Contains("MaxConcurrency must be >= 1", refused.Message);
        Assert.Equal("""{"set":{"Node:MaxConcurrency":0},"remove":[]}""", handler.LastBody);
    }

    [Fact]
    public async Task ATokenPastedInTheAppsSettingsIsTriedFirst()
    {
        var tried = new List<string>();
        var locator = new NodeLocator((endpoint, _) =>
        {
            tried.Add(endpoint.Source);
            return Task.FromResult(endpoint.Token == "pasted");
        });

        var found = await locator.LocateAsync(new DesktopSettings { Url = "http://127.0.0.1:6000", Token = "pasted" });

        Assert.Equal("pasted", found.Endpoint!.Token);
        Assert.Equal(new Uri("http://127.0.0.1:6000"), found.Endpoint.BaseUrl);
        Assert.Equal(["the token in this app's settings"], tried);
    }

    [Fact]
    public async Task TheAppsOwnNodesTokenIsFoundWhereItsNodeWritesIt()
    {
        var home = Directory.CreateTempSubdirectory("inferhub-desktop-").FullName;
        Environment.SetEnvironmentVariable("INFERHUB_DESKTOP_HOME", home);

        try
        {
            Directory.CreateDirectory(AppPaths.NodeData);
            File.WriteAllText(Path.Combine(AppPaths.NodeData, "control.token"), "own-token\n");
            var locator = new NodeLocator((endpoint, _) => Task.FromResult(endpoint.Token == "own-token"));

            var found = await locator.LocateAsync(new DesktopSettings());

            Assert.Equal("this app's own node", found.Endpoint!.Source);
            Assert.Equal(new Uri(NodeLocator.DefaultUrl), found.Endpoint.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFERHUB_DESKTOP_HOME", null);
        }
    }

    [Fact]
    public void TheAppsOwnNodeStartsOnLoopbackWithItsDataAndSettingsInAppData()
    {
        var node = new EmbeddedNode(executable: Path.Combine(Path.GetTempPath(), "InferHub.Node.exe"), dataDirectory: "/data/node", port: 5999);

        var info = node.StartInfo();

        Assert.Equal("true", info.Environment["LocalApi__Enabled"]);
        Assert.Equal("http://127.0.0.1:5999", info.Environment["LocalApi__Urls"]);
        Assert.Equal("/data/node", info.Environment["Node__DataDirectory"]);
        Assert.Equal(Path.Combine("/data/node", "node.local.json"), info.Environment["Node__LocalSettingsFile"]);
        Assert.Equal("desktop", info.Environment["INFERHUB_SUPERVISOR"]);
        // A default only: the node reads node.local.json after the environment, so "join a network" there wins.
        Assert.Equal("false", info.Environment["Coordinator__Enabled"]);
    }

    [Fact]
    public void AFreePortIsPickedWhenTheNodesOwnIsTaken()
    {
        using var held = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        held.Start();
        var taken = ((IPEndPoint)held.LocalEndpoint).Port;

        Assert.NotEqual(taken, EmbeddedNode.PickPort(taken));
    }

    [Fact]
    public void TheAppsSettingsRoundTripAndABrokenFileIsReportedNotThrown()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("inferhub-desktop-").FullName, "settings.json");
        new DesktopSettings { Mode = "attach", Url = "http://box:5081", Token = "t", CloseToTray = false }.Save(path);

        var loaded = DesktopSettings.Load(path);
        Assert.Equal(("attach", "http://box:5081", "t", false), (loaded.Mode, loaded.Url, loaded.Token, loaded.CloseToTray));

        File.WriteAllText(path, "{ not json");
        Assert.NotNull(DesktopSettings.Load(path).LoadError);
    }

    private sealed class Scripted(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, body) = answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
