using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Shared.CloudMedia;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 104, the coordinator's half: a hub with <b>no node at all</b> and two providers carrying
/// <c>Media</c> maps, serving pictures, voices and transcripts through the shipped routes — real
/// Kestrel, real router, real job registry — with <see cref="FakeMediaVendor"/> as OpenAI and Google.
/// </summary>
public sealed class CloudMediaProviderTests : IAsyncLifetime
{
    private const string OpenAiKey = "sk-openai-media-test-0123456789";
    private const string GoogleKey = "AIza-google-media-test-0123456789";
    private const string Prompt = "a red lighthouse on a cliff at dusk";

    private readonly FakeMediaVendor vendor = new();
    private readonly RecordingLedger ledger = new();
    private readonly CapturingLoggerProvider logs = new();
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        var admission = new AdmissionControl();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<INodeRegistry>(new NodeRegistry());
        builder.Services.AddSingleton<IRouter, Router>();
        builder.Services.AddSingleton<IConversationAffinity>(
            new ConversationAffinity(Options.Create(new RouterOptions()), new NoAffinity(), TimeProvider.System));
        builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<ThroughputTracker>();
        builder.Services.AddSingleton<IUsageLedger>(ledger);
        builder.Services.AddSingleton(admission);
        builder.Services.AddSingleton(services => new UsageMeter(ledger, admission, services.GetRequiredService<ILogger<UsageMeter>>()));
        builder.Services.AddSingleton(services => TestUsage.Queue(services.GetRequiredService<INodeRegistry>()));
        builder.Services.Configure<DispatcherOptions>(_ => { });
        builder.Services.Configure<RouterOptions>(_ => { });
        builder.Services.Configure<ToolEdgeOptions>(_ => { });
        builder.Services.Configure<InferHub.Shared.Images.ImageEdgeOptions>(_ => { });
        builder.Services.AddSingleton<Dispatcher>();
        builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<InferHub.Shared.Images.IImageJobArchive>(sp =>
            InferHub.Shared.Images.ImageJobArchives.Create(
                sp.GetRequiredService<IOptions<InferHub.Shared.Images.ImageEdgeOptions>>().Value.Jobs));
        builder.Services.AddSingleton<ImageJobRegistry>();
        builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
            InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

        builder.Services.AddSingleton(Options.Create(Providers()));
        builder.Services.AddSingleton(Options.Create(new FallbackOptions()));
        builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        builder.Services.AddHttpClient(MediaProviders.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => vendor.Borrowed());
        builder.Services.AddSingleton<MediaProviders>();

        app = builder.Build();

        var resolved = new ResolvedClient("media-client", null, null);
        app.Use(async (context, next) =>
        {
            context.Items[BearerApiKeyMiddleware.ClientItemKey] = resolved;
            await next();
        });

        app.MapAudioEndpoints();
        app.MapImageEndpoints();
        app.MapImageJobEndpoints();
        app.MapToolEndpoints();
        app.MapOpenAiEndpoints();

        await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        vendor.Dispose();
    }

    internal static ProviderOptions Providers()
    {
        var options = new ProviderOptions();

        options.Entries["openai"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeOpenAiCompatible,
            BaseUrl = "https://api.openai.test/v1",
            ApiKey = OpenAiKey,
            Media = new(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = new() { ["gpt-image"] = "gpt-image-1.5" },
                ["speak"] = new() { ["tts"] = "gpt-4o-mini-tts" },
                ["transcribe"] = new() { ["stt"] = "whisper-1" }
            }
        };

        options.Entries["google"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeGemini,
            BaseUrl = "https://gemini.test/v1beta",
            ApiKey = GoogleKey,
            Media = new(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = new() { ["imagen"] = "imagen-4.0-generate-001" },
                ["speak"] = new() { ["gemini-tts"] = "gemini-2.5-flash-preview-tts" }
            }
        };

        return options;
    }

    [Fact]
    public async Task APictureFromACloudProviderIsTheImagesEnvelopeAndIsMetered()
    {
        var response = await client.PostAsJsonAsync("/v1/images/generations", new { model = "gpt-image", prompt = Prompt, size = "1024x1024" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        Assert.Equal("provider:openai", response.Headers.GetValues("X-InferHub-Served-By").Single());

        using var envelope = JsonDocument.Parse(body);
        var png = Convert.FromBase64String(envelope.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()!);
        Assert.Equal(FakeMediaVendor.Png(1024, 1024), png);

        Assert.Equal("/v1/images/generations", Assert.Single(vendor.Requests).Path);
        await ledger.WaitForAsync(record => record.Model == "gpt-image" && record.UnitKind == UsageUnitKinds.MegapixelSteps && record.Units > 1);
    }

    [Fact]
    public async Task AVoiceAndATranscriptComeFromTheirProvidersAndAreMeteredInTheirUnits()
    {
        var speech = await client.PostAsJsonAsync("/v1/audio/speech", new { model = "gemini-tts", input = "hello there", response_format = "wav" });
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal("provider:google", speech.Headers.GetValues("X-InferHub-Served-By").Single());
        Assert.Equal("audio/wav", speech.Content.Headers.ContentType?.MediaType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString((await speech.Content.ReadAsByteArrayAsync())[..4]));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("stt"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        var file = new ByteArrayContent(FakeMediaVendor.Wav(1));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "my-private-meeting.wav");

        var transcription = await client.PostAsync("/v1/audio/transcriptions", form);
        var text = await transcription.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, transcription.StatusCode);
        Assert.Contains("the quick brown fox", text);
        Assert.Equal("provider:openai", transcription.Headers.GetValues("X-InferHub-Served-By").Single());

        await ledger.WaitForAsync(record => record.Model == "gemini-tts" && record.UnitKind == UsageUnitKinds.Characters && record.Units == 11);
        await ledger.WaitForAsync(record => record.Model == "stt" && record.UnitKind == UsageUnitKinds.AudioSeconds && record.Units == 2.5);

        // The caller's filename never reaches the vendor (42 D5).
        Assert.DoesNotContain(vendor.Requests, request => request.Text.Contains("my-private-meeting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStreamedCloudVoiceIsA400BeforeAnythingIsSent()
    {
        var response = await client.PostAsJsonAsync("/v1/audio/speech", new { model = "tts", input = "hi", response_format = "pcm", stream_format = "audio" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("answered once", await response.Content.ReadAsStringAsync());
        Assert.Empty(vendor.Requests);
    }

    [Fact]
    public async Task TheGenericToolRouteReachesACloudProviderToo()
    {
        var response = await client.PostAsJsonAsync("/api/tools/speak", new { model = "tts", input = "hi", response_format = "mp3" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("provider:openai", response.Headers.GetValues("X-InferHub-Served-By").Single());
        Assert.Equal(FakeMediaVendor.Mp3, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AModelNoProviderClaimsIsStillThe404AndAVendorFailureIsItsOwnStatus()
    {
        var unknown = await client.PostAsJsonAsync("/v1/audio/speech", new { model = "nobody-has-this", input = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        vendor.Fail = _ => (HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""", 9);
        var limited = await client.PostAsJsonAsync("/v1/audio/speech", new { model = "tts", input = "hi", response_format = "mp3" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, limited.StatusCode);
        Assert.Equal("9", limited.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.Contains("slow down", await limited.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ModelsListsTheCloudModelsWithTheKindsTheyServe()
    {
        var models = await client.GetFromJsonAsync<JsonElement>("/v1/models");
        var byId = models.GetProperty("data").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!);

        Assert.Equal(["image"], byId["imagen"].GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(["transcribe"], byId["stt"].GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToArray());
    }

    [Fact]
    public async Task NoKeyNoPromptAndNoFilenameReachesAnyLogLine()
    {
        await client.PostAsJsonAsync("/v1/images/generations", new { model = "imagen", prompt = Prompt, size = "1024x1024" });
        await client.PostAsJsonAsync("/v1/audio/speech", new { model = "tts", input = Prompt, response_format = "mp3" });

        Assert.NotEmpty(logs.Lines);
        Assert.False(logs.Contains(Prompt), "the prompt reached a log line");
        Assert.False(logs.Contains(OpenAiKey), "the OpenAI key reached a log line");
        Assert.False(logs.Contains(GoogleKey), "the Google key reached a log line");
        Assert.True(logs.Contains("provider google"), "the provider that served it should be logged");
    }

    // ---- the policy and the validator, without a socket -------------------------------------------

    [Fact]
    public void TheDefaultPolicyLeavesThePairToANodeAndPreferAndOnlyTakeIt()
    {
        MediaProviders With(string? policy)
        {
            var options = Providers();
            options.Entries["openai"].Policy = policy;
            return new MediaProviders(Options.Create(options), new StubFactory(), new Metrics(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaProviders>.Instance);
        }

        Assert.Null(With(null).Choose("speak", "tts", nodeAvailable: true));
        Assert.NotNull(With(null).Choose("speak", "tts", nodeAvailable: false));
        Assert.NotNull(With(ProviderPolicy.Prefer).Choose("speak", "tts", nodeAvailable: true));
        Assert.True(With(ProviderPolicy.Prefer).NodesAllowed("speak", "tts"));
        Assert.False(With(ProviderPolicy.Only).NodesAllowed("speak", "tts"));

        // Another provider's names are untouched by this one's policy.
        Assert.True(With(ProviderPolicy.Only).NodesAllowed("speak", "gemini-tts"));
        Assert.Null(With(null).Choose("image", "tts", nodeAvailable: false));
    }

    [Fact]
    public void MediaOnATypeWithoutAMediaDialectOrWithoutAKeyFailsStartup()
    {
        var options = Providers();
        options.Entries["plan"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeChatGpt,
            Media = new() { ["image"] = new() { ["plan-picture"] = "gpt-image-1.5" } }
        };
        options.Entries["google"].ApiKey = null;
        options.Entries["google"].Media["speak"]["tts"] = "dup";

        var result = new ProviderOptionsValidator(Options.Create(new FallbackOptions())).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Providers:plan:Media is set on a provider of type 'chatgpt'", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("Providers:google:ApiKey is required", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("'tts' for 'speak' is claimed by both", StringComparison.Ordinal));
    }

    [Fact]
    public void AHubWithoutMediaMapsValidatesAsBeforeAndChoosesNothing()
    {
        var options = new ProviderOptions();
        options.Entries["openai"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeOpenAiCompatible,
            BaseUrl = "https://api.openai.test/v1",
            ModelMap = new() { ["gpt"] = "gpt-5-mini" }
        };

        Assert.True(new ProviderOptionsValidator(Options.Create(new FallbackOptions())).Validate(null, options).Succeeded);

        var media = new MediaProviders(Options.Create(options), new StubFactory(), new Metrics(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaProviders>.Instance);
        Assert.True(media.Catalog.IsEmpty);
        Assert.Null(media.Choose("image", "gpt", nodeAvailable: false));
    }

    private sealed class RecordingLedger : IUsageLedger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<UsageRecord> records = new();

        public ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default)
        {
            records.Enqueue(record);
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<UsageAggregate>> QueryAsync(UsageQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UsageAggregate>>([]);

        public async Task WaitForAsync(Func<UsageRecord, bool> match)
        {
            for (var i = 0; i < 100 && !records.Any(match); i++)
            {
                await Task.Delay(20);
            }

            Assert.Contains(records, record => match(record));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoAffinity : IAffinityStore
    {
        public IReadOnlyCollection<PersistedAffinity> Load() => [];

        public void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed)
        {
        }

        public void Forget(string conversationKey)
        {
        }
    }
}
