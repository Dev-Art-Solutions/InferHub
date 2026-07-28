using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// A coordinator on a real Kestrel port, mapping the <em>real</em> inference and OpenAI endpoints
/// over a scripted dispatcher — one node, always routable, answering with canned Ollama JSON.
/// </summary>
/// <remarks>
/// It is the other half of <see cref="SoloParityTests"/>. Everything above the dispatcher is the
/// shipped code — translators, formatters, the SSE and NDJSON writers, the error envelope — and
/// only the fleet below it is scripted. Which is the point: a fleet of one is what a solo node is,
/// so if the two hosts disagree, the disagreement is in the layer this phase touched.
/// </remarks>
internal sealed class HubHost : IAsyncDisposable
{
    private WebApplication app = null!;

    public ScriptedDispatcher Dispatcher { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The Ollama-shaped job body the hub handed the node, for cross-checking translation.</summary>
    public string? LastJobJson => Dispatcher.LastJobJson;

    public static async Task<HubHost> StartAsync(
        string blockingResponse,
        string[]? streamChunks = null,
        string? failure = null,
        IReadOnlyList<ModelInfo>? models = null)
    {
        var host = new HubHost();
        host.Dispatcher.BlockingResponse = blockingResponse;
        host.Dispatcher.Failure = failure;
        host.Dispatcher.StreamChunks = streamChunks ?? [];

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The real registry with one real registration: /v1/models and /api/tags then run the
        // shipped code path rather than a stub's idea of it.
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("conn-1", new NodeRegistration("node-1", "parity-node", "http://localhost:11434/", "test"), now);
        registry.ReportModels(
            "conn-1",
            new NodeModels("node-1", models ?? [new ModelInfo("llama3", "sha256:abc", 4661211808)], now),
            now);

        builder.Services.AddSingleton<INodeRegistry>(registry);
        builder.Services.AddSingleton<IRouter>(new AlwaysRoutes());
        builder.Services.AddSingleton<IDispatcher>(host.Dispatcher);
        builder.Services.AddSingleton<IFallbackDispatcher>(new NoFallback());
        builder.Services.AddSingleton<IEmbeddingDispatcher>(new ScriptedEmbeddings());
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<AdmissionControl>();
        builder.Services.AddSingleton(services => TestUsage.Meter(
            admission: services.GetRequiredService<AdmissionControl>()));
        builder.Services.AddSingleton(services => TestUsage.Queue(
            services.GetRequiredService<INodeRegistry>()));

        host.app = builder.Build();
        host.app.MapInferenceEndpoints();
        host.app.MapOpenAiEndpoints();

        await host.app.StartAsync();

        host.Client = new HttpClient { BaseAddress = new Uri(host.app.Urls.First()) };

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    // ---- the scripted fleet ------------------------------------------------------------------

    internal sealed class ScriptedDispatcher : IDispatcher
    {
        public string BlockingResponse { get; set; } = "{}";

        public string[] StreamChunks { get; set; } = [];

        public string? Failure { get; set; }

        public string? LastJobJson { get; private set; }

        public Task<InferenceResult> DispatchAsync(
            RoutableNode node,
            InferenceJob job,
            CancellationToken cancellationToken)
        {
            LastJobJson = job.RequestJson;

            return Task.FromResult(Failure is null
                ? InferenceResult.Succeeded(job.JobId, BlockingResponse)
                : InferenceResult.Failed(job.JobId, Failure));
        }

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(
            RoutableNode node,
            InferenceJob job,
            CancellationToken cancellationToken)
        {
            LastJobJson = job.RequestJson;

            var channel = Channel.CreateUnbounded<InferenceChunk>();

            foreach (var chunk in StreamChunks)
            {
                channel.Writer.TryWrite(new InferenceChunk(job.JobId, chunk, IsTerminal(chunk)));
            }

            channel.Writer.TryComplete();
            return Task.FromResult(channel.Reader);
        }

        internal static bool IsTerminal(string chunkJson)
            => chunkJson.Replace(" ", string.Empty).Contains("\"done\":true");

        public bool Complete(InferenceResult result) => true;

        public bool WriteChunk(InferenceChunk chunk) => true;

        public void FailForConnection(string connectionId, Exception? exception)
        {
        }
    }

    private sealed class AlwaysRoutes : IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null)
            => new("conn-1", "node-1", "parity-node");
    }

    private sealed class NoFallback : IFallbackDispatcher
    {
        public bool ShouldServe(string model, bool hasCapableNode) => false;

        public Task<FallbackResult> DispatchAsync(
            string kind,
            string rawJson,
            string model,
            bool stream,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ScriptedEmbeddings : IEmbeddingDispatcher
    {
        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult("""{"model":"nomic","embeddings":[[0.1,0.2,0.3]]}""");

        public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
            => Task.FromResult<float[]>([0.1f, 0.2f, 0.3f]);
    }
}
