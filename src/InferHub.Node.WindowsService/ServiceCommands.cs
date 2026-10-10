using System.Diagnostics;
using System.Security.Principal;
using InferHub.Node.Configuration;
using InferHub.Node.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Node.WindowsService;

/// <summary>
/// The two things this exe does besides being the service (phase 101):
/// <c>configure</c>, which the setup runs to write its answers (D1), and <c>update</c>, the way an operator
/// updates a node by hand — "the other option" when <c>Update:Auto</c> is off and nobody uses the console.
/// </summary>
internal static class ServiceCommands
{
    public static bool Handles(string[] args)
        => args.Length > 0 && args[0] is "configure" or "update";

    public static async Task<int> RunAsync(string[] args)
    {
        // "3.65.0 → 3.66.0" and a node name in Cyrillic, not "3.65.0  3.66.0" in the console's code page.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            return args[0] switch
            {
                "configure" => Configure(args[1..]),
                _ => await UpdateAsync(args[1..]),
            };
        }
        catch (Exception ex) when (ex is FormatException or UpdateException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <c>configure [--file &lt;settings.json&gt;] [--input &lt;Key=value file&gt;] [--set Key=value]…</c>.
    /// The setup passes its answers in a file it deletes afterwards, so the secret is never on a command line.
    /// </summary>
    private static int Configure(string[] args)
    {
        var file = NodeSettingsFile.DefaultPath;
        var values = new List<KeyValuePair<string, string>>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file" when i + 1 < args.Length:
                    file = args[++i];
                    break;
                case "--input" when i + 1 < args.Length:
                    values.AddRange(NodeSettingsFile.ParseInput(File.ReadAllText(args[++i])));
                    break;
                case "--set" when i + 1 < args.Length:
                    values.AddRange(NodeSettingsFile.ParseInput(args[++i]));
                    break;
                default:
                    throw new FormatException($"unknown argument '{args[i]}'. Usage: configure [--file <path>] [--input <file>] [--set Key=value]...");
            }
        }

        NodeSettingsFile.Apply(file, values);
        Console.WriteLine($"Wrote {values.Count} setting(s) to {file}");
        return 0;
    }

    /// <summary>
    /// <c>update [--check] [--yes] [--pause]</c>: looks for a newer release and, after asking, runs its setup
    /// with a progress window. Elevates itself through UAC when it was not started as an administrator — even
    /// to check, because the settings it reads (the release source among them) are readable by administrators
    /// only (D1) — so the Start-menu shortcut just works.
    /// </summary>
    private static async Task<int> UpdateAsync(string[] args)
    {
        if (OperatingSystem.IsWindows() && !IsAdministrator())
        {
            // No pause here: this process waits for the elevated one and must not outlive it holding the
            // exe open, which the setup it starts is about to replace.
            return Elevate(args);
        }

        var (code, launched) = await RunUpdateAsync(args.Contains("--check"), args.Contains("--yes"));

        // Nor after the setup was started, for the same reason; the setup shows its own progress window.
        if (args.Contains("--pause") && !launched)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
        }

        return code;
    }

    private static async Task<(int Code, bool Launched)> RunUpdateAsync(bool checkOnly, bool yes)
    {
        try
        {
            return await CheckAndApplyAsync(checkOnly, yes);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return (1, false);
        }
    }

    private static async Task<(int Code, bool Launched)> CheckAndApplyAsync(bool checkOnly, bool yes)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(NodeSettingsFile.DefaultPath, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = configuration.GetSection(UpdateOptions.SectionName).Get<UpdateOptions>() ?? new UpdateOptions();
        var dataDirectory = configuration[$"{NodeOptions.SectionName}:{nameof(NodeOptions.DataDirectory)}"] is { Length: > 0 } configured
            ? configured
            : NodeSettingsFile.DefaultDirectory;

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var feedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var feed = new GitHubReleaseFeed(feedHttp, new Uri(options.Source));

        Console.WriteLine($"This node runs InferHub {NodeVersion.Current}. Asking {options.Source} ...");
        var release = await feed.NewestAboveAsync(NodeVersion.Parsed, CancellationToken.None);

        if (release is null)
        {
            Console.WriteLine("It is the newest release.");
            return (0, false);
        }

        var target = NodeVersion.Format(release.Version);
        Console.WriteLine($"InferHub {target} is available: {release.PageUrl}");

        if (checkOnly)
        {
            return (0, false);
        }

        var applier = new InstallerUpdateApplier(quiet: false);

        if (applier.WhyNot is { } reason)
        {
            Console.Error.WriteLine($"This node cannot be updated from here: {reason}.");
            return (1, false);
        }

        if (!yes)
        {
            Console.Write($"Install {target} now? The service stops for about a minute. [y/N] ");

            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.WriteLine("Nothing was changed.");
                return (0, false);
            }
        }

        // The same manager the service runs, so the marker is written and the restarted node reports
        // "updated X → Y (requested by ...)" exactly as it would for an update from the hub.
        using var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var manager = new UpdateManager(
            Options.Create(options),
            new FixedReleaseFeed(release),
            new UpdateDownloader(http),
            applier,
            dataDirectory,
            () => 0,
            TimeProvider.System,
            loggers.CreateLogger<UpdateManager>());

        Console.WriteLine($"Downloading {release.SetupName} ...");
        var outcome = await manager.ApplyAsync($"{Environment.UserName} on the box", fromHub: false, CancellationToken.None);
        Console.WriteLine(outcome.Accepted ? $"The setup is running: {outcome.Message}." : $"error: {outcome.Message}");
        return (outcome.Accepted ? 0 : 1, outcome.Accepted);
    }

    private static int Elevate(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', new[] { "update" }.Concat(args.Where(a => a != "--pause")).Append("--pause")),
        };

        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit();
            return process?.ExitCode ?? 1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Updating needs an administrator; the prompt was declined.");
            return 1;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The release already found, so the manager does not ask the feed a second time.</summary>
    private sealed class FixedReleaseFeed(UpdateRelease release) : IReleaseFeed
    {
        public Task<UpdateRelease?> NewestAboveAsync(Version current, CancellationToken cancellationToken)
            => Task.FromResult<UpdateRelease?>(release);
    }
}
