using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// The seam is measured, reported, and never a failure (phase 49, D5).
/// </summary>
/// <remarks>
/// <para>
/// <b>This pins the behaviour, not the implementation.</b> The number itself is computed in Python
/// on the real worker and in C# on the fixture, and the two will never agree to five decimal places
/// on the same picture — nor do they need to. What has to hold, and what a wrong threshold or a
/// swallowed field would break, is: a wrapping panorama scores near zero, a discontinuous one scores
/// high, the number reaches the client, and crossing the threshold produces a <b>warning</b> on a
/// <c>200</c> rather than a failed job.
/// </para>
/// <para>
/// The fixture's two rasters are chosen so the assertion is not a coin flip: seamless is a
/// horizontal sinusoid with a period of exactly the width, so its first and last columns are one
/// step apart; not-seamless is a 0→255 ramp, so they are as far apart as eight bits allow.
/// </para>
/// </remarks>
public class SeamMetricTests
{
    private const string PanoramaSize = "1024x512";

    [Fact]
    public async Task AWrappingPanoramaScoresNearZeroAndCarriesNoWarning()
    {
        await using var mesh = await ImageMesh.StartAsync(
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "wrap"]);

        using var document = await Generate(mesh);
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        var delta = item.GetProperty("seam_delta").GetDouble();

        Assert.InRange(delta, 0d, 0.02d);
        Assert.False(document.RootElement.TryGetProperty("warnings", out var warnings) && warnings.ValueKind is JsonValueKind.Array);
    }

    /// <summary>
    /// A visible seam is the operator's own aesthetic judgement, and failing a two-minute job over a
    /// metric threshold would be the tool overriding the person about a picture only they can see.
    /// So this is a <c>200</c> with a warning on it — phase-35 D4 against phase-37 D4, one more time.
    /// </summary>
    [Fact]
    public async Task ADiscontinuousPanoramaScoresHighAndWarnsWithoutFailing()
    {
        await using var mesh = await ImageMesh.StartAsync(
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "break"]);

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = PanoramaSize });

        // The status is the assertion. A seam is never a failure.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.True(item.GetProperty("seam_delta").GetDouble() > 0.5);

        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();

        Assert.Contains("seam", warnings);
    }

    /// <summary>
    /// <c>Tools:Image:SeamWarnThreshold</c> reaches the worker at all — which it can only do because
    /// the node <em>states</em> it into a child environment it clears first (phase-41 D3). Raised
    /// past the discontinuous raster's score, the same picture comes back with no warning, and the
    /// measurement is still there: only the warning is thresholded.
    /// </summary>
    [Fact]
    public async Task RaisingTheThresholdSilencesTheWarningAndKeepsTheMeasurement()
    {
        await using var mesh = await ImageMesh.StartAsync(
            seamWarnThreshold: 1.5,
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "break"]);

        using var document = await Generate(mesh);
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.True(item.GetProperty("seam_delta").GetDouble() > 0.5);
        Assert.False(document.RootElement.TryGetProperty("warnings", out var warnings) && warnings.ValueKind is JsonValueKind.Array);
    }

    /// <summary>A flat recipe measures nothing, because there is no seam to measure.</summary>
    [Fact]
    public async Task AFlatRecipeReportsNoSeamAtAll()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.False(item.TryGetProperty("seam_delta", out _));
    }

    private static async Task<JsonDocument> Generate(ImageMesh mesh)
    {
        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = PanoramaSize });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
