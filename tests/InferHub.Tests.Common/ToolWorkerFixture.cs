using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Locates the real echo worker and builds manifests that point at it.
/// </summary>
/// <remarks>
/// Everything in the phase-41 suite runs a <b>real child process</b>. A stubbed
/// <c>Process</c> would prove that the stub behaves, and the failures this runtime exists to
/// survive — a worker that never says <c>ready</c>, one that segfaults mid-request, one that
/// inherits a secret it should not have — are precisely the ones a stub cannot produce. Phase-36's
/// <c>OllamaProbe</c> learned the same lesson against a real socket.
/// </remarks>
internal static class ToolWorkerFixture
{
    private static readonly Lazy<string> WorkerDll = new(FindWorkerDll);

    /// <summary>The argv for the echo worker: `dotnet &lt;dll&gt; [args]`.</summary>
    public static string[] Command(params string[] arguments) =>
        new[] { DotnetPath(), WorkerDll.Value }.Concat(arguments).ToArray();

    public static ToolManifest Manifest(
        string id = "echo",
        string kind = "echo",
        string[]? models = null,
        string[]? arguments = null,
        int maxWorkers = 1,
        int minWorkers = 0,
        int startTimeoutSeconds = 60,
        int requestTimeoutSeconds = 60,
        int idleTimeoutSeconds = 900,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new()
        {
            Id = id,
            Capabilities = [new InferHub.Shared.Contracts.NodeCapability(kind, models ?? ["echo"])],
            Command = Command(arguments ?? []),
            MinWorkers = minWorkers,
            MaxWorkers = maxWorkers,
            StartTimeoutSeconds = startTimeoutSeconds,
            RequestTimeoutSeconds = requestTimeoutSeconds,
            IdleTimeoutSeconds = idleTimeoutSeconds,
            Environment = environment ?? new Dictionary<string, string>()
        };

    public static ToolOptions Options(string scratch, params string[] allowed) => new()
    {
        Enabled = true,
        Allowed = allowed.ToList(),
        ScratchDirectory = scratch,
        QueueMaxWaitSeconds = 2,
        MaxStartAttempts = 3,
        RestartWindow = TimeSpan.FromMinutes(10),
        RestartBackoff = TimeSpan.FromMilliseconds(10),
        RecoveryProbeInterval = TimeSpan.FromMilliseconds(50),
        MaintenanceInterval = TimeSpan.FromMilliseconds(200)
    };

    public static IOptions<ToolOptions> Wrap(ToolOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    /// <summary>A temp directory that cleans itself up.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix = "inferhub-tools")
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{prefix}-{Guid.NewGuid():N}");

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteManifest(string fileName, object manifest)
        {
            var target = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(target, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));

            return target;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    public static ILogger Logger(ILoggerFactory? factory = null) =>
        (factory ?? NullLoggerFactory.Instance).CreateLogger("tool-test");

    private static string DotnetPath()
    {
        // The muxer that is running this test process. Resolving it from the environment rather
        // than PATH keeps the fixture working on a machine with several SDKs.
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        if (!string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        var main = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

        return main is not null && Path.GetFileName(main).Equals(exe, StringComparison.OrdinalIgnoreCase)
            ? main
            : exe;
    }

    private static string FindWorkerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not find the repository root from the test assembly location.");
        }

        var workerRoot = Path.Combine(directory.FullName, "tools", "InferHub.ToolWorker.Echo", "bin");

        var dll = Directory.Exists(workerRoot)
            ? Directory.EnumerateFiles(workerRoot, "inferhub-echo-worker.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return dll ?? throw new InvalidOperationException(
            $"The echo worker has not been built. Expected inferhub-echo-worker.dll under {workerRoot}.");
    }
}
