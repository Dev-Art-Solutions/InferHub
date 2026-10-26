using System.Text.RegularExpressions;
using InferHub.Shared.HuggingFace;

namespace InferHub.Shared.Fit;

/// <summary>
/// A model as the owner pastes it (phase 118): a Hugging Face link (<see cref="HfReference"/>'s forms) or a model in
/// Ollama's library — <c>qwen2.5:7b</c>, <c>ollama.com/library/qwen2.5:7b</c>, <c>ollama.com/owner/model:tag</c>.
/// </summary>
/// <remarks>
/// A bare <c>owner/repo</c> is Hugging Face: it is both an Ollama name and a repo (96 D3), and a pasted link is far more
/// often the second — an Ollama user's model is given with its <c>ollama.com/</c> host.
/// </remarks>
public sealed partial record ModelLink(HfReference? HuggingFace, OllamaName? Ollama)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.IgnoreCase)]
    private static partial Regex OllamaSegment();

    public static bool TryParse(string? text, out ModelLink? link, out string? error)
    {
        link = null;
        error = null;
        var value = (text ?? string.Empty).Trim();

        foreach (var prefix in new[] { "https://ollama.com/", "http://ollama.com/", "https://www.ollama.com/", "ollama.com/", "registry.ollama.ai/" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return TryOllama(value[prefix.Length..], text!, out link, out error);
            }
        }

        if (value.Length > 0 && !value.Contains('/') && !value.Contains("://", StringComparison.Ordinal))
        {
            return TryOllama(value, text!, out link, out error);
        }

        if (HfReference.TryParse(value, null, out var reference, out error))
        {
            link = new ModelLink(reference, null);
            return true;
        }

        return false;
    }

    private static bool TryOllama(string value, string original, out ModelLink? link, out string? error)
    {
        link = null;
        error = null;
        value = value.Split('?', '#')[0].TrimEnd('/');

        var tag = "latest";
        var colon = value.LastIndexOf(':');

        if (colon > value.LastIndexOf('/'))
        {
            tag = value[(colon + 1)..];
            value = value[..colon];
        }

        var parts = value.Split('/');

        if (parts.Length == 2 && parts[0] == "library")
        {
            parts = [parts[1]];
        }

        var (owner, model) = parts.Length switch
        {
            1 => ("library", parts[0]),
            2 => (parts[0], parts[1]),
            _ => (null, null)
        };

        if (owner is null || model is null || !OllamaSegment().IsMatch(owner) || !OllamaSegment().IsMatch(model) || !OllamaSegment().IsMatch(tag))
        {
            error = $"'{original}' is not a model in Ollama's library (give e.g. qwen2.5:7b or ollama.com/library/qwen2.5:7b)";
            return false;
        }

        link = new ModelLink(null, new OllamaName(owner.ToLowerInvariant(), model.ToLowerInvariant(), tag.ToLowerInvariant()));
        return true;
    }
}

/// <summary>A model in Ollama's registry: <c>library/qwen2.5:7b</c>; <see cref="PullName"/> is what <c>ollama pull</c> takes.</summary>
public sealed record OllamaName(string Owner, string Model, string Tag)
{
    public string PullName => Owner == "library" ? $"{Model}:{Tag}" : $"{Owner}/{Model}:{Tag}";

    public override string ToString() => PullName;
}
