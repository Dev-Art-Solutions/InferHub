using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Mcp;
using InferHub.Coordinator.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 154: users with roles, the hub as an MCP server, and the OAuth sign-in a connector needs.</summary>
public sealed class UsersAndMcpTests
{
    private const string Answer = """{"model":"llama3","message":{"role":"assistant","content":"Paris."},"done":true,"prompt_eval_count":5,"eval_count":2}""";
    private const string AdminKey = "admin-key-154";
    private const string ClientKey = "client-key-154";

    private static UserDirectory Directory(UserOptions? options = null, ApiKeyOptions? auth = null)
        => new(
            new TestMonitor<UserOptions>(options ?? new UserOptions { Enabled = true, Persistence = "none" }),
            NullLogger<UserDirectory>.Instance,
            auth is null ? null : new TestMonitor<ApiKeyOptions>(auth));

    [Fact]
    public void AKeyIsShownOnceKeptAsAHashAndStopsWorkingWhenItsUserCannotSignIn()
    {
        var users = Directory();
        var (alice, key) = users.Create("alice", "Alice", BuiltInRoles.Operator);

        Assert.StartsWith(UserDirectory.KeyPrefix, key);
        Assert.Equal(UserDirectory.Hash(key), alice.Keys.Single().Hash);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(users.All()));

        var signedIn = users.Resolve(key)!;
        Assert.Equal("alice", signedIn.Client.Id);
        Assert.True(signedIn.Can(Permissions.AdminRead));
        Assert.False(signedIn.Can(Permissions.AdminWrite));
        Assert.Null(users.Resolve(key + "x"));
        Assert.Null(users.Resolve("not-a-user-key"));

        users.Update("alice", new UserChange(Disabled: true));
        Assert.Null(users.Resolve(key));
        users.Update("alice", new UserChange(Disabled: false));
        Assert.NotNull(users.Resolve(key));

        var (second, secret) = users.AddKey("alice", "laptop");
        Assert.True(users.RemoveKey("alice", alice.Keys.Single().Id));
        Assert.Null(users.Resolve(key));
        Assert.Equal("alice", users.Resolve(secret)!.Client.Id);
        Assert.Equal("laptop", second.Name);
    }

    [Fact]
    public void RolesAreTheFourBuiltInPlusYoursAndABadOneStopsTheHub()
    {
        var options = new UserOptions { Enabled = true, Persistence = "none" };
        options.Roles["auditor"] = ["models", "admin.read"];
        var users = Directory(options);

        Assert.Equal(["viewer", "user", "operator", "admin", "auditor"], users.Roles().Keys.ToArray());
        var (_, key) = users.Create("eve", null, "auditor");
        Assert.True(users.Resolve(key)!.Can(Permissions.AdminRead));
        Assert.False(users.Resolve(key)!.Can(Permissions.Inference));
        Assert.Throws<UserDirectoryException>(() => users.Create("mallory", null, "superuser"));
        Assert.Throws<UserDirectoryException>(() => users.Create("Not A Name", null, "user"));

        var validator = new UserOptionsValidator();
        var redefined = new UserOptions();
        redefined.Roles["admin"] = ["models"];
        Assert.True(validator.Validate(null, redefined).Failed);
        var misspelt = new UserOptions();
        misspelt.Roles["ops"] = ["admin.reed"];
        Assert.True(validator.Validate(null, misspelt).Failed);
    }

    [Fact]
    public void AUserCannotTakeAnAuthClientsIdAndTheFileSurvivesARestart()
    {
        var auth = new ApiKeyOptions { Clients = [new ClientConfig { Id = "ci", Key = "k" }] };
        Assert.Equal(409, Assert.Throws<UserDirectoryException>(() => Directory(auth: auth).Create("ci", null, null)).Status);

        var directory = Path.Combine(Path.GetTempPath(), "inferhub-users-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new UserOptions { Enabled = true, Persistence = "file", DataDirectory = directory };
            var (_, key) = Directory(options).Create("bob", "Bob", "viewer");
            Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(directory, "users.json")));

            var reloaded = Directory(options);
            Assert.Equal("bob", reloaded.Resolve(key)!.Client.Id);
            Assert.Equal("viewer", reloaded.Get("bob")!.Role);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ARoleDecidesHowFarAKeyGoesOnTheClientAndAdminSurfaces()
    {
        await using var hub = await Hub();
        var users = hub.Users;
        var (_, viewer) = users.Create("vic", null, "viewer");
        var (_, user) = users.Create("uma", null, "user");
        var (_, op) = users.Create("oli", null, "operator");
        var (_, admin) = users.Create("ada", null, "admin");

        Assert.Equal(HttpStatusCode.OK, (await Send(hub.Http, HttpMethod.Get, "/v1/models", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Chat(hub.Http, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Chat(hub.Http, user)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Chat(hub.Http, "ihu_nobody")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(hub.Http, HttpMethod.Get, "/api/admin/probe", user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(hub.Http, HttpMethod.Get, "/api/admin/probe", op)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(hub.Http, HttpMethod.Post, "/api/admin/probe", op)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(hub.Http, HttpMethod.Get, "/api/admin/users", op)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(hub.Http, HttpMethod.Post, "/api/admin/probe", admin)).StatusCode);

        // An admin user makes a user over HTTP; the key is in that one answer and nowhere after.
        var made = await Send(hub.Http, HttpMethod.Post, "/api/admin/users", admin, new { id = "nia", role = "user" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var key = (await made.Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await Chat(hub.Http, key)).StatusCode);
        var listing = await (await Send(hub.Http, HttpMethod.Get, "/api/admin/users", AdminKey)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, listing);
        Assert.Contains("\"nia\"", listing);
    }

    [Fact]
    public async Task McpSpeaksJsonRpcOverStreamableHttpAndOffersToolsByRole()
    {
        await using var hub = await Hub();
        var (_, viewer) = hub.Users.Create("vic", null, "viewer");
        var (_, user) = hub.Users.Create("uma", null, "user");

        var unauthenticated = await Rpc(hub.Http, null, "initialize", new JsonObject());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Contains("resource_metadata=", unauthenticated.Headers.WwwAuthenticate.ToString());

        var init = await Rpc(hub.Http, user, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" }
        });
        var initBody = await Body(init);
        Assert.Equal("2025-06-18", initBody["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(initBody["result"]!["capabilities"]!["tools"]);
        var session = init.Headers.GetValues(McpEndpoints.SessionHeader).Single();

        Assert.Equal(HttpStatusCode.Accepted, (await Rpc(hub.Http, user, "notifications/initialized", null, session, notification: true)).StatusCode);

        var userTools = Names(await Body(await Rpc(hub.Http, user, "tools/list", new JsonObject(), session)));
        Assert.Equal(["list_models", "chat", "embed"], userTools);
        var viewerTools = Names(await Body(await Rpc(hub.Http, viewer, "tools/list", new JsonObject())));
        Assert.Equal(["list_models"], viewerTools);

        // A session is bound to the caller who opened it.
        Assert.Equal(HttpStatusCode.NotFound, (await Rpc(hub.Http, viewer, "tools/list", new JsonObject(), session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Rpc(hub.Http, user, "tools/list", new JsonObject(), "deadbeef")).StatusCode);

        var models = await Body(await Call(hub.Http, viewer, "list_models", new JsonObject()));
        var model = models["result"]!["structuredContent"]!["models"]!.AsArray().Single()!;
        Assert.Equal("llama3", model["name"]!.GetValue<string>());
        Assert.Equal("node", model["source"]!.GetValue<string>());

        // chat is the same dispatch /api/chat runs: an Ollama body, not streamed, reaching the node.
        var chat = await Body(await Call(hub.Http, user, "chat", new JsonObject { ["model"] = "llama3", ["prompt"] = "Capital of France?", ["system"] = "Be brief.", ["max_tokens"] = 20 }));
        Assert.Equal("Paris.", chat["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(2, chat["result"]!["structuredContent"]!["completion_tokens"]!.GetValue<int>());
        var job = JsonNode.Parse(hub.Host.LastJobJson!)!;
        Assert.False(job["stream"]!.GetValue<bool>());
        Assert.Equal("system", job["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(20, job["options"]!["num_predict"]!.GetValue<int>());

        // A tool the role does not hold is absent, not forbidden; a viewer cannot chat.
        var refused = await Body(await Call(hub.Http, viewer, "chat", new JsonObject { ["model"] = "llama3", ["prompt"] = "hi" }));
        Assert.Equal(-32602, refused["error"]!["code"]!.GetValue<int>());

        // A tool error is a result the model can read.
        var missing = await Body(await Call(hub.Http, user, "chat", new JsonObject { ["prompt"] = "hi" }));
        Assert.True(missing["result"]!["isError"]!.GetValue<bool>());

        var embed = await Body(await Call(hub.Http, user, "embed", new JsonObject { ["model"] = "nomic", ["input"] = "hello" }));
        Assert.Equal(3, embed["result"]!["structuredContent"]!["dimensions"]!.GetValue<int>());

        var unknown = await Body(await Rpc(hub.Http, user, "resources/list", new JsonObject()));
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(hub.Http, HttpMethod.Get, "/mcp", user)).StatusCode);

        var foreign = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Rpcs("ping", new JsonObject()) };
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user);
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await hub.Http.SendAsync(foreign)).StatusCode);

        var end = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        end.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user);
        end.Headers.Add(McpEndpoints.SessionHeader, session);
        Assert.Equal(HttpStatusCode.NoContent, (await hub.Http.SendAsync(end)).StatusCode);
    }

    [Fact]
    public async Task EveryCredentialTheHubAcceptsWorksOnMcpWithTheSamePermissions()
    {
        await using var hub = await Hub();

        // An Auth:Clients key runs models; an admin key reads the fleet and never runs a model (the root CLAUDE.md table).
        Assert.Equal(["list_models", "chat", "embed"], Names(await Body(await Rpc(hub.Http, ClientKey, "tools/list", new JsonObject()))));
        Assert.Equal(["list_models", "fleet_status"], Names(await Body(await Rpc(hub.Http, AdminKey, "tools/list", new JsonObject()))));

        var fleet = await Body(await Call(hub.Http, AdminKey, "fleet_status", new JsonObject()));
        Assert.Equal("parity-node", fleet["result"]!["structuredContent"]!["nodes"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AConnectorSignsInWithOAuthAndAUserKeyThenUsesMcpUntilTheUserIsDisabled()
    {
        await using var hub = await Hub();
        var (_, key) = hub.Users.Create("uma", null, "user");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = hub.Http.BaseAddress };
        var resource = hub.Http.BaseAddress!.ToString().TrimEnd('/') + "/mcp";

        var metadata = await http.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(resource, metadata!["resource"]!.GetValue<string>());
        var server = await http.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server");
        Assert.Equal("S256", server!["code_challenge_methods_supported"]![0]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { "http://evil.example/cb" } })).StatusCode);
        var registered = await (await http.PostAsJsonAsync("/oauth/register", new
        {
            redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" },
            client_name = "Claude"
        })).Content.ReadFromJsonAsync<JsonObject>();
        var clientId = registered!["client_id"]!.GetValue<string>();

        const string verifier = "a-verifier-that-is-long-enough-for-pkce-0123456789";
        var query = $"response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString("https://claude.ai/api/mcp/auth_callback")}"
                    + $"&code_challenge={OAuthStore.Challenge(verifier)}&code_challenge_method=S256&state=xyz&resource={Uri.EscapeDataString(resource)}";
        var page = await http.GetAsync("/oauth/authorize?" + query);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Claude", await page.Content.ReadAsStringAsync());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());

        // No PKCE: back to the (registered) client with the error, never a code.
        var noPkce = await http.GetAsync($"/oauth/authorize?response_type=code&client_id={clientId}&state=s");
        Assert.Contains("error=invalid_request", noPkce.Headers.Location!.ToString());

        var form = new Dictionary<string, string>(query.Split('&').Select(p => p.Split('=', 2)).Select(p => KeyValuePair.Create(p[0], Uri.UnescapeDataString(p[1]))))
        {
            ["decision"] = "allow",
            ["key"] = "ihu_wrong"
        };
        Assert.Contains("not a key", await (await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync());

        form["key"] = key;
        var redirect = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var back = redirect.Headers.Location!.ToString();
        Assert.StartsWith("https://claude.ai/api/mcp/auth_callback?", back);
        Assert.Contains("&iss=", back);
        Assert.Contains("state=xyz", back);
        var code = Uri.UnescapeDataString(back.Split("code=")[1].Split('&')[0]);

        async Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> fields)
            => await http.PostAsync("/oauth/token", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["redirect_uri"] = "https://claude.ai/api/mcp/auth_callback", ["code_verifier"] = "wrong" })).StatusCode);

        // The failed attempt used the code up: single use, whatever the outcome. Authorize again.
        var again = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));
        code = Uri.UnescapeDataString(again.Headers.Location!.ToString().Split("code=")[1].Split('&')[0]);
        var tokens = await (await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["redirect_uri"] = "https://claude.ai/api/mcp/auth_callback", ["code_verifier"] = verifier, ["resource"] = resource })).Content.ReadFromJsonAsync<JsonObject>();
        var access = tokens!["access_token"]!.GetValue<string>();
        var refresh = tokens["refresh_token"]!.GetValue<string>();

        Assert.Equal(["list_models", "chat", "embed"], Names(await Body(await Rpc(hub.Http, access, "tools/list", new JsonObject()))));
        // An MCP token is for /mcp only.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Chat(hub.Http, access)).StatusCode);

        var rotated = await (await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(rotated!["access_token"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId })).StatusCode);
        Assert.Equal(1, hub.Host.Services.GetRequiredService<OAuthStore>().SessionsOf("uma"));

        hub.Users.Update("uma", new UserChange(Disabled: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(hub.Http, rotated["access_token"]!.GetValue<string>(), "tools/list", new JsonObject())).StatusCode);
    }

    // ---- the rig ------------------------------------------------------------------------------------------------------------

    private sealed class Rig(HubHost host) : IAsyncDisposable
    {
        public HubHost Host { get; } = host;
        public HttpClient Http => Host.Client;
        public UserDirectory Users => Host.Services.GetRequiredService<UserDirectory>();
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> Hub()
    {
        var host = await HubHost.StartAsync(
            Answer,
            configure: builder =>
            {
                builder.Services.Configure<ApiKeyOptions>(options =>
                {
                    options.RequireAuthForLoopback = true;
                    options.AdminApiKeys = [AdminKey];
                    options.Clients = [new ClientConfig { Id = "ci", Key = ClientKey }];
                });
                builder.Services.Configure<InferHub.Coordinator.Observability.MetricsOptions>(_ => { });
                builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
                builder.Services.Configure<UserOptions>(options => { options.Enabled = true; options.Persistence = "none"; });
                builder.Services.Configure<McpOptions>(options => options.Enabled = true);
                builder.Services.AddSingleton<UserDirectory>();
                builder.Services.AddSingleton<OAuthStore>();
                builder.Services.AddSingleton<McpSessions>();
                builder.Services.AddSingleton<McpTools>();
            },
            map: app =>
            {
                app.UseMiddleware<AdminApiKeyMiddleware>();
                app.UseMiddleware<BearerApiKeyMiddleware>();
                app.MapUserEndpoints();
                app.MapMcpEndpoints();
                app.MapOAuthEndpoints();
                app.MapGet("/api/admin/probe", () => "read");
                app.MapPost("/api/admin/probe", () => "written");
            });
        return new Rig(host);
    }

    private static Task<HttpResponseMessage> Chat(HttpClient http, string key)
        => Send(http, HttpMethod.Post, "/v1/chat/completions", key, new { model = "llama3", messages = new[] { new { role = "user", content = "hi" } } });

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return http.SendAsync(request);
    }

    private static StringContent Rpcs(string method, JsonObject? parameters, bool notification = false)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification)
        {
            message["id"] = 1;
        }

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static Task<HttpResponseMessage> Rpc(HttpClient http, string? key, string method, JsonObject? parameters, string? session = null, bool notification = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Rpcs(method, parameters, notification) };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (session is not null)
        {
            request.Headers.Add(McpEndpoints.SessionHeader, session);
        }

        return http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Call(HttpClient http, string key, string tool, JsonObject arguments)
        => Rpc(http, key, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });

    private static async Task<JsonObject> Body(HttpResponseMessage response)
        => (JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject)!;

    private static string[] Names(JsonObject body)
        => body["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();

    private sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
