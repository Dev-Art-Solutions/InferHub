using System.Diagnostics;

namespace InferHub.Migrate;

/// <summary>
/// Copies a populated vector store from one provider to another — local, Postgres or Qdrant, any
/// pair in either direction. Every release since the store became pluggable carried the caveat that
/// switching providers meant re-ingesting from the original documents, which is awkward advice from
/// a system that deliberately does not keep them (phase-23 D2). This deletes the caveat.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }

        if (cli.ShowHelp)
        {
            Console.WriteLine(CliOptions.Usage);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var source = await OpenAsync(cli.From, cli.FromKey, "from", cts.Token);
        if (source is null) return 1;
        await using var target = await OpenAsync(cli.To, cli.ToKey, "to", cts.Token);
        if (target is null) return 1;

        Console.WriteLine($"{(cli.DryRun ? "DRY RUN: " : "")}{source.Provider} -> {target.Provider}");
        Console.WriteLine();

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<CollectionResult> results;
        try
        {
            var migrator = new Migrator(source.Store, target.Store, Console.WriteLine);
            results = await migrator.RunAsync(
                new MigrationOptions(cli.Collection, cli.BatchSize, cli.Parallelism, cli.DryRun), cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled. Nothing was rolled back — re-running is safe and resumes.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("Records copied before the failure are in the target; re-running is safe.");
            return 1;
        }
        stopwatch.Stop();

        return Report(results, cli.DryRun, stopwatch.Elapsed);
    }

    private static async Task<VectorStoreHandle?> OpenAsync(string spec, string? apiKey, string side, CancellationToken cancellationToken)
    {
        try
        {
            return await VectorStoreHandle.OpenAsync(StoreSpec.Parse(spec, apiKey, side), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error opening --{side}: {ex.Message}");
            return null;
        }
    }

    private static int Report(IReadOnlyList<CollectionResult> results, bool dryRun, TimeSpan elapsed)
    {
        Console.WriteLine();
        if (results.Count == 0)
        {
            Console.WriteLine("no collections in the source store; nothing to do.");
            return 0;
        }

        Console.WriteLine($"{"collection",-28} {"dim",5} {"distance",-9} {"source",9} {"copied",9} {"target",9}");
        Console.WriteLine(new string('-', 74));
        foreach (var r in results)
        {
            Console.WriteLine($"{Truncate(r.Name, 28),-28} {r.Dimension,5} {r.Distance,-9} {r.SourceCount,9} {r.Copied,9} {r.TargetCount,9}");
        }
        Console.WriteLine();

        var skipped = results.Where(r => r.Skipped is not null).ToArray();
        foreach (var r in skipped)
        {
            Console.WriteLine($"skipped {r.Name}: {r.Skipped}");
        }

        if (dryRun)
        {
            Console.WriteLine($"dry run: nothing was written. {results.Count} collection(s), {results.Sum(r => r.SourceCount)} record(s) would be copied.");
            return skipped.Length > 0 ? 1 : 0;
        }

        // The count the target reports back is the check that matters: "copied N" only says the
        // upserts returned, and a target that quietly held fewer records than it was handed is
        // exactly the failure a migration must not report as success.
        var incomplete = results.Where(r => r.Skipped is null && r.TargetCount < r.SourceCount).ToArray();
        foreach (var r in incomplete)
        {
            Console.Error.WriteLine(
                $"WARNING {r.Name}: target holds {r.TargetCount} record(s) but the source had {r.SourceCount}.");
        }

        Console.WriteLine($"copied {results.Sum(r => r.Copied)} record(s) across {results.Count - skipped.Length} collection(s) in {elapsed.TotalSeconds:F1}s.");
        return skipped.Length > 0 || incomplete.Length > 0 ? 1 : 0;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

internal sealed record CliOptions(
    string From,
    string To,
    string? FromKey,
    string? ToKey,
    string? Collection,
    int BatchSize,
    int Parallelism,
    bool DryRun,
    bool ShowHelp)
{
    public const string Usage = """
        inferhub-migrate — copy a vector store from one provider to another.

          inferhub-migrate --from <spec> --to <spec> [options]

        A <spec> is either a provider shorthand or the path to a JSON config file
        holding a "VectorStore" section (a coordinator's own appsettings.json will do):

          local:./data/vectors
          postgres:Host=localhost;Database=inferhub;Username=inferhub;Password=…
          qdrant:http://localhost:6333
          ./appsettings.Production.json

        Options:
          --collection <name>   Copy just this one collection (default: all of them).
          --dry-run             Report the plan and write nothing.
          --batch-size <n>      Records read per page (default 256).
          --parallel <n>        Concurrent upserts into the target (default 4).
          --from-key <key>      Qdrant API key for the source.
          --to-key <key>        Qdrant API key for the target.
          -h, --help

        Notes:
          * Re-running is safe: ids are deterministic, so a second run overwrites rather
            than duplicates. Nothing is ever deleted from the target.
          * A secret on a command line lands in your shell history — for a connection
            string or an API key, prefer the JSON config file form.
          * Exit code 1 means at least one collection was skipped or came up short; the
            table says which.

        Examples:
          inferhub-migrate --from local:./data/vectors --to qdrant:http://localhost:6333
          inferhub-migrate --from ./appsettings.json --to qdrant:https://qdrant.internal:6333 --to-key "$QDRANT_KEY"
          inferhub-migrate --from postgres:"Host=db;Database=inferhub;Username=u;Password=p" --to local:./restored --dry-run
        """;

    public static CliOptions Parse(string[] args)
    {
        string? from = null, to = null, fromKey = null, toKey = null, collection = null;
        var batchSize = 256;
        var parallelism = 4;
        var dryRun = false;
        var help = args.Length == 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from": from = Next(args, ref i); break;
                case "--to": to = Next(args, ref i); break;
                case "--from-key": fromKey = Next(args, ref i); break;
                case "--to-key": toKey = Next(args, ref i); break;
                case "--collection": collection = Next(args, ref i); break;
                case "--batch-size": batchSize = Int(Next(args, ref i), "--batch-size", 1, 10_000); break;
                case "--parallel": parallelism = Int(Next(args, ref i), "--parallel", 1, 64); break;
                case "--dry-run": dryRun = true; break;
                case "-h" or "--help": help = true; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (!help && from is null) throw new ArgumentException("--from is required");
        if (!help && to is null) throw new ArgumentException("--to is required");

        return new CliOptions(from ?? "", to ?? "", fromKey, toKey, collection, batchSize, parallelism, dryRun, help);
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value");
        return args[++i];
    }

    private static int Int(string raw, string name, int min, int max)
    {
        if (!int.TryParse(raw, out var value) || value < min || value > max)
        {
            throw new ArgumentException($"{name} must be an integer between {min} and {max} (got '{raw}')");
        }
        return value;
    }
}
