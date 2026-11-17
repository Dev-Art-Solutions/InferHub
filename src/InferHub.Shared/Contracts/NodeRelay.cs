namespace InferHub.Shared.Contracts;

/// <summary>
/// Phase 122. The coordinator's relay for a sealed node (<c>Node:Sealed</c>): the one door such a node has to
/// the internet is the hub it already dials, so what it would have fetched itself — a Hugging Face file, an
/// Ollama registry blob, a page of Ollama's library — it asks the hub for, at
/// <c>{hub}/node/relay/{upstream}/{path}</c>, with its enrollment secret.
/// </summary>
/// <remarks>
/// Three upstreams by name rather than a URL in the request: a relay that fetches any URL a node names is an open
/// proxy with the hub's address on it, and the node never needed one.
/// </remarks>
public static class NodeRelay
{
    public const string PathPrefix = "/node/relay";

    /// <summary>Header the node presents — the same one the SignalR handshake carries.</summary>
    public const string SecretHeader = "X-Node-Enrollment-Secret";

    /// <summary>Phase 123: <c>hit</c> or <c>miss</c> on a blob the hub's cache could hold; absent on anything else.</summary>
    public const string CacheHeader = "X-InferHub-Relay-Cache";

    /// <summary>
    /// Phase 124: <c>supplied</c> on an answer from the hub's supply (the upstream was not asked), <c>not-supplied</c>
    /// on the 404 a hub with <c>Relay:Supply:Only</c> gives for anything else; absent while the supply is off.
    /// </summary>
    public const string SupplyHeader = "X-InferHub-Relay-Supply";

    public const string Supplied = "supplied";

    public const string NotSupplied = "not-supplied";

    public const string HuggingFace = "hf";

    public const string OllamaRegistry = "ollama-registry";

    public const string OllamaLibrary = "ollama-library";

    public static readonly string[] Upstreams = [HuggingFace, OllamaRegistry, OllamaLibrary];

    /// <summary>
    /// Phase 151: node releases this coordinator supplied. Not an upstream — never relayed live, only answered from the
    /// supply: <c>{hub}/node/relay/releases</c> is a list in GitHub's shape, <c>…/releases/download/{tag}/{file}</c> a file.
    /// </summary>
    public const string Releases = "releases";
}

/// <summary>
/// Phase 151. What a node release is made of, read the same way by the node that installs it (101) and the hub that
/// supplies it: the Windows setup and the SHA-256 published beside it.
/// </summary>
public static class NodeReleaseAssets
{
    /// <summary>The setup's file name for a version (<c>3.104.0</c>) — what the release workflow attaches.</summary>
    public static string SetupName(string version) => $"InferHub-Node-Setup-{version}-win-x64.exe";

    public const string SetupPrefix = "InferHub-Node-Setup-";

    public const string SetupSuffix = "-win-x64.exe";

    /// <summary>
    /// <c>sha256sum</c>'s shape (<c>&lt;hex&gt;  &lt;name&gt;</c>), PowerShell's <c>Get-FileHash</c> hex alone, or either in
    /// upper case. A file listing several names is matched by the setup's. Null when it holds no SHA-256.
    /// </summary>
    public static string? ParseChecksum(string text, string setupName)
    {
        string? first = null;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit))
            {
                continue;
            }

            if (parts.Length > 1 && string.Equals(parts[^1], setupName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }

            first ??= parts[0].ToLowerInvariant();
        }

        return first;
    }
}
