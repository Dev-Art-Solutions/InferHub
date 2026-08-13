using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// Phase-53 D7: a solo node streams a large upload straight into its scratch directory, and a
/// client cannot tell it apart from the hub except where the difference is deliberate.
/// </summary>
/// <remarks>
/// <para>
/// The node's <c>LocalUploadPath</c> is a hand-copy of the coordinator's <c>UploadPath</c> —
/// phase-37 D6's line, because the multipart plumbing is ASP.NET and design rule 2 keeps ASP.NET
/// out of <c>InferHub.Shared</c>. That copy is this phase's parity risk, so it gets the treatment
/// every other hand-copy in this repo has: the same request at both hosts, compared.
/// </para>
/// <para>
/// <b>One difference is deliberate and asserted as such</b> — a solo node has nothing to route, so
/// it accepts fields after the file where the hub refuses them (D3). It is one-directional:
/// everything the hub accepts, solo accepts too.
/// </para>
/// </remarks>
[Collection("heavy-mesh")]
public class SoloUploadParityTests
{
    private const long Streamed = 256L * 1024 * 1024;
    private const long Buffered = 1024 * 1024;

    [Fact]
    public async Task ASoloNodeStreamsALargeUploadOntoDiskWithEveryByteIntact()
    {
        var (host, cleanup) = await SoloUpload.StartAsync();
        using var _ = cleanup;
        await using var solo = host;

        var payload = RandomBytes(16 * 1024 * 1024);
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var content = Multipart(payload, behaviour: "digest");
        var response = await solo.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var digested = document.RootElement.GetProperty("digested");

        Assert.Equal(1, digested.GetArrayLength());
        Assert.Equal(payload.LongLength, digested[0].GetProperty("bytes").GetInt64());
        Assert.Equal(expected, digested[0].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task TheRefusalSentenceAndStatusAreTheSameOnesTheHubProduces()
    {
        var (host, cleanup) = await SoloUpload.StartAsync(
            maxAttachmentBytes: 32 * 1024,
            maxStreamedBytes: 64 * 1024);
        using var _ = cleanup;
        await using var solo = host;

        // The same 256 KB the hub's twin uses, and the size matters: a client still writing a much
        // larger body when the refusal arrives sees a connection reset instead of the status, which
        // is the "best-effort past this point" the docs state rather than a difference between the
        // hosts. Comparing the two demands the same payload.
        using var content = Multipart(RandomBytes(256 * 1024), behaviour: "digest");
        var response = await solo.Client.PostAsync("/api/tools/echo", content);

        // Same status, same key named, as ToolUploadTests asserts of the hub. A client must not be
        // able to tell which host refused it.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Tools:MaxStreamedBytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithTheKeyUnsetASoloNodeRefusesExactlyAsItDidInV320()
    {
        var (host, cleanup) = await SoloUpload.StartAsync(maxAttachmentBytes: 1024, maxStreamedBytes: null);
        using var _ = cleanup;
        await using var solo = host;

        using var content = Multipart(RandomBytes(8192), behaviour: "digest");
        var response = await solo.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Tools:MaxAttachmentBytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASoloNodeAcceptsFieldsAfterTheFileWhereTheHubRefusesThem()
    {
        var (host, cleanup) = await SoloUpload.StartAsync();
        using var _ = cleanup;
        await using var solo = host;

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("echo"), "model");
        content.Add(new StringContent("digest"), "behaviour");
        content.Add(FilePart(RandomBytes(2 * 1024 * 1024)), "file", "big.bin");
        content.Add(new StringContent("bg"), "language");

        var response = await solo.Client.PostAsync("/api/tools/echo", content);

        // D7's deliberate asymmetry: with nothing to route, no decision had to be made before the
        // bytes, so there is nothing to refuse. The hub's 400 for the same request is asserted in
        // ToolUploadTests — the two live side by side on purpose.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static MultipartFormDataContent Multipart(byte[] payload, string behaviour)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("echo"), "model");
        content.Add(new StringContent(behaviour), "behaviour");
        content.Add(FilePart(payload), "file", "big.bin");
        return content;
    }

    private static ByteArrayContent FilePart(byte[] payload)
    {
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    private static byte[] RandomBytes(int bytes)
    {
        var payload = new byte[bytes];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    /// <summary>A solo node with the echo worker loaded and the streamed path configured.</summary>
    private static class SoloUpload
    {
        public static async Task<(SoloHost Host, IDisposable Cleanup)> StartAsync(
            long? maxAttachmentBytes = null,
            long? maxStreamedBytes = 256L * 1024 * 1024)
        {
            var manifests = new ToolWorkerFixture.TempDirectory("inferhub-upload-manifests");
            var scratch = new ToolWorkerFixture.TempDirectory("inferhub-upload-scratch");

            manifests.WriteManifest("echo.json", new
            {
                id = "echo",
                capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
                command = ToolWorkerFixture.Command(),
                requestTimeoutSeconds = 30,
                startTimeoutSeconds = 30
            });

            List<string> settings =
            [
                "--Tools:Enabled=true",
                "--Tools:Allowed:0=echo",
                $"--Tools:ManifestDirectory={manifests.Path}",
                $"--Tools:ScratchDirectory={scratch.Path}",
                "--Tools:QueueMaxWaitSeconds=5",
                $"--Tools:MaxAttachmentBytes={maxAttachmentBytes ?? Buffered}"
            ];

            if (maxStreamedBytes is { } streamed)
            {
                settings.Add($"--Tools:MaxStreamedBytes={streamed}");
            }

            var host = await SoloHost.StartAsync(settings: [.. settings]);

            return (host, new Cleanups(manifests, scratch));
        }

        private sealed class Cleanups(params IDisposable[] items) : IDisposable
        {
            public void Dispose()
            {
                foreach (var item in items)
                {
                    item.Dispose();
                }
            }
        }
    }
}
