using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Node.Tools;
using InferHub.Shared.CloudMedia;
using Microsoft.Extensions.DependencyInjection;

namespace InferHub.Tests;

/// <summary>
/// Phase 105, the node's half: a solo node with no GPU and a <c>CloudMedia</c> provider mapping
/// <c>video</c>, rendering a clip from <see cref="FakeMediaVendor"/>'s Veo through its own
/// <c>/v1/videos</c> — the composition root, the job runner and the executor all real.
/// </summary>
public sealed class CloudVideoNodeTests : IAsyncLifetime
{
    private const string Key = "AIza-node-cloud-video-0123456789";

    private readonly FakeMediaVendor vendor = new();
    private SoloHost host = null!;

    public async Task InitializeAsync()
    {
        host = await SoloHost.StartAsync(
            backend: null,
            configure: builder =>
            {
                builder.Services
                    .AddHttpClient(CloudMediaRuntime.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => vendor.Borrowed());
                builder.Services.AddSingleton(new CloudMediaTiming(TimeSpan.FromMilliseconds(10)));
            },
            "--CloudMedia:Providers:google:Type=gemini",
            "--CloudMedia:Providers:google:BaseUrl=https://gemini.test/v1beta",
            $"--CloudMedia:Providers:google:ApiKey={Key}",
            "--CloudMedia:Providers:google:Media:video:veo=veo-3.1-generate-preview");
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        vendor.Dispose();
    }

    [Fact]
    public async Task ASoloNodeRendersAClipFromItsOwnKeyThroughItsOwnVideosRoute()
    {
        var declared = host.Services.GetRequiredService<ToolExecutor>().ToolCapabilities.Single(c => c.Kind == "video");
        Assert.Equal(["veo"], declared.Models);

        using var created = await host.Client.PostAsJsonAsync("/v1/videos", new { model = "veo", prompt = "waves on rocks", seconds = "4" });
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.OK, createdBody);
        var id = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString()!;

        JsonElement video = default;

        for (var i = 0; i < 400; i++)
        {
            video = await host.Client.GetFromJsonAsync<JsonElement>($"/v1/videos/{id}");

            if (video.GetProperty("status").GetString() is "completed" or "failed")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Equal("completed", video.GetProperty("status").GetString());

        using var content = await host.Client.GetAsync($"/v1/videos/{id}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(FakeMediaVendor.Mp4(8), await content.Content.ReadAsByteArrayAsync());

        var submit = vendor.Requests.First(r => r.Path.EndsWith(":predictLongRunning", StringComparison.Ordinal));
        Assert.Equal("4", submit.Json.GetProperty("parameters").GetProperty("durationSeconds").GetString());
        Assert.Equal(Key, submit.Headers["x-goog-api-key"]);
    }
}
