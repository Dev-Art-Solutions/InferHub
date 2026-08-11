using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// The bug that shipped v3.10.0 dead on arrival, asserted before it can happen again (phase 46 D4).
/// </summary>
/// <remarks>
/// <para>
/// SignalR's default <c>MaximumReceiveMessageSize</c> is 32 KB, and exceeding it <b>tears the
/// connection down</b> rather than failing the message. Phase 41 verified attachments across a real
/// wire with a <b>16-byte</b> file — four orders of magnitude under the cap — so the test proved the
/// plumbing and said nothing about the size, and the first real six-second WAV (~300 KB) dropped the
/// node on every call.
/// </para>
/// <para>
/// An image is where that stops being an edge case: a 1024×1024 PNG is megabytes, every time, on the
/// happy path. So the assertion that matters here is not "a response came back" — it is <b>"the node
/// is still registered afterwards"</b>, which is the thing a green suite got wrong last time.
/// </para>
/// </remarks>
public class ImageWireSizeTests
{
    [Fact]
    public async Task AMultiMegabyteImageCrossesTheMeshAndTheNodeSurvivesIt()
    {
        // ~3 MB of padding on top of the real raster. Well past the 32 KB default, and past the
        // ~300 KB that was enough to break the audio path.
        await using var mesh = await ImageMesh.StartAsync(workerArguments: ["--image-pad-bytes", "3000000"]);

        Assert.True(mesh.NodeIsRegistered());

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        var bytes = Convert.FromBase64String(item.GetProperty("b64_json").GetString()!);

        Assert.True(bytes.Length > 3_000_000, $"expected a multi-megabyte image, got {bytes.Length} bytes");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal((512, 512), ImageEndpointTests.PngHeader(bytes));

        // The assertion this file exists for.
        Assert.True(mesh.NodeIsRegistered(), "the node dropped off the mesh after a large message");

        // …and it still serves. A connection torn down and silently re-established would satisfy the
        // line above on a slow enough test; this one would not.
        Assert.Equal(
            HttpStatusCode.OK,
            (await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body())).StatusCode);
    }

    /// <summary>
    /// The wire cap is derived from <c>Tools:MaxAttachmentBytes</c>
    /// (<c>NodeHubLimits.ReceiveSizeFor</c>), so lowering that lowers what may cross — and the
    /// failure has to be a failed <em>job</em> rather than a dropped node.
    /// </summary>
    [Fact]
    public async Task AnImageOverTheConfiguredCapFailsTheJobAndLeavesTheNodeServing()
    {
        await using var mesh = await ImageMesh.StartAsync(
            maxAttachmentBytes: 256 * 1024,
            workerArguments: ["--image-pad-bytes", "2000000"]);

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());

        // Whatever the failure renders as, it must not be a success and it must not be silence.
        Assert.False(response.IsSuccessStatusCode);

        // The node is what this is about: a message over the cap must not cost the fleet a node.
        for (var i = 0; i < 100 && !mesh.NodeIsRegistered(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(mesh.NodeIsRegistered(), "the node dropped off the mesh instead of failing the job");
    }
}
