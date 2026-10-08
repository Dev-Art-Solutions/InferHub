using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using InferHub.Node.Backends;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Strata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 99. A Strata install on disk (a folder of <c>strata-*.json</c> configs, as Strata's setup
/// writes them), a catalogue over it whose models are <see cref="FakeColibriLauncher"/> sockets, and a
/// <c>setup.py</c> that prints what the real one prints and writes the config. The real engine and the
/// real installer are in the notes.
/// </summary>
internal static class FakeStrata
{
    /// <summary>A Strata "checkout": <c>serve/server.py</c> exists, and one config per name.</summary>
    public static string Install(params string[] configs)
    {
        var root = Path.Combine(Path.GetTempPath(), "inferhub-strata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "serve"));
        File.WriteAllText(Path.Combine(root, "serve", "server.py"), "# not run by these tests\n");

        foreach (var name in configs)
        {
            WriteConfig(root, name);
        }

        return root;
    }

    /// <summary>What setup.py writes, cut to the two keys Strata itself checks (its #549).</summary>
    public static void WriteConfig(string root, string name, string? apiKey = null) =>
        File.WriteAllText(
            Path.Combine(root, name + ".json"),
            $$"""{"exe": "/opt/strata/engine/strata", "args": ["--native", "x.gguf"], "model_name": "qwen3.8-flash-next", "port": 8080{{(apiKey is null ? "" : $", \"api_key\": \"{apiKey}\"")}}}""");

    public static StrataOptions Options(string root, Action<StrataOptions>? configure = null)
    {
        var options = new StrataOptions { Root = root, Serve = { Port = 18300, LoadTimeout = TimeSpan.FromSeconds(10) } };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>The catalogue, and every API key a loaded model's client was given, by base URL.</summary>
    public static StrataCatalog Catalog(
        StrataOptions options,
        FakeColibriLauncher launcher,
        ConcurrentDictionary<string, string?>? keys = null,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(UpstreamBackend.HttpClientName);
        services.AddHttpClient("probe", http => http.Timeout = TimeSpan.FromSeconds(2));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        launcher.StrataHealth = true;

        return new StrataCatalog(
            options,
            launcher,
            (baseUrl, apiKey) =>
            {
                keys?.TryAdd(baseUrl, apiKey);
                return new UpstreamBackend(
                    factory,
                    Microsoft.Extensions.Options.Options.Create(new BackendOptions { Type = BackendOptions.Strata }),
                    Microsoft.Extensions.Options.Options.Create(new UpstreamBackendOptions { BaseUrl = baseUrl, ApiKey = apiKey, TimeoutSeconds = 30 }),
                    NullLogger<UpstreamBackend>.Instance);
            },
            () => factory.CreateClient("probe"),
            TimeSpan.FromSeconds(2),
            time ?? TimeProvider.System,
            NullLogger.Instance);
    }

    public static StrataInstaller Installer(StrataCatalog catalog, StrataOptions options, FakeSetupRunner runner, HuggingFaceOptions? hf = null)
        => new(options, hf ?? new HuggingFaceOptions { Enabled = true }, catalog, runner, NullLogger.Instance);
}

/// <summary>
/// Prints the lines Strata's setup prints for an install — its step headers and its download bar, the
/// bar redrawn several times per percent — writes the config on success, and exits with a code.
/// </summary>
internal sealed class FakeSetupRunner(string root) : IStrataSetupRunner
{
    public ConcurrentQueue<ProcessStartInfo> Runs { get; } = new();

    public int ExitCode { get; set; }

    /// <summary>Exit 0 without writing a config: setup "finished" and nothing is listed.</summary>
    public bool WriteNothing { get; set; }

    /// <summary>Held open until released, so a second install can be seen waiting.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async IAsyncEnumerable<SetupOutput> RunAsync(ProcessStartInfo info, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Runs.Enqueue(info);
        var family = Arg(info, "--family");
        var size = Arg(info, "--model");

        yield return new SetupOutput("Strata - Qwen3.8-Flash-Next on a normal PC (a GPU + system RAM + CPU)");
        yield return new SetupOutput($"=== Step 5: downloading Qwen3.8-Flash-Next {size} ===");

        foreach (var done in new[] { "0.01", "0.02", "14.80", "14.80", "29.61" })
        {
            var percent = (int)(double.Parse(done, System.Globalization.CultureInfo.InvariantCulture) / 29.61 * 100);
            yield return new SetupOutput($"Qwen3.8-Flash-Next-GSQ-RCO-{size}-00001-of-00002.gguf:  {done} / 29.61 GB ({percent}%)");
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (ExitCode != 0)
        {
            yield return new SetupOutput("  [x] not enough free disk space on E: (needs 70 GB)");
            yield return new SetupOutput(null, ExitCode);
            yield break;
        }

        if (!WriteNothing)
        {
            var tag = family == "qwen" ? "" : family + "-";
            FakeStrata.WriteConfig(root, $"strata-{tag}{size!.ToLowerInvariant()}");
        }

        yield return new SetupOutput("  [ok] installed");
        yield return new SetupOutput(null, 0);
    }

    private static string? Arg(ProcessStartInfo info, string flag)
    {
        var i = info.ArgumentList.IndexOf(flag);
        return i >= 0 && i + 1 < info.ArgumentList.Count ? info.ArgumentList[i + 1] : null;
    }
}
