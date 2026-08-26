using System.Runtime.CompilerServices;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// A solo node on a real Kestrel port, with a scripted backend in place of Ollama.
/// </summary>
/// <remarks>
/// It goes through the real <see cref="NodeHostFactory"/> and the real
/// <see cref="NodeHostBuilderExtensions.AddInferHubNode"/>, driving configuration in through
/// command-line args — so the pre-flight "is solo on?" read, the options binding, the validators
/// and the middleware pipeline are all the shipped ones. A harness that hand-registered the
/// endpoints would prove the handlers work and nothing about whether they are reachable.
/// </remarks>
internal sealed class SoloHost : IAsyncDisposable
{
    private WebApplication app = null!;
    private string dataDirectory = null!;

    public ScriptedBackend Backend { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public string Url { get; private set; } = null!;

    public static async Task<SoloHost> StartAsync(
        ScriptedBackend? backend = null,
        params string[] settings)
    {
        var host = new SoloHost
        {
            Backend = backend ?? new ScriptedBackend(),
            dataDirectory = Path.Combine(Path.GetTempPath(), "inferhub-solo-" + Guid.NewGuid().ToString("N"))
        };

        var builder = (WebApplicationBuilder)NodeHostFactory.Create([
            "--LocalApi:Enabled=true",
            "--Coordinator:Enabled=false",
            "--Node:Name=solo-test",
            $"--Node:DataDirectory={host.dataDirectory}",
            // The corpus default is a *relative* path, so without this every test in the run would
            // share one directory under the test working directory and see each other's documents.
            // Later args win, so a test that wants a specific directory (to prove a corpus survives
            // a restart) still gets one.
            $"--LocalApi:Retrieval:DataDirectory={Path.Combine(host.dataDirectory, "retrieval")}",
            .. settings
        ]);

        // Port 0: the OS picks, so tests never collide with each other or with a real node.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.AddInferHubNode();
        builder.Services.AddSingleton<IInferenceBackend>(host.Backend);

        host.app = (WebApplication)NodeHostFactory.Build(builder);
        await host.app.StartAsync();

        host.Url = host.app.Urls.First();
        host.Client = new HttpClient { BaseAddress = new Uri(host.Url) };

        return host;
    }

    /// <summary>Starts a host that is expected to fail validation, returning the exception.</summary>
    public static async Task<Exception> StartExpectingFailureAsync(params string[] settings)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "inferhub-solo-" + Guid.NewGuid().ToString("N"));

        var builder = NodeHostFactory.Create([
            "--Node:Name=solo-test",
            $"--Node:DataDirectory={dataDirectory}",
            .. settings
        ]);

        if (builder is WebApplicationBuilder web)
        {
            web.WebHost.UseUrls("http://127.0.0.1:0");
        }

        builder.Logging.ClearProviders();
        builder.AddInferHubNode();
        builder.Services.AddSingleton<IInferenceBackend>(new ScriptedBackend());

        // A bad config can abort at either point and both are "refuses to boot": ValidateOnStart
        // fires in StartAsync, while mapping the local API resolves LocalApiOptions a moment
        // earlier. What the operator sees is the same message either way, which is what matters.
        return await Record.ExceptionAsync(async () =>
        {
            using var host = NodeHostFactory.Build(builder);
            await host.StartAsync();
            await host.StopAsync();
        }) ?? throw new InvalidOperationException("expected the host to refuse to start");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();

        try
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Stands in for Ollama, returning exactly what it is told to. The same instance backs both sides
/// of <c>SoloParityTests</c>, which is what makes "the hub and the node answered differently" the
/// only thing those assertions can be detecting.
/// </summary>
internal sealed class ScriptedBackend : IInferenceBackend
{
    private readonly List<string> streamChunks = [];

    public string Name => "scripted";

    public string Endpoint => "http://localhost:11434/";

    /// <summary>Phase 67. A stand-in for an ordinary backend, so it serves both kinds.</summary>
    public IReadOnlyList<string> Kinds { get; set; } = [CapabilityKinds.Chat, CapabilityKinds.Embed];

    public bool SupportsModelManagement => true;

    public string BlockingResponse { get; set; } = """
    {"model":"llama3","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"Hello there."},"done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":4}
    """;

    public string EmbedResponse { get; set; } = """{"model":"nomic","embeddings":[[0.1,0.2,0.3]]}""";

    public IReadOnlyList<ModelInfo> Models { get; set; } = [new ModelInfo("llama3", "sha256:abc", 4661211808)];

    /// <summary>Thrown from every inference call, to exercise the error paths.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Blocks each inference call until released — used to fill the concurrency gate.</summary>
    public SemaphoreSlim? Hold { get; set; }

    /// <summary>How many calls have reached the backend and not yet returned.</summary>
    public int InFlight => Volatile.Read(ref inFlight);

    /// <summary>
    /// The Ollama-shaped body the node handed the backend. Cross-checked against what the hub
    /// handed its node, so a translation that drifted on one side is visible.
    /// </summary>
    public string? LastRequestJson { get; private set; }

    private int inFlight;

    public ScriptedBackend Streaming(params string[] chunks)
    {
        streamChunks.Clear();
        streamChunks.AddRange(chunks);
        return this;
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        return Models;
    }

    public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
        => RespondAsync(requestJson, cancellationToken);

    public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
        => RespondAsync(requestJson, cancellationToken);

    /// <summary>
    /// When set, <c>/api/embed</c> answers with <see cref="TestEmbeddings"/> over the request's own
    /// input rather than the canned <see cref="EmbedResponse"/> — so a corpus actually ranks, and
    /// the hub and the node embed identically (phase 38).
    /// </summary>
    public bool DeterministicEmbeddings { get; set; }

    public async Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
    {
        LastRequestJson = requestJson;
        await GateAsync(cancellationToken);

        if (Failure is not null)
        {
            throw Failure;
        }

        return DeterministicEmbeddings
            ? TestEmbeddings.RespondTo(requestJson)
            : EmbedResponse;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastRequestJson = requestJson;
        await GateAsync(cancellationToken);

        if (Failure is not null)
        {
            throw Failure;
        }

        foreach (var chunk in streamChunks)
        {
            yield return chunk;
        }
    }

    private async Task<string> RespondAsync(string requestJson, CancellationToken cancellationToken)
    {
        LastRequestJson = requestJson;
        await GateAsync(cancellationToken);

        if (Failure is not null)
        {
            throw Failure;
        }

        return BlockingResponse;
    }

    private async Task GateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref inFlight);

        if (Hold is not null)
        {
            await Hold.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Waits until <paramref name="count"/> calls have reached the backend.</summary>
    public async Task WaitForInFlightAsync(int count)
    {
        for (var i = 0; i < 500 && InFlight < count; i++)
        {
            await Task.Delay(10);
        }

        if (InFlight < count)
        {
            throw new TimeoutException($"expected {count} backend call(s); saw {InFlight}");
        }
    }

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task DeleteAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task WarmAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
