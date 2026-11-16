using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 150. A text job through the real <see cref="InferenceCore"/> and the real queue, over a dispatcher the
/// test holds the stream of: what a job does with a 503, what its status says while it runs, and what is left of
/// it afterwards. The node's half — its reports, a cancel reaching it, it going away — is <c>TextJobMeshTests</c>.
/// </summary>
public class TextJobTests
{
    private const string Chat = """{"model":"llama3","messages":[{"role":"user","content":"a prompt nobody may log"}],"stream":true}""";

    [Fact]
    public async Task AJobWaitsOutAFullFleetAndThenRunsToItsAnswer()
    {
        var harness = new Harness(maxConcurrency: 1);
        harness.Registry.IncrementInFlight("conn-a");

        var job = harness.Submit();

        // The blocking route answers this 503 (QueueTests); a job keeps it as the reason it is waiting.
        await WaitForAsync(() => job.Detail is not null);
        Assert.Equal(TextJobStatus.Queued, job.Status);
        Assert.Equal(TextJobStage.Waiting, job.Stage);
        Assert.Equal(0, harness.Dispatcher.Dispatches);

        harness.Registry.DecrementInFlight("conn-a");
        await WaitForAsync(() => harness.Dispatcher.Dispatches == 1);

        Assert.Equal(TextJobStatus.Running, job.Status);
        Assert.Equal(TextJobStage.Sent, job.Stage);
        Assert.Equal("node-a", job.NodeId);
        Assert.Equal(job.Id, harness.Dispatcher.LastJob!.Watch);
        Assert.Equal(TimeSpan.FromSeconds(3600), harness.Dispatcher.LastWatch!.Deadline.Timeout);
        Assert.Equal("Jobs:DeadlineSeconds", harness.Dispatcher.LastWatch.Deadline.Source);

        var stream = harness.Dispatcher.Stream;
        stream.TryWrite(Piece(job, """{"model":"llama3","message":{"role":"assistant","content":"Hel"},"done":false}"""));
        stream.TryWrite(Piece(job, """{"model":"llama3","message":{"role":"assistant","content":"lo"},"done":false}"""));
        await WaitForAsync(() => job.Pieces == 2);

        Assert.Equal(TextJobStage.Generating, job.Stage);
        using (var partial = JsonDocument.Parse(job.ResultJson()))
        {
            Assert.Equal("Hello", partial.RootElement.GetProperty("message").GetProperty("content").GetString());
            Assert.False(partial.RootElement.GetProperty("done").GetBoolean());
        }

        stream.TryWrite(Piece(job, """{"model":"llama3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","eval_count":2,"eval_duration":1000000000,"prompt_eval_count":5}""", done: true));
        await WaitForAsync(() => job.IsTerminal);

        Assert.Equal(TextJobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.Tokens);
        Assert.Equal(2.0, job.TokensPerSecond);

        using var result = JsonDocument.Parse(job.ResultJson());
        Assert.Equal("Hello", result.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.True(result.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal("stop", result.RootElement.GetProperty("done_reason").GetString());
        Assert.Equal(5, result.RootElement.GetProperty("prompt_eval_count").GetInt32());
    }

    [Fact]
    public async Task WithNoQueueTimeA503IsTheJobsFailure()
    {
        var harness = new Harness(maxConcurrency: 1, new JobsOptions { QueueSeconds = 0 });
        harness.Registry.IncrementInFlight("conn-a");

        var job = harness.Submit();
        await WaitForAsync(() => job.IsTerminal);

        Assert.Equal(TextJobStatus.Failed, job.Status);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, job.ErrorStatus);
        Assert.NotNull(job.RetryAfterSeconds);
    }

    [Fact]
    public async Task AModelNobodyHoldsFailsAtOnceAndIsNotWaitedFor()
    {
        var harness = new Harness(maxConcurrency: null, route: false);

        var job = harness.Submit(model: "no-such-model");
        await WaitForAsync(() => job.IsTerminal);

        Assert.Equal(TextJobStatus.Failed, job.Status);
        Assert.Equal(StatusCodes.Status404NotFound, job.ErrorStatus);
        Assert.Equal(0, harness.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task ANodesErrorPieceFailsTheJobWithTheNodesStatusAndKeepsNoText()
    {
        var harness = new Harness(maxConcurrency: null);
        var job = harness.Submit();
        await WaitForAsync(() => harness.Dispatcher.Dispatches == 1);

        harness.Dispatcher.Stream.TryWrite(Piece(job, """{"model":"llama3","message":{"role":"assistant","content":"half"},"done":false}"""));
        harness.Dispatcher.Stream.TryWrite(Piece(job, """{"error":"this model takes no pictures","status":400,"done":true}""", done: true));
        await WaitForAsync(() => job.IsTerminal);

        Assert.Equal(TextJobStatus.Failed, job.Status);
        Assert.Equal(400, job.ErrorStatus);
        Assert.Equal("this model takes no pictures", job.ErrorMessage);
        Assert.DoesNotContain("half", job.ResultJson());
    }

    [Fact]
    public async Task ANodeThatGoesAwayMidAnswerFailsTheJobAndItIsNotSentAgain()
    {
        var harness = new Harness(maxConcurrency: null);
        var job = harness.Submit();
        await WaitForAsync(() => harness.Dispatcher.Dispatches == 1);

        harness.Dispatcher.Stream.TryWrite(Piece(job, """{"message":{"role":"assistant","content":"x"},"done":false}"""));
        harness.Dispatcher.Stream.TryComplete(new NodeDisconnectedException("conn-a", "node disconnected before finishing the stream", null));
        await WaitForAsync(() => job.IsTerminal);

        Assert.Equal(TextJobStatus.Failed, job.Status);
        Assert.Equal("node_lost", job.ErrorCode);
        Assert.Equal(1, harness.Dispatcher.Dispatches);
    }

    [Fact]
    public async Task OnlyTheJobsOwnNodeMovesItsStatus()
    {
        var harness = new Harness(maxConcurrency: null);
        var job = harness.Submit();
        await WaitForAsync(() => harness.Dispatcher.Dispatches == 1);
        var watch = harness.Dispatcher.LastWatch!;

        watch.NodeReported(new InferenceJobProgress(Guid.NewGuid(), "some-other-node", InferenceJobProgress.Preparing, 0, 10));
        Assert.Equal(TextJobStage.Sent, job.Stage);
        Assert.Null(job.NodeReportedAt);

        watch.NodeReported(new InferenceJobProgress(Guid.NewGuid(), "node-a", InferenceJobProgress.Preparing, 0, 10, "group starting: 12 of 39 MiB sent to its members"));
        Assert.Equal(TextJobStage.Preparing, job.Stage);
        Assert.NotNull(job.NodeReportedAt);
        Assert.Equal("group starting: 12 of 39 MiB sent to its members", job.Detail);

        harness.Jobs.Cancel(job);
        await WaitForAsync(() => harness.Dispatcher.Cancelled);
        Assert.Equal(TextJobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task AJobIsItsSubmittersAndAClientHasACap()
    {
        var harness = new Harness(maxConcurrency: null, new JobsOptions { MaxActivePerClient = 1 });
        var job = harness.Submit();

        Assert.NotNull(harness.Jobs.Find(job.Id, ResolvedClient.Anonymous.Id));
        Assert.Null(harness.Jobs.Find(job.Id, "somebody-else"));
        Assert.Empty(harness.Jobs.ForClient("somebody-else"));

        var (second, refusal) = harness.TrySubmit();
        Assert.Null(second);
        Assert.Equal(StatusCodes.Status429TooManyRequests, refusal!.Status);

        harness.Jobs.Cancel(job);
        await WaitForAsync(() => job.IsTerminal);
        Assert.NotNull(harness.TrySubmit().Job);
    }

    [Fact]
    public async Task AFinishedJobGoesAfterItsRetentionAndTheStatusNeverCarriesTheRequestOrTheAnswer()
    {
        var clock = new MovableClock();
        var harness = new Harness(maxConcurrency: null, new JobsOptions { RetentionSeconds = 60 }, clock: clock);
        var job = harness.Submit();
        await WaitForAsync(() => harness.Dispatcher.Dispatches == 1);

        harness.Dispatcher.Stream.TryWrite(Piece(job, """{"message":{"role":"assistant","content":"the secret answer"},"done":true}""", done: true));
        await WaitForAsync(() => job.IsTerminal);

        var status = JsonSerializer.Serialize(JobEndpoints.Describe(job, harness.Jobs, withClient: true));
        Assert.DoesNotContain("secret answer", status);
        Assert.DoesNotContain("nobody may log", status);

        clock.Advance(TimeSpan.FromSeconds(59));
        harness.Jobs.Sweep();
        Assert.NotNull(harness.Jobs.Find(job.Id, ResolvedClient.Anonymous.Id));

        clock.Advance(TimeSpan.FromSeconds(2));
        harness.Jobs.Sweep();
        Assert.Null(harness.Jobs.Find(job.Id, ResolvedClient.Anonymous.Id));
    }

    [Fact]
    public void AValueThatCannotBeMeantFailsStartup()
    {
        var result = new JobsOptionsValidator().Validate(null, new JobsOptions { MaxJobs = 0, DeadlineSeconds = 0 });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Jobs:MaxJobs"));
        Assert.Contains(result.Failures!, f => f.Contains("Jobs:DeadlineSeconds"));
        Assert.True(new JobsOptionsValidator().Validate(null, new JobsOptions()).Succeeded);
    }

    private static InferenceChunk Piece(TextJob job, string json, bool done = false) => new(job.Id, json, done);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 400 && !predicate(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(predicate(), "timed out");
    }

    private sealed class Harness
    {
        private readonly QueueOptions queue = new() { MaxWaitSeconds = 0 };

        public Harness(int? maxConcurrency, JobsOptions? options = null, bool route = true, TimeProvider? clock = null)
        {
            Registry.Upsert(
                "conn-a",
                new NodeRegistration("node-a", "node-a", "http://localhost:11434/", "3.103.0", null, maxConcurrency),
                DateTimeOffset.UtcNow);
            Registry.ReportModels(
                "conn-a",
                new NodeModels("node-a", [new ModelInfo("llama3", null, null)], DateTimeOffset.UtcNow),
                DateTimeOffset.UtcNow);

            Jobs = new TextJobRegistry(
                Options.Create(options ?? new JobsOptions()),
                new StubRouter(route ? new RoutableNode("conn-a", "node-a", "alpha") : null),
                Registry,
                Dispatcher,
                new NoProvider(),
                new Metrics(),
                NullLogger<TextJobRegistry>.Instance,
                time: clock);
        }

        public NodeRegistry Registry { get; } = new();

        public StreamDispatcher Dispatcher { get; } = new();

        public TextJobRegistry Jobs { get; }

        public TextJob Submit(string model = "llama3") => TrySubmit(model).Job!;

        public (TextJob? Job, TextJobRefusal? Refusal) TrySubmit(string model = "llama3") => Jobs.Submit(
            TestUsage.Context(Registry, queueOptions: queue), "chat", Chat.Replace("llama3", model), model, conversationKey: null, default);
    }

    private sealed class MovableClock : TimeProvider
    {
        private TimeSpan offset;

        public void Advance(TimeSpan by) => offset += by;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    private sealed class StubRouter(RoutableNode? node) : InferHub.Coordinator.Services.IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null, string? capability = null, bool requireStreamedAttachments = false, bool requireStreamedSpeech = false)
            => node;
    }

    /// <summary>The dispatcher's contract with a followed job, as <see cref="Dispatcher"/> keeps it: it tells the watch the node, and hands back a stream.</summary>
    private sealed class StreamDispatcher : IDispatcher
    {
        private int dispatches;

        public int Dispatches => Volatile.Read(ref dispatches);

        public InferenceJob? LastJob { get; private set; }

        public JobWatch? LastWatch { get; private set; }

        public bool Cancelled { get; private set; }

        public Channel<InferenceChunk> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<InferenceChunk>();

        public ChannelWriter<InferenceChunk> Stream => Channel.Writer;

        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
        {
            LastJob = job;
            LastWatch = JobWatch.Current;
            LastWatch?.Dispatched(node);
            cancellationToken.Register(() =>
            {
                Cancelled = true;
                Channel.Writer.TryComplete(new OperationCanceledException());
            });
            Interlocked.Increment(ref dispatches);
            return Task.FromResult(Channel.Reader);
        }

        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? exception) => throw new NotImplementedException();
    }

    private sealed class NoProvider : IProviderDispatcher
    {
        public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

        public Task<ProviderResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("must not be called");
    }
}
