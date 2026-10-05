using System.Text.Json;

namespace InferHub.Shared.Brio;

/// <summary>
/// What the edge reads of a <c>POST /v1/brio</c> body (phase 94, D2): the model, which of the three
/// forms it is, and how many things it asks about. Everything else is the engine's to validate.
/// </summary>
/// <remarks>
/// <para>
/// The body itself is passed through untouched as the <c>ToolJob</c> payload. colibri's gateway owns
/// the rules — option limits, duplicates, field names — and its <c>400</c> comes back with its own
/// sentence; a second copy of those rules here would be a second answer that drifts the day the
/// engine changes one.
/// </para>
/// <para>
/// <see cref="Count"/> exists for the log line and nothing else. Rule 7: a <c>state</c> is a document
/// somebody wanted judged and an option is part of the question, so neither leaves this record.
/// </para>
/// </remarks>
public sealed record BrioRequest(string Model, string Form, int Count, string Raw)
{
    public const string Options = "options";

    public const string Questions = "questions";

    public const string Schema = "schema";

    private static readonly string[] Forms = [Options, Questions, Schema];

    public static BrioRequest? TryParse(string raw, out string error)
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
                error = "model is required";
                return null;
            }

            // colibri's own sentence, so a caller reads the same refusal from the edge as from the
            // engine behind it. A null form is absent, exactly as the gateway reads it.
            var present = Forms
                .Where(form => root.TryGetProperty(form, out var value) && value.ValueKind != JsonValueKind.Null)
                .ToArray();

            if (present.Length != 1)
            {
                error = "Provide exactly one of `options`, `questions` or `schema`.";
                return null;
            }

            var form = present[0];
            var body = root.GetProperty(form);

            var count = body.ValueKind switch
            {
                JsonValueKind.Array => body.GetArrayLength(),
                JsonValueKind.Object => body.EnumerateObject().Count(),
                _ => 0
            };

            return new BrioRequest(model.GetString()!.Trim(), form, count, raw);
        }
    }
}
