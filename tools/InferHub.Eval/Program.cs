using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Eval;

/// <summary>
/// Runs a golden set of queries against a live InferHub coordinator in each retrieval mode and
/// reports Recall@k, MRR, nDCG@k and median latency as a table. It uses the phase-24 search
/// endpoint (<c>POST /api/collections/{c}/search</c>), which returns the ranked chunks directly, so
/// the numbers are about retrieval and not about the chat model on top of it.
/// </summary>
internal static class Program
{
    private static readonly string[] Modes = ["vector", "keyword", "hybrid", "hybrid+rerank"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        List<GoldenQuery> golden;
        try
        {
            golden = LoadGolden(options.GoldenPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error reading golden set '{options.GoldenPath}': {ex.Message}");
            return 2;
        }

        if (golden.Count == 0)
        {
            Console.Error.WriteLine("golden set is empty; nothing to evaluate.");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        Console.WriteLine($"InferHub retrieval eval — collection '{options.Collection}', {golden.Count} queries, k={options.K}");
        Console.WriteLine($"coordinator: {options.BaseUrl}");
        Console.WriteLine();

        var results = new List<ModeResult>();
        foreach (var mode in Modes)
        {
            try
            {
                results.Add(await EvaluateModeAsync(http, options, golden, mode));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"mode '{mode}' failed: {ex.Message}");
                return 1;
            }
        }

        PrintTable(results, options.K);
        return 0;
    }

    private static async Task<ModeResult> EvaluateModeAsync(HttpClient http, Options options, List<GoldenQuery> golden, string mode)
    {
        var (searchMode, rerank) = mode == "hybrid+rerank" ? ("hybrid", true) : (mode, false);

        var recalls = new List<double>();
        var reciprocalRanks = new List<double>();
        var ndcgs = new List<double>();
        var latenciesMs = new List<double>();

        foreach (var query in golden)
        {
            var body = new SearchRequest(query.Query, searchMode, options.K, rerank, options.Model, options.EmbeddingModel);

            var sw = Stopwatch.StartNew();
            using var response = await http.PostAsJsonAsync($"/api/collections/{options.Collection}/search", body, Json);
            sw.Stop();
            latenciesMs.Add(sw.Elapsed.TotalMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} for query '{query.Query}': {text}");
            }

            var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(Json);
            var retrieved = (payload?.Hits ?? []).Select(h => h.Id).ToList();
            var retrievedDocs = (payload?.Hits ?? []).Select(h => h.DocumentId).Where(d => d is not null).Select(d => d!).ToList();

            var relevant = query.Relevant.ToHashSet(StringComparer.Ordinal);
            recalls.Add(Recall(retrieved, retrievedDocs, relevant));
            reciprocalRanks.Add(ReciprocalRank(retrieved, retrievedDocs, relevant));
            ndcgs.Add(NormalizedDcg(retrieved, retrievedDocs, relevant, options.K));
        }

        return new ModeResult(
            mode,
            Mean(recalls),
            Mean(reciprocalRanks),
            Mean(ndcgs),
            Median(latenciesMs));
    }

    // A hit counts as relevant if either its chunk id or its documentId is named in the golden set,
    // so a golden file can be written at whichever granularity the corpus author has to hand.
    private static double Recall(IReadOnlyList<string> retrieved, IReadOnlyList<string> retrievedDocs, HashSet<string> relevant)
    {
        if (relevant.Count == 0) return 0;
        var hit = 0;
        foreach (var r in relevant)
        {
            if (retrieved.Contains(r) || retrievedDocs.Contains(r)) hit++;
        }
        return hit / (double)relevant.Count;
    }

    private static double ReciprocalRank(IReadOnlyList<string> retrieved, IReadOnlyList<string> retrievedDocs, HashSet<string> relevant)
    {
        for (var i = 0; i < retrieved.Count; i++)
        {
            if (relevant.Contains(retrieved[i]) || (i < retrievedDocs.Count && relevant.Contains(retrievedDocs[i])))
            {
                return 1.0 / (i + 1);
            }
        }
        return 0;
    }

    private static double NormalizedDcg(IReadOnlyList<string> retrieved, IReadOnlyList<string> retrievedDocs, HashSet<string> relevant, int k)
    {
        double dcg = 0;
        for (var i = 0; i < retrieved.Count && i < k; i++)
        {
            var rel = relevant.Contains(retrieved[i]) || (i < retrievedDocs.Count && relevant.Contains(retrievedDocs[i])) ? 1.0 : 0.0;
            dcg += rel / Math.Log2(i + 2);
        }

        double idcg = 0;
        var ideal = Math.Min(relevant.Count, k);
        for (var i = 0; i < ideal; i++)
        {
            idcg += 1.0 / Math.Log2(i + 2);
        }

        return idcg == 0 ? 0 : dcg / idcg;
    }

    private static void PrintTable(List<ModeResult> results, int k)
    {
        Console.WriteLine($"{"mode",-16}{"Recall@" + k,12}{"MRR",10}{"nDCG@" + k,12}{"median ms",12}");
        Console.WriteLine(new string('-', 62));
        foreach (var r in results)
        {
            Console.WriteLine(
                $"{r.Mode,-16}{r.Recall,12:F3}{r.Mrr,10:F3}{r.Ndcg,12:F3}{r.MedianLatencyMs,12:F1}");
        }
        Console.WriteLine();
        Console.WriteLine("Higher is better for Recall/MRR/nDCG; latency is per-query wall time including the network hop.");
    }

    private static List<GoldenQuery> LoadGolden(string path)
    {
        var list = new List<GoldenQuery>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var q = JsonSerializer.Deserialize<GoldenQuery>(line, Json)
                ?? throw new FormatException($"could not parse line: {line}");
            if (string.IsNullOrWhiteSpace(q.Query) || q.Relevant.Count == 0)
            {
                throw new FormatException($"each line needs a non-empty 'query' and at least one 'relevant': {line}");
            }
            list.Add(q);
        }
        return list;
    }

    private static double Mean(IReadOnlyCollection<double> values) => values.Count == 0 ? 0 : values.Sum() / values.Count;

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private sealed record GoldenQuery(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("relevant")] IReadOnlyList<string> Relevant);

    private sealed record SearchRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("k")] int K,
        [property: JsonPropertyName("rerank")] bool Rerank,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel);

    private sealed record SearchResponse(
        [property: JsonPropertyName("collection")] string Collection,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("hits")] IReadOnlyList<Hit> Hits);

    private sealed record Hit(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("documentId")] string? DocumentId,
        [property: JsonPropertyName("page")] int? Page);

    private sealed record ModeResult(string Mode, double Recall, double Mrr, double Ndcg, double MedianLatencyMs);

    private sealed record Options(
        string BaseUrl,
        string ApiKey,
        string Collection,
        string GoldenPath,
        int K,
        string? Model,
        string? EmbeddingModel,
        bool ShowHelp)
    {
        public const string Usage =
            """
            InferHub retrieval eval harness

            Usage:
              inferhub-eval --collection <name> --golden <file.jsonl> [options]

            Options:
              --base-url <url>          Coordinator base URL (default http://localhost:5080)
              --api-key <key>           Client bearer key (or env INFERHUB_API_KEY)
              --collection <name>       Collection to evaluate (required)
              --golden <file.jsonl>     Golden set, one JSON object per line (required)
              --k <n>                   Cut-off for Recall@k / nDCG@k (default 5)
              --model <name>            Chat model for the reranker (default: server default)
              --embedding-model <name>  Embedding model override (default: server default)
              -h, --help                Show this help

            Golden set line format:
              {"query": "What does error E-4021 mean?", "relevant": ["handbook#12", "doc-id-or-chunk-id"]}

            A 'relevant' entry matches a retrieved hit by its chunk id OR its documentId.
            """;

        public static Options Parse(string[] args)
        {
            string baseUrl = "http://localhost:5080";
            string apiKey = Environment.GetEnvironmentVariable("INFERHUB_API_KEY") ?? "";
            string? collection = null;
            string? golden = null;
            var k = 5;
            string? model = null;
            string? embeddingModel = null;
            var help = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--base-url": baseUrl = Next(args, ref i); break;
                    case "--api-key": apiKey = Next(args, ref i); break;
                    case "--collection": collection = Next(args, ref i); break;
                    case "--golden": golden = Next(args, ref i); break;
                    case "--k": k = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--model": model = Next(args, ref i); break;
                    case "--embedding-model": embeddingModel = Next(args, ref i); break;
                    case "-h" or "--help": help = true; break;
                    default: throw new ArgumentException($"unknown argument '{args[i]}'");
                }
            }

            if (help)
            {
                return new Options(baseUrl, apiKey, "", "", k, model, embeddingModel, ShowHelp: true);
            }

            if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("--collection is required");
            if (string.IsNullOrWhiteSpace(golden)) throw new ArgumentException("--golden is required");
            if (k < 1) throw new ArgumentException("--k must be >= 1");

            return new Options(baseUrl, apiKey, collection!, golden!, k, model, embeddingModel, ShowHelp: false);
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"missing value for '{args[i]}'");
            return args[++i];
        }
    }
}
