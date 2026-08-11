using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// 360° panoramas, end to end (phase 49): the 2:1 refusal, the trigger phrase, and the projection
/// metadata surviving to every surface a client can read it from.
/// </summary>
/// <remarks>
/// <para>
/// The worker is the echo fixture in its equirectangular mode, which enforces 2:1 buckets, appends
/// the trigger, declares a projection and measures its own seam — the same four things a real recipe
/// does, against a real child process on a machine with no card. What it cannot prove is that a
/// panorama is a panorama; that is the published-image verification's job, and it is done by opening
/// the file in a viewer rather than by any assertion here.
/// </para>
/// <para>
/// <b>The most valuable test in this file is the last one.</b> A flat recipe reporting <c>flat</c> is
/// what makes the field readable at all — an omitted projection would be indistinguishable from a
/// node too old to have an opinion, and a client that has to tell those apart has learnt nothing.
/// </para>
/// </remarks>
public class EquirectangularTests
{
    private const string Trigger = "360 degree panorama with equirectangular projection";

    /// <summary>The fixture's 2:1 buckets. A real recipe's are 2048×1024, 1536×768 and 1024×512.</summary>
    private const string PanoramaSize = "1024x512";

    private static string[] Equirectangular(params string[] extra) =>
        ["--image-projection", "equirectangular", .. extra];

    // ---- D3: the 2:1 refusal ------------------------------------------------------------------

    /// <summary>
    /// A well-formed size that is not 2:1 is a <c>400</c>, and — this is the phase's addition — it
    /// says <em>why</em> rather than only listing the alternatives. A non-2:1 equirectangular render
    /// is not a failure anybody can see: it is a panorama that wraps wrongly, and the person who
    /// finds out is wearing a headset three days later.
    /// </summary>
    [Fact]
    public async Task ANonTwoToOneSizeIsRefusedWithTheReasonAndNotOnlyTheList()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular());

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = "1024x1024" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("always 2:1", message);
        Assert.Contains("wraps wrongly", message);
        Assert.Contains("1024x512", message);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- D2: the trigger ----------------------------------------------------------------------

    [Fact]
    public async Task TheTriggerIsAppendedWhenAbsentAndTheResponseSaysSo()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular());

        using var document = await Generate(mesh, "a Bulgarian mountain monastery courtyard at golden hour");

        Assert.True(document.RootElement.GetProperty("prompt_augmented").GetBoolean());
        Assert.Equal(Trigger, document.RootElement.GetProperty("trigger").GetString());
    }

    /// <summary>
    /// Present already, so nothing is appended — and the flag still travels. A client that had to
    /// infer "nothing happened to my prompt" from a missing key is a client guessing.
    /// </summary>
    [Fact]
    public async Task TheTriggerIsNotAppendedTwiceAndTheFlagIsStillPresent()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular());

        using var document = await Generate(mesh, $"a lighthouse, {Trigger}");

        Assert.False(document.RootElement.GetProperty("prompt_augmented").GetBoolean());
        Assert.Equal(Trigger, document.RootElement.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task AutoTriggerOffNeverAppendsAnything()
    {
        await using var mesh = await ImageMesh.StartAsync(
            workerArguments: Equirectangular("--image-no-auto-trigger"));

        using var document = await Generate(mesh, "a monastery courtyard");

        Assert.False(document.RootElement.GetProperty("prompt_augmented").GetBoolean());
    }

    // ---- D4: the projection reaches every surface ----------------------------------------------

    [Fact]
    public async Task TheProjectionReachesTheSynchronousResponse()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular());

        using var document = await Generate(mesh, "a monastery courtyard");
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal("equirectangular", item.GetProperty("projection").GetString());
        Assert.Equal(PanoramaSize, item.GetProperty("size").GetString());
    }

    [Fact]
    public async Task TheProjectionReachesTheJobDocumentAndTheContentHeader()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: Equirectangular());

        var submitted = await mesh.Client.PostAsJsonAsync(
            "/api/images/jobs",
            new { model = ImageFixture.Model, prompt = "a monastery courtyard", size = PanoramaSize });

        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        var id = await Succeeded(mesh, submitted);
        using var job = JsonDocument.Parse(await (await mesh.Client.GetAsync($"/api/images/jobs/{id}")).Content.ReadAsStringAsync());

        Assert.True(job.RootElement.GetProperty("promptAugmented").GetBoolean());
        Assert.Equal(Trigger, job.RootElement.GetProperty("trigger").GetString());

        var image = Assert.Single(job.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal("equirectangular", image.GetProperty("projection").GetString());

        // The content route is where a viewer actually fetches the bytes, and it is the one place a
        // client has no JSON to read the projection from — so the header is not decoration.
        var content = await mesh.Client.GetAsync($"/api/images/jobs/{id}/content/0");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("equirectangular", content.Headers.GetValues(ImageProjections.Header).Single());
    }

    /// <summary>
    /// The one that makes the field mean anything: a flat recipe <b>reports</b> <c>flat</c>. This is
    /// a deliberate exception to phase-28 D5's "absence is a fact" — there, absence meant nothing had
    /// been measured; here the field is a declaration, and omitting it would read as "this node has
    /// never heard of projections".
    /// </summary>
    [Fact]
    public async Task AFlatRecipeReportsFlatRatherThanOmittingTheField()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());

        Assert.Equal("flat", item.GetProperty("projection").GetString());

        // No trigger, so neither the phrase nor the flag — those two *are* absences. A permanent
        // `prompt_augmented: false` on every SDXL response would be a field that means nothing,
        // which is the opposite failure to the one `projection: "flat"` exists to avoid.
        Assert.False(document.RootElement.TryGetProperty("trigger", out _));
        Assert.False(document.RootElement.TryGetProperty("prompt_augmented", out _));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<JsonDocument> Generate(ImageMesh mesh, string prompt)
    {
        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt, size = PanoramaSize });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Waits for a submitted job to reach <c>succeeded</c> and returns its id.</summary>
    internal static async Task<string> Succeeded(ImageMesh mesh, HttpResponseMessage submitted)
    {
        using var accepted = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        var id = accepted.RootElement.GetProperty("id").GetString()!;

        for (var i = 0; i < 200; i++)
        {
            using var document = JsonDocument.Parse(
                await (await mesh.Client.GetAsync($"/api/images/jobs/{id}")).Content.ReadAsStringAsync());

            var state = document.RootElement.GetProperty("state").GetString();

            if (state == "succeeded")
            {
                return id;
            }

            Assert.True(state is "queued" or "running", $"the job ended {state}");
            await Task.Delay(25);
        }

        Assert.Fail("the job never succeeded");
        return id;
    }

    private static async Task<string> Message(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString()!;
    }
}
