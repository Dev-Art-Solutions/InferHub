using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.ChatGpt;
using InferHub.Shared.ChatGpt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 103, the node's half: <c>Backend:Type=chatgpt</c> and the <c>chatgpt</c> engine compose the
/// node's own session, keep it in the node's own directory, and report only model names.
/// </summary>
public sealed class ChatGptBackendTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "inferhub-node-chatgpt-" + Guid.NewGuid().ToString("N"));
    private readonly FakeOpenAi openAi = new();

    public void Dispose()
    {
        openAi.Dispose();

        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void AChatGptNodeComposesItsOwnBackendAndLeavesUpstreamAlone()
    {
        using var host = BuildNode(("Backend:Type", "chatgpt"));

        Assert.IsType<ChatGptBackend>(host.Services.GetRequiredService<IInferenceBackend>());
        Assert.Equal(Path.Combine(dataDirectory, "chatgpt"), host.Services.GetRequiredService<NodeChatGpt>().Directory);
    }

    [Fact]
    public void AnOpenAiNodeWithAKeyIsStillTheUpstreamBackend()
    {
        using var host = BuildNode(("Backend:Type", "openai"), ("Upstream:BaseUrl", "https://api.openai.com/v1"), ("Upstream:ApiKey", "sk-test"));

        Assert.IsType<UpstreamBackend>(host.Services.GetRequiredService<IInferenceBackend>());
    }

    [Fact]
    public void AChatGptEngineSitsBesideOllama()
    {
        Validate(new()
        {
            ["Backend:Engines:ollama:Type"] = "ollama",
            ["Backend:Engines:plan:Type"] = "chatgpt"
        });

        using var host = BuildNode(("Backend:Engines:ollama:Type", "ollama"), ("Backend:Engines:plan:Type", "chatgpt"));
        Assert.IsType<MultiBackend>(host.Services.GetRequiredService<IInferenceBackend>());
    }

    [Fact]
    public void AChatGptEngineTakesNoApiKeyAndOnlyOneIsAllowed()
    {
        var keyed = Assert.Throws<OptionsValidationException>(() => Validate(new()
        {
            ["Backend:Engines:plan:Type"] = "chatgpt",
            ["Backend:Engines:plan:ApiKey"] = "sk-test"
        }));
        Assert.Contains("takes no BaseUrl or ApiKey", keyed.Message);

        var two = Assert.Throws<OptionsValidationException>(() => Validate(new()
        {
            ["Backend:Engines:a:Type"] = "chatgpt",
            ["Backend:Engines:b:Type"] = "chatgpt"
        }));
        Assert.Contains("chatgpt engines", two.Message);
    }

    [Fact]
    public async Task ASignedOutNodeReportsNoModelsAndASignedInOneReportsItsAccountsSlugs()
    {
        using var host = BuildNode(("Backend:Type", "chatgpt"), ("ChatGpt:Models:Exclude:0", "gpt-6.1-mini"));
        var backend = host.Services.GetRequiredService<IInferenceBackend>();

        Assert.Empty((await backend.ListModelsAsync(CancellationToken.None))!);

        var session = host.Services.GetRequiredService<NodeChatGpt>().Session;
        var signIn = ChatGptBrowserSignIn.Begin(session.HostId(), session.Hints(null, false), 0);
        var grant = signIn.CompleteAsync(openAi.Client(), session.Validator, null, TimeSpan.FromSeconds(20), CancellationToken.None);
        using (var browser = new HttpClient())
        {
            await browser.GetStringAsync(openAi.Authorize(signIn.AuthorizeUrl));
        }

        await session.ApplySignInAsync(await grant, CancellationToken.None);

        var models = await backend.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["gpt-6.1-sol"], models!.Select(m => m.Name).ToArray());
        Assert.All(models!, m => Assert.Null(m.Digest));
    }

    [Fact]
    public async Task ASignedOutNodeRefusesWithA401RatherThanA502()
    {
        using var host = BuildNode(("Backend:Type", "chatgpt"));
        var executor = host.Services.GetRequiredService<InferenceExecutor>();

        var result = await executor.RunAsync(
            new InferHub.Shared.Contracts.InferenceJob(Guid.NewGuid(), "chat", """{"model":"gpt-6.1-sol","messages":[{"role":"user","content":"hi"}]}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(401, result.Status);
        Assert.Contains("Continue with ChatGPT", result.Error);
        Assert.Empty(openAi.Requests);
    }

    [Fact]
    public async Task TheVerbReadsTheSameStoreTheNodeDoes()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ChatGptCommands.RunAsync(["chatgpt", "status", "--data-dir", Path.Combine(dataDirectory, "chatgpt")], dataDirectory, null, output, error);

        Assert.Equal(0, code);
        Assert.Contains("Signed in:  no", output.ToString());
        Assert.Contains("urn:uuid:", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void TheNodesStoreIsBesideItsIdentityUnlessChatGptNamesItsOwn()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:DataDirectory"] = "/data"
        }).Build();

        Assert.Equal(Path.Combine(Path.GetFullPath("/data"), "chatgpt"), ChatGptNodeOptions.ResolveDirectory(configuration, "/app"));

        var own = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChatGpt:DataDirectory"] = "creds"
        }).Build();

        Assert.Equal(Path.GetFullPath("creds", Path.GetFullPath("/app")), ChatGptNodeOptions.ResolveDirectory(own, Path.GetFullPath("/app")));
    }

    private IHost BuildNode(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Coordinator:EnrollmentSecret"] = "test-secret",
            ["Ollama:Endpoint"] = "http://localhost:11434/",
            ["Node:Name"] = "test-node",
            ["Node:DataDirectory"] = dataDirectory
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddInferHubNode();

        // OpenAI is the fixture: the node's sign-in and its model listing both reach it.
        builder.Services.AddHttpClient(NodeChatGpt.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Borrowed(openAi));
        builder.Services.AddHttpClient(ChatGptBackend.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Borrowed(openAi));

        return builder.Build();
    }

    private static void Validate(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = configuration.GetSection(BackendOptions.SectionName).Get<BackendOptions>() ?? new BackendOptions();
        var result = new BackendOptionsValidator(configuration).Validate(null, options);

        if (result.Failed)
        {
            throw new OptionsValidationException("Backend", typeof(BackendOptions), result.Failures!);
        }
    }

    private sealed class Borrowed(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
