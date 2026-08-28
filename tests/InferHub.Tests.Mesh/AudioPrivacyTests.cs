using System.Net.Http.Json;
using InferHub.Coordinator.Services;

namespace InferHub.Tests;

/// <summary>
/// Design rule 7, at its most literal (phase 42, D5): a transcription request is a recording of
/// somebody's voice and the answer is what they said, so <b>none of it is kept</b>.
/// </summary>
/// <remarks>
/// <para>
/// Shaped after <c>UsageLedgerTests.NoPromptOrCompletionTextExistsAnywhereInTheUsagePath</c> and
/// asking the harder version of the question: not "is the field absent" but "does this phrase appear
/// <em>anywhere</em>". The mesh runs with a capturing logger at <c>Trace</c>, so anything the hub
/// wrote at any level is in scope, and the assertion is over the whole log rather than a line
/// somebody remembered to check.
/// </para>
/// <para>
/// The audio side is the same claim from the other end: what the caller uploaded is held for the
/// dispatch and dropped. There is no temp file on the hub, and the node's per-request scratch
/// directory is deleted in a <c>finally</c> whatever happened (phase-41 D5).
/// </para>
/// </remarks>
public class AudioPrivacyTests
{
    private const string SpokenText = "InferHub can talk now, and this sentence must not be logged.";

    [Fact]
    public async Task NoTranscriptTextAppearsInAnyLogLineOrInTheLedger()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync(
            "/v1/audio/transcriptions",
            new MultipartFormDataContent
            {
                { new StringContent(AudioFixture.TranscribeModel), "model" },
                { new ByteArrayContent("not really audio"u8.ToArray()), "file", "board-meeting.m4a" }
            });

        // The caller did get the words — the point is that only the caller did.
        Assert.Contains(AudioFixture.KnownPhrase, await response.Content.ReadAsStringAsync());

        var log = string.Join("\n", mesh.Logs.Lines);
        Assert.DoesNotContain(AudioFixture.KnownPhrase, log, StringComparison.OrdinalIgnoreCase);

        // Not one word of it, either. "fox" is in the fixture phrase and nowhere in this codebase.
        Assert.DoesNotContain("quick brown", log, StringComparison.OrdinalIgnoreCase);

        // The usage row is a client, a model, a kind, a number and a unit. Nothing about the row
        // could hold a transcript, and this asserts the shape rather than trusting it.
        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(AudioFixture.TranscribeModel, row.Model);
        Assert.Equal(3.25, row.AudioSeconds);
        Assert.DoesNotContain(AudioFixture.KnownPhrase, row.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoSynthesisedTextAppearsInAnyLogLineOrInTheLedger()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync(
            "/v1/audio/speech",
            JsonContent.Create(new { model = AudioFixture.SpeakModel, input = SpokenText, response_format = "wav" }));

        Assert.True(response.IsSuccessStatusCode);

        var log = string.Join("\n", mesh.Logs.Lines);
        Assert.DoesNotContain(SpokenText, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must not be logged", log, StringComparison.OrdinalIgnoreCase);

        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(SpokenText.Length, row.Characters);
        Assert.DoesNotContain(SpokenText, row.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phase 70: the streamed path writes a different log line, at a different moment, from a
    /// different method — which is exactly the shape of change that reintroduces this bug.
    /// </summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("sse")]
    public async Task NoSynthesisedTextAppearsAnywhereWhenTheAnswerIsStreamed(string streamFormat)
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync(
            "/v1/audio/speech",
            JsonContent.Create(new
            {
                model = AudioFixture.SpeakModel,
                input = SpokenText,
                response_format = "pcm",
                stream_format = streamFormat
            }));

        Assert.True(response.IsSuccessStatusCode);

        var log = string.Join("\n", mesh.Logs.Lines);
        Assert.DoesNotContain(SpokenText, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must not be logged", log, StringComparison.OrdinalIgnoreCase);

        // The line that IS written carries the counts and nothing that could hold a sample.
        var line = Assert.Single(mesh.Logs.Lines, l => l.Contains("Streamed speech "));
        Assert.Contains(AudioFixture.SpeakModel, line);
        Assert.Contains($"{SpokenText.Length} characters", line);

        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(SpokenText.Length, row.Characters);
    }

    /// <summary>
    /// The log line that <em>is</em> written. It has to carry enough to operate the fleet — which
    /// model, how much work, what happened — and the assertion is here so that a future change
    /// which adds "and the text was…" fails in the file that explains why it must not.
    /// </summary>
    [Fact]
    public async Task TheTranscriptionLogLineCarriesTheModelTheDurationAndTheOutcome()
    {
        await using var mesh = await AudioMesh.StartAsync();

        await mesh.Client.PostAsync(
            "/v1/audio/transcriptions",
            new MultipartFormDataContent
            {
                { new StringContent(AudioFixture.TranscribeModel), "model" },
                { new ByteArrayContent("not really audio"u8.ToArray()), "file", "board-meeting.m4a" }
            });

        var line = Assert.Single(mesh.Logs.Lines, l => l.Contains("Transcription "));

        Assert.Contains(AudioFixture.TranscribeModel, line);
        Assert.Contains("3.2s of audio", line);
        Assert.Contains("200", line);

        // Not the filename the caller chose, either: "board-meeting" is metadata about somebody's
        // day, and it is not needed to run a fleet.
        Assert.DoesNotContain("board-meeting", string.Join("\n", mesh.Logs.Lines), StringComparison.OrdinalIgnoreCase);
    }
}
