using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Resources;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 85, <c>Node:OnDemand</c>. The arbiter is real and the clock is real — the waits are short
/// rather than faked, because what is under test is ordering between concurrent callers, and a
/// fake clock that the test advances would decide that ordering for it.
/// </summary>
public class GpuArbiterTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private static GpuArbiter Arbiter(bool enabled = true, int releaseAfter = 0, int switchWait = 5) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new NodeOptions
            {
                OnDemand = new OnDemandOptions
                {
                    Enabled = enabled,
                    ReleaseAfterSeconds = releaseAfter,
                    SwitchWaitSeconds = switchWait
                }
            }),
            TimeProvider.System,
            NullLogger<GpuArbiter>.Instance);

    /// <summary>Records every release, in order, and can signal the test when one happens.</summary>
    private sealed class Releases
    {
        public ConcurrentQueue<string> Log { get; } = new();

        private readonly SemaphoreSlim signal = new(0);

        public Func<CancellationToken, Task> For(string tenant) => _ =>
        {
            Log.Enqueue(tenant);
            signal.Release();
            return Task.CompletedTask;
        };

        public Task<bool> WaitAsync() => signal.WaitAsync(Soon);
    }

    [Fact]
    public async Task OffIsAPassThroughThatNeverReleasesAnything()
    {
        await using var arbiter = Arbiter(enabled: false);
        var releases = new Releases();
        arbiter.RegisterReleaser("a", releases.For("a"));

        await using (await arbiter.AcquireAsync("a", CancellationToken.None))
        await using (await arbiter.AcquireAsync("b", CancellationToken.None))
        {
            Assert.Null(arbiter.Owner);
        }

        await Task.Delay(100);
        Assert.Empty(releases.Log);
    }

    [Fact]
    public async Task RequestsForTheSameServiceShareTheCard()
    {
        await using var arbiter = Arbiter();

        var first = await arbiter.AcquireAsync("ollama", CancellationToken.None);
        var second = await arbiter.AcquireAsync("ollama", CancellationToken.None).WaitAsync(Soon);

        Assert.Equal("ollama", arbiter.Owner);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AnotherServiceWaitsForTheOwnerToFinishAndTheOwnerIsReleasedBeforeItStarts()
    {
        await using var arbiter = Arbiter();
        var releases = new Releases();
        arbiter.RegisterReleaser("ollama", releases.For("ollama"));
        arbiter.RegisterReleaser("tool:piper", releases.For("tool:piper"));

        var chat = await arbiter.AcquireAsync("ollama", CancellationToken.None);
        var speech = arbiter.AcquireAsync("tool:piper", CancellationToken.None);

        await Task.Delay(200);
        Assert.False(speech.IsCompleted, "a different service must not share the card with a request in flight");
        Assert.Empty(releases.Log);

        await chat.DisposeAsync();

        var lease = await speech.WaitAsync(Soon);

        // Released BEFORE the newcomer was admitted — the peak is never both at once.
        Assert.Equal(new[] { "ollama" }, releases.Log.ToArray());
        Assert.Equal("tool:piper", arbiter.Owner);

        await lease.DisposeAsync();
        Assert.True(await releases.WaitAsync() && await releases.WaitAsync());
        Assert.Equal(new[] { "ollama", "tool:piper" }, releases.Log.ToArray());
    }

    [Fact]
    public async Task WithNoLingerTheCardIsFreedTheMomentTheLastRequestEnds()
    {
        await using var arbiter = Arbiter(releaseAfter: 0);
        var releases = new Releases();
        arbiter.RegisterReleaser("ollama", releases.For("ollama"));

        await (await arbiter.AcquireAsync("ollama", CancellationToken.None)).DisposeAsync();

        Assert.True(await releases.WaitAsync(), "the model should have been unloaded");
        Assert.Null(arbiter.Owner);
    }

    [Fact]
    public async Task ARequestInsideTheLingerKeepsTheModelLoaded()
    {
        await using var arbiter = Arbiter(releaseAfter: 1);
        var releases = new Releases();
        arbiter.RegisterReleaser("ollama", releases.For("ollama"));

        await (await arbiter.AcquireAsync("ollama", CancellationToken.None)).DisposeAsync();
        await Task.Delay(400);
        var again = await arbiter.AcquireAsync("ollama", CancellationToken.None);
        await Task.Delay(900);

        Assert.Empty(releases.Log);
        await again.DisposeAsync();

        Assert.True(await releases.WaitAsync(), "the linger should release once the service is quiet");
        Assert.Equal(new[] { "ollama" }, releases.Log.ToArray());
    }

    [Fact]
    public async Task ALingeringOwnerIsReleasedAtOnceWhenAnotherServiceAsks()
    {
        await using var arbiter = Arbiter(releaseAfter: 600);
        var releases = new Releases();
        arbiter.RegisterReleaser("ollama", releases.For("ollama"));

        await (await arbiter.AcquireAsync("ollama", CancellationToken.None)).DisposeAsync();

        // Ten minutes of linger must not make an image request wait ten minutes.
        var image = await arbiter.AcquireAsync("tool:diffusion", CancellationToken.None).WaitAsync(Soon);

        Assert.Equal(new[] { "ollama" }, releases.Log.ToArray());
        Assert.Equal("tool:diffusion", arbiter.Owner);
        await image.DisposeAsync();
    }

    [Fact]
    public async Task ANewRequestForTheOwnerQueuesBehindAServiceAlreadyWaiting()
    {
        await using var arbiter = Arbiter();

        var chat = await arbiter.AcquireAsync("ollama", CancellationToken.None);
        var video = arbiter.AcquireAsync("tool:video", CancellationToken.None);
        await Task.Delay(100);
        var moreChat = arbiter.AcquireAsync("ollama", CancellationToken.None);
        await Task.Delay(100);

        Assert.False(moreChat.IsCompleted, "a steady trickle of chat must not starve a waiting video forever");

        await chat.DisposeAsync();
        var videoLease = await video.WaitAsync(Soon);
        Assert.Equal("tool:video", arbiter.Owner);
        Assert.False(moreChat.IsCompleted);

        await videoLease.DisposeAsync();
        var chatLease = await moreChat.WaitAsync(Soon);
        Assert.Equal("ollama", arbiter.Owner);
        await chatLease.DisposeAsync();
    }

    [Fact]
    public async Task WaitingPastTheBudgetIsA503ShapedRefusalAndLeavesTheOwnerAlone()
    {
        await using var arbiter = Arbiter(switchWait: 1);

        var video = await arbiter.AcquireAsync("tool:video", CancellationToken.None);

        var refused = await Assert.ThrowsAsync<GpuBusyException>(
            () => arbiter.AcquireAsync("ollama", CancellationToken.None));

        Assert.Contains("tool:video", refused.Message);
        Assert.Contains("Node:OnDemand", refused.Message);
        Assert.Equal("tool:video", arbiter.Owner);

        // And the refusal left no ghost waiter behind: the owner can still be joined.
        await using (await arbiter.AcquireAsync("tool:video", CancellationToken.None).WaitAsync(Soon))
        {
        }

        await video.DisposeAsync();
    }

    [Fact]
    public async Task AFailingReleaseDoesNotWedgeTheCard()
    {
        await using var arbiter = Arbiter();
        arbiter.RegisterReleaser("ollama", _ => throw new HttpRequestException("ollama is gone"));

        await (await arbiter.AcquireAsync("ollama", CancellationToken.None)).DisposeAsync();

        await using var next = await arbiter.AcquireAsync("tool:whisper", CancellationToken.None).WaitAsync(Soon);
        Assert.Equal("tool:whisper", arbiter.Owner);
    }

    [Fact]
    public async Task TheBackendHoldsTheCardForTheWholeStreamAndNotForListingModels()
    {
        await using var arbiter = Arbiter();
        var releases = new Releases();
        var inner = new SlowStreamBackend();
        var release = releases.For(OnDemandBackend.Tenant);
        var backend = new OnDemandBackend(inner, arbiter, (_, token) => release(token));

        await backend.ListModelsAsync(CancellationToken.None);
        Assert.Null(arbiter.Owner);

        var stream = backend.StreamAsync("chat", "{}", CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(OnDemandBackend.Tenant, arbiter.Owner);

        var image = arbiter.AcquireAsync("tool:diffusion", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(image.IsCompleted, "the model is mid-answer; the swap happens between jobs, never inside one");

        while (await stream.MoveNextAsync())
        {
        }

        await stream.DisposeAsync();

        await using var lease = await image.WaitAsync(Soon);
        Assert.Equal(new[] { OnDemandBackend.Tenant }, releases.Log.ToArray());
    }

    [Fact]
    public async Task TheReleaseUnloadsOnlyTheModelsThisNodeAskedFor()
    {
        await using var arbiter = Arbiter();
        var unloaded = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new OnDemandBackend(new SlowStreamBackend(), arbiter, (models, _) =>
        {
            unloaded.TrySetResult(models.OrderBy(m => m).ToArray());
            return Task.CompletedTask;
        });

        await backend.ChatAsync("{\"model\":\"qwen2.5:0.5b\",\"messages\":[]}", CancellationToken.None);

        // The Ollama on a desktop is shared; a model somebody else loaded is not this node's to drop.
        Assert.Equal(new[] { "qwen2.5:0.5b" }, await unloaded.Task.WaitAsync(Soon));
        Assert.Empty(backend.Loaded);
    }

    [Theory]
    [InlineData("llama3", "llama3:latest")]
    [InlineData("llama3:latest", "llama3:latest")]
    [InlineData("qwen2.5:0.5b", "qwen2.5:0.5b")]
    [InlineData("batiai/qwen3.8-27b", "batiai/qwen3.8-27b:latest")]
    [InlineData("registry.local:5000/team/model", "registry.local:5000/team/model:latest")]
    public void AnUntaggedNameMeansLatest(string name, string expected) =>
        Assert.Equal(expected, OllamaBackend.NormalizeModelName(name));

    private sealed class SlowStreamBackend : IInferenceBackend
    {
        public string Name => "fake";

        public string Endpoint => "fake";

        public IReadOnlyList<string> Kinds => [CapabilityKinds.Chat];

        public bool SupportsModelManagement => false;

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>?>([]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public async IAsyncEnumerable<string> StreamAsync(
            string kind,
            string requestJson,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(50, cancellationToken);
                yield return "{\"done\":" + (i == 2 ? "true" : "false") + "}";
            }
        }

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
