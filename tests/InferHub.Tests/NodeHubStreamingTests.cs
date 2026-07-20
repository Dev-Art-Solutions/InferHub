using System.Collections.Concurrent;
using System.Reflection;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace InferHub.Tests;

/// <summary>
/// These cross the real SignalR wire — a real Kestrel host, a real <see cref="HubConnection"/>,
/// real protocol negotiation and argument binding.
///
/// Why they exist: streaming was silently broken end-to-end for several releases. Every unit
/// test passed the whole time, because they all stub <see cref="IDispatcher"/> and never touch
/// the hub. The bug lived in the one seam nothing covered — SignalR's binding of
/// <c>NodeHub.StreamChunks</c> — so `stream: true` hung and returned nothing on both the Ollama
/// and OpenAI surfaces. A test that does not leave the process could not have caught it.
/// </summary>
public class NodeHubStreamingTests
{
    private const string Secret = "test-enrollment-secret";

    [Fact]
    public async Task StreamChunksBindsAndDeliversChunksToTheDispatcher()
    {
        await using var host = await NodeHubHost.StartAsync();
        var connection = await host.ConnectNodeAsync();

        var jobId = Guid.NewGuid();

        await connection.InvokeAsync("StreamChunks", Chunks(
            new InferenceChunk(jobId, """{"message":{"content":"He"},"done":false}""", false),
            new InferenceChunk(jobId, """{"message":{"content":"llo"},"done":false}""", false),
            new InferenceChunk(jobId, """{"message":{"content":""},"done":true}""", true)));

        var received = host.Dispatcher.Chunks;

        Assert.Equal(3, received.Count);
        Assert.All(received, chunk => Assert.Equal(jobId, chunk.JobId));
        Assert.True(received[^1].Done);
        Assert.Contains("He", received[0].ResponseJson);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task StreamChunksSurvivesAStreamWithASingleTerminalChunk()
    {
        await using var host = await NodeHubHost.StartAsync();
        var connection = await host.ConnectNodeAsync();

        var jobId = Guid.NewGuid();
        await connection.InvokeAsync("StreamChunks", Chunks(
            new InferenceChunk(jobId, """{"response":"done","done":true}""", true)));

        Assert.Single(host.Dispatcher.Chunks);
        Assert.True(host.Dispatcher.Chunks[0].Done);

        await connection.DisposeAsync();
    }

    /// <summary>
    /// The exact shape of the bug, pinned so it cannot come back.
    ///
    /// SignalR only treats a <see cref="CancellationToken"/> parameter as synthetic (supplied
    /// by the server) on hub methods that <em>return</em> a stream. <c>StreamChunks</c> returns
    /// <c>Task</c> — it is a client-to-server upload — so a token parameter is counted as a
    /// real argument the caller is expected to send. The client sends none (the enumerable
    /// travels as a stream, not an argument), the binder throws "Invocation provides 0
    /// argument(s) but target expects 1", the stream never binds, and the caller hangs forever.
    /// </summary>
    [Fact]
    public void StreamChunksMustNotDeclareACancellationTokenParameter()
    {
        var method = typeof(NodeHub).GetMethod(nameof(NodeHub.StreamChunks), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var parameters = method!.GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(IAsyncEnumerable<InferenceChunk>), parameters[0].ParameterType);
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(CancellationToken));
    }

    private static async IAsyncEnumerable<InferenceChunk> Chunks(params InferenceChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    /// <summary>A real coordinator host with only the node hub mapped and the services faked.</summary>
    private sealed class NodeHubHost : IAsyncDisposable
    {
        private WebApplication app = null!;

        public RecordingDispatcher Dispatcher { get; } = new();

        public string Url { get; private set; } = null!;

        public static async Task<NodeHubHost> StartAsync()
        {
            var host = new NodeHubHost();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new StaticOptionsMonitor(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<IDispatcher>(host.Dispatcher);
            builder.Services.AddSingleton<INodeRegistry, NoopRegistry>();
            builder.Services.AddSingleton<IConversationAffinity, NoopAffinity>();
            builder.Services.AddSingleton<INodeConnectionTracker, NoopConnections>();

            host.app = builder.Build();
            host.app.MapHub<NodeHub>("/hubs/node");

            await host.app.StartAsync();
            host.Url = host.app.Urls.First();

            return host;
        }

        public async Task<HubConnection> ConnectNodeAsync()
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{Url}/hubs/node", options =>
                {
                    options.Headers[NodeAuthFilter.EnrollmentSecretHeader] = Secret;
                })
                .Build();

            await connection.StartAsync();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        private readonly ConcurrentQueue<InferenceChunk> chunks = new();

        public IReadOnlyList<InferenceChunk> Chunks => chunks.ToArray();

        public bool WriteChunk(InferenceChunk chunk)
        {
            chunks.Enqueue(chunk);
            return true;
        }

        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool Complete(InferenceResult result) => true;

        public void FailForConnection(string connectionId, Exception? exception)
        {
        }
    }

    private sealed class NoopRegistry : INodeRegistry
    {
        public event Action? Changed { add { } remove { } }

        public void Upsert(string connectionId, NodeRegistration registration, DateTimeOffset now) { }

        public bool Touch(string connectionId, Heartbeat heartbeat, DateTimeOffset now) => true;

        public bool ReportModels(string connectionId, NodeModels models, DateTimeOffset now) => true;

        public bool Remove(string connectionId) => false;

        public bool Cordon(string nodeId) => false;

        public bool Uncordon(string nodeId) => false;

        public string? FindConnectionIdByNodeId(string nodeId) => null;

        public IReadOnlyCollection<NodeSnapshot> Snapshot(DateTimeOffset now) => [];

        public IReadOnlyCollection<ModelInfo> DistinctModels() => [];

        public IReadOnlyCollection<NodeModelInventory> ModelInventory() => [];

        public IReadOnlyCollection<RoutableNode> FindNodesWithModel(string model) => [];

        public int IncrementInFlight(string connectionId) => 0;

        public int DecrementInFlight(string connectionId) => 0;

        public int GetLocalInFlight(string connectionId) => 0;

        public IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now) => [];
    }

    private sealed class NoopAffinity : IConversationAffinity
    {
        public int Count => 0;

        public string? GetNodeFor(string conversationKey) => null;

        public void Record(string conversationKey, string nodeId) { }

        public void Forget(string conversationKey) { }

        public int ForgetNode(string nodeId) => 0;
    }

    private sealed class NoopConnections : INodeConnectionTracker
    {
        public void Track(string connectionId, Microsoft.AspNetCore.SignalR.HubCallerContext context) { }

        public void Forget(string connectionId) { }

        public bool Abort(string connectionId) => false;
    }

    private sealed class StaticOptionsMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
