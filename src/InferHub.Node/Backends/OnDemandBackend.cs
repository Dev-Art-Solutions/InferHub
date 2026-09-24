using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InferHub.Node.Resources;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

/// <summary>
/// The inference backend as one tenant of <see cref="GpuArbiter"/> (phase 85). Every call that makes
/// the backend load weights holds the card for its whole duration — a stream until its last chunk —
/// and listing, pulling and deleting models do not, because none of them touch the GPU.
/// </summary>
/// <remarks>
/// A decorator rather than a branch in <see cref="OllamaBackend"/>, so the backend stays the thing
/// that talks to Ollama and nothing else, and the mesh, solo mode and retrieval's embeddings all go
/// through the gate without any of them knowing it exists.
/// <para>
/// It remembers which models it asked for, and the release unloads <b>those</b> — the Ollama on a
/// desktop is shared with whatever else its owner runs, and their models are not ours to drop.
/// </para>
/// </remarks>
public sealed class OnDemandBackend : IInferenceBackend
{
    public const string Tenant = "ollama";

    private readonly IInferenceBackend inner;
    private readonly GpuArbiter arbiter;
    private readonly ConcurrentDictionary<string, byte> loaded = new(StringComparer.OrdinalIgnoreCase);

    public OnDemandBackend(
        IInferenceBackend inner,
        GpuArbiter arbiter,
        Func<IReadOnlyCollection<string>, CancellationToken, Task> unload)
    {
        this.inner = inner;
        this.arbiter = arbiter;

        // Runs only with nothing of this tenant in flight, so nothing can be added while it reads.
        arbiter.RegisterReleaser(Tenant, async cancellationToken =>
        {
            var models = loaded.Keys.ToArray();
            loaded.Clear();
            await unload(models, cancellationToken);
        });
    }

    /// <summary>What the next release will unload. For tests.</summary>
    internal IReadOnlyCollection<string> Loaded => loaded.Keys.ToArray();

    public string Name => inner.Name;

    public string Endpoint => inner.Endpoint;

    public IReadOnlyList<string> Kinds => inner.Kinds;

    public bool SupportsModelManagement => inner.SupportsModelManagement;

    public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
        => inner.ListModelsAsync(cancellationToken);

    public async Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(requestJson, cancellationToken);
        return await inner.GenerateAsync(requestJson, cancellationToken);
    }

    public async Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(requestJson, cancellationToken);
        return await inner.ChatAsync(requestJson, cancellationToken);
    }

    public async Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(requestJson, cancellationToken);
        return await inner.EmbedAsync(requestJson, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(requestJson, cancellationToken);

        await foreach (var chunk in inner.StreamAsync(kind, requestJson, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return chunk;
        }
    }

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
        => inner.PullAsync(model, cancellationToken);

    public Task DeleteAsync(string model, CancellationToken cancellationToken)
        => inner.DeleteAsync(model, cancellationToken);

    public async Task WarmAsync(string model, CancellationToken cancellationToken)
    {
        // A warm is a load, so it takes the card like any request — and then the release timer
        // gives it back, which makes "warm" mean "for the next ReleaseAfterSeconds" on such a node.
        await using var lease = await arbiter.AcquireAsync(Tenant, cancellationToken);
        loaded[model] = 0;
        await inner.WarmAsync(model, cancellationToken);
    }

    /// <summary>Takes the card, then records the model — after, so a refusal records nothing.</summary>
    private async Task<IAsyncDisposable> AcquireAsync(string requestJson, CancellationToken cancellationToken)
    {
        var lease = await arbiter.AcquireAsync(Tenant, cancellationToken);

        if (ModelOf(requestJson) is { } model)
        {
            loaded[model] = 0;
        }

        return lease;
    }

    private static string? ModelOf(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(model.GetString())
                    ? model.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            // The backend will refuse it with a better message than this layer could.
            return null;
        }
    }
}
