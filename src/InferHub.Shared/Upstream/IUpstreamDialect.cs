namespace InferHub.Shared.Upstream;

/// <summary>
/// What a cloud provider looks like from the inside of this project: Ollama JSON in, Ollama JSON
/// out, and nothing else. Phase 61.
/// </summary>
/// <remarks>
/// <para>
/// The shape is not invented here — it is the shape
/// <see cref="InferHub.Shared.OpenAi.OpenAiUpstreamClient"/> already had, because the node's
/// <c>OpenAiBackend</c> and the coordinator's provider dispatcher have both driven it since phase 22
/// (22 D1). Naming it is what lets phases 63 and 64 add Anthropic's <c>/v1/messages</c> and Gemini's
/// <c>:generateContent</c> without either of them touching a dispatcher, a router or an endpoint.
/// </para>
/// <para>
/// <b>Ollama JSON on both sides is the whole point</b>, and it is rule 6 arriving here rather than
/// being weakened: a provider is an <em>upstream-facing</em> dialect, translated at the boundary, so
/// the mesh's internals never learn that a second wire format exists. An implementation that wants
/// to hand back its own envelope is asking for a polymorphic job payload, which 22 D3 costed out and
/// refused.
/// </para>
/// <para>
/// The <see cref="System.Net.Http.HttpClient"/> an implementation talks over is supplied and owned by
/// its caller — including for the async iterator, whose enumeration must not outlive it.
/// </para>
/// </remarks>
public interface IUpstreamDialect
{
    /// <summary>The model ids the upstream serves, or an empty list where it cannot be asked.</summary>
    Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken);

    Task<string> ChatAsync(string ollamaJson, CancellationToken cancellationToken);

    Task<string> GenerateAsync(string ollamaJson, CancellationToken cancellationToken);

    Task<string> EmbedAsync(string ollamaJson, CancellationToken cancellationToken);

    /// <summary><paramref name="kind"/> is <c>chat</c> or <c>generate</c>, as the job carries it.</summary>
    IAsyncEnumerable<string> StreamAsync(string kind, string ollamaJson, CancellationToken cancellationToken);
}
