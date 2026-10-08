using System.Collections.Concurrent;
using InferHub.Node.Backends.Catalog;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// What the hub and the node's profile applier can ask of a colibri catalogue (phase 97). Registered
/// only on a node that has one, so the applier and the connection hold a nullable.
/// </summary>
public interface IColibriControl : ICatalogControl;

/// <summary>
/// A colibri node's catalogue of converted models (phase 97): every model is listed to the hub, the
/// one a request names is loaded, at most <c>Serve:MaxLoaded</c> run at once, and on demand an idle
/// one is stopped to give its RAM back. The admission, eviction and pins are <see cref="ModelCatalog"/>'s
/// (shared with Strata's since phase 99); what is colibri's is the directory scan and Brio.
/// </summary>
/// <remarks>
/// Everything a v3.58 colibri node does per request — the dialect, the sized bodies, the KV slot
/// pinning, Brio — is the per-model <see cref="UpstreamBackend"/>, unchanged.
/// </remarks>
public sealed class ColibriCatalog : ModelCatalog, IClosedSetScorer, IColibriControl
{
    private static readonly string[] ChatAndScore = [CapabilityKinds.Chat, CapabilityKinds.Score];

    private readonly ColibriOptions options;
    private readonly ConcurrentDictionary<string, byte> warned = new(StringComparer.OrdinalIgnoreCase);

    public ColibriCatalog(
        ColibriOptions options,
        ICatalogLauncher launcher,
        Func<string, UpstreamBackend> upstreamFor,
        Func<HttpClient> probeClient,
        TimeSpan stopDrain,
        TimeProvider time,
        ILogger logger)
        : base(options.Serve, launcher, upstreamFor, probeClient, stopDrain, time, logger)
    {
        this.options = options;
    }

    public override string Name => BackendOptions.Colibri;

    protected override string SectionName => ColibriOptions.SectionName;

    protected override string ProcessName => "coli serve";

    protected override string CatalogueSource => $"{ColibriOptions.SectionName}:Serve:ModelsDir";

    public override IReadOnlyList<string> Kinds => ChatAndScore;

    public async Task<ToolResult> ScoreAsync(ToolJob job, CancellationToken cancellationToken)
    {
        Resident resident;

        try
        {
            resident = await EnsureLoadedAsync(job.Model, cancellationToken);
        }
        catch (CatalogException ex) when (ex.NotInCatalogue)
        {
            return ToolResult.Refused(job.JobId, ex.Message, BrioErrorCodes.ModelNotFound);
        }
        catch (CatalogException ex)
        {
            return ToolResult.Failed(job.JobId, ex.Message);
        }

        try
        {
            return await resident.Upstream.ScoreAsync(job, cancellationToken);
        }
        finally
        {
            Release(resident);
        }
    }

    /// <summary>A pull is refused in a sentence (97 D2): <c>coli convert</c> is hours of CPU, not a download.</summary>
    public override IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"colibri models are converted with `coli convert`, not pulled; put the converted directory under {ColibriOptions.SectionName}:Serve:ModelsDir and it is listed on the next refresh");

    public override Task DeleteAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"this node does not delete colibri models; remove the directory from {ColibriOptions.SectionName}:Serve:ModelsDir on the box");

    /// <summary>Sub-directories of <c>ModelsDir</c> holding a <c>config.json</c>, named by directory, plus <c>Serve:Models</c>.</summary>
    protected internal override Dictionary<string, string> Scan()
    {
        var catalogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = options.Serve.ModelsDir;

        if (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(dir))
            {
                foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(Path.Combine(sub, "config.json")))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(sub);

                    // A dot-directory is somebody's work in progress (v3.63's conversions stage in
                    // `.converting-<name>`), not a model somebody misnamed: skipped without a warning.
                    if (name.StartsWith('.'))
                    {
                        continue;
                    }

                    if (!ColibriOptions.IsModelName(name))
                    {
                        if (warned.TryAdd("name:" + name, 0))
                        {
                            Logger.LogWarning(
                                "'{Directory}' is a converted model, but '{Name}' is not a model name (letters, digits, '.', '_', '-'); it is not listed. Rename it, or name it under {Section}:Serve:Models.",
                                sub, name, ColibriOptions.SectionName);
                        }

                        continue;
                    }

                    catalogue[name] = sub;
                }
            }
            else if (warned.TryAdd("dir:" + dir, 0))
            {
                Logger.LogWarning(
                    "{Section}:Serve:ModelsDir is '{Directory}', which is not a directory this node can see; it lists nothing until it is mounted.",
                    ColibriOptions.SectionName, dir);
            }
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
}

/// <summary>The shipped launcher: one supervised <c>coli serve</c> per model, 95's loop and Job Object.</summary>
public sealed class ColibriProcessLauncher(ColibriOptions options, TimeProvider time, ILogger logger) : ICatalogLauncher
{
    public ICatalogProcess Launch(string model, string directory, int port)
    {
        var process = new EngineProcess(
            $"colibri:{model}",
            () => ColibriServe.StartInfo(options, directory, model, port),
            () => Directory.Exists(directory) ? null : $"'{directory}' is not a directory this node can see",
            time,
            logger);

        process.Start();
        return new LaunchedCatalogProcess(process, $"http://127.0.0.1:{port}/v1");
    }
}
