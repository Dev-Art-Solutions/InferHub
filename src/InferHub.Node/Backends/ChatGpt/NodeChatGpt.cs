using InferHub.Node.Configuration;
using InferHub.Shared.ChatGpt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.ChatGpt;

/// <summary>
/// <c>ChatGpt:</c> — this node's own ChatGPT sign-in (phase 103). Read only when the node runs a
/// <c>chatgpt</c> backend or engine, or when the <c>chatgpt</c> verb is used.
/// </summary>
public sealed class ChatGptNodeOptions
{
    public const string SectionName = "ChatGpt";

    /// <summary>
    /// Where this node keeps its host id and its encrypted session. Absent means
    /// <c>&lt;Node:DataDirectory&gt;/chatgpt</c> — beside the node id, which is the one directory a
    /// node is already known to be able to write.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>The Responses API base. Overridable for a proxy; never for a paid API key.</summary>
    public string BaseUrl { get; set; } = ChatGptOAuth.Resource;

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>The preferred loopback port for this node's sign-in callback; the next free one is used when it is taken.</summary>
    public int CallbackPort { get; set; } = ChatGptOAuth.DefaultCallbackPort;

    /// <summary>After a usage-limit refusal, how long new requests on the account are refused here.</summary>
    public int UsageLimitPauseSeconds { get; set; } = 300;

    /// <summary>
    /// A narrower list of the account's models to report to the coordinator. Empty means every model
    /// the account's catalogue lists.
    /// </summary>
    public ModelFilterOptions Models { get; set; } = new();

    /// <summary>The store directory for a node whose content root is <paramref name="contentRoot"/>.</summary>
    public static string ResolveDirectory(IConfiguration configuration, string contentRoot)
    {
        contentRoot = Path.GetFullPath(contentRoot);
        var own = configuration[$"{SectionName}:{nameof(DataDirectory)}"];

        if (!string.IsNullOrWhiteSpace(own))
        {
            return Path.GetFullPath(own, contentRoot);
        }

        var node = configuration[$"{NodeOptions.SectionName}:{nameof(NodeOptions.DataDirectory)}"];
        var root = string.IsNullOrWhiteSpace(node) ? contentRoot : Path.GetFullPath(node, contentRoot);

        return Path.Combine(root, "chatgpt");
    }
}

/// <summary>
/// The node's one ChatGPT session. Its store is this node's and nobody else's: the coordinator has no
/// path to it, and nothing that crosses SignalR carries a token — only the model names the account
/// can use, through the same model report every backend makes (103 D1).
/// </summary>
public sealed class NodeChatGpt
{
    public const string HttpClientName = "chatgpt-oauth";

    private readonly IHttpClientFactory httpClientFactory;

    public NodeChatGpt(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptions<ChatGptNodeOptions> options,
        IHttpClientFactory httpClientFactory,
        TimeProvider time)
    {
        this.httpClientFactory = httpClientFactory;
        Options = options.Value;
        Directory = ChatGptNodeOptions.ResolveDirectory(configuration, environment.ContentRootPath);

        var store = new ChatGptCredentialStore(Directory);
        Session = new ChatGptSession(store, CreateHttpClient, new IdTokenValidator(async cancellationToken =>
        {
            using var http = CreateHttpClient();
            return await http.GetStringAsync(ChatGptOAuth.JwksUri, cancellationToken);
        }, time), time);
    }

    public ChatGptNodeOptions Options { get; }

    public string Directory { get; }

    public ChatGptSession Session { get; }

    public TimeSpan UsagePause => TimeSpan.FromSeconds(Math.Max(1, Options.UsageLimitPauseSeconds));

    public HttpClient CreateHttpClient()
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(60);
        return http;
    }
}
