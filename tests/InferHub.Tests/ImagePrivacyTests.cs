using System.Net.Http.Json;
using InferHub.Coordinator.Services;

namespace InferHub.Tests;

/// <summary>
/// Design rule 7, in a form it had not met before (phase 46): <b>a prompt is content.</b>
/// </summary>
/// <remarks>
/// <para>
/// A transcript is content because it is what somebody said. A prompt is content because it is what
/// somebody <em>wanted</em>, and the picture is the answer — which makes an image request the most
/// revealing thing a caller sends this fleet, and the least defensible thing to find in a log.
/// </para>
/// <para>
/// Shaped after <c>AudioPrivacyTests</c> and
/// <c>UsageLedgerTests.NoPromptOrCompletionTextExistsAnywhereInTheUsagePath</c>, asking the harder
/// version of the question: not "is the field absent" but "does this phrase appear <em>anywhere</em>".
/// The mesh runs with a capturing logger at <c>Trace</c>, so anything the hub wrote at any level is
/// in scope.
/// </para>
/// </remarks>
public class ImagePrivacyTests
{
    [Fact]
    public async Task NoPromptAppearsInAnyLogLineOrInTheLedger()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new
            {
                model = ImageFixture.Model,
                prompt = ImageFixture.KnownPrompt,
                negative_prompt = "blurry, watermark, a secret nobody should log",
                size = ImageFixture.Size
            });

        Assert.True(response.IsSuccessStatusCode);

        var log = string.Join("\n", mesh.Logs.Lines);

        Assert.DoesNotContain(ImageFixture.KnownPrompt, log, StringComparison.OrdinalIgnoreCase);

        // Not a fragment of it, either. None of these appears anywhere else in this codebase.
        Assert.DoesNotContain("lighthouse", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oil painting", log, StringComparison.OrdinalIgnoreCase);

        // The negative prompt is a prompt. It travels in the body rather than in a header precisely
        // because a header is the one part of a request every proxy in the path writes down.
        Assert.DoesNotContain("a secret nobody should log", log, StringComparison.OrdinalIgnoreCase);

        // The usage row is a client, a model, a kind, a number and a unit. Nothing in it could hold
        // a prompt, and this asserts the shape rather than trusting it.
        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(ImageFixture.Model, row.Model);
        Assert.True(row.MegapixelSteps > 0);
        Assert.DoesNotContain("lighthouse", row.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of the same claim: the bytes. There is no URL, no temp file and no cache on
    /// the hub (D5), and the node's per-request scratch directory is deleted in a <c>finally</c>
    /// whatever happened (phase-41 D5).
    /// </summary>
    [Fact]
    public async Task NoImageSurvivesTheRequestOnEitherSide()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body(n: 2));
        Assert.True(response.IsSuccessStatusCode);

        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    [Fact]
    public async Task NoImageSurvivesAFailedRequestEither()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: ["--image-fail", "boom"]);

        await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());

        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    /// <summary>
    /// Phase 49 put a second string beside the prompt, and the two are not the same kind of thing.
    /// </summary>
    /// <remarks>
    /// The <b>trigger</b> is a constant of the recipe — the model's own words, identical for every
    /// caller — so it may appear in a response and in a log, and it should: "why does this not look
    /// like a panorama" is almost always "the trigger did not apply", and a diagnosis nobody can see
    /// is not one. The <b>prompt it was appended to</b> is still the caller's, and augmenting it must
    /// not become a reason to write it down.
    /// </remarks>
    [Fact]
    public async Task AnAugmentedPromptIsStillNeverLoggedThoughTheTriggerMayBe()
    {
        await using var mesh = await ImageMesh.StartAsync(
            workerArguments: ["--image-projection", "equirectangular"]);

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = ImageFixture.KnownPrompt, size = "1024x512" });

        Assert.True(response.IsSuccessStatusCode);

        var log = string.Join("\n", mesh.Logs.Lines);

        Assert.DoesNotContain(ImageFixture.KnownPrompt, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lighthouse", log, StringComparison.OrdinalIgnoreCase);

        // …and the augmented form is not there either. A worker that echoed the whole rewritten
        // prompt back into a payload the hub logs would leak the original with three words on the
        // end of it, which is a leak that reads like a feature.
        Assert.DoesNotContain("360 degree panorama", log, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The log line that <em>is</em> written. It has to carry enough to operate a fleet — which
    /// model, how much work, what happened — and the assertion is here so that a future change which
    /// adds "and the prompt was…" fails in the file that explains why it must not.
    /// </summary>
    [Fact]
    public async Task TheImageLogLineCarriesTheModelTheCountTheUnitsAndTheOutcome()
    {
        await using var mesh = await ImageMesh.StartAsync();

        await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body(n: 2));

        var line = Assert.Single(mesh.Logs.Lines, l => l.Contains("Image job "));

        Assert.Contains(ImageFixture.Model, line);
        Assert.Contains("2 image(s)", line);
        Assert.Contains("megapixel-steps", line);
        Assert.Contains("200", line);
    }
}
