using System.Text.Json;

namespace InferHub.Shared.Brio;

/// <summary>
/// What the edge reads of a <c>POST /v1/systemone</c> body (phase 111): the model and how many
/// questions it asks. Like <see cref="BrioRequest"/> (94 D2), everything else — the question types, their
/// criteria, the 64-question ceiling's details — is the engine's to validate, in its own words.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model is required here</b> where colibri makes it optional ("any name: the served model
/// answers"): one engine has one model, a fleet has many, and the hub routes by it. A Jev client's
/// <c>jev-latest</c> is therefore a model no node serves — a 404 that names it.
/// </para>
/// <para>
/// Rule 7: <see cref="Count"/> exists for the log line. The state and the questions never leave this record.
/// </para>
/// </remarks>
public sealed record SystemOneRequest(string Model, int Count, string Raw)
{
    public static SystemOneRequest? TryParse(string raw, out string error)
    {
        error = "";
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            error = "request body must be a JSON object";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "request body must be a JSON object";
                return null;
            }

            if (!root.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(model.GetString()))
            {
                error = "model is required: it names the served model this question goes to";
                return null;
            }

            if (!root.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Object
                || !questions.EnumerateObject().Any())
            {
                error = "questions must be an object of one or more questions, id: question";
                return null;
            }

            return new SystemOneRequest(model.GetString()!.Trim(), questions.EnumerateObject().Count(), raw);
        }
    }
}
