using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 145 D1/D2 at the dispatch: a request on a shard group with <c>RetrySeconds</c> that fails before its first token waits for
/// the group to run again and is sent again; one that arrives while it reassembles waits instead of the 503. A refusal, a stream
/// that has sent a token and a group without the setting are as before.
/// </summary>
public sealed class ShardGroupRetryTests
{
    private const string Group = "llama-8b-split";
    private static readonly RoutableNode Head = new("conn-a", "a", "shard-a");

    /// <summary>The fleet as the dispatch sees it: whether the group serves, and how often it stopped.</summary>
    private sealed class Fleet
    {
        public volatile bool Serving = true;
        public long Interruptions;

        public NodeRegistry Registry(int retrySeconds) => new()
        {
            WhyGroupIsNotServing = model => model == Group && !Serving ? $"group '{Group}' is reassembling: shard-d stands in for shard-c" : null,
            GroupRetryWindow = model => model == Group && retrySeconds > 0 ? TimeSpan.FromSeconds(retrySeconds) : null,
            GroupInterruptions = _ => Interlocked.Read(ref Interruptions)
        };

        /// <summary>A member leaves and the spare stands in: the group stops serving, then serves again.</summary>
        public void Reassemble(TimeSpan after) => _ = Task.Run(async () =>
        {
            Serving = false;
            Interlocked.Increment(ref Interruptions);
            await Task.Delay(after);
            Serving = true;
        });
    }

    private sealed class FleetRouter(Fleet fleet) : IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null, string? capability = null, bool requireStreamedAttachments = false, bool requireStreamedSpeech = false)
            => fleet.Serving && excludeConnectionId is null ? Head : null;
    }

    /// <summary>Answers each attempt from a script, in order; the first call can also start the reassembly the failure stands for.</summary>
    private sealed class ScriptDispatcher(params Func<InferenceJob, object>[] script) : IDispatcher
    {
        public List<Guid> Jobs { get; } = [];

        private object Next(InferenceJob job)
        {
            Jobs.Add(job.JobId);
            return script[Math.Min(Jobs.Count, script.Length) - 1](job);
        }

        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken) => Next(job) switch
        {
            InferenceResult result => Task.FromResult(result),
            Exception ex => Task.FromException<InferenceResult>(ex),
            var other => throw new InvalidOperationException($"not a blocking answer: {other}")
        };

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
        {
            switch (Next(job))
            {
                case string[] chunks:
                    var channel = Channel.CreateUnbounded<InferenceChunk>();

                    for (var i = 0; i < chunks.Length; i++)
                    {
                        channel.Writer.TryWrite(new InferenceChunk(job.JobId, chunks[i], i == chunks.Length - 1));
                    }

                    channel.Writer.TryComplete();
                    return Task.FromResult(channel.Reader);
                case Exception ex:
                    return Task.FromException<ChannelReader<InferenceChunk>>(ex);
                default:
                    throw new InvalidOperationException("not a stream");
            }
        }

        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? error = null) => throw new NotImplementedException();
    }

    private sealed class NeverFallback : IProviderDispatcher
    {
        public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

        public Task<ProviderResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no provider in these tests");
    }

    private static Task<InferenceCore.DispatchOutcome> Ask(Fleet fleet, NodeRegistry registry, IDispatcher dispatcher, bool stream = false) =>
        InferenceCore.DispatchAsync(
            "chat",
            $$"""{"model":"{{Group}}","messages":[{"role":"user","content":"hi"}]}""",
            Group,
            stream,
            conversationKey: null,
            TestUsage.Context(registry),
            new FleetRouter(fleet),
            registry,
            dispatcher,
            new NeverFallback(),
            new Metrics(),
            NullLogger.Instance,
            CancellationToken.None);

    private static readonly string Answer = """{"message":{"role":"assistant","content":"Paris."},"done":true}""";

    [Fact]
    public async Task ABlockingRequestThatDiedBeforeItsFirstTokenIsSentAgainOnceTheGroupRunsAgain()
    {
        var fleet = new Fleet();
        var dispatcher = new ScriptDispatcher(
            job => { fleet.Reassemble(TimeSpan.FromMilliseconds(800)); return InferenceResult.Failed(job.JobId, "llama-server: connection reset by peer"); },
            job => InferenceResult.Succeeded(job.JobId, Answer));

        var outcome = await Ask(fleet, fleet.Registry(retrySeconds: 30), dispatcher);

        Assert.False(outcome.IsError, outcome.ErrorMessage);
        Assert.Equal(Answer, outcome.ResponseJson);
        Assert.Equal(2, dispatcher.Jobs.Distinct().Count());
    }

    [Fact]
    public async Task AStreamWhoseFirstChunkIsTheNodesErrorIsSentAgainAndTheCallerSeesOnlyTheAnswer()
    {
        var fleet = new Fleet();
        var dispatcher = new ScriptDispatcher(
            _ => { fleet.Reassemble(TimeSpan.FromMilliseconds(500)); return new[] { """{"error":"group 'llama-8b-split' is draining","done":true}""" }; },
            _ => new[] { """{"message":{"content":"Par"},"done":false}""", """{"message":{"content":"is."},"done":true}""" });

        var outcome = await Ask(fleet, fleet.Registry(retrySeconds: 30), dispatcher, stream: true);

        var chunks = new List<string>();
        await foreach (var chunk in outcome.Stream!.ReadAllAsync())
        {
            chunks.Add(chunk.ResponseJson);
        }

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks, c => c.Contains("error"));
        Assert.Equal(2, dispatcher.Jobs.Count);
    }

    [Fact]
    public async Task AHeadThatDroppedBeforeTheJobStartedIsWaitedForToo()
    {
        var fleet = new Fleet();
        var dispatcher = new ScriptDispatcher(
            _ => { fleet.Reassemble(TimeSpan.FromMilliseconds(300)); return new NodeDisconnectedException("conn-a"); },
            job => InferenceResult.Succeeded(job.JobId, Answer));

        var outcome = await Ask(fleet, fleet.Registry(retrySeconds: 30), dispatcher);

        Assert.Equal(Answer, outcome.ResponseJson);
    }

    [Fact]
    public async Task ARefusalAStartedStreamAndAGroupWithoutRetrySecondsAreReturnedAsTheyCame()
    {
        // A 4xx is the request's: no second try.
        var fleet = new Fleet();
        var refusing = new ScriptDispatcher(job => InferenceResult.Refused(job.JobId, "this model has no image encoder", 400));
        var refused = await Ask(fleet, fleet.Registry(retrySeconds: 30), refusing);
        Assert.Equal(400, refused.ErrorStatus);
        Assert.Single(refusing.Jobs);

        // A stream that sent a token and then failed: the caller has text; the error reaches it in the stream.
        var started = new ScriptDispatcher(_ => new[] { """{"message":{"content":"Par"},"done":false}""", """{"error":"connection reset","done":true}""" });
        var outcome = await Ask(fleet, fleet.Registry(retrySeconds: 30), started, stream: true);
        var chunks = new List<string>();
        await foreach (var chunk in outcome.Stream!.ReadAllAsync())
        {
            chunks.Add(chunk.ResponseJson);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Single(started.Jobs);

        // RetrySeconds 0, as 3.109: the engine's error, at once.
        var failing = new ScriptDispatcher(job => InferenceResult.Failed(job.JobId, "connection reset"));
        var failed = await Ask(fleet, fleet.Registry(retrySeconds: 0), failing);
        Assert.Equal(502, failed.ErrorStatus);
        Assert.Single(failing.Jobs);
    }

    [Fact]
    public async Task AGroupThatDoesNotRunAgainInTimeGivesTheFirstError()
    {
        var fleet = new Fleet();
        var dispatcher = new ScriptDispatcher(job =>
        {
            fleet.Serving = false;
            Interlocked.Increment(ref fleet.Interruptions);
            return InferenceResult.Failed(job.JobId, "connection reset");
        });

        var started = DateTimeOffset.UtcNow;
        var outcome = await Ask(fleet, fleet.Registry(retrySeconds: 2), dispatcher);

        Assert.Equal(502, outcome.ErrorStatus);
        Assert.Contains("connection reset", outcome.ErrorMessage);
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ARequestThatArrivesWhileTheGroupReassemblesWaitsInsteadOfThe503()
    {
        var fleet = new Fleet();
        fleet.Reassemble(TimeSpan.FromMilliseconds(700));
        await Task.Delay(50);
        var dispatcher = new ScriptDispatcher(job => InferenceResult.Succeeded(job.JobId, Answer));

        var outcome = await Ask(fleet, fleet.Registry(retrySeconds: 30), dispatcher);

        Assert.Equal(Answer, outcome.ResponseJson);

        // Without the setting: 137's 503 with the reason.
        fleet.Reassemble(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        var refused = await Ask(fleet, fleet.Registry(retrySeconds: 0), dispatcher);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refused.ErrorStatus);
        Assert.Contains("reassembling", refused.ErrorMessage);
    }
}
