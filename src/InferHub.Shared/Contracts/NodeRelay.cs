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

    public const string HuggingFace = "hf";

    public const string OllamaRegistry = "ollama-registry";

    public const string OllamaLibrary = "ollama-library";

    public static readonly string[] Upstreams = [HuggingFace, OllamaRegistry, OllamaLibrary];
}
