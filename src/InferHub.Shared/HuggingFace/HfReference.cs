using System.Text.RegularExpressions;

namespace InferHub.Shared.HuggingFace;

/// <summary>
/// A Hugging Face model, as an operator pastes it (phase 98): a repo, a revision, and optionally one
/// file or a quantization. Pure, so the hub refuses a bad link before it travels and the node reads
/// the same thing the hub checked.
/// </summary>
/// <remarks>
/// Accepted: <c>https://huggingface.co/owner/repo</c> (with <c>/tree/rev</c>, <c>/blob/rev/path</c>
/// or <c>/resolve/rev/path</c>), <c>hf.co/owner/repo[:quant]</c>, <c>owner/repo[:quant]</c>, and the
/// canonical form <see cref="ToString"/> writes: <c>owner/repo[@rev][:quant]</c> or
/// <c>owner/repo[@rev]//path/in/repo</c>. Never a URL of another host: the node fetches from its own
/// configured endpoint, so a link cannot point it anywhere else.
/// </remarks>
public sealed partial record HfReference(string Owner, string Repo, string Revision = "main", string? File = null, string? Quant = null)
{
    public const string DefaultRevision = "main";

    public string RepoId => $"{Owner}/{Repo}";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,95}$")]
    private static partial Regex Segment();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex QuantPattern();

    public override string ToString()
    {
        var revision = Revision == DefaultRevision ? string.Empty : "@" + Revision;

        return File is not null
            ? $"{RepoId}{revision}//{File}"
            : Quant is not null ? $"{RepoId}{revision}:{Quant}" : RepoId + revision;
    }

    /// <summary>Parses a link; <paramref name="quant"/> is the separate field a form carries, and wins over one in the link.</summary>
    public static bool TryParse(string? link, string? quant, out HfReference? reference, out string? error)
    {
        reference = null;
        error = null;
        var text = (link ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            error = "a Hugging Face link is required";
            return false;
        }

        foreach (var prefix in new[] { "https://huggingface.co/", "http://huggingface.co/", "https://hf.co/", "http://hf.co/", "huggingface.co/", "hf.co/" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            error = $"'{link}' is not a Hugging Face link; give https://huggingface.co/<owner>/<repo>";
            return false;
        }

        text = text.Split('?', '#')[0].TrimEnd('/');

        string? file = null;
        var doubleSlash = text.IndexOf("//", StringComparison.Ordinal);

        if (doubleSlash >= 0)
        {
            file = text[(doubleSlash + 2)..];
            text = text[..doubleSlash];
        }

        var parts = text.Split('/');

        if (parts.Length < 2)
        {
            error = $"'{link}' names no repository; give <owner>/<repo>";
            return false;
        }

        var owner = parts[0];
        var repo = parts[1];
        var revision = DefaultRevision;
        string? linkQuant = null;

        var colon = repo.IndexOf(':');

        if (colon >= 0)
        {
            linkQuant = repo[(colon + 1)..];
            repo = repo[..colon];
        }

        var at = repo.IndexOf('@');

        if (at >= 0)
        {
            revision = repo[(at + 1)..];
            repo = repo[..at];
        }

        if (parts.Length > 2)
        {
            // /tree/<rev>, /blob/<rev>/<path>, /resolve/<rev>/<path>
            if (parts.Length < 4 || parts[2] is not ("tree" or "blob" or "resolve"))
            {
                error = $"'{link}' is not a repository or a file in one";
                return false;
            }

            revision = parts[3];

            if (parts[2] != "tree" && parts.Length > 4)
            {
                file = string.Join('/', parts[4..]);
            }
        }

        if (!Segment().IsMatch(owner) || !Segment().IsMatch(repo))
        {
            error = $"'{owner}/{repo}' is not a Hugging Face repository name";
            return false;
        }

        if (!Segment().IsMatch(revision))
        {
            error = $"'{revision}' is not a revision (a branch, tag or commit)";
            return false;
        }

        if (file is not null && (file.Length == 0 || file.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\'))))
        {
            error = $"'{file}' is not a path inside a repository";
            return false;
        }

        var chosen = string.IsNullOrWhiteSpace(quant) ? linkQuant : quant.Trim();

        if (chosen is not null && !QuantPattern().IsMatch(chosen))
        {
            error = $"'{chosen}' is not a quantization name (e.g. Q4_K_M)";
            return false;
        }

        reference = new HfReference(owner, repo, revision, file, file is null ? chosen : null);
        return true;
    }
}
