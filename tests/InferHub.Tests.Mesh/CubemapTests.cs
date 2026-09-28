using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// <c>X-InferHub-Image-Reproject: cubemap</c> travels from the edge to the worker and back, on every
/// surface a panorama already reached (phase 88).
/// </summary>
/// <remarks>
/// The echo worker returns a plain raster of the strip's shape. The resampling is the real worker's
/// and is run by <c>CubemapReprojectionTests</c>, so these tests are about the route: the header
/// arrives, <c>cubemap</c> comes back on the response, the job and the content header, the PNG has
/// the strip's dimensions, and the bill is the render's.
/// </remarks>
public class CubemapTests
{
    private const string PanoramaSize = "1024x512";

    private static readonly string[] Equirectangular = ["--image-projection", "equirectangular"];

    [Fact]
    public async Task TheSynchronousResponseSaysCubemapAndKeepsTheRendersSize()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular);

        var response = await Post(mesh, "/v1/images/generations", "cubemap");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal("cubemap", item.GetProperty("projection").GetString());
        Assert.Equal(PanoramaSize, item.GetProperty("size").GetString());

        // The seam is the panorama's and keeps its meaning (D4).
        Assert.True(item.TryGetProperty("seam_delta", out _));

        // D2: six faces of width/4 in one strip, read from the PNG's own header rather than trusted.
        var (width, height) = PngSize(Convert.FromBase64String(item.GetProperty("b64_json").GetString()!));
        Assert.Equal((6 * 256, 256), (width, height));
    }

    /// <summary>
    /// D3, the reason <c>size</c> stays the render's: the same render is billed the same with and
    /// without the header.
    /// </summary>
    [Fact]
    public async Task TheBillIsTheRendersWithOrWithoutTheHeader()
    {
        await using var plain = await ImageMesh.StartAsync(workerArguments: Equirectangular);
        await using var cubed = await ImageMesh.StartAsync(workerArguments: Equirectangular);

        Assert.Equal(HttpStatusCode.OK, (await Post(plain, "/v1/images/generations", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(cubed, "/v1/images/generations", "cubemap")).StatusCode);

        var before = Assert.Single(await plain.Ledger.QueryAsync(new UsageQuery())).MegapixelSteps;
        var after = Assert.Single(await cubed.Ledger.QueryAsync(new UsageQuery())).MegapixelSteps;

        Assert.True(before > 0);
        Assert.Equal(before, after, 6);
    }

    [Fact]
    public async Task TheJobDocumentAndTheContentHeaderSayCubemap()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular);

        var submitted = await Post(mesh, "/api/images/jobs", "cubemap");
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        var id = await EquirectangularTests.Succeeded(mesh, submitted);
        using var job = JsonDocument.Parse(
            await (await mesh.Client.GetAsync($"/api/images/jobs/{id}")).Content.ReadAsStringAsync());

        Assert.Equal("cubemap", Assert.Single(job.RootElement.GetProperty("images").EnumerateArray())
            .GetProperty("projection").GetString());

        // The content route is where a skybox loader actually fetches the bytes, and it has no JSON.
        var content = await mesh.Client.GetAsync($"/api/images/jobs/{id}/content/0");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("cubemap", content.Headers.GetValues(ImageProjections.Header).Single());
        Assert.Equal((1536, 256), PngSize(await content.Content.ReadAsByteArrayAsync()));
    }

    /// <summary>D1: no header and <c>off</c> are today's panorama, unchanged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public async Task WithoutTheHeaderThePanoramaComesBackAsItAlwaysHas(string? header)
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular);

        var response = await Post(mesh, "/v1/images/generations", header);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal("equirectangular", item.GetProperty("projection").GetString());
        Assert.Equal((1024, 512), PngSize(Convert.FromBase64String(item.GetProperty("b64_json").GetString()!)));
    }

    [Fact]
    public async Task AFlatRecipeIsRefusedRatherThanCutIntoSomethingThatLooksLikeACube()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = JsonContent.Create(ImageEndpointTests.Body())
        };

        request.Headers.TryAddWithoutValidation(ImageExtensions.Reproject, "cubemap");

        var response = await mesh.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot be cut into a cubemap", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnknownValueIsRefusedAtTheEdgeNamingCubemap()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular);

        var response = await Post(mesh, "/v1/images/generations", "cross");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cubemap", body);
        Assert.Contains("cross", body);
    }

    /// <summary>Solo mode is its own edge (37), so it has to say the same thing without a hub.</summary>
    [Fact]
    public async Task ASoloNodeAnswersTheSame()
    {
        var (solo, cleanup) = await ImageFixture.SoloAsync(Equirectangular);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
            {
                Content = JsonContent.Create(
                    new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = PanoramaSize })
            };

            request.Headers.TryAddWithoutValidation(ImageExtensions.Reproject, "cubemap");

            var response = await solo.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

            Assert.Equal("cubemap", item.GetProperty("projection").GetString());
            Assert.Equal(PanoramaSize, item.GetProperty("size").GetString());
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    private static Task<HttpResponseMessage> Post(ImageMesh mesh, string route, string? reproject)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(
                new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = PanoramaSize })
        };

        if (reproject is not null)
        {
            request.Headers.TryAddWithoutValidation(ImageExtensions.Reproject, reproject);
        }

        return mesh.Client.SendAsync(request);
    }

    /// <summary>The IHDR's width and height — the only two numbers read out of a PNG anywhere here.</summary>
    private static (int Width, int Height) PngSize(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
}
