using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Shared.Fit;
using InferHub.Shared.HuggingFace;
using InferHub.Shared.Strata;

namespace InferHub.Node.Fit;

/// <summary>
/// Reads what a model is from where it lives (phase 118, D2): Hugging Face's API and tree, a GGUF's header by a Range
/// read, a checkpoint's <c>config.json</c>, Strata's own size table, or Ollama's registry and its model blob.
/// Nothing is downloaded beyond the header's megabytes.
/// </summary>
public sealed partial class ModelFactsReader(Func<HuggingFaceClient>? huggingFace, HttpClient registryHttp, Uri registry)
{
    public static readonly Uri DefaultRegistry = new("https://registry.ollama.ai/");

    public const string RegistryClientName = "ollama-registry";

    // A vocabulary of 150k tokens is a few MB; the tensor list of a big MoE one more. Most headers end in the first.
    private static readonly long[] HeaderReads = [16L << 20, 128L << 20];

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitSuffix();

    [GeneratedRegex(@"(?:^|[-_.])((?:UD-)?(?:I?Q\d[A-Z0-9_]*|BF16|F16|F32|FP16|FP32|MXFP4[A-Z0-9_]*|TQ\d_\d))$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantAtEnd();

    // Strata's table: "(~66 GB download, 48 GB RAM)", "27.6 GB of VRAM + RAM together", "every expert in RAM from ~108 GB", "for a 192 GB PC".
    [GeneratedRegex(@"~(\d+(?:\.\d+)?)\s*GB", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadGb();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*GB\s+(?:of\s+)?(?:VRAM \+ RAM|RAM)|in RAM from ~?(\d+(?:\.\d+)?)\s*GB|for a (\d+(?:\.\d+)?)\s*GB PC", RegexOptions.IgnoreCase)]
    private static partial Regex RamGb();

    public async Task<ModelFacts> ReadAsync(ModelLink link, CancellationToken cancellationToken)
    {
        if (link.Ollama is { } ollama)
        {
            return await OllamaAsync(ollama, cancellationToken);
        }

        if (huggingFace is null)
        {
            throw new FitRefusedException(403, $"this node does not reach Hugging Face ({HuggingFaceOptions.SectionName}:Enabled=false); its owner turns that on to look up or download a model from a link");
        }

        return await HuggingFaceAsync(huggingFace(), link.HuggingFace!, cancellationToken);
    }

    // ------------------------------------------------------------------ Hugging Face

    private async Task<ModelFacts> HuggingFaceAsync(HuggingFaceClient hf, HfReference reference, CancellationToken cancellationToken)
    {
        string? task = null;
        long? parameters = null;
        int? apiContext = null;

        using (var info = await hf.ModelInfoAsync(reference, cancellationToken))
        {
            var root = info.RootElement;
            task = root.TryGetProperty("pipeline_tag", out var tag) ? tag.GetString() : null;

            if (root.TryGetProperty("safetensors", out var st) && st.TryGetProperty("total", out var stTotal) && stTotal.TryGetInt64(out var n))
            {
                parameters = n;
            }

            if (root.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
            {
                if (parameters is null && gguf.TryGetProperty("total", out var gTotal) && gTotal.TryGetInt64(out var g))
                {
                    parameters = g;
                }

                if (gguf.TryGetProperty("context_length", out var ctx) && ctx.TryGetInt32(out var c))
                {
                    apiContext = c;
                }
            }
        }

        if (StrataModels.IsStrataRepo(reference))
        {
            return Strata(reference, parameters, task);
        }

        var commit = await hf.CommitAsync(reference, cancellationToken);
        var tree = await hf.TreeAsync(reference, commit, cancellationToken);
        var ggufs = tree.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (ggufs.Length > 0)
        {
            var variants = GgufVariants(reference, ggufs);

            if (variants.Count == 0)
            {
                throw new FitRefusedException(400, reference.Quant is { } q
                    ? $"'{reference.RepoId}' has no {q} GGUF"
                    : $"'{reference.RepoId}' has only a projector (mmproj) GGUF, no model");
            }

            var smallest = variants.OrderBy(v => v.Variant.Bytes).First();
            var header = await HeaderAsync(bytes => hf.RangeAsync(reference, commit, smallest.First.Path, bytes, cancellationToken), cancellationToken);
            var facts = ModelFacts.FromGguf(ModelFacts.SourceGguf, reference.RepoId, header, smallest.First.Size, variants.Select(v => v.Variant).ToArray(), parameters, task);
            return facts.TrainedContext is null && apiContext is not null ? facts with { TrainedContext = apiContext } : facts;
        }

        if (HuggingFaceStore.IsCheckpoint(tree))
        {
            var bytes = tree.Where(f => f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Size);
            using var response = await hf.RangeAsync(reference, commit, "config.json", 4 << 20, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var config = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var variant = new ModelVariant("checkpoint", bytes, tree.Count(f => f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)), About: "the repository's own weights", Link: (reference with { Quant = null, File = null }).ToString());
            return ModelFacts.FromConfig(reference.RepoId, config.RootElement, parameters, [variant], task);
        }

        return new ModelFacts(ModelFacts.SourceCheckpoint, reference.RepoId, null, task, parameters, null, null, null, null, null, null, null, [],
            [task is null or "text-generation"
                ? $"'{reference.RepoId}' has no .gguf and is not a safetensors checkpoint (config.json beside *.safetensors); no engine here can load it"
                : $"'{reference.RepoId}' is a {task} model; this node places text models from a link (pictures, voices and video go through the node's recipes)"]);
    }

    internal sealed record GgufGroup(ModelVariant Variant, HfFile First);

    /// <summary>A repo's GGUFs grouped into quants (a split model's shards together, its projector added to each).</summary>
    internal static IReadOnlyList<GgufGroup> GgufVariants(HfReference reference, IReadOnlyList<HfFile> ggufs)
    {
        static bool IsProjector(HfFile f) => Path.GetFileName(f.Path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase);

        var projector = ggufs.Where(IsProjector).OrderBy(f => f.Path.Contains("f16", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        var groups = ggufs.Where(f => !IsProjector(f))
            .GroupBy(f => SplitSuffix().Replace(f.Path, ".gguf"), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, Files: g.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray()))
            .ToArray();

        var labels = groups.Select(g => Label(g.Key)).ToArray();
        var result = new List<GgufGroup>();

        for (var i = 0; i < groups.Length; i++)
        {
            var (key, files) = groups[i];
            var label = labels[i];
            var unique = label is not null && labels.Count(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase)) == 1;
            var name = unique ? label! : Path.GetFileNameWithoutExtension(Path.GetFileName(key));
            var link = unique
                ? (reference with { File = null, Quant = label }).ToString()
                : (reference with { Quant = null, File = files[0].Path }).ToString();

            if (reference.Quant is { } quant && !string.Equals(label, quant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (reference.File is { } file && !files.Any(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var bytes = files.Sum(f => f.Size) + (projector?.Size ?? 0);
            result.Add(new GgufGroup(new ModelVariant(name, bytes, files.Length, About: projector is null ? null : "with its vision projector", Link: link), files[0]));
        }

        return result;
    }

    private static string? Label(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(path));
        var match = QuantAtEnd().Match(stem);

        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // ISTA-DASLab style: the quant is the folder.
        var folder = Path.GetDirectoryName(path)?.Replace('\\', '/').Split('/').LastOrDefault();
        return folder is { Length: > 0 } && QuantAtEnd().IsMatch("-" + folder) ? folder : null;
    }

    private static ModelFacts Strata(HfReference reference, long? parameters, string? task)
    {
        var family = StrataModels.Families.First(f => string.Equals(f.Repo, reference.RepoId, StringComparison.OrdinalIgnoreCase));
        var variants = new List<ModelVariant>();

        foreach (var size in family.Sizes)
        {
            if (reference.Quant is { } q && !string.Equals(q, size.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var download = DownloadGb().Match(size.About) is { Success: true } d ? Gb(d.Groups[1].Value) : 0;
            long? ram = RamGb().Match(size.About) is { Success: true } r ? Gb(r.Groups.Values.Skip(1).First(g => g.Success).Value) / (1024 * 1024) : null;
            variants.Add(new ModelVariant(size.Name, download, About: size.About, RamHintMiB: ram, Link: StrataModels.Link(family, size)));
        }

        return new ModelFacts(ModelFacts.SourceStrata, $"{family.Title} ({reference.RepoId})", "strata", task, parameters, null, null, null, null, null, null, null, variants,
            ["sizes and RAM are Strata's own table (Strata:Root's setup.py at the release this node was built against)"]);
    }

    private static long Gb(string value) => (long)(double.Parse(value, CultureInfo.InvariantCulture) * 1_000_000_000);

    // ------------------------------------------------------------------ Ollama's registry

    private async Task<ModelFacts> OllamaAsync(OllamaName name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(registry, $"v2/{name.Owner}/{name.Model}/manifests/{name.Tag}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await registryHttp.SendAsync(request, timeout.Token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FitRefusedException(404, $"Ollama's library has no '{name}'");
        }

        response.EnsureSuccessStatusCode();
        using var manifest = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);

        string? model = null;
        long modelBytes = 0, projectorBytes = 0;

        foreach (var layer in manifest.RootElement.GetProperty("layers").EnumerateArray())
        {
            var type = layer.GetProperty("mediaType").GetString();
            var size = layer.GetProperty("size").GetInt64();

            if (type == "application/vnd.ollama.image.model")
            {
                model = layer.GetProperty("digest").GetString();
                modelBytes = size;
            }
            else if (type == "application/vnd.ollama.image.projector")
            {
                projectorBytes += size;
            }
        }

        if (model is null)
        {
            throw new FitRefusedException(400, $"'{name}' in Ollama's library has no model layer");
        }

        var header = await HeaderAsync(async bytes =>
        {
            using var blob = new HttpRequestMessage(HttpMethod.Get, new Uri(registry, $"v2/{name.Owner}/{name.Model}/blobs/{model}"));
            blob.Headers.Range = new RangeHeaderValue(0, bytes - 1);
            var answer = await registryHttp.SendAsync(blob, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!answer.IsSuccessStatusCode)
            {
                answer.Dispose();
                throw new FitRefusedException(502, $"Ollama's registry answered {(int)answer.StatusCode} for '{name}''s model");
            }

            return answer;
        }, cancellationToken);

        var variant = new ModelVariant(name.Tag, modelBytes + projectorBytes, About: projectorBytes > 0 ? "with its vision projector" : null, Link: name.PullName);
        var parameters = manifest.RootElement.TryGetProperty("config", out var config) && config.TryGetProperty("digest", out var digest)
            ? await OllamaParametersAsync(name, digest.GetString(), cancellationToken)
            : null;

        return ModelFacts.FromGguf(ModelFacts.SourceOllama, name.PullName, header, modelBytes, [variant], parameters);
    }

    /// <summary>The manifest's config blob names the size the way Ollama shows it (<c>"model_type": "7.6B"</c>).</summary>
    private async Task<long?> OllamaParametersAsync(OllamaName name, string? digest, CancellationToken cancellationToken)
    {
        if (digest is null)
        {
            return null;
        }

        try
        {
            using var answer = await registryHttp.GetAsync(new Uri(registry, $"v2/{name.Owner}/{name.Model}/blobs/{digest}"), cancellationToken);

            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("model_type", out var type) ? ParseSize(type.GetString()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            return null;
        }

        var unit = char.ToUpperInvariant(text[^1]) switch { 'K' => 1e3, 'M' => 1e6, 'B' => 1e9, 'T' => 1e12, _ => 0 };
        return unit > 0 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (long)(n * unit) : null;
    }

    // ------------------------------------------------------------------ the header

    private static async Task<GgufHeader> HeaderAsync(Func<long, Task<HttpResponseMessage>> open, CancellationToken cancellationToken)
    {
        for (var i = 0; ; i++)
        {
            using var response = await open(HeaderReads[i]);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var last = i == HeaderReads.Length - 1;

            try
            {
                var header = await GgufHeader.ReadAsync(body, cancellationToken);

                if (header.Tensors.Count > 0 || last)
                {
                    return header;
                }
            }
            catch (EndOfStreamException) when (!last)
            {
            }
            catch (EndOfStreamException)
            {
                throw new FitRefusedException(502, $"the model's GGUF header is longer than {HeaderReads[^1] >> 20} MB; it was not read");
            }
            catch (InvalidDataException ex)
            {
                throw new FitRefusedException(502, $"the model's file is not a GGUF this node can read: {ex.Message}");
            }
        }
    }
}

/// <summary>A link the node will not or cannot look up, with the status the control API answers.</summary>
public sealed class FitRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
