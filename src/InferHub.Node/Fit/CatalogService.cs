using System.Collections.Concurrent;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Shared.Fit;
using InferHub.Shared.HuggingFace;
using InferHub.Shared.Strata;

namespace InferHub.Node.Fit;

/// <summary>
/// Phase 119: what is trending and popular on Hugging Face and in Ollama's library, each entry classed by where its
/// weights would sit on this machine (D1) and the list ordered by it. Each source is asked only with the consent 118
/// uses, fails on its own, and is kept ten minutes per query (D4). Nothing here downloads a model (D5).
/// </summary>
public sealed class CatalogService(Func<HuggingFaceClient>? huggingFace, HttpClient libraryHttp, Uri library, Func<MachineFacts> machine, TimeProvider? time = null)
{
    public static readonly Uri DefaultLibrary = new("https://ollama.com/");

    public const string LibraryClientName = "ollama-library";

    public const int DefaultLimit = 30;

    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object Models)> kept = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public sealed record Request(string? Source, string? Sort, string? Kind, string? Search, int? Limit, bool FitsOnly);

    public sealed record SourceState(string Name, bool Ok, int Count, string? Error);

    public sealed record Answer(string Sort, string Kind, MachineFacts Machine, IReadOnlyList<SourceState> Sources, IReadOnlyList<CatalogEntry> Entries, string Note);

    public async Task<Answer> ListAsync(Request request, CancellationToken cancellationToken)
    {
        var sort = Pick(request.Sort, CatalogSorts.All, CatalogSorts.Trending, "sort");
        var kind = Pick(request.Kind, CatalogKinds.All, CatalogKinds.Text, "kind");
        var source = (request.Source ?? "all").Trim().ToLowerInvariant();

        if (source is not ("all" or CatalogSources.HuggingFace or CatalogSources.Ollama))
        {
            throw new FitRefusedException(400, $"source is all, {CatalogSources.HuggingFace} or {CatalogSources.Ollama}, not '{request.Source}'");
        }

        var limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, 100);
        var search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();
        var here = machine();
        var states = new List<SourceState>();
        var entries = new List<CatalogEntry>();

        if (source is "all" or CatalogSources.HuggingFace)
        {
            await AskAsync(CatalogSources.HuggingFace, () => HuggingFaceAsync(sort, kind, search, limit, here, cancellationToken), states, entries, cancellationToken);
        }

        if (source is "all" or CatalogSources.Ollama)
        {
            await AskAsync(CatalogSources.Ollama, () => OllamaAsync(sort, kind, search, limit, here, cancellationToken), states, entries, cancellationToken);
        }

        var ordered = CatalogRough.Order(request.FitsOnly ? entries.Where(e => e.Fit.Fits) : entries);
        var note = "ordered by where the weights alone would sit at a 4-bit quant, then by each source's own order; "
                   + "the context is chosen in Check fit, which reads the model and does the arithmetic"
                   + (sort == CatalogSorts.Trending && source != CatalogSources.HuggingFace ? "; Ollama's library has no trending order, so its popular one is used" : "");

        return new Answer(sort, kind, here, states, ordered, note);
    }

    private static async Task AskAsync(string name, Func<Task<IReadOnlyList<CatalogEntry>>> ask, List<SourceState> states, List<CatalogEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            var found = await ask();
            entries.AddRange(found);
            states.Add(new SourceState(name, true, found.Count, null));
        }
        catch (FitRefusedException ex)
        {
            states.Add(new SourceState(name, false, 0, ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException
                                       or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            states.Add(new SourceState(name, false, 0, $"{(name == CatalogSources.Ollama ? "Ollama's library" : "Hugging Face")} did not answer: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ Hugging Face

    private async Task<IReadOnlyList<CatalogEntry>> HuggingFaceAsync(string sort, string kind, string? search, int limit, MachineFacts here, CancellationToken cancellationToken)
    {
        if (huggingFace is null)
        {
            throw new FitRefusedException(403, $"this node does not reach Hugging Face ({HuggingFaceOptions.SectionName}:Enabled=false); its owner turns that on to browse or download from it");
        }

        var query = HuggingFaceListing.Query(sort, kind, search, limit);
        var models = await KeptAsync(query, async () =>
        {
            using var document = await huggingFace().ListAsync(query, cancellationToken);
            return HuggingFaceListing.Parse(document.RootElement);
        });

        var client = huggingFace();
        using var gate = new SemaphoreSlim(TreeReads);

        var entries = await Task.WhenAll(models.Select(async (m, i) =>
        {
            var parsed = HfReference.TryParse(m.Id, null, out var reference, out _);
            var strata = parsed && StrataModels.IsStrataRepo(reference!);
            CatalogSize[] sizes = [];
            CatalogFit fit;

            if (strata)
            {
                fit = new CatalogFit(CatalogClasses.Unknown, null, "a Strata repository — only Strata's own table sizes it; Check fit asks Strata");
            }
            else
            {
                // 119 D3, found live: the listing's parameter count can describe a side file (a LoRA, a draft model) —
                // the repo's own tree says what each quant weighs, and that is what is classed.
                sizes = parsed ? await QuantsAsync(client, reference!, m.Moe, here, gate, cancellationToken) : [];
                var rough = CatalogRough.Classify(m.Parameters, m.Moe, here);
                fit = Best(sizes)?.Fit
                      ?? (rough.Class == CatalogClasses.Unknown ? rough : rough with { Why = rough.Why + " (from Hugging Face's summary, which can describe a side file; the repo's files could not be read)" });
            }

            return new CatalogEntry(
                CatalogSources.HuggingFace, m.Id, Best(sizes)?.Link ?? m.Id, null,
                kind == CatalogKinds.Vision ? [CatalogKinds.Vision] : [],
                m.Architecture, m.Moe, m.TrainedContext, m.Downloads, m.Likes,
                sort == CatalogSorts.Newest ? m.Created : m.Modified ?? m.Created, null,
                strata, sizes, fit, i);
        }));

        return entries;
    }

    private const int TreeReads = 6;

    /// <summary>A repo's quants with their real bytes, each classed — one tree listing, no file read; kept ten minutes.</summary>
    private async Task<CatalogSize[]> QuantsAsync(HuggingFaceClient client, HfReference reference, bool moe, MachineFacts here, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelFactsReader.GgufGroup> groups;

        try
        {
            groups = await KeptAsync("tree:" + reference.RepoId, async () =>
            {
                await gate.WaitAsync(cancellationToken);

                try
                {
                    var tree = await client.TreeAsync(reference, reference.Revision, cancellationToken);
                    return ModelFactsReader.GgufVariants(reference, tree.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !IsNotAModel(f.Path)).ToArray());
                }
                finally
                {
                    gate.Release();
                }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
                                       or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        return groups
            .Select(g => new CatalogSize(g.Variant.Name, g.Variant.Link ?? reference.RepoId, null, CatalogRough.ClassifyBytes(g.Variant.Bytes, moe, here)))
            .OrderBy(s => s.Fit.WeightsMiB)
            .ToArray();
    }

    /// <summary>
    /// A LoRA, an importance matrix or a speculative-decoding draft beside the model is not a quant of it — each found
    /// live listing a 200 GB model as "on the card" (a 460 MB LoRA, a 2.4 GB imatrix, a 4 GB <c>mtp-…Q8_0</c>). A quant
    /// that carries its MTP layers (<c>IQ4_XS-MTP-00001-of-00003</c>) is the whole model and stays.
    /// </summary>
    internal static bool IsNotAModel(string path)
    {
        var name = path.Split('/')[^1];

        return path.Split('/').Any(part => part.Contains("lora", StringComparison.OrdinalIgnoreCase) || part.Contains("imatrix", StringComparison.OrdinalIgnoreCase))
               || name.StartsWith("mtp", StringComparison.OrdinalIgnoreCase)
               || name.Contains("draft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The size Check fit opens with: of the best class, a 4-bit K-quant if there is one (what a pull gives by
    /// default), else the largest Ollama size / the largest quant of that class; with nothing fitting, the smallest.
    /// </summary>
    internal static CatalogSize? Best(IReadOnlyList<CatalogSize> sizes)
    {
        if (sizes.Count == 0)
        {
            return null;
        }

        var fitting = sizes.Where(s => s.Fit.Fits).ToArray();

        if (fitting.Length == 0)
        {
            return sizes.OrderBy(s => s.Fit.Order).ThenBy(s => s.Fit.WeightsMiB ?? long.MaxValue).First();
        }

        var top = fitting.Where(s => s.Fit.Order == fitting.Min(f => f.Fit.Order)).ToArray();
        return top.Where(s => s.Name.Contains("Q4_K", StringComparison.OrdinalIgnoreCase)).MaxBy(s => s.Fit.WeightsMiB)
               ?? top.MaxBy(s => s.Parameters ?? s.Fit.WeightsMiB ?? 0);
    }

    // ------------------------------------------------------------------ Ollama's library

    private async Task<IReadOnlyList<CatalogEntry>> OllamaAsync(string sort, string kind, string? search, int limit, MachineFacts here, CancellationToken cancellationToken)
    {
        if (!here.Engines.Any(e => e.Kind == FitEngines.Ollama && e.Installs))
        {
            throw new FitRefusedException(403, "this node has no Ollama engine that pulls models; Ollama's library is listed where one does");
        }

        var query = OllamaLibraryPage.Query(sort, search);
        var models = await KeptAsync(query, async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await libraryHttp.GetAsync(new Uri(library, query), timeout.Token);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            var parsed = OllamaLibraryPage.Parse(html);

            if (parsed.Count == 0 && html.Length > 0 && search is null)
            {
                throw new InvalidDataException("Ollama's library page changed shape — no model could be read from it (this node reads the page by hand; there is no API)");
            }

            return parsed;
        });

        return models
            .Where(m => !m.CloudOnly)
            .Where(m => kind == CatalogKinds.Vision
                ? m.Capabilities.Contains(CatalogKinds.Vision)
                : !m.Capabilities.Contains("embedding"))
            .Take(limit)
            .Select((m, i) =>
            {
                var sizes = m.Sizes
                    .Select(s => (Name: s, Parameters: OllamaLibraryPage.Parameters(s)))
                    .Select(s => new CatalogSize(s.Name, $"{m.Name}:{s.Name}", s.Parameters, CatalogRough.Classify(s.Parameters, s.Name.Contains('x'), here)))
                    .ToArray();

                var best = Best(sizes);
                var fit = best?.Fit ?? new CatalogFit(CatalogClasses.Unknown, null, "the library shows no size for it — Check fit reads the model itself");

                return new CatalogEntry(
                    CatalogSources.Ollama, m.Name, best?.Link ?? m.Name, m.Description,
                    m.Capabilities.Where(c => c != "cloud").ToArray(),
                    null, sizes.Any(s => s.Name.Contains('x')), null, m.Pulls, null, m.Updated, m.UpdatedText,
                    false, sizes, fit, i);
            })
            .ToArray();
    }

    private async Task<T> KeptAsync<T>(string key, Func<Task<T>> read) where T : class
    {
        var now = clock.GetUtcNow();

        if (kept.TryGetValue(key, out var hit) && now - hit.At < Keep && hit.Models is T models)
        {
            return models;
        }

        var fresh = await read();
        kept[key] = (now, fresh);

        foreach (var old in kept.Where(p => now - p.Value.At >= Keep).Select(p => p.Key).ToArray())
        {
            kept.TryRemove(old, out _);
        }

        return fresh;
    }

    private static string Pick(string? value, IReadOnlyList<string> allowed, string fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normal = value.Trim().ToLowerInvariant();
        return allowed.Contains(normal) ? normal : throw new FitRefusedException(400, $"{name} is {string.Join(", ", allowed)}, not '{value}'");
    }
}
