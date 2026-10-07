using System.Collections.Concurrent;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 97. Launches a socket that answers what one <c>coli serve --model-id {model}</c> answers —
/// <c>/health</c>, <c>/v1/models</c>, a chat, a Brio — in place of the Python process, so the
/// catalogue's admission, eviction and idle stops cross real HTTP. The real engine is in the notes.
/// </summary>
internal sealed class FakeColibriLauncher : IColibriLauncher
{
    private readonly ConcurrentDictionary<string, int> chats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every launch and every stop, in order: <c>launch:a</c>, <c>stop:a</c>.</summary>
    public ConcurrentQueue<string> Events { get; } = new();

    /// <summary>How long a launched model stays unanswering, as if reading its dense weights.</summary>
    public TimeSpan LoadDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Models whose process exits before it ever answers.</summary>
    public HashSet<string> Crashing { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The directory each launch was given, by model.</summary>
    public ConcurrentDictionary<string, string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, FakeColibriProcess> Alive { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Chats(string model) => chats.GetValueOrDefault(model);

    public IColibriProcess Launch(string model, string directory, int port)
    {
        Events.Enqueue($"launch:{model}");
        Directories[model] = directory;

        if (Crashing.Contains(model))
        {
            return new CrashedProcess(this, model);
        }

        var process = FakeColibriProcess.Start(this, model, LoadDelay);
        Alive[model] = process;
        return process;
    }

    internal void Chatted(string model) => chats.AddOrUpdate(model, 1, (_, n) => n + 1);

    internal void Stopped(string model)
    {
        Events.Enqueue($"stop:{model}");
        Alive.TryRemove(model, out _);
    }

    private sealed class CrashedProcess(FakeColibriLauncher owner, string model) : IColibriProcess
    {
        public string BaseUrl => "http://127.0.0.1:9/v1";

        public string? Exited => "exited with code 1";

        public Task StopAsync()
        {
            owner.Stopped(model);
            return Task.CompletedTask;
        }
    }
}

internal sealed class FakeColibriProcess : IColibriProcess
{
    private WebApplication app = null!;
    private int stopped;

    public string BaseUrl { get; private set; } = null!;

    public string? Exited => null;

    public static FakeColibriProcess Start(FakeColibriLauncher owner, string model, TimeSpan loadDelay)
    {
        var process = new FakeColibriProcess();
        var ready = DateTimeOffset.UtcNow + loadDelay;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/health", () => DateTimeOffset.UtcNow < ready ? Results.StatusCode(503) : Results.Json(new { status = "ok" }));
        app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = model, @object = "model", owned_by = "colibri" } } }));
        app.MapPost("/v1/chat/completions", () =>
        {
            owner.Chatted(model);
            return Results.Json(new
            {
                id = "chatcmpl-1",
                @object = "chat.completion",
                created = 0,
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = $"hello from {model}" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 3, completion_tokens = 1, total_tokens = 4 }
            });
        });
        app.MapPost("/v1/brio", () => Results.Json(new
        {
            @object = "brio.choice",
            answer = "b",
            entropy = 0.2,
            choices = new[] { new { option = "b", p = 0.9 }, new { option = "a", p = 0.1 } },
            model,
            usage = new { prompt_tokens = 12, completion_tokens = 0, total_tokens = 12 }
        }));

        app.StartAsync().GetAwaiter().GetResult();
        process.app = app;
        process.BaseUrl = app.Urls.First() + "/v1";
        process.owner = owner;
        process.model = model;
        return process;
    }

    private FakeColibriLauncher owner = null!;
    private string model = null!;

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1)
        {
            return;
        }

        owner.Stopped(model);
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>A clock that can be moved forward, while its timers still run in real time (the readiness polls).</summary>
internal sealed class ShiftableTime : TimeProvider
{
    private long offsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref offsetTicks, by.Ticks);
}

internal static class FakeColibri
{
    /// <summary>A directory holding one sub-directory per name, each a "converted model" (a <c>config.json</c>).</summary>
    public static string Catalogue(params string[] models)
    {
        var root = Path.Combine(Path.GetTempPath(), "inferhub-colibri-" + Guid.NewGuid().ToString("N"));

        foreach (var model in models)
        {
            Directory.CreateDirectory(Path.Combine(root, model));
            File.WriteAllText(Path.Combine(root, model, "config.json"), "{}");
        }

        return root;
    }

    public static ColibriCatalog Catalog(
        ColibriOptions options,
        FakeColibriLauncher launcher,
        TimeProvider? time = null,
        TimeSpan? stopDrain = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(UpstreamBackend.ColibriHttpClientName)
            .AddHttpMessageHandler(() => new ColibriRequestHandler(options.KvSlots));
        services.AddHttpClient("probe", http => http.Timeout = TimeSpan.FromSeconds(2));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        return new ColibriCatalog(
            options,
            launcher,
            baseUrl => new UpstreamBackend(
                factory,
                Options.Create(new BackendOptions { Type = BackendOptions.Colibri }),
                Options.Create(new UpstreamBackendOptions { BaseUrl = baseUrl, TimeoutSeconds = 30 }),
                NullLogger<UpstreamBackend>.Instance),
            () => factory.CreateClient("probe"),
            stopDrain ?? TimeSpan.FromSeconds(2),
            time ?? TimeProvider.System,
            NullLogger.Instance);
    }

    public static string Chat(string model) =>
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}],"stream":false}""";

    public static ToolJob Score(string model) => new(
        Guid.NewGuid(),
        CapabilityKinds.Score,
        model,
        $$"""{"model":"{{model}}","state":"A document.","question":"Which?","options":["a","b"]}""");
}
