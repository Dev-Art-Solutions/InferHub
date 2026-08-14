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

    // ---- phase 55: the repair somebody asked for -------------------------------------------------

    /// <summary>
    /// <b>The claim the release makes, asserted rather than assumed.</b> With no
    /// <c>X-InferHub-Image-Seam-Repair</c> header, a discontinuous panorama comes back exactly as
    /// v3.22 returned it — the same delta, the same warning, and neither of the two new fields.
    /// </summary>
    [Fact]
    public async Task WithNoHeaderNothingAboutTheResponseChanged()
    {
        await using var mesh = await ImageMesh.StartAsync(
            seamRepair: "any",
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "break"]);

        using var document = await Generate(mesh);
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.True(item.GetProperty("seam_delta").GetDouble() > 0.5);
        Assert.False(item.TryGetProperty("seam_repair", out _));
        Assert.False(item.TryGetProperty("seam_delta_before", out _));
        Assert.Contains("seam", Warnings(document));
    }

    /// <summary>
    /// <c>blend</c> closes the join: the delta falls, both numbers ride on the response, and the
    /// warning the original earned is gone because the warning follows the <em>final</em> number.
    /// </summary>
    [Fact]
    public async Task BlendLowersTheSeamAndReportsBothNumbers()
    {
        await using var mesh = await ImageMesh.StartAsync(
            seamRepair: "blend",
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "break"]);

        using var document = await Generate(mesh, repair: "blend");
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        var before = item.GetProperty("seam_delta_before").GetDouble();
        var after = item.GetProperty("seam_delta").GetDouble();

        Assert.Equal("blend", item.GetProperty("seam_repair").GetString());
        Assert.True(before > 0.5, $"the fixture's broken raster should score high, and scored {before}");
        Assert.True(after < before, $"blend should lower {before}, and produced {after}");
        Assert.DoesNotContain("seam", Warnings(document));
    }

    /// <summary>
    /// D4: a repair that did not lower the number is <b>discarded</b>, and the outcome is reported
    /// rather than hidden — the mechanism, two equal numbers, and the warning the image still earns.
    /// A pass that quietly made a seam worse is the one outcome nobody would ever look for.
    /// </summary>
    [Fact]
    public async Task ARepairThatDoesNotImproveTheNumberIsDiscardedAndSaidSo()
    {
        await using var mesh = await ImageMesh.StartAsync(
            seamRepair: "blend",
            workerArguments:
            [
                "--image-projection", "equirectangular",
                "--image-seam", "wrap",
                "--image-repair-worse"
            ]);

        using var document = await Generate(mesh, repair: "blend");
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal("blend", item.GetProperty("seam_repair").GetString());
        Assert.Equal(
            item.GetProperty("seam_delta_before").GetDouble(),
            item.GetProperty("seam_delta").GetDouble());
    }

    /// <summary>
    /// The operator's ceiling, and the reason it is a ceiling: the node refuses a mechanism it does
    /// not permit and the refusal <b>names the key</b> — phase-43 D1's shape, where a refusal that
    /// does not say which of your own settings stopped you reads as a bug.
    /// </summary>
    [Fact]
    public async Task AMechanismTheOperatorDidNotPermitIsRefusedNamingTheKey()
    {
        await using var mesh = await ImageMesh.StartAsync(
            seamRepair: "blend",
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "break"]);

        var response = await Post(mesh, repair: "diffuse");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Tools:Image:SeamRepair", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// And the default is that ceiling at its tightest: a node whose operator changed nothing cannot
    /// be made to spend anything by a header alone.
    /// </summary>
    [Fact]
    public async Task TheDefaultCeilingPermitsNothing()
    {
        await using var mesh = await ImageMesh.StartAsync(
            workerArguments: ["--image-projection", "equirectangular", "--image-seam", "break"]);

        var response = await Post(mesh, repair: "blend");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Tools:Image:SeamRepair", body);

        // The quotes around it arrive JSON-escaped, so the assertion is on the word.
        Assert.Contains("off", body);
    }

    /// <summary>A flat recipe has no wrap, so there is nothing to repair and the refusal says that.</summary>
    [Fact]
    public async Task AskingAFlatRecipeToRepairItsSeamIsRefusedRatherThanIgnored()
    {
        await using var mesh = await ImageMesh.StartAsync(seamRepair: "any");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = JsonContent.Create(ImageEndpointTests.Body())
        };

        request.Headers.TryAddWithoutValidation("X-InferHub-Image-Seam-Repair", "blend");

        var response = await mesh.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no seam to repair", await response.Content.ReadAsStringAsync());
    }

    /// <summary>An unknown mechanism never reaches a node: it is a 400 at the edge, naming both.</summary>
    [Fact]
    public async Task AnUnknownMechanismIsRefusedAtTheEdge()
    {
        await using var mesh = await ImageMesh.StartAsync(
            seamRepair: "any",
            workerArguments: ["--image-projection", "equirectangular"]);

        var response = await Post(mesh, repair: "inpaint");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("blend", body);
        Assert.Contains("diffuse", body);
    }

    private static string[] Warnings(JsonDocument document) =>
        document.RootElement.TryGetProperty("warnings", out var warnings) && warnings.ValueKind is JsonValueKind.Array
            ? warnings.EnumerateArray().Select(entry => entry.GetString()!).ToArray()
            : [];

    private static async Task<JsonDocument> Generate(ImageMesh mesh, string? repair = null)
    {
        var response = await Post(mesh, repair);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> Post(ImageMesh mesh, string? repair = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = JsonContent.Create(
                new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = PanoramaSize })
        };

        if (repair is not null)
        {
            request.Headers.TryAddWithoutValidation("X-InferHub-Image-Seam-Repair", repair);
        }

        return mesh.Client.SendAsync(request);
    }
}
