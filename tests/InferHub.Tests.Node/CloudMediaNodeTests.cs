using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.CloudMedia;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 104, the node's half (D8): a solo node with no tool runtime at all and a <c>CloudMedia</c>
/// section, declaring and serving pictures, voices and transcripts from <see cref="FakeMediaVendor"/>
/// through its own routes — the composition root, the validator and the executor all real.
/// </summary>
public sealed class CloudMediaNodeTests : IAsyncLifetime
{
    private const string Key = "sk-node-cloud-media-0123456789";

    private readonly FakeMediaVendor vendor = new();
    private SoloHost host = null!;

    private static readonly string[] Settings =
    [
        "--CloudMedia:Providers:openai:Type=openai",
        "--CloudMedia:Providers:openai:BaseUrl=https://api.openai.test/v1",
        $"--CloudMedia:Providers:openai:ApiKey={Key}",
        "--CloudMedia:Providers:openai:Media:speak:tts=gpt-4o-mini-tts",
        "--CloudMedia:Providers:openai:Media:transcribe:stt=whisper-1",
        "--CloudMedia:Providers:google:Type=gemini",
        "--CloudMedia:Providers:google:BaseUrl=https://gemini.test/v1beta",
        $"--CloudMedia:Providers:google:ApiKey={Key}",
        "--CloudMedia:Providers:google:Media:image:nano-banana=gemini-2.5-flash-image"
    ];

    public async Task InitializeAsync()
    {
        host = await SoloHost.StartAsync(
            backend: null,
            configure: builder => builder.Services
                .AddHttpClient(CloudMediaRuntime.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => vendor.Borrowed()),
            Settings);
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        vendor.Dispose();
    }

    [Fact]
    public async Task TheNodeDeclaresItsCloudModelsBesideItsOwn()
    {
        // Solo status lists the kinds; the models per kind are what a meshed node reports to the hub,
        // and that list is the executor's — asserted directly beside it.
        var status = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");
        var kinds = status.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToArray();

        Assert.Contains("speak", kinds);
        Assert.Contains("transcribe", kinds);
        Assert.Contains("image", kinds);

        var declared = host.Services.GetRequiredService<ToolExecutor>().ToolCapabilities
            .ToDictionary(c => c.Kind, c => c.Models.ToArray());

        Assert.Equal(["tts"], declared["speak"]);
        Assert.Equal(["stt"], declared["transcribe"]);
        Assert.Equal(["nano-banana"], declared["image"]);
    }

    [Fact]
    public async Task ASoloNodeServesAPictureAVoiceAndATranscriptFromItsOwnKeys()
    {
        var picture = await host.Client.PostAsJsonAsync("/v1/images/generations", new { model = "nano-banana", prompt = "a boat", size = "1024x1024" });
        var pictureBody = await picture.Content.ReadAsStringAsync();
        Assert.True(picture.StatusCode == HttpStatusCode.OK, pictureBody);
        Assert.Contains("b64_json", pictureBody);

        var voice = await host.Client.PostAsJsonAsync("/v1/audio/speech", new { model = "tts", input = "hello", response_format = "mp3" });
        Assert.Equal(HttpStatusCode.OK, voice.StatusCode);
        Assert.Equal(FakeMediaVendor.Mp3, await voice.Content.ReadAsByteArrayAsync());

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("stt"), "model");
        var file = new ByteArrayContent(FakeMediaVendor.Wav(1));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "clip.wav");

        var transcript = await host.Client.PostAsync("/v1/audio/transcriptions", form);
        Assert.Equal(HttpStatusCode.OK, transcript.StatusCode);
        Assert.Contains("the quick brown fox", await transcript.Content.ReadAsStringAsync());

        Assert.Contains(vendor.Requests, r => r.Path == "/v1beta/models/gemini-2.5-flash-image:generateContent" && r.Headers["x-goog-api-key"] == Key);
        Assert.Contains(vendor.Requests, r => r.Path == "/v1/audio/speech" && r.Headers["Authorization"] == $"Bearer {Key}");
    }

    [Fact]
    public async Task AModelTheNodeDoesNotMapIsStillRefusedAndNothingIsSent()
    {
        var response = await host.Client.PostAsJsonAsync("/v1/audio/speech", new { model = "not-mapped", input = "hello" });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(vendor.Requests);
    }

    [Theory]
    [InlineData("anthropic", "serves no pictures, voices or transcripts")]
    [InlineData("openai", "ApiKey is required")]
    public async Task ABadCloudMediaSectionRefusesToBoot(string type, string expected)
    {
        var failure = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=true",
            "--Coordinator:Enabled=false",
            $"--CloudMedia:Providers:x:Type={type}",
            "--CloudMedia:Providers:x:Media:speak:tts=tts-1");

        Assert.NotNull(failure);
        Assert.Contains(expected, failure.ToString());
    }

    [Fact]
    public async Task ACloudVoiceIsAnsweredOnceAndTheExecutorSaysSoOnAStream()
    {
        var options = Options.Create(new CloudMediaOptions());
        options.Value.Providers["openai"] = new CloudMediaProviderDefinition
        {
            Type = "openai",
            ApiKey = Key,
            Media = new() { ["speak"] = new() { ["tts"] = "tts-1" } }
        };

        var cloud = new CloudMediaRuntime(options, new StubFactory(), NullLogger<CloudMediaRuntime>.Instance);
        var executor = new ToolExecutor(new NoToolRuntime(), Options.Create(new ToolOptions()), NullLogger<ToolExecutor>.Instance, cloud: cloud);

        Assert.True(executor.Provides("speak", "tts"));
        Assert.Equal(["tts"], Assert.Single(executor.ToolCapabilities, c => c.Kind == "speak").Models);

        var chunks = new List<ToolChunk>();
        await foreach (var chunk in executor.StreamAsync(new ToolJob(Guid.NewGuid(), "speak", "tts", """{"input":"hi"}"""), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var terminal = Assert.Single(chunks);
        Assert.True(terminal.Done);
        Assert.Contains("answered once", terminal.Payload);
    }

    [Fact]
    public void AnEmptySectionDeclaresNothingAndServesNothing()
    {
        var cloud = new CloudMediaRuntime(Options.Create(new CloudMediaOptions()), new StubFactory(), NullLogger<CloudMediaRuntime>.Instance);
        var executor = new ToolExecutor(new NoToolRuntime(), Options.Create(new ToolOptions()), NullLogger<ToolExecutor>.Instance, cloud: cloud);

        Assert.Empty(cloud.Capabilities);
        Assert.Empty(executor.ToolCapabilities);
        Assert.False(executor.Provides("speak", "tts"));
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
