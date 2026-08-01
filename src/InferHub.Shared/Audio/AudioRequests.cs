using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Audio;

/// <summary>
/// A validated <c>POST /v1/audio/transcriptions</c> request, and the tool payload it becomes.
/// </summary>
/// <remarks>
/// <para>
/// Validation lives here rather than in either host because both hosts serve this route and a
/// client must not be able to tell which one answered it (phase-37 D6, phase-38 D9). Reading the
/// multipart form is ASP.NET and stays per host — design rule 2 keeps ASP.NET out of this library —
/// but the sentences a caller reads, and the JSON the worker receives, are decided once.
/// </para>
/// <para>
/// <b>Nothing here retains the audio.</b> The bytes travel as a <c>ToolAttachment</c> and this
/// record never sees them; what it carries is the model, the format and three optional hints.
/// </para>
/// </remarks>
public sealed record TranscriptionRequest(
    string Model,
    string ResponseFormat,
    string? Language,
    string? Prompt,
    double? Temperature)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Validates the form fields. Returns null and sets <paramref name="error"/> on the first
    /// problem, in the order a caller would hit them.
    /// </summary>
    public static TranscriptionRequest? TryCreate(
        string? model,
        string? responseFormat,
        string? language,
        string? prompt,
        string? temperature,
        bool hasFile,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "model is required";
            return null;
        }

        if (!hasFile)
        {
            // Named, because the field is `file` and the most common mistake is `-F audio=@…`.
            error = "a 'file' part is required";
            return null;
        }

        var format = string.IsNullOrWhiteSpace(responseFormat)
            ? TranscriptionFormats.Json
            : responseFormat.Trim().ToLowerInvariant();

        if (!TranscriptionFormats.IsKnown(format))
        {
            error = TranscriptionFormats.Refusal(responseFormat);
            return null;
        }

        double? parsedTemperature = null;

        if (!string.IsNullOrWhiteSpace(temperature))
        {
            if (!double.TryParse(
                    temperature,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value))
            {
                error = $"temperature '{temperature}' is not a number";
                return null;
            }

            parsedTemperature = value;
        }

        return new TranscriptionRequest(
            model.Trim(),
            format,
            Blank(language),
            Blank(prompt),
            parsedTemperature);
    }

    /// <summary>
    /// The payload the worker receives. <c>response_format</c> is deliberately <b>not</b> in it: a
    /// worker always answers with the verbose shape and the edge formats, so a worker author never
    /// writes an SRT timestamp.
    /// </summary>
    public string ToToolPayload() => JsonSerializer.Serialize(
        new
        {
            model = Model,
            language = Language,
            prompt = Prompt,
            temperature = Temperature
        },
        Json);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>A validated <c>POST /v1/audio/speech</c> request, and the tool payload it becomes.</summary>
public sealed record SpeechRequest(
    string Model,
    string Input,
    string? Voice,
    string ResponseFormat,
    double? Speed)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Parses and validates the JSON body. The shape is OpenAI's, so an SDK's
    /// <c>audio.speech.create(...)</c> works against this unchanged.
    /// </summary>
    public static SpeechRequest? TryParse(string rawJson, out string error)
    {
        error = string.Empty;

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return null;
        }

        if (root.ValueKind is not JsonValueKind.Object)
        {
            error = "the request body must be a JSON object";
            return null;
        }

        var model = String(root, "model");

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "model is required";
            return null;
        }

        var input = String(root, "input");

        if (string.IsNullOrEmpty(input))
        {
            // Empty rather than whitespace: " " is a legitimate thing to synthesise (a pause), and
            // refusing it would be us deciding what a caller meant.
            error = "input is required";
            return null;
        }

        var format = String(root, "response_format");
        format = string.IsNullOrWhiteSpace(format) ? SpeechFormats.Wav : format.Trim().ToLowerInvariant();

        if (!SpeechFormats.IsKnown(format))
        {
            error = SpeechFormats.Refusal(String(root, "response_format"));
            return null;
        }

        double? speed = null;

        if (root.TryGetProperty("speed", out var speedElement) && speedElement.ValueKind is JsonValueKind.Number)
        {
            speed = speedElement.GetDouble();

            if (speed is < 0.25 or > 4.0)
            {
                error = "speed must be between 0.25 and 4.0";
                return null;
            }
        }

        return new SpeechRequest(model!.Trim(), input!, String(root, "voice"), format, speed);
    }

    /// <summary>
    /// What is metered (D7): <b>input characters</b>, counted here rather than reported by the
    /// worker. The unit is a fact about the request, so the edge already knows it and does not have
    /// to trust a third-party script for a number that appears on somebody's bill.
    /// </summary>
    public long Characters => Input.Length;

    public string ToToolPayload() => JsonSerializer.Serialize(
        new
        {
            model = Model,
            input = Input,
            voice = Voice,
            response_format = ResponseFormat,
            speed = Speed
        },
        Json);

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
