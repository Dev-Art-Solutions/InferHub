using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 53, across a real hub, a real SignalR wire and a real child process.
/// </summary>
/// <remarks>
/// <para>
/// It reuses <c>ToolMeshTests.ToolMesh</c> rather than standing up a second fixture, because the
/// thing under test is what the shipped composition does with a large body — and a fixture written
/// beside the feature would agree with the feature by construction.
/// </para>
/// <para>
/// <b>The payload is deliberately far past every buffered limit.</b> Phase 41 "proved" attachments
/// with a 16-byte file and phase 42 then tore the connection down with 300 KB; the lesson written
/// into <c>tests/CLAUDE.md</c> is that a wire test under the cap is decoration. These push
/// <b>40 MB</b> through a path whose whole claim is that it never holds the body — enough to be
/// far past Kestrel's 30 000 000-byte default, ASP.NET's 64 KB spill threshold and the 25 MB
/// attachment cap at once, and small enough that the suite still runs in CI.
/// </para>
/// </remarks>
[Collection("heavy-mesh")]
public class ToolUploadTests
{
    private const long Streamed = 256L * 1024 * 1024;

    /// <summary>A small buffered cap, so a test payload is genuinely on the streamed path.</summary>
    private const long Buffered = 1024 * 1024;
    /// <summary>
    /// Past all three real ceilings at once — Kestrel's 30 000 000-byte default, ASP.NET's 64 KB
    /// spill threshold and the 25 MB attachment cap — and no larger. It was 64 MB first, and two of
    /// these running beside the image suite starved a timing-sensitive queue test: a test that makes
    /// its point at 40 MB and costs another test its run is not worth the extra 24.
    /// </summary>
    private const int Big = 40 * 1024 * 1024;

    /// <summary>Enough to still be uploading when the client walks away, and nothing more.</summary>
    private const int Aborted = 16 * 1024 * 1024;

    [Fact]
    public async Task ABodyFarPastTheAttachmentCapReachesTheWorkerWithEveryByteIntact()
    {
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(
            maxAttachmentBytes: Buffered,
            maxStreamedBytes: Streamed);

        var payload = RandomBytes(Big);
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var content = Multipart(payload, model: "echo", behaviour: "digest");
        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The worker hashes what the node wrote and answers with the hash — so this is fidelity,
        // not a length. A path that dropped a 64 KB window and padded the file would produce the
        // right size and the wrong digest, which is the whole reason the echo worker grew this mode.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var digested = document.RootElement.GetProperty("digested");

        Assert.Equal(1, digested.GetArrayLength());
        Assert.Equal("file", digested[0].GetProperty("name").GetString());
        Assert.Equal(Big, digested[0].GetProperty("bytes").GetInt64());
        Assert.Equal(expected, digested[0].GetProperty("sha256").GetString());

        // Rule 7 and the phase's own point: the hub never materialised the body. The buffered path
        // spills anything over 64 KB into an ASPNETCORE_*.tmp file (measured — see the phase notes),
        // so a body that took that path would leave one the size of the upload.
        //
        // It asks about *size* rather than comparing the directory before and after, because the
        // temp directory is shared with every other test in this assembly and they run in parallel:
        // an exact comparison is a test that fails for reasons that have nothing to do with it.
        Assert.DoesNotContain(AspNetTempFiles(), file => Length(file) > Big / 4);

        // The v3.10.0 assertion: not "did we get a 200" but "is the node still there". Exceeding a
        // SignalR limit kills the connection rather than the message, and a 200 on the way out says
        // nothing about whether the fleet survived it.
        Assert.True(mesh.NodeIsRegistered());
    }

    [Fact]
    public async Task TheNodeEnforcesItsOwnCeilingEvenWhenTheHubAcceptedTheUpload()
    {
        // The hub takes 256 MB; this node will write 1 MB. Phase-41 D2: the box that accepts an
        // upload is not the box that has to put it on a disk, and each is entitled to its answer.
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(
            maxAttachmentBytes: Buffered,
            maxStreamedBytes: Streamed,
            nodeMaxStreamedBytes: Buffered);

        using var content = Multipart(RandomBytes(4 * 1024 * 1024), model: "echo", behaviour: "digest");
        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Tools:MaxStreamedBytes", await response.Content.ReadAsStringAsync());
        Assert.True(mesh.NodeIsRegistered());
    }

    [Fact]
    public async Task TheJobThatCrossesTheWireCarriesNoAttachmentAtAll()
    {
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(maxAttachmentBytes: Buffered, maxStreamedBytes: Streamed);

        using var content = Multipart(2 * 1024 * 1024 + 1, model: "echo");
        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The distinction the whole phase turns on: the bytes did not travel *on* the job. If a
        // regression puts them back on it, everything above still passes and the memory profile
        // quietly returns to v3.20's.
        var job = Assert.Single(mesh.DispatchedJobs);
        Assert.True(job.HasStreamedAttachments);
        Assert.Null(job.Attachments);
    }

    [Fact]
    public async Task WithTheKeyUnsetALargeUploadIsRefusedExactlyAsItWasInV320()
    {
        // No maxStreamedBytes: the second path does not exist, and the refusal must be the old one
        // from the old key, word for word.
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(maxAttachmentBytes: 1024);

        using var content = Multipart(4096, model: "echo");
        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ToolAttachmentLimits.MaxAttachmentBytesKey, body);
        Assert.DoesNotContain(ToolAttachmentLimits.MaxStreamedBytesKey, body);
    }

    [Fact]
    public async Task AnUploadPastTheStreamedCeilingIsRefusedNamingTheStreamedKey()
    {
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(
            maxAttachmentBytes: 1024,
            maxStreamedBytes: 64 * 1024);

        using var content = Multipart(256 * 1024, model: "echo");
        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(ToolAttachmentLimits.MaxStreamedBytesKey, await response.Content.ReadAsStringAsync());
        Assert.True(mesh.NodeIsRegistered());
    }

    [Fact]
    public async Task AFleetWhoseNodesCannotPullAStreamAnswers503AndNeverFallsBackToBuffering()
    {
        // D5's mixed fleet: the hub has the path, the node is a v3.20 one that never declares it.
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(
            maxAttachmentBytes: Buffered,
            maxStreamedBytes: Streamed,
            nodeTakesStreamedUploads: false);

        using var content = Multipart(2 * 1024 * 1024, model: "echo");
        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);

        // The sentence has to name the real reason. "No node provides echo" would be false and
        // would send an operator looking at the wrong thing entirely.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("streamed upload", body);
        Assert.Contains("Tools:MaxStreamedBytes", body);
    }

    [Fact]
    public async Task FieldsAfterTheFileAreRefusedRatherThanDropped()
    {
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(maxAttachmentBytes: Buffered, maxStreamedBytes: Streamed);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("echo"), "model");
        content.Add(FilePart(2 * 1024 * 1024), "file", "big.bin");
        content.Add(new StringContent("bg"), "language");

        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        // D3. A transcription that ignored `language=bg` and answered in English is the phase-42
        // failure with no error in it, so the field is a refusal rather than a silent drop.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("after a file part", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AModelSentAfterTheFileIsA400ThatExplainsTheOrdering()
    {
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(maxAttachmentBytes: Buffered, maxStreamedBytes: Streamed);

        using var content = new MultipartFormDataContent();
        content.Add(FilePart(2 * 1024 * 1024), "file", "big.bin");
        content.Add(new StringContent("echo"), "model");

        var response = await mesh.Client.PostAsync("/api/tools/echo", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("model is required", body);
        Assert.Contains("before the file part", body);
    }

    [Fact]
    public async Task AnAbortedUploadLeavesNoScratchDirectoryBehind()
    {
        await using var mesh = await ToolMeshTests.ToolMesh.StartAsync(maxAttachmentBytes: Buffered, maxStreamedBytes: Streamed);

        using var cts = new CancellationTokenSource();
        using var content = Multipart(Aborted, model: "echo");

        var post = mesh.Client.PostAsync("/api/tools/echo", content, cts.Token);

        // Far enough in that the node has opened a file and started appending to it, and nowhere
        // near the end of a 64 MB body.
        await mesh.WaitForScratchContentAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => post);

        // D8. The `finally` that deletes the scratch directory has to run on this path too, or a
        // half-written 64 MB file stays on the volume with somebody's audio in it.
        // D8. The `finally` that deletes the per-job scratch directory has to run on this path too,
        // or a half-written 64 MB file stays on the volume with somebody's audio in it — and it has
        // to run *promptly*: the tool's own requestTimeoutSeconds is 30, so a wait that generous
        // would pass against a node hanging until the deadline, which is the bug this found.
        var cleaned = await mesh.WaitForJobScratchCleanupAsync(TimeSpan.FromSeconds(8));

        Assert.True(cleaned, "left behind: " + string.Join(", ", mesh.JobScratchDirectories()));
        Assert.True(mesh.NodeIsRegistered());
        Assert.True(mesh.NodeIsRegistered());
    }

    private static MultipartFormDataContent Multipart(int bytes, string model, string? behaviour = null)
        => Multipart(RandomBytes(bytes), model, behaviour);

    private static MultipartFormDataContent Multipart(byte[] payload, string model, string? behaviour = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");

        if (behaviour is not null)
        {
            content.Add(new StringContent(behaviour), "behaviour");
        }

        content.Add(FilePart(payload), "file", "big.bin");
        return content;
    }

    private static ByteArrayContent FilePart(int bytes) => FilePart(RandomBytes(bytes));

    private static ByteArrayContent FilePart(byte[] payload)
    {
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    /// <summary>
    /// Never zeros: a path that dropped a window and padded with a zeroed buffer would pass both a
    /// length check and a digest against an all-zero payload.
    /// </summary>
    private static byte[] RandomBytes(int bytes)
    {
        var payload = new byte[bytes];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    private static string[] AspNetTempFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "ASPNETCORE_*").Order(StringComparer.Ordinal).ToArray();

    /// <summary>A file that vanished between the listing and the stat is a file of length zero.</summary>
    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
