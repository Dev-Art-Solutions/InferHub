using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;

namespace InferHub.Shared.Audio;

/// <summary>
/// What the client sees, decided once for both hosts (phase 42).
/// </summary>
/// <remarks>
/// <para>
/// It is not an <c>IResult</c> and it must not become one: design rule 2 keeps ASP.NET out of
/// <c>InferHub.Shared</c>, and phase-37 D6 draws the line at exactly this level — the frame
/// <em>bodies</em> are shared, the ten lines that write them to a response are per host. So this
/// says "200, <c>text/vtt</c>, these characters" and the coordinator and the solo node each write
/// it their own way.
/// </para>
/// <para>
/// The alternative — each host deciding its own statuses — is what <c>AudioParityTests</c> exists to
/// catch, and the shape of bug it catches is a caller who moves from a hub to a solo node and finds
/// that an unproducible format is a 400 on one and a 502 on the other.
/// </para>
/// </remarks>
public sealed record AudioOutcome
{
    public required int Status { get; init; }

    /// <summary>The response content type. Null on a failure — the envelope is always JSON.</summary>
    public string? ContentType { get; init; }

    /// <summary>A textual body (JSON, plain text, SRT, WebVTT), or null when the body is bytes.</summary>
    public string? Text { get; init; }

    /// <summary>A binary body (the synthesised audio), or null when the body is text.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Download name for a binary body.</summary>
    public string? FileName { get; init; }

    public string? Error { get; init; }

    public string ErrorType { get; init; } = OpenAiErrorTypes.ApiError;

    public string? ErrorCode { get; init; }

    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// What to meter, in the unit the work is actually in (D7). Zero when there is nothing to
    /// charge for — a failed job is not billed.
    /// </summary>
    public double Units { get; init; }

    public string UnitKind { get; init; } = UsageUnitKinds.Tokens;

    public bool IsError => Status >= 400;
}

public static class AudioRenderer
{
    /// <summary>Turns a transcription tool result into the response the caller gets.</summary>
    public static AudioOutcome Transcription(ToolResult result, TranscriptionRequest request)
    {
        if (Failure(result) is { } failure)
        {
            return failure;
        }

        var transcript = Transcript.TryParse(result.Payload);

        if (transcript is null)
        {
            return new AudioOutcome
            {
                Status = 502,
                Error = "the transcription worker returned no text",
                ErrorType = OpenAiErrorTypes.ApiError
            };
        }

        // Refused rather than emitted empty. A zero-cue WebVTT file is not an error anywhere in the
        // toolchain that consumes it: it opens, it plays, and it shows nothing — so the caller
        // concludes the audio was silent rather than that they asked a worker for something it does
        // not produce.
        if (TranscriptionFormats.NeedsSegments(request.ResponseFormat) && transcript.Segments.Count == 0)
        {
            return new AudioOutcome
            {
                Status = 502,
                Error =
                    $"the transcription worker returned no segments, so '{request.ResponseFormat}' cannot be produced. " +
                    $"Use response_format={TranscriptionFormats.Json} or {TranscriptionFormats.Text}.",
                ErrorType = OpenAiErrorTypes.ApiError
            };
        }

        return new AudioOutcome
        {
            Status = 200,
            ContentType = TranscriptionFormats.ContentTypeOf(request.ResponseFormat),
            Text = TranscriptFormatter.Format(transcript, request.ResponseFormat),

            // The duration the worker measured off the file it decoded, not one derived from the
            // upload's byte count — a variable-bitrate file would make that a guess.
            Units = transcript.Duration ?? 0,
            UnitKind = UsageUnitKinds.AudioSeconds
        };
    }

    /// <summary>Turns a speech tool result into the response the caller gets.</summary>
    public static AudioOutcome Speech(ToolResult result, SpeechRequest request)
    {
        if (Failure(result) is { } failure)
        {
            return failure;
        }

        var audio = result.Attachments is { Count: > 0 } attachments ? attachments[0] : null;

        if (audio is null || audio.Bytes.Length == 0)
        {
            return new AudioOutcome
            {
                Status = 502,
                Error = "the speech worker returned no audio",
                ErrorType = OpenAiErrorTypes.ApiError
            };
        }

        return new AudioOutcome
        {
            Status = 200,

            // The format the caller asked for, not the media type the worker labelled its file
            // with. The edge already refused every format that is not in the known set, and a
            // worker that mislabels its own output would otherwise hand a caller an mp3 announced
            // as a wav.
            ContentType = SpeechFormats.ContentTypeOf(request.ResponseFormat),
            Bytes = audio.Bytes,
            FileName = SpeechFormats.FileNameFor(request.ResponseFormat),
            Units = request.Characters,
            UnitKind = UsageUnitKinds.Characters
        };
    }

    /// <summary>
    /// The failure shapes both routes share. Nothing here reads the error <em>text</em> to decide a
    /// status — the node states the kind and this renders it (phase-29 D6, phase-41's
    /// <c>RetryAfterSeconds</c>, phase-42's <c>ToolErrorCodes</c>).
    /// </summary>
    private static AudioOutcome? Failure(ToolResult result)
    {
        if (result.Success)
        {
            return null;
        }

        if (result.RetryAfterSeconds is { } retryAfter)
        {
            return new AudioOutcome
            {
                Status = 503,
                Error = result.Error ?? "the tool is busy",
                ErrorType = OpenAiErrorTypes.ApiError,
                ErrorCode = "tool_busy",
                RetryAfterSeconds = retryAfter
            };
        }

        if (ToolErrorCodes.IsClientError(result.ErrorCode))
        {
            return new AudioOutcome
            {
                Status = 400,
                Error = NodeErrorText.Readable(result.Error),
                ErrorType = OpenAiErrorTypes.InvalidRequest,
                ErrorCode = result.ErrorCode
            };
        }

        return new AudioOutcome
        {
            Status = 502,
            Error = NodeErrorText.Readable(result.Error),
            ErrorType = OpenAiErrorTypes.ApiError,
            ErrorCode = result.ErrorCode
        };
    }
}
