using System.Runtime.CompilerServices;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

public class ModelCommandTests
{
    // ---- node-side executor: pull / delete / warm produce progress, failures do not throw ----

    [Fact]
    public async Task PullStreamsProgressThenTerminalSuccess()
    {
        var backend = new FakeBackend(supportsManagement: true)
        {
            PullFrames =
            [
                new ModelPullProgress("pulling manifest", null, null),
                new ModelPullProgress("downloading", 100, 50),
                new ModelPullProgress("downloading", 100, 100),
            ]
        };
        var executor = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        var frames = await Collect(executor, ModelCommand.KindPull, "llama3");

        Assert.Equal("llama3", frames[0].ModelName);
        Assert.Contains(frames, f => f.Status == "downloading" && f.Percent == 50);
        var terminal = frames[^1];
        Assert.True(terminal.Done);
        Assert.Null(terminal.Error);
        Assert.Equal("success", terminal.Status);
        Assert.False(frames[0].Done); // progress frames are not terminal
    }

    [Fact]
    public async Task DeleteEmitsStartThenTerminal()
    {
        var backend = new FakeBackend(supportsManagement: true);
        var executor = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        var frames = await Collect(executor, ModelCommand.KindDelete, "llama3");

        Assert.Equal("deleting", frames[0].Status);
        Assert.False(frames[0].Done);
        Assert.True(frames[^1].Done);
        Assert.Equal("deleted", frames[^1].Status);
        Assert.Equal("llama3", backend.Deleted);
    }

    [Fact]
    public async Task WarmEmitsTerminal()
    {
        var backend = new FakeBackend(supportsManagement: true);
        var executor = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        var frames = await Collect(executor, ModelCommand.KindWarm, "llama3");

        Assert.True(frames[^1].Done);
        Assert.Equal("warmed", frames[^1].Status);
        Assert.Equal("llama3", backend.Warmed);
    }

    [Fact]
    public async Task UnsupportedBackendRefusesCleanlyWithoutThrowing()
    {
        var backend = new FakeBackend(supportsManagement: false);
        var executor = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        // Must NOT throw (which would surface as a 500) — it yields one terminal error frame.
        var frames = await Collect(executor, ModelCommand.KindPull, "llama3");

        Assert.Single(frames);
        Assert.True(frames[0].Done);
        Assert.NotNull(frames[0].Error);
        Assert.Contains("cannot manage models", frames[0].Error);
    }

    [Fact]
    public async Task BackendFailureBecomesTerminalErrorFrameNotException()
    {
        var backend = new FakeBackend(supportsManagement: true) { DeleteThrows = true };
        var executor = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        var frames = await Collect(executor, ModelCommand.KindDelete, "llama3");

        var terminal = frames[^1];
        Assert.True(terminal.Done);
        Assert.Equal("error", terminal.Status);
        Assert.NotNull(terminal.Error);
    }

    [Fact]
    public async Task PullFailureMidStreamBecomesTerminalError()
    {
        var backend = new FakeBackend(supportsManagement: true)
        {
            PullFrames = [new ModelPullProgress("downloading", 100, 10)],
            PullThrowsAfterFrames = true
        };
        var executor = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        var frames = await Collect(executor, ModelCommand.KindPull, "llama3");

        Assert.True(frames[^1].Done);
        Assert.Equal("error", frames[^1].Status);
        Assert.NotNull(frames[^1].Error);
    }

    // ---- coordinator-side coalescing: an identical in-flight command is reused, not re-sent ----

    [Fact]
    public async Task DuplicateCommandCoalescesOntoTheRunningOne()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("conn-1", new NodeRegistration("node-1", "n", "http://x", "1", SupportsModelManagement: true), now);

        var hub = new NoOpHubContext();
        var coord = new ModelCommandCoordinator(hub, registry, NullLogger<ModelCommandCoordinator>.Instance);

        var first = await coord.SendAsync("node-1", ModelCommand.KindPull, "llama3", default);
        var second = await coord.SendAsync("node-1", ModelCommand.KindPull, "llama3", default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first!.Reused);
        Assert.True(second!.Reused);
        Assert.Equal(first.CommandId, second.CommandId);

        // A different kind on the same model is a distinct command, not a coalesce.
        var warm = await coord.SendAsync("node-1", ModelCommand.KindWarm, "llama3", default);
        Assert.NotNull(warm);
        Assert.False(warm!.Reused);
    }

    [Fact]
    public async Task TerminalProgressClearsTheInFlightSoTheNextCommandIsNew()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("conn-1", new NodeRegistration("node-1", "n", "http://x", "1", SupportsModelManagement: true), now);
        var coord = new ModelCommandCoordinator(new NoOpHubContext(), registry, NullLogger<ModelCommandCoordinator>.Instance);

        var first = await coord.SendAsync("node-1", ModelCommand.KindPull, "llama3", default);
        ModelCommandProgress? relayed = null;
        coord.ProgressReceived += p => relayed = p;

        coord.ReportProgress(new ModelCommandProgress(first!.CommandId, "node-1", "pull", "llama3", "success", 100, Done: true, Error: null));
        Assert.NotNull(relayed);

        var again = await coord.SendAsync("node-1", ModelCommand.KindPull, "llama3", default);
        Assert.False(again!.Reused); // the previous command finished, so this is a fresh pull
    }

    [Fact]
    public async Task SendToUnknownNodeReturnsNull()
    {
        var registry = new NodeRegistry();
        var coord = new ModelCommandCoordinator(new NoOpHubContext(), registry, NullLogger<ModelCommandCoordinator>.Instance);

        var result = await coord.SendAsync("ghost", ModelCommand.KindPull, "llama3", default);

        Assert.Null(result);
    }

    private static async Task<List<ModelCommandProgress>> Collect(
        ModelCommandExecutor executor, string kind, string model)
    {
        var frames = new List<ModelCommandProgress>();
        await foreach (var f in executor.ExecuteAsync(new ModelCommand(Guid.NewGuid(), kind, model), "node-1", default))
        {
            frames.Add(f);
        }
        return frames;
    }

    private sealed class FakeBackend(bool supportsManagement) : IInferenceBackend
    {
        public IReadOnlyList<ModelPullProgress> PullFrames { get; set; } = [];
        public bool PullThrowsAfterFrames { get; set; }
        public bool DeleteThrows { get; set; }
        public string? Deleted { get; private set; }
        public string? Warmed { get; private set; }

        public string Name => "fake";
        public string Endpoint => "http://fake";
        public bool SupportsModelManagement => supportsManagement;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public Task<string> GenerateAsync(string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> ChatAsync(string r, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> EmbedAsync(string r, CancellationToken ct) => throw new NotImplementedException();
        public IAsyncEnumerable<string> StreamAsync(string k, string r, CancellationToken ct) => throw new NotImplementedException();

        public async IAsyncEnumerable<ModelPullProgress> PullAsync(string model, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var frame in PullFrames)
            {
                await Task.Yield();
                yield return frame;
            }
            if (PullThrowsAfterFrames)
            {
                throw new InvalidOperationException("upstream pull failed");
            }
        }

        public Task DeleteAsync(string model, CancellationToken ct)
        {
            if (DeleteThrows) throw new InvalidOperationException("cannot delete");
            Deleted = model;
            return Task.CompletedTask;
        }

        public Task WarmAsync(string model, CancellationToken ct)
        {
            Warmed = model;
            return Task.CompletedTask;
        }
    }

    // Minimal IHubContext that accepts sends without a transport — enough to exercise coalescing.
    private sealed class NoOpHubContext : IHubContext<NodeHub>
    {
        public IHubClients Clients { get; } = new NoOpClients();
        public IGroupManager Groups { get; } = new NoOpGroups();
    }

    private sealed class NoOpClients : IHubClients
    {
        public IClientProxy All => new NoOpProxy();
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => new NoOpProxy();
        public IClientProxy Client(string connectionId) => new NoOpProxy();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoOpProxy();
        public IClientProxy Group(string groupName) => new NoOpProxy();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => new NoOpProxy();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoOpProxy();
        public IClientProxy User(string userId) => new NoOpProxy();
        public IClientProxy Users(IReadOnlyList<string> userIds) => new NoOpProxy();
    }

    private sealed class NoOpProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
