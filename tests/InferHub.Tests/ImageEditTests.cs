using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// <c>/v1/images/edits</c> and <c>/v1/images/variations</c>, end to end (phase 50).
/// </summary>
/// <remarks>
/// <para>
/// Every one of these crosses a real SignalR wire to a real child process, because the two things
/// this phase actually adds are only true out there: <b>bytes travelling hub → node</b>, which the
/// attachment path had never carried before, and <b>a mask being opened by something that can open
/// it</b>. The hub never decodes a pixel (phase-46 D6), so the alpha-channel check and the size
/// check happen in the worker or nowhere, and a test that stubbed the worker would be asserting
/// that a stub agrees with itself.
/// </para>
/// <para>
/// The mask convention is the load-bearing pair below: the <em>same file</em> is refused under
/// <c>openai</c> and accepted under <c>luminance</c>. That is D2 stated as a test — the conventions
/// are opposite, and a worker that quietly accepted both would be editing everything except what
/// the caller selected.
/// </para>
/// </remarks>
public class ImageEditTests
{
    [Fact]
    public async Task AnEditRoundTripsThroughTheMeshAndReturnsTheOpenAiEnvelope()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(Form(mask: TestPng.Mask(512, 512))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        var bytes = Convert.FromBase64String(item.GetProperty("b64_json").GetString()!);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal((512, 512), ImageEndpointTests.PngHeader(bytes));

        // The size the caller never named. An edit with no `size` takes the input picture's own
        // dimensions, because substituting a recipe default would silently rescale their photograph.
        Assert.Equal("512x512", item.GetProperty("size").GetString());
        Assert.Equal("flat", item.GetProperty("projection").GetString());
    }

    /// <summary>
    /// <b>D2, as a pair.</b> One file, two conventions, two answers — and the refusal is the one
    /// that would otherwise have edited the whole picture.
    /// </summary>
    [Fact]
    public async Task AMaskWithNoAlphaIsRefusedUnderOpenAisConventionAndAcceptedUnderLuminance()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var opaque = TestPng.Create(512, 512);

        var refused = await mesh.Client.SendAsync(Edit(Form(mask: opaque)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var message = await Message(refused);
        Assert.Contains("no alpha channel", message);
        Assert.Contains("TRANSPARENT", message);
        Assert.Contains("luminance", message);

        // The same bytes, declared the other way round. This is what the header is for: a caller
        // whose mask is already white-is-edit says so, rather than inverting their own file to
        // satisfy a convention they did not choose.
        var accepted = await mesh.Client.SendAsync(Edit(
            Form(mask: opaque),
            (ImageExtensions.MaskConvention, MaskConventions.Luminance)));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task AnUnknownMaskConventionIsA400ThatNamesBothAndSaysWhichIsWhich()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(
            Form(mask: TestPng.Mask(512, 512)),
            (ImageExtensions.MaskConvention, "inverted")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("'inverted' is not a mask convention", message);
        Assert.Contains("TRANSPARENT pixels are the area to edit", message);
        Assert.Contains("WHITE pixels are the area to edit", message);
    }

    /// <summary>
    /// A mask names <em>which pixels</em>, so it is never rescaled — the edit would land next to
    /// what the caller selected, which reads as a bad model rather than a bad mask.
    /// </summary>
    [Fact]
    public async Task AMaskThatDoesNotMatchTheImageIsA400NamingBothSizes()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(Form(mask: TestPng.Mask(256, 256))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("256x256", message);
        Assert.Contains("512x512", message);
        Assert.Contains("never rescaled", message);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("0,75")]
    public async Task AStrengthOutsideZeroToOneIsA400(string strength)
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(Form(), (ImageExtensions.Strength, strength)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not a number between 0 and 1", await Message(response));
    }

    /// <summary>
    /// <b>D3.</b> <c>diffusers</c> enters the schedule at <c>int(steps × strength)</c>, so an edit at
    /// 0.5 over 30 steps denoises for 15 — and 15 is what the ledger gets. Metering the asked-for 30
    /// would bill for work nobody did.
    /// </summary>
    [Fact]
    public async Task WhatIsMeteredIsTheStepsStrengthActuallyRan()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(
            Form(),
            (ImageExtensions.Strength, "0.5"),
            (ImageExtensions.Steps, "30")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await mesh.Ledger.QueryAsync(new InferHub.Coordinator.Services.UsageQuery()));

        // 512×512 = 0.262144 megapixels, over 15 steps rather than 30.
        Assert.Equal(0.262144 * 15, row.MegapixelSteps, 5);

        // And it is booked against the capability that ran it, so an operator reading the log can
        // see what the card spent its minutes on. The unit is unchanged: megapixel-steps is a fact
        // about pixels and steps, whichever operation produced them.
        Assert.Contains(
            mesh.Logs.Lines,
            line => line.Contains($"{CapabilityKinds.ImageEdit} job") && line.Contains("megapixel-steps"));
    }

    /// <summary>
    /// <b>D1.</b> A recipe that only generates is not declared under <c>image-edit</c>, so this is a
    /// fleet-state answer — and it names the recipes on this fleet that <em>can</em> edit, which the
    /// hub knows from its own registrations rather than from a model catalogue it does not have.
    /// </summary>
    [Fact]
    public async Task ARecipeThatOnlyGeneratesIsA503NamingTheOnesThatCanEdit()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(Form(model: ImageFixture.GenerateOnlyModel)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());

        var message = await Message(response);
        Assert.Contains($"no node currently provides 'image-edit' for model '{ImageFixture.GenerateOnlyModel}'", message);
        Assert.Contains($"Models on this fleet that do: {ImageFixture.Model}", message);

        // …and the same model still generates. A capability refusal that took the model offline
        // entirely would be a much bigger claim than the one being made.
        var generation = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.GenerateOnlyModel, prompt = "a cat", size = ImageFixture.Size });

        Assert.Equal(HttpStatusCode.OK, generation.StatusCode);
    }

    [Fact]
    public async Task AVariationRoundTripsAndTakesNeitherAPromptNorAMask()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var ok = await mesh.Client.SendAsync(Variation(Form(prompt: null)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Refused rather than ignored: a caller whose prompt vanished silently would conclude the
        // model ignores prompts, which is the wrong lesson about the right request on the wrong route.
        var withPrompt = await mesh.Client.SendAsync(Variation(Form()));
        Assert.Equal(HttpStatusCode.BadRequest, withPrompt.StatusCode);
        Assert.Contains("a variation takes no prompt", await Message(withPrompt));
        Assert.Contains("/v1/images/edits", await Message(withPrompt));

        var withMask = await mesh.Client.SendAsync(Variation(Form(prompt: null, mask: TestPng.Mask(512, 512))));
        Assert.Equal(HttpStatusCode.BadRequest, withMask.StatusCode);
        Assert.Contains("a variation takes no mask", await Message(withMask));
    }

    [Fact]
    public async Task AnEditWithNoPromptAndNoImageAreBoth400sThatNameTheField()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var noPrompt = await mesh.Client.SendAsync(Edit(Form(prompt: null)));
        Assert.Equal(HttpStatusCode.BadRequest, noPrompt.StatusCode);
        Assert.Contains("prompt is required", await Message(noPrompt));

        var noImage = await mesh.Client.SendAsync(Edit(Form(includeImage: false)));
        Assert.Equal(HttpStatusCode.BadRequest, noImage.StatusCode);
        Assert.Contains("an 'image' part is required", await Message(noImage));
    }

    /// <summary>
    /// <b>D4, both halves.</b> One part over the attachment cap, and two parts that together exceed
    /// the request budget — each refused at the edge, before anything is buffered onward, with the
    /// limit and its key in the sentence.
    /// </summary>
    [Fact]
    public async Task AnOversizedPartAndAnOversizedRequestAreBoth413sNamingTheirLimit()
    {
        // A 512×512 truecolour raster is already ~787 KB, so the caps here are chosen to sit either
        // side of a real file rather than either side of a round number.
        await using var mesh = await ImageMesh.StartAsync(
            maxAttachmentBytes: 1_500_000,
            configureImages: options => options.MaxRequestBytes = 2_000_000);

        var oversizedPart = await mesh.Client.SendAsync(Edit(Form(image: TestPng.Create(512, 512, pad: 1_000_000))));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedPart.StatusCode);
        Assert.Contains("Tools:MaxAttachmentBytes", await Message(oversizedPart));

        // Each part fits; together they do not. That is the whole reason the total is its own key:
        // two files nobody would refuse individually are still two files' worth of memory.
        var oversizedTotal = await mesh.Client.SendAsync(Edit(Form(
            image: TestPng.Create(512, 512, pad: 200_000),
            mask: TestPng.Mask(512, 512, pad: 200_000))));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedTotal.StatusCode);
        Assert.Contains("Images:MaxRequestBytes", await Message(oversizedTotal));
    }

    /// <summary>
    /// The v3.10.0 lesson, in the direction it had never been tested in: <b>hub → node</b>. Phase 42
    /// tore a connection down with a 300 KB WAV coming back; nothing had ever pushed megabytes the
    /// other way. The assertion that matters is "still registered", not "got a response".
    /// </summary>
    [Fact]
    public async Task AMultiMegabyteInputImageCrossesTheWireAndTheNodeIsStillRegistered()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(Form(image: TestPng.Create(512, 512, pad: 3 * 1024 * 1024))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(mesh.NodeIsRegistered(), "the node dropped off the fleet after a 3 MB input image");

        // And it still serves. A connection that survived the message but not the next request would
        // pass the assertion above and be broken anyway.
        var again = await mesh.Client.SendAsync(Edit(Form()));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>
    /// Nothing an edit carries is kept: not the uploaded picture, not the mask, not the prompt.
    /// Rule 7, with two more kinds of content than a generation has.
    /// </summary>
    [Fact]
    public async Task AnEditLeavesNothingInTheLogsOrTheScratchDirectory()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.SendAsync(Edit(Form(prompt: ImageFixture.KnownPrompt)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = string.Join("\n", mesh.Logs.Lines);
        Assert.DoesNotContain(ImageFixture.KnownPrompt, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lighthouse", log, StringComparison.OrdinalIgnoreCase);

        // The filename the caller chose is not there either: it never left the edge. What somebody
        // called a file on their disk is metadata about their day (phase-42 D5).
        Assert.DoesNotContain("my-holiday-photo", log, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    /// <summary>
    /// The async surface takes an edit too, because an edit is as slow as a generation — same queue,
    /// same progress, same retention, same read-once collection.
    /// </summary>
    [Fact]
    public async Task AnEditCanBeSubmittedAsAJobAndCollectedFromTheContentRoute()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var form = Form();
        form.Add(new StringContent(ImageOperations.Edit), "operation");

        var submitted = await mesh.Client.PostAsync("/api/images/jobs", form);

        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        using var accepted = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        var id = accepted.RootElement.GetProperty("id").GetString();

        var succeeded = await Poll(mesh, id!);
        Assert.Equal("succeeded", succeeded.GetProperty("state").GetString());

        var content = await mesh.Client.GetAsync($"/api/images/jobs/{id}/content/0");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal("flat", content.Headers.GetValues(ImageProjections.Header).Single());
    }

    [Fact]
    public async Task AMultipartJobWithNoOperationIsA400NamingBoth()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/api/images/jobs", Form());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("must name its operation", message);
        Assert.Contains("Send JSON to generate", message);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<JsonElement> Poll(ImageMesh mesh, string id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var response = await mesh.Client.GetAsync($"/api/images/jobs/{id}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var state = document.RootElement.GetProperty("state").GetString();

            if (state is "succeeded" or "failed" or "cancelled" or "expired")
            {
                return document.RootElement.Clone();
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"image job {id} never reached a terminal state");
    }

    internal static MultipartFormDataContent Form(
        string? model = ImageFixture.Model,
        string? prompt = "a tall window with morning light",
        byte[]? image = null,
        byte[]? mask = null,
        bool includeImage = true)
    {
        var form = new MultipartFormDataContent();

        if (model is not null)
        {
            form.Add(new StringContent(model), "model");
        }

        if (prompt is not null)
        {
            form.Add(new StringContent(prompt), "prompt");
        }

        if (includeImage)
        {
            form.Add(Png(image ?? TestPng.Create(512, 512)), "image", "my-holiday-photo.png");
        }

        if (mask is not null)
        {
            form.Add(Png(mask), "mask", "my-holiday-mask.png");
        }

        return form;
    }

    private static ByteArrayContent Png(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private static HttpRequestMessage Edit(MultipartFormDataContent form, params (string Name, string Value)[] headers)
        => Request("/v1/images/edits", form, headers);

    private static HttpRequestMessage Variation(MultipartFormDataContent form, params (string Name, string Value)[] headers)
        => Request("/v1/images/variations", form, headers);

    private static HttpRequestMessage Request(
        string route,
        MultipartFormDataContent form,
        (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = form };

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private static async Task<string> Message(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
    }
}

/// <summary>
/// Real PNGs for the test suite: signature, IHDR, a zlib-stored IDAT, IEND.
/// </summary>
/// <remarks>
/// Hand-rolled for design rule 5's reason — there is no image library anywhere in this solution, and
/// adding one to a test project is how one ends up in a shipped one. Real rather than random for
/// phase-42's reason: the worker reads the IHDR back, so a random byte array would be refused for
/// the wrong reason and the mask tests would prove nothing.
/// </remarks>
internal static class TestPng
{
    /// <summary>An opaque truecolour PNG — a picture, and a mask that selects <em>nothing</em>.</summary>
    public static byte[] Create(int width, int height, int pad = 0) => Build(width, height, pad, alpha: false);

    /// <summary>
    /// A mask with a real alpha channel, transparent down the left third.
    /// </summary>
    /// <remarks>
    /// Transparent <b>is</b> the selection under OpenAI's convention, so a mask whose alpha is
    /// uniformly opaque selects nothing — which is the request the worker refuses, and a fixture
    /// that produced one by accident would make the happy path untestable.
    /// </remarks>
    public static byte[] Mask(int width, int height, int pad = 0) => Build(width, height, pad, alpha: true);

    private static byte[] Build(int width, int height, int pad, bool alpha)
    {
        using var buffer = new MemoryStream();

        buffer.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BigEndian(ihdr, 0, width);
        BigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(alpha ? 6 : 2);
        Chunk(buffer, "IHDR", ihdr);

        var channels = alpha ? 4 : 3;
        var raw = new byte[height * (1 + (width * channels))];
        var at = 0;

        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0;

            for (var x = 0; x < width; x++)
            {
                raw[at++] = (byte)(x & 0xFF);
                raw[at++] = (byte)(y & 0xFF);
                raw[at++] = 0x40;

                if (alpha)
                {
                    raw[at++] = (byte)(x < width / 3 ? 0 : 255);
                }
            }
        }

        Chunk(buffer, "IDAT", ZlibStored(raw));

        if (pad > 0)
        {
            // An ancillary chunk a decoder skips. Padding the IDAT would corrupt the raster;
            // padding here keeps the file both valid and as large as the test needs.
            Chunk(buffer, "teXt", new byte[pad]);
        }

        Chunk(buffer, "IEND", []);
        return buffer.ToArray();
    }

    private static void Chunk(Stream target, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, data.Length);
        target.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        var crc = new byte[4];
        BigEndian(crc, 0, (int)Crc32(typeBytes, data));
        target.Write(crc);
    }

    private static byte[] ZlibStored(byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0x78);
        buffer.WriteByte(0x01);

        var offset = 0;

        while (offset < data.Length)
        {
            var block = Math.Min(0xFFFF, data.Length - offset);
            var final = offset + block >= data.Length;

            buffer.WriteByte((byte)(final ? 1 : 0));
            buffer.WriteByte((byte)(block & 0xFF));
            buffer.WriteByte((byte)((block >> 8) & 0xFF));
            buffer.WriteByte((byte)(~block & 0xFF));
            buffer.WriteByte((byte)((~block >> 8) & 0xFF));
            buffer.Write(data, offset, block);

            offset += block;
        }

        uint a = 1, b = 0;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = new byte[4];
        BigEndian(adler, 0, (int)((b << 16) | a));
        buffer.Write(adler);

        return buffer.ToArray();
    }

    private static void BigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)((value >> 24) & 0xFF);
        target[offset + 1] = (byte)((value >> 16) & 0xFF);
        target[offset + 2] = (byte)((value >> 8) & 0xFF);
        target[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in first)
        {
            crc = Step(crc, value);
        }

        foreach (var value in second)
        {
            crc = Step(crc, value);
        }

        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint crc, byte value)
        {
            crc ^= value;

            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            return crc;
        }
    }
}
