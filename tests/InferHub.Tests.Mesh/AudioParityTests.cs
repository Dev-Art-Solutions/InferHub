using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// The same audio requests, against a real Kestrel hub and a real Kestrel solo node, compared as a
/// client would see them.
/// </summary>
/// <remarks>
/// <para>
/// <c>SoloParityTests</c>' discipline (phase-37 D7), applied to the routes phase 42 adds. Both sides
/// drive the <b>same worker binary</b> as a real child process, so the only thing a difference can
/// be is ours: the status, the content type, the body, the <c>Retry-After</c>. Handler-level
/// comparison would prove the handlers agree and say nothing about the response, which is the lesson
/// <c>NodeHubStreamingTests</c> was written after.
/// </para>
/// <para>
/// The one header that legitimately differs is <c>X-InferHub-Served-By</c> — <c>node</c> against a
/// hub and <c>node-solo</c> against a standalone box — because it exists precisely to tell them
/// apart.
/// </para>
/// </remarks>
public class AudioParityTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("verbose_json")]
    public async Task EveryTranscriptionFormatIsIdenticalOnBothHosts(string format)
        => await CompareAsync(client => client.PostAsync("/v1/audio/transcriptions", Upload(format: format)));

    [Fact]
    public async Task SpeechIsIdenticalOnBothHosts()
        => await CompareAsync(client => client.PostAsync("/v1/audio/speech", Speech()));

    [Fact]
    public async Task AnUnproducibleFormatFailsIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsync("/v1/audio/speech", Speech(format: "mp3")));

    [Fact]
    public async Task AnInvalidRequestFailsIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsync(
            "/v1/audio/transcriptions",
            new MultipartFormDataContent { { new StringContent("x"), "model" } }));

    /// <summary>
    /// The guard on the guard, and it is not ceremony: without it, a comparison that silently
    /// stopped comparing anything would pass forever. Phase-37 D7 and phase-38 D9 both carry one.
    /// </summary>
    [Fact]
    public async Task TheComparisonActuallyDetectsADifference()
    {
        await using var mesh = await AudioMesh.StartAsync();
        var (solo, cleanup) = await AudioFixture.SoloAsync("--audio-no-segments");

        try
        {
            var hub = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "verbose_json"));
            var node = await solo.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "verbose_json"));

            // Same status, different body: the hub's worker returns segments and the node's was
            // started with --audio-no-segments. If the assertion below stopped reading the body,
            // this test would go green and every other one in the file would become decoration.
            Assert.Equal(hub.StatusCode, node.StatusCode);
            Assert.NotEqual(
                await hub.Content.ReadAsStringAsync(),
                await node.Content.ReadAsStringAsync());
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    private static async Task CompareAsync(Func<HttpClient, Task<HttpResponseMessage>> call)
    {
        await using var mesh = await AudioMesh.StartAsync();
        var (solo, cleanup) = await AudioFixture.SoloAsync();

        try
        {
            var hub = await call(mesh.Client);
            var node = await call(solo.Client);

            Assert.Equal(hub.StatusCode, node.StatusCode);

            Assert.Equal(
                hub.Content.Headers.ContentType?.ToString(),
                node.Content.Headers.ContentType?.ToString());

            Assert.Equal(
                hub.Headers.TryGetValues("Retry-After", out var hubRetry) ? hubRetry.Single() : null,
                node.Headers.TryGetValues("Retry-After", out var nodeRetry) ? nodeRetry.Single() : null);

            Assert.Equal(
                await hub.Content.ReadAsByteArrayAsync(),
                await node.Content.ReadAsByteArrayAsync());

            // Served-By is the one header that must *differ* where it is set at all: it exists to
            // tell a client which shape answered, and a solo node claiming to be a mesh node would
            // be the lie it prevents. A request refused before dispatch sets it on neither host —
            // and "one of them set it" is itself a difference worth failing on.
            var hubServedBy = hub.Headers.TryGetValues("X-InferHub-Served-By", out var h) ? h.Single() : null;
            var nodeServedBy = node.Headers.TryGetValues("X-InferHub-Served-By", out var n) ? n.Single() : null;

            Assert.Equal(hubServedBy is null, nodeServedBy is null);

            if (hubServedBy is not null)
            {
                Assert.Equal("node", hubServedBy);
                Assert.Equal("node-solo", nodeServedBy);
            }
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    private static MultipartFormDataContent Upload(string? format = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(AudioFixture.TranscribeModel), "model" },
            { new ByteArrayContent("not really audio"u8.ToArray()), "file", "meeting.m4a" }
        };

        if (format is not null)
        {
            form.Add(new StringContent(format), "response_format");
        }

        return form;
    }

    private static JsonContent Speech(string? format = null)
        => JsonContent.Create(new
        {
            model = AudioFixture.SpeakModel,
            input = "InferHub can talk now.",
            response_format = format ?? "wav"
        });
}
