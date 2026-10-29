using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>Phase 122: what a sealed node refuses to start with, and what it refuses at run time.</summary>
public class SealedModeTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Node:Sealed"] = "true" }
                .Concat(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))))
            .Build();

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("fd00::1", true)]
    [InlineData("ollama", true)]
    [InlineData("gpu-box.lan", true)]
    [InlineData("nas.home.arpa", true)]
    [InlineData("hub.internal", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("huggingface.co", false)]
    [InlineData("hub.example.com", false)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("*", false)]
    public void LocalIsAnAddressYouCanSeeIsNotTheInternet(string host, bool local)
        => Assert.Equal(local, SealedMode.IsLocalHost(host));

    [Fact]
    public void ADefaultMeshedOllamaNodeIsAlreadySealable()
    {
        Assert.Empty(SealedMode.Violations(Config()));
    }

    [Fact]
    public void EveryOpenDoorIsNamedAtOnce()
    {
        var doors = SealedMode.Violations(Config(
            ("Backend:Engines:gpt:Type", "chatgpt"),
            ("Backend:Engines:vllm:Type", "openai"),
            ("Backend:Engines:vllm:BaseUrl", "https://api.openai.com/v1"),
            ("Backend:Engines:local:Type", "openai"),
            ("Backend:Engines:local:BaseUrl", "http://192.168.1.50:8000/v1"),
            ("LocalApi:Enabled", "true"),
            ("LocalApi:Urls", "http://+:5081"),
            ("LocalApi:ApiKeys:0", "k"),
            ("Ollama:Supervisor:AutoInstall", "true"),
            ("Update:Check", "true"),
            ("Tools:AllowModelDownload", "true"),
            ("CloudMedia:Providers:openai:Type", "openai")));

        Assert.Equal(7, doors.Count);
        Assert.Contains(doors, d => d.StartsWith("Backend:Engines:gpt is chatgpt"));
        Assert.Contains(doors, d => d.StartsWith("Backend:Engines:vllm (openai) is at 'https://api.openai.com/v1'"));
        Assert.DoesNotContain(doors, d => d.Contains("Backend:Engines:local"));
        Assert.Contains(doors, d => d.StartsWith("LocalApi:Urls listens on 'http://+:5081'"));
        Assert.Contains(doors, d => d.StartsWith("Ollama:Supervisor:AutoInstall"));
        Assert.Contains(doors, d => d.StartsWith("Update:Check/Auto/AllowFromHub"));
        Assert.Contains(doors, d => d.StartsWith("Tools:AllowModelDownload"));
        Assert.Contains(doors, d => d.StartsWith("CloudMedia:Providers:openai"));
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("openrouter")]
    public void ACloudVendorBackendIsADoor(string type)
    {
        var door = Assert.Single(SealedMode.Violations(Config(("Backend:Type", type), ("Upstream:Models:0", "m"))));
        Assert.Contains("outside this network", door);
    }

    [Fact]
    public void AnOllamaOnAnotherContinentIsADoor()
    {
        var door = Assert.Single(SealedMode.Violations(Config(("Ollama:Endpoint", "https://ollama.example.com/"))));
        Assert.Contains("drives an Ollama at 'https://ollama.example.com/'", door);
        Assert.Empty(SealedMode.Violations(Config(("Ollama:Endpoint", "http://ollama:11434/"))));
    }

    [Fact]
    public void ALocalApiOnLoopbackOrOnePrivateAddressIsNot()
    {
        Assert.Empty(SealedMode.Violations(Config(("LocalApi:Enabled", "true"), ("LocalApi:Urls", "http://127.0.0.1:5081"))));
        Assert.Empty(SealedMode.Violations(Config(("LocalApi:Enabled", "true"), ("LocalApi:Urls", "http://192.168.1.20:5081"), ("LocalApi:ApiKeys:0", "k"))));
        Assert.Single(SealedMode.Violations(Config(("LocalApi:Enabled", "true"), ("LocalApi:Urls", "http://203.0.113.9:5081"), ("LocalApi:ApiKeys:0", "k"))));
    }

    [Fact]
    public void HuggingFaceNeedsAHubToFetchThroughOrAMirrorHere()
    {
        Assert.Empty(SealedMode.Violations(Config(("HuggingFace:Enabled", "true"))));

        var solo = Config(("HuggingFace:Enabled", "true"), ("Coordinator:Enabled", "false"), ("LocalApi:Enabled", "true"));
        Assert.Contains("no coordinator to fetch through", Assert.Single(SealedMode.Violations(solo)));

        var mirror = Config(("HuggingFace:Enabled", "true"), ("HuggingFace:Endpoint", "http://hf-mirror.lan"), ("Coordinator:Enabled", "false"), ("LocalApi:Enabled", "true"));
        Assert.Empty(SealedMode.Violations(mirror));
    }

    [Fact]
    public void UpdatesFromAFeedHereAreNotADoor()
    {
        Assert.Empty(SealedMode.Violations(Config(("Update:Check", "true"), ("Update:Source", "http://releases.internal/inferhub"))));
    }

    [Fact]
    public void ARemoteCorpusIsADoor()
    {
        var doors = SealedMode.Violations(Config(
            ("LocalApi:Enabled", "true"),
            ("LocalApi:Urls", "http://127.0.0.1:5081"),
            ("LocalApi:Retrieval:Enabled", "true"),
            ("LocalApi:Retrieval:Provider", "postgres"),
            ("LocalApi:Retrieval:Postgres:ConnectionString", "Host=db.example.com;Database=x")));

        Assert.Contains("db.example.com", Assert.Single(doors));
    }

    [Fact]
    public void TheValidatorIsSilentUnlessSealed()
    {
        var open = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Type"] = "chatgpt" })
            .Build();

        Assert.True(new SealedModeValidator(open).Validate(null, new NodeOptions()).Succeeded);
        Assert.False(new SealedModeValidator(Config(("Backend:Type", "chatgpt"))).Validate(null, new NodeOptions { Sealed = true }).Succeeded);
    }

    [Fact]
    public void TheRelayIsOnTheFirstCoordinatorUnderItsPathBase()
    {
        var egress = new SealedEgress(Config(("Coordinator:Url", "https://hub.example.com/inferhub"), ("Coordinator:EnrollmentSecret", "s")));

        Assert.Equal("https://hub.example.com/inferhub/node/relay/hf/", egress.RelayBase(NodeRelay.HuggingFace)!.ToString());
        Assert.True(egress.IsCoordinator(new Uri("https://hub.example.com/anything")));
        Assert.False(egress.IsCoordinator(new Uri("https://huggingface.co/")));
        Assert.Null(new SealedEgress(Config(("Coordinator:Enabled", "false"))).RelayBase(NodeRelay.HuggingFace));
    }

    [Fact]
    public async Task TheRelayCarriesTheSecretAndNothingElseLeaves()
    {
        var egress = new SealedEgress(Config(("Coordinator:Url", "https://hub.example.com/"), ("Coordinator:EnrollmentSecret", "s3cret")));
        var seen = new List<HttpRequestMessage>();
        using var client = new HttpClient(new SealedEgressHandler(egress) { InnerHandler = new Recording(seen) });

        await client.GetAsync("https://hub.example.com/node/relay/hf/api/models/a/b");
        await client.GetAsync("https://hub.example.com/health");
        await client.GetAsync("http://127.0.0.1:11434/api/tags");

        Assert.Equal("s3cret", Assert.Single(seen[0].Headers.GetValues(NodeRelay.SecretHeader)));
        Assert.False(seen[1].Headers.Contains(NodeRelay.SecretHeader));
        Assert.Equal(3, seen.Count);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://registry.ollama.ai/v2/"));
        Assert.Contains("registry.ollama.ai", refused.Message);
        Assert.Equal(3, seen.Count);
    }

    [Theory]
    [InlineData("qwen3:8b", "library", "qwen3", "8b")]
    [InlineData("llama3", "library", "llama3", "latest")]
    [InlineData("batiai/qwen3.8-27b:q6", "batiai", "qwen3.8-27b", "q6")]
    [InlineData("registry.ollama.ai/library/gemma:2b", "library", "gemma", "2b")]
    public void AnOllamaNameIsReadAsOllamaReadsIt(string model, string ns, string repo, string tag)
        => Assert.Equal((ns, repo, tag), SealedOllama.ParseName(model));

    [Theory]
    [InlineData("hf.co/bartowski/Tiny-GGUF:Q4_K_M")]
    [InlineData("gpt-oss:120b-cloud")]
    [InlineData("glm-5.2:cloud")]
    public void AnotherRegistryOrACloudModelIsNotPulled(string model)
        => Assert.Throws<InvalidOperationException>(() => SealedOllama.ParseName(model));

    [Fact]
    public void CloudModelsAreNotListedAndAreRefused()
    {
        var guard = new SealedOllama(new SealedEgress(Config()), new NoFactory(), NullLogger<SealedOllama>.Instance);
        using var tags = JsonDocument.Parse("""
            {"models":[
              {"name":"qwen2.5:0.5b","digest":"a","size":397821319},
              {"name":"glm-5.2:cloud","remote_model":"glm-5.2","remote_host":"https://ollama.com:443","size":338},
              {"name":"custom:big","remote_host":"https://ollama.com:443","size":1}
            ]}
            """);

        var kept = guard.KeepLocal(tags.RootElement);

        Assert.Equal(["qwen2.5:0.5b"], kept.Select(m => m.Name));
        guard.EnsureLocal("qwen2.5:0.5b");
        Assert.Throws<InvalidOperationException>(() => guard.EnsureLocal("custom:big"));
        Assert.Throws<InvalidOperationException>(() => guard.EnsureLocal("glm-5.2:cloud"));
        // Not pulled yet, and named as Ollama names its cloud models: refused before Ollama is asked.
        Assert.Throws<InvalidOperationException>(() => guard.EnsureLocal("deepseek-v4-pro:cloud"));
    }

    [Fact]
    public async Task ASoloSealedNodeHasNothingToPullThrough()
    {
        var guard = new SealedOllama(new SealedEgress(Config(("Coordinator:Enabled", "false"))), new NoFactory(), NullLogger<SealedOllama>.Instance);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in guard.PullAsync("qwen3:8b", new Uri("http://127.0.0.1:11434/"), CancellationToken.None))
            {
            }
        });

        Assert.Contains("no coordinator to pull 'qwen3:8b' through", refused.Message);
    }

    private sealed class Recording(List<HttpRequestMessage> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no HTTP in this test");
    }
}
