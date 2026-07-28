using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// A deterministic stand-in for an embedding model: a bag-of-words vector, so texts that share
/// words come out close and texts that do not come out far apart.
/// </summary>
/// <remarks>
/// <para>
/// Phase 38's parity suite needs the hub and a solo node to embed <em>identically</em>, or every
/// comparison is really a comparison of two random draws. It also needs retrieval to actually rank
/// — a stub that returns one constant vector makes every chunk equidistant and turns "the right
/// document came back" into a coin toss that passes by luck.
/// </para>
/// <para>
/// It is not a model and does not pretend to be one. What it is, is a function: same text in, same
/// vector out, on both hosts, forever.
/// </para>
/// </remarks>
internal static class TestEmbeddings
{
    public const int Dimension = 32;

    public static float[] Of(string text)
    {
        var vector = new float[Dimension];

        foreach (var token in Tokenize(text))
        {
            vector[Bucket(token)] += 1f;
        }

        // L2-normalise so cosine is a plain dot product and an empty text is a zero vector rather
        // than a NaN.
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        else
        {
            vector[0] = 1f;
        }

        return vector;
    }

    /// <summary>Answers an Ollama <c>/api/embed</c> body, honouring both the string and array input shapes.</summary>
    public static string RespondTo(string requestJson)
    {
        using var doc = JsonDocument.Parse(requestJson);
        var root = doc.RootElement;

        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "test-embed"
            : "test-embed";

        var inputs = new List<string>();
        if (root.TryGetProperty("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.Array)
            {
                inputs.AddRange(input.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? string.Empty));
            }
            else if (input.ValueKind == JsonValueKind.String)
            {
                inputs.Add(input.GetString() ?? string.Empty);
            }
        }

        if (inputs.Count == 0)
        {
            inputs.Add(string.Empty);
        }

        return JsonSerializer.Serialize(new
        {
            model,
            embeddings = inputs.Select(Of).ToArray()
        });
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static int Bucket(string token)
    {
        // FNV-1a, so the mapping is stable across processes and runs — a token's bucket must not
        // depend on string hashing that is randomised per process.
        var hash = 2166136261u;
        foreach (var ch in token)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return (int)(hash % Dimension);
    }
}
