using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InferHub.Node.Backends.Catalog;
using InferHub.Node.Backends.Colibri;
using InferHub.Shared.Contracts;
using InferHub.Shared.Strata;

namespace InferHub.Node.Backends.Strata;

/// <summary>What the hub and the profile applier can ask of a Strata catalogue (phase 99).</summary>
public interface IStrataControl : ICatalogControl;

/// <summary>
/// A node's Strata catalogue (phase 99): every <c>strata-*.json</c> config a Strata install holds is a
/// model, listed by its file name; the one a request names is served by Strata's own
/// <c>serve/server.py</c> on a loopback port; <see cref="ModelCatalog"/>'s admission decides how many
/// run at once and which one gives way.
/// </summary>
/// <remarks>
/// <para>
/// <b>A model is a config, not a directory</b> (99 D2). Strata's setup writes one
/// <c>strata-&lt;model&gt;.json</c> per installed size — the pack, the expert profile, the KV decision
/// for <em>this</em> box — and its sizes share files (the Coder reads the original's shard 2), so the
/// config is the only unit that means "one servable model". The name is the file's name, because
/// every config's own <c>model_name</c> is the same family name for every size.
/// </para>
/// <para>
/// <b>Chat only.</b> Strata also speaks Anthropic's and OpenAI's Responses routes, which the hub has its
/// own edges for; it has no embeddings, and pictures need a setup choice the node cannot see from
/// the config — 99's non-goal, not declared.
/// </para>
/// </remarks>
public sealed class StrataCatalog : ModelCatalog, IStrataControl
{
    private static readonly string[] ChatOnly = [CapabilityKinds.Chat];

    private readonly StrataOptions options;
    private readonly Func<string, string?, UpstreamBackend> upstreamWithKey;
    private readonly ConcurrentDictionary<string, byte> warned = new(StringComparer.OrdinalIgnoreCase);

    public StrataCatalog(
        StrataOptions options,
        ICatalogLauncher launcher,
        Func<string, string?, UpstreamBackend> upstreamFor,
        Func<HttpClient> probeClient,
        TimeSpan stopDrain,
        TimeProvider time,
        ILogger logger)
        : base(options.Serve, launcher, baseUrl => upstreamFor(baseUrl, null), probeClient, stopDrain, time, logger)
    {
        this.options = options;
        upstreamWithKey = upstreamFor;
    }

    public override string Name => BackendOptions.Strata;

    protected override string SectionName => StrataOptions.SectionName;

    protected override string ProcessName => "Strata's server.py";

    protected override string CatalogueSource =>
        $"no strata-*.json in {options.ResolvedConfigDir()}; install one with Strata's setup on the box, or from the hub's Strata panel";

    public override IReadOnlyList<string> Kinds => ChatOnly;

    /// <summary>Set when this node can install from the hub (<c>HuggingFace:Enabled</c>): the panel offers <see cref="StrataModels"/>.</summary>
    public bool InstallsFromHub { get; set; }

    /// <summary>An install wrote a new config: the catalogue grew, and the hub's panel hears it now.</summary>
    internal void Installed() => OnChanged();

    protected override IReadOnlyList<CatalogInstallable>? Installable(IReadOnlyCollection<string> catalogue)
    {
        if (!InstallsFromHub)
        {
            return null;
        }

        var installed = new HashSet<string>(catalogue, StringComparer.OrdinalIgnoreCase);

        return StrataModels.All()
            .Select(pair =>
            {
                var name = StrataModels.CatalogName(pair.Family, pair.Size);
                return new CatalogInstallable(name, StrataModels.Link(pair.Family, pair.Size), $"{pair.Family.Title} {pair.Size.Name}: {pair.Size.About}", installed.Contains(name));
            })
            .ToArray();
    }

    /// <summary>
    /// 96 D3 keeps an unnamed pull for the one engine that can pull; an install is a Hugging Face link
    /// instead (99 D4), so an <c>owner/repo</c> name is never two engines' business.
    /// </summary>
    public override IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Strata models are installed from a Hugging Face link (POST /api/admin/nodes/{id}/huggingface, or the hub's Strata panel), which runs Strata's own setup on the node");

    public override Task DeleteAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "this node does not delete Strata models: Strata's sizes share files, and only its setup knows which; remove the config and files on the box");

    /// <summary>The <c>strata-*.json</c> model configs in <c>ConfigDir</c>, named by file, plus <c>Serve:Models</c>.</summary>
    protected internal override Dictionary<string, string> Scan()
    {
        var catalogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = options.ResolvedConfigDir();

        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "strata-*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                // Strata keeps each config's chat settings beside it in this file (its #346): not a model.
                if (file.EndsWith(".shared-settings.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);

                if (!ColibriOptions.IsModelName(name))
                {
                    Warn("name:" + name, "'{File}' is a Strata config, but '{Name}' is not a model name; it is not listed. Name it under Strata:Serve:Models.", file, name);
                    continue;
                }

                if (!IsModelConfig(file))
                {
                    Warn("config:" + file, "'{File}' is not a Strata model config (a JSON object with \"exe\" and \"args\", which setup.py writes); it is not listed.", file, name);
                    continue;
                }

                catalogue[name] = file;
            }
        }
        else
        {
            Warn("dir:" + dir, "Strata's config directory '{Directory}' is not one this node can see; it lists nothing until it is there.", dir, null);
        }

        foreach (var pair in options.Serve.Models)
        {
            if (ColibriOptions.IsModelName(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                catalogue[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        return catalogue;
    }

    protected override UpstreamBackend UpstreamFor(string model, string path, string baseUrl)
        => upstreamWithKey(baseUrl, string.IsNullOrWhiteSpace(options.ApiKey) ? ApiKeyOf(path) : options.ApiKey.Trim());

    /// <summary>Strata's <c>/health</c> answers before a lazy engine loads; <c>"loaded": false</c> is not ready.</summary>
    protected override async Task<bool> IsReadyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            return !(body.RootElement.ValueKind == JsonValueKind.Object
                     && body.RootElement.TryGetProperty("loaded", out var loaded)
                     && loaded.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Strata's own test for a config (its #549): a JSON object with <c>exe</c> and an <c>args</c> list.</summary>
    internal static bool IsModelConfig(string path)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(path));
            var root = config.RootElement;

            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("exe", out var exe) && exe.ValueKind == JsonValueKind.String && exe.GetString()!.Length > 0
                   && root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The config's own <c>api_key</c> — a string (<c>"k1,k2"</c>, llama.cpp's form) or a list; the first
    /// key is the one the node sends. A config written with one would otherwise answer every request 401.
    /// </summary>
    internal static string? ApiKeyOf(string path)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(path));

            if (!config.RootElement.TryGetProperty("api_key", out var key))
            {
                return null;
            }

            var first = key.ValueKind switch
            {
                JsonValueKind.String => key.GetString()?.Split(',').FirstOrDefault(),
                JsonValueKind.Array => key.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()).FirstOrDefault(),
                _ => null
            };

            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Warn(string key, string message, string first, string? second)
    {
        if (warned.TryAdd(key, 0))
        {
            Logger.LogWarning(message, first, second);
        }
    }
}

/// <summary><c>serve/server.py</c> for one config on one port — pure, so the tests read the command line without Python.</summary>
public static class StrataServe
{
    /// <summary>Makes the server print each answer's raw model text (Strata's own debug switch); rule 7 keeps it from the child.</summary>
    public const string ContentTeeVariable = "STRATA_DEBUG";

    /// <summary>The server's key variable: set to <c>Strata:ApiKey</c> when that is written, removed otherwise.</summary>
    public const string ApiKeyVariable = "STRATA_API_KEY";

    public static ProcessStartInfo StartInfo(StrataOptions options, string config, int port)
    {
        var root = options.Root!.Trim();
        var serve = options.Serve;

        var info = new ProcessStartInfo(options.ResolvedPython())
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
                 {
                     Path.Combine(root, "serve", "server.py"),
                     "--engine", serve.Engine,
                     "--config", config,
                     "--host", "127.0.0.1",
                     "--port", port.ToString(CultureInfo.InvariantCulture)
                 })
        {
            info.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(serve.Gpu))
        {
            info.ArgumentList.Add("--gpu");
            info.ArgumentList.Add(serve.Gpu.Trim());
        }

        foreach (var argument in serve.Arguments.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            info.ArgumentList.Add(argument.Trim());
        }

        info.Environment.Remove(ContentTeeVariable);
        info.Environment.Remove(ApiKeyVariable);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            // The environment, not --api-key: an argument is in every process listing on the box.
            info.Environment[ApiKeyVariable] = options.ApiKey.Trim();
        }

        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        return info;
    }
}

/// <summary>The shipped launcher: one supervised <c>server.py</c> per config, 95's loop and Job Object.</summary>
public sealed class StrataProcessLauncher(StrataOptions options, TimeProvider time, ILogger logger) : ICatalogLauncher
{
    public ICatalogProcess Launch(string model, string config, int port)
    {
        var server = Path.Combine(options.Root!.Trim(), "serve", "server.py");

        var process = new EngineProcess(
            $"strata:{model}",
            () => StrataServe.StartInfo(options, config, port),
            () => !File.Exists(server)
                ? $"'{server}' is not there; Strata:Root must be a Strata checkout"
                : File.Exists(config) ? null : $"'{config}' is not a file this node can see",
            time,
            logger);

        process.Start();
        return new LaunchedCatalogProcess(process, $"http://127.0.0.1:{port}/v1");
    }
}
