using System.Collections.Concurrent;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 130: an image or video job is routed later, by the queue's pump, from whatever flow runs it. It must still go only
/// to a node its submitter's organisation may use.
/// </summary>
public sealed class OrganisationJobTests
{
    [Fact]
    public async Task AJobIsRoutedInItsSubmittersOrganisationWhicheverFlowPumpsTheQueue()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        // lab-b's node sorts first by name, so an unfiltered router would pick it.
        foreach (var (connection, nodeId, organisation) in new[] { ("conn-b", "a-node-b", "lab-b"), ("conn-a", "z-node-a", "lab-a") })
        {
            registry.Upsert(
                connection,
                new NodeRegistration(nodeId, nodeId, "http://localhost:11434/", "3.90.0",
                    Capabilities: [new NodeCapability(CapabilityKinds.Image, ["sdxl"])], Organisation: organisation),
                now);
        }

        var tools = new RecordingTools();
        var routerOptions = Options.Create(new RouterOptions());
        var jobs = new ImageJobRegistry(
            Options.Create(new ImageEdgeOptions()),
            new Router(registry, new ConversationAffinity(routerOptions), new ThroughputTracker(), routerOptions),
            registry,
            tools,
            TestUsage.Meter(),
            new Metrics(),
            new NoImageJobArchive(),
            NullLogger<ImageJobRegistry>.Instance);
        var policy = new OrganisationPolicy(Options.Create(new OrganisationOptions { Enabled = true }));

        // Submitted inside a lab-a request.
        await Task.Run(() =>
        {
            OrganisationScope.Begin(policy, "lab-a");
            Assert.NotNull(jobs.TrySubmit(new ResolvedClient("alice", null, Organisation: "lab-a"), new Picture(), null));
        });

        // Another job by a lab-b client, pumped from a flow with no scope at all (a completion, a sweep).
        await Task.Run(() => Assert.NotNull(jobs.TrySubmit(new ResolvedClient("bob", null, Organisation: "lab-b"), new Picture(), null)));

        for (var i = 0; i < 200 && tools.Dispatched.Count < 1; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("z-node-a", tools.Dispatched.First());
    }

    private sealed class Picture : IImageRequest
    {
        public string Model => "sdxl";

        public string Capability => CapabilityKinds.Image;

        public int Count => 1;

        public ImageSize? Size => null;

        public int? Steps => null;

        public string ToToolPayload() => """{"prompt":"x"}""";

        public IReadOnlyList<ToolAttachment> Attachments => [];
    }

    /// <summary>Records where a job was sent and holds it, so the node stays busy and the next job waits.</summary>
    private sealed class RecordingTools : IToolDispatcher
    {
        private readonly TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Dispatched { get; } = new();

        public Task<ToolResult> DispatchToolAsync(RoutableNode node, ToolJob job, CancellationToken cancellationToken)
            => DispatchToolAsync(node, job, null, cancellationToken);

        public async Task<ToolResult> DispatchToolAsync(RoutableNode node, ToolJob job, IProgress<ToolChunk>? progress, CancellationToken cancellationToken)
        {
            Dispatched.Enqueue(node.NodeId);
            await never.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public Task<ChannelReader<ToolChunk>> DispatchToolStreamAsync(RoutableNode node, ToolJob job, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool CompleteTool(ToolResult result) => false;

        public bool WriteToolChunk(ToolChunk chunk) => false;

        public IDisposable RegisterUpload(Guid jobId, StreamedUpload upload) => throw new NotSupportedException();

        public IAsyncEnumerable<AttachmentChunk> ReadUploadAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void RequestCancel(RoutableNode node, Guid jobId)
        {
        }
    }
}
