using System.Collections.Concurrent;
using System.Diagnostics;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 89: <c>Dispatcher:Deadlines</c>, one deadline per capability. Until this phase no test in
/// the solution let a dispatch deadline actually expire — the v3.28 F5 finding was made on a card.
/// </summary>
public class DispatchDeadlineTests
{
    private static readonly RoutableNode Node = new("conn-a", "node-a", "alpha");

    // --- The lookup (89 D1, D2) ------------------------------------------------------------------

    [Fact]
    public void WithNoDeadlinesEveryCapabilityGetsTimeoutSeconds()
    {
        var options = new DispatcherOptions();

        foreach (var capability in new[] { "chat", "embed", "image", "video", "my-tool", null })
        {
            var deadline = options.DeadlineFor(capability);
            Assert.Equal(TimeSpan.FromSeconds(300), deadline.Timeout);
            Assert.Equal("Dispatcher:TimeoutSeconds", deadline.Source);
        }
    }

    [Fact]
    public void ANamedCapabilityGetsItsOwnDeadlineAndTheOthersKeepTheDefault()
    {
        var options = new DispatcherOptions
        {
            TimeoutSeconds = 120,
            Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["video"] = 3600 },
        };

        Assert.Equal(TimeSpan.FromSeconds(3600), options.DeadlineFor("video").Timeout);
        Assert.Equal("Dispatcher:Deadlines:video", options.DeadlineFor("video").Source);
        Assert.Equal(TimeSpan.FromSeconds(120), options.DeadlineFor("chat").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(120), options.DeadlineFor("image").Timeout);
    }

    [Fact]
    public void TheLookupIsCaseInsensitiveEvenWhenTheDictionaryIsNot()
    {
        // A caller (or a binder) that hands in a default-comparer dictionary must not turn "Video"
        // in config into a silent miss.
        var options = new DispatcherOptions { Deadlines = new() { ["Video"] = 3600 } };

        Assert.Equal(TimeSpan.FromSeconds(3600), options.DeadlineFor("video").Timeout);
    }

    [Fact]
    public void ATimeoutSecondsBelowOneIsStillReadAsOne()
    {
        // Byte-identical to every release before this one: TimeoutSeconds was clamped, never refused.
        Assert.Equal(TimeSpan.FromSeconds(1), new DispatcherOptions { TimeoutSeconds = 0 }.DeadlineFor("chat").Timeout);
    }

    [Theory]
    [InlineData("generate", "chat")]
    [InlineData("chat", "chat")]
    [InlineData("embed", "embed")]
    public void AnInferenceJobIsTimedByTheCapabilityItIsRoutedOn(string kind, string capability)
    {
        var options = new DispatcherOptions
        {
            Deadlines = new(StringComparer.OrdinalIgnoreCase) { [capability] = 42 },
        };

        var deadline = options.DeadlineFor(new InferenceJob(Guid.NewGuid(), kind, "{}"));

        Assert.Equal(TimeSpan.FromSeconds(42), deadline.Timeout);
        Assert.Equal(capability, deadline.Capability);
    }

    [Fact]
    public void TheHubsInternalJobKindsAreNotCapabilitiesAndTakeTheDefault()
    {
        // 89 D2: a "vector-query" key must not become a hidden knob on an internal job.
        var options = new DispatcherOptions
        {
            Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["vector-query"] = 5 },
        };

        var deadline = options.DeadlineFor(new InferenceJob(Guid.NewGuid(), "vector-query", "{}"));

        Assert.Equal(TimeSpan.FromSeconds(300), deadline.Timeout);
        Assert.Null(deadline.Capability);
    }

    // --- Config binding and validation (89 D4) ---------------------------------------------------

    [Fact]
    public void DeadlinesBindFromConfigurationAndAnEmptySectionBindsToNothing()
    {
        var bound = Bind(new Dictionary<string, string?>
        {
            ["Dispatcher:TimeoutSeconds"] = "300",
            ["Dispatcher:Deadlines:video"] = "3600",
            ["Dispatcher:Deadlines:IMAGE"] = "900",
        });

        Assert.Equal(TimeSpan.FromSeconds(3600), bound.DeadlineFor("video").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(900), bound.DeadlineFor("image").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(300), bound.DeadlineFor("chat").Timeout);

        var empty = Bind(new Dictionary<string, string?> { ["Dispatcher:TimeoutSeconds"] = "300" });
        Assert.Empty(empty.Deadlines);
    }

    [Fact]
    public void TheShippedAppsettingsDeclaresNoDeadline()
    {
        // "A deployment that changes no config behaves identically" — checked against the file that
        // ships in the image, not against the class default.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "InferHub.Coordinator", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var options = new DispatcherOptions();
        configuration.GetSection(DispatcherOptions.SectionName).Bind(options);

        Assert.Equal(300, options.TimeoutSeconds);
        Assert.Empty(options.Deadlines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ADeadlineBelowOneIsRefusedNamingTheKey(int seconds)
    {
        var result = new DispatcherOptionsValidator().Validate(null, new DispatcherOptions
        {
            Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["video"] = seconds },
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains("Dispatcher:Deadlines:video"));
    }

    [Fact]
    public void ACustomToolsCapabilityIsAValidKey()
    {
        // 89 D4: the hub does not decide which capabilities may exist (phase-40 D1).
        var result = new DispatcherOptionsValidator().Validate(null, new DispatcherOptions
        {
            Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["ocr-invoices"] = 60 },
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ABadDeadlineStopsTheHostFromStarting()
    {
        var services = new ServiceCollection();
        services.AddOptions<DispatcherOptions>()
            .Configure(options => options.Deadlines["video"] = 0)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DispatcherOptions>, DispatcherOptionsValidator>();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DispatcherOptions>>().Value);
        Assert.Contains("Dispatcher:Deadlines:video", ex.Message);
    }

    // --- The dispatcher, with a clock that really runs out (89 D1, D3) ---------------------------

    [Fact]
    public async Task ABlockingToolJobDiesAtItsCapabilitysDeadlineNamingIt()
    {
        var dispatcher = NewDispatcher(
            new DispatcherOptions
            {
                TimeoutSeconds = 30,
                Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["echo"] = 1 },
            },
            out var proxy,
            out var metrics);
        var job = new ToolJob(Guid.NewGuid(), "echo", "echo-1", "{}");

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.DispatchToolAsync(Node, job, CancellationToken.None));
        clock.Stop();

        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
        Assert.Contains("echo", ex.Message);
        Assert.Contains("1 s", ex.Message);
        Assert.Contains("Dispatcher:Deadlines:echo", ex.Message);

        // The node is told to stop — a deadline that only releases the caller leaves a GPU busy.
        Assert.Contains(proxy.Sent, sent => sent.Method == "CancelJob" && Equals(sent.Args[0], job.JobId));
        Assert.Equal(0, metrics.Snapshot(DateTimeOffset.UtcNow).RequestsInFlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABlockingJobTheHubGaveUpOnIsCountedFailedExactlyOnce(bool cancelInsteadOfTimeout)
    {
        // Before phase 89 the registry was told and the metrics were not, so the in-flight gauge on
        // /metrics climbed by one for every blocking job that timed out or whose caller walked away.
        var dispatcher = NewDispatcher(new DispatcherOptions { TimeoutSeconds = 1 }, out _, out var metrics);
        var job = new InferenceJob(Guid.NewGuid(), "chat", "{}");
        using var cts = new CancellationTokenSource();

        var dispatch = dispatcher.DispatchAsync(Node, job, cts.Token);
        if (cancelInsteadOfTimeout)
        {
            cts.Cancel();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => dispatch);

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(0, snapshot.RequestsInFlight);
        Assert.Equal(1, snapshot.RequestsFailed);

        // A late answer from the node finds nothing and counts nothing.
        Assert.False(dispatcher.Complete(new InferenceResult(job.JobId, true, "{}", null)));
        Assert.Equal(1, metrics.Snapshot(DateTimeOffset.UtcNow).RequestsFailed);
        Assert.Equal(0, metrics.Snapshot(DateTimeOffset.UtcNow).RequestsCompleted);
    }

    [Fact]
    public async Task AnotherCapabilityOnTheSameHubKeepsTheLongerDefault()
    {
        var dispatcher = NewDispatcher(
            new DispatcherOptions
            {
                TimeoutSeconds = 30,
                Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["echo"] = 1 },
            },
            out _,
            out _);
        var job = new ToolJob(Guid.NewGuid(), "transcribe", "whisper", "{}");

        var dispatch = dispatcher.DispatchToolAsync(Node, job, CancellationToken.None);

        // Past the 1-second echo deadline, and still alive.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(dispatch.IsCompleted);

        Assert.True(dispatcher.CompleteTool(new ToolResult(job.JobId, true, "{}", null)));
        var result = await dispatch;
        Assert.True(result.Success);
    }

    [Fact]
    public async Task WithNoDeadlinesAToolJobDiesAtTimeoutSecondsAndSaysSo()
    {
        var dispatcher = NewDispatcher(new DispatcherOptions { TimeoutSeconds = 1 }, out _, out _);
        var job = new ToolJob(Guid.NewGuid(), "video", "wan-t2v-1.3b", "{}");

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.DispatchToolAsync(Node, job, CancellationToken.None));

        Assert.Contains("video", ex.Message);
        Assert.Contains("Dispatcher:TimeoutSeconds", ex.Message);
    }

    [Fact]
    public async Task ABlockingChatJobDiesAtTheChatDeadline()
    {
        var dispatcher = NewDispatcher(
            new DispatcherOptions
            {
                TimeoutSeconds = 30,
                Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["chat"] = 1 },
            },
            out _,
            out _);

        // `generate` is chat's other surface, and must be timed as chat.
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.DispatchAsync(Node, job, CancellationToken.None));

        Assert.Contains("Dispatcher:Deadlines:chat", ex.Message);
    }

    [Fact]
    public async Task AStreamThatNeverStartsDiesAtItsCapabilitysDeadline()
    {
        var dispatcher = NewDispatcher(
            new DispatcherOptions
            {
                TimeoutSeconds = 30,
                Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["speak"] = 1 },
            },
            out var proxy,
            out var metrics);
        var job = new ToolJob(Guid.NewGuid(), "speak", "piper", "{}");

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.DispatchToolStreamAsync(Node, job, CancellationToken.None));

        Assert.Contains("Dispatcher:Deadlines:speak", ex.Message);
        Assert.Contains(proxy.Sent, sent => sent.Method == "CancelJob" && Equals(sent.Args[0], job.JobId));
        Assert.Equal(0, metrics.Snapshot(DateTimeOffset.UtcNow).RequestsInFlight);
    }

    [Fact]
    public async Task AStreamThatStartedIsEndedAtItsDeadlineWithTheSameMessage()
    {
        var dispatcher = NewDispatcher(
            new DispatcherOptions
            {
                TimeoutSeconds = 30,
                Deadlines = new(StringComparer.OrdinalIgnoreCase) { ["chat"] = 1 },
            },
            out _,
            out _);
        var job = new InferenceJob(Guid.NewGuid(), "chat", "{}");

        var dispatch = dispatcher.DispatchStreamAsync(Node, job, CancellationToken.None);
        dispatcher.WriteChunk(new InferenceChunk(job.JobId, "{\"message\":{\"content\":\"hi\"}}", Done: false));
        var reader = await dispatch;

        Assert.True(reader.TryRead(out _));
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync())
            {
            }
        });

        Assert.Contains("Dispatcher:Deadlines:chat", ex.Message);
    }

    private static DispatcherOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<DispatcherOptions>().Bind(configuration.GetSection(DispatcherOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<DispatcherOptions>>().Value;
    }

    private static Dispatcher NewDispatcher(
        DispatcherOptions options,
        out RecordingProxy proxy,
        out Metrics metrics)
    {
        var registry = new NodeRegistry();
        registry.Upsert(
            "conn-a",
            new NodeRegistration("node-a", "alpha", "http://localhost/", "1.0.0"),
            DateTimeOffset.UtcNow);

        metrics = new Metrics();
        proxy = new RecordingProxy();

        return new Dispatcher(
            new RecordingHubContext(proxy),
            registry,
            metrics,
            new ThroughputTracker(),
            Options.Create(options),
            NullLogger<Dispatcher>.Instance);
    }

    private sealed class RecordingHubContext(RecordingProxy proxy) : IHubContext<NodeHub>
    {
        public IHubClients Clients { get; } = new RecordingClients(proxy);
        public IGroupManager Groups { get; } = new NoOpGroups();
    }

    private sealed class RecordingClients(RecordingProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class RecordingProxy : IClientProxy
    {
        public ConcurrentQueue<(string Method, object?[] Args)> Sent { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
