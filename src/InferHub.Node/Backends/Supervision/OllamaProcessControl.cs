using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// The single platform seam (design decision D8). Everything about <c>sc.exe</c>,
/// <c>systemctl</c>, <c>PATH</c> and <c>Process</c> lives here and nowhere else.
/// </summary>
/// <remarks>
/// <strong>A service manager wins over spawning.</strong> Starting <c>ollama serve</c> next to a
/// service-managed install gets you two servers fighting over <c>:11434</c>, and the one that
/// loses is the one whose logs the operator is reading.
/// </remarks>
public sealed class OllamaProcessControl(
    IOptions<OllamaSupervisorOptions> options,
    ILogger<OllamaProcessControl> logger) : IOllamaProcessControl
{
    private const string WindowsServiceName = "Ollama";
    private const string SystemdUnitName = "ollama.service";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Long enough for a bad binary to fail and short enough not to stall the loop.</summary>
    private static readonly TimeSpan SpawnGrace = TimeSpan.FromMilliseconds(750);

    private readonly OllamaSupervisorOptions supervisor = options.Value;

    private Process? spawned;

    public async Task<OllamaInstallation> DiscoverAsync(CancellationToken cancellationToken)
    {
        var configuredService = supervisor.ServiceName;

        if (!string.IsNullOrWhiteSpace(configuredService))
        {
            if (await ServiceExistsAsync(configuredService, cancellationToken))
            {
                return OllamaInstallation.Service(configuredService);
            }

            logger.LogWarning(
                "{Key} names service '{ServiceName}', which this machine's service manager does not know; falling back to binary discovery.",
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.ServiceName)}",
                configuredService);
        }
        else
        {
            var wellKnown = OperatingSystem.IsWindows() ? WindowsServiceName
                : OperatingSystem.IsLinux() ? SystemdUnitName
                : null;

            if (wellKnown is not null && await ServiceExistsAsync(wellKnown, cancellationToken))
            {
                return OllamaInstallation.Service(wellKnown);
            }
        }

        var configuredPath = supervisor.ExecutablePath;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return OllamaInstallation.Binary(configuredPath);
            }

            logger.LogWarning(
                "{Key} points at '{Path}', which does not exist; falling back to PATH.",
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.ExecutablePath)}",
                configuredPath);
        }

        var discovered = FindExecutable();

        return discovered is null ? OllamaInstallation.Missing : OllamaInstallation.Binary(discovered);
    }

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
        => (await DiscoverAsync(cancellationToken)).Kind is not OllamaInstallKind.Missing;

    public async Task<ProcessControlResult> StartAsync(
        OllamaInstallation installation,
        CancellationToken cancellationToken)
    {
        switch (installation.Kind)
        {
            case OllamaInstallKind.Service when OperatingSystem.IsWindows():
                // 1056 = already running. Somebody else won the race; that is success, not failure.
                return Interpret(
                    await RunAsync("sc.exe", ["start", installation.Target], cancellationToken),
                    okExitCodes: [0, 1056]);

            case OllamaInstallKind.Service:
                return Interpret(
                    await RunAsync("systemctl", ["start", installation.Target], cancellationToken));

            case OllamaInstallKind.Binary:
                return SpawnServe(installation.Target);

            default:
                return ProcessControlResult.Failed("Ollama is not installed on this machine.");
        }
    }

    public async Task<ProcessControlResult> StopAsync(
        OllamaInstallation installation,
        CancellationToken cancellationToken)
    {
        switch (installation.Kind)
        {
            case OllamaInstallKind.Service when OperatingSystem.IsWindows():
                // 1062 = not started. Stopping something already stopped is the outcome we wanted.
                return Interpret(
                    await RunAsync("sc.exe", ["stop", installation.Target], cancellationToken),
                    okExitCodes: [0, 1062]);

            case OllamaInstallKind.Service:
                return Interpret(
                    await RunAsync("systemctl", ["stop", installation.Target], cancellationToken));

            case OllamaInstallKind.Binary:
                return KillServe(cancellationToken);

            default:
                return ProcessControlResult.Failed("Ollama is not installed on this machine.");
        }
    }

    // ---- service manager -------------------------------------------------------------------

    private async Task<bool> ServiceExistsAsync(string name, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var query = await RunAsync("sc.exe", ["query", name], cancellationToken);
            return query.Started && query.ExitCode == 0;
        }

        if (!OperatingSystem.IsLinux())
        {
            // launchd is a different enough shape that guessing at it would be worse than
            // falling through to the binary, which is how Ollama ships on macOS anyway.
            return false;
        }

        var load = await RunAsync(
            "systemctl",
            ["show", "-p", "LoadState", "--value", name],
            cancellationToken);

        return load.Started && load.Output.Trim().Equals("loaded", StringComparison.OrdinalIgnoreCase);
    }

    // ---- binary ----------------------------------------------------------------------------

    private ProcessControlResult SpawnServe(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("serve");

        Process process;

        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"'{executablePath} serve' produced no process.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return ProcessControlResult.Denied(
                $"Access denied starting '{executablePath} serve'. The account this node runs as cannot execute it.");
        }
        catch (Exception ex)
        {
            return ProcessControlResult.Failed($"Could not start '{executablePath} serve': {ex.Message}");
        }

        // Redirect and pump rather than discard: when a spawn dies on a missing GPU library, the
        // reason is on stderr and nowhere else.
        process.OutputDataReceived += (_, e) => LogServeOutput(e.Data, error: false);
        process.ErrorDataReceived += (_, e) => LogServeOutput(e.Data, error: true);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        spawned = process;

        if (process.WaitForExit((int)SpawnGrace.TotalMilliseconds))
        {
            return ProcessControlResult.Failed(
                $"'{executablePath} serve' exited immediately with code {process.ExitCode}; see the ollama-serve log lines above.");
        }

        return ProcessControlResult.Ok;
    }

    private void LogServeOutput(string? line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (error)
        {
            logger.LogWarning("ollama-serve: {Line}", line);
        }
        else
        {
            logger.LogDebug("ollama-serve: {Line}", line);
        }
    }

    private ProcessControlResult KillServe(CancellationToken cancellationToken)
    {
        var killed = 0;
        string? denial = null;

        foreach (var process in CandidateServeProcesses())
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)CommandTimeout.TotalMilliseconds);
                killed++;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                denial ??= $"Access denied stopping ollama (pid {process.Id}).";
            }
            catch (InvalidOperationException)
            {
                // Exited between the enumeration and the kill. That is the outcome we wanted.
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not stop ollama process {Pid}", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }

        spawned = null;

        if (denial is not null)
        {
            return ProcessControlResult.Denied(denial);
        }

        // Nothing to kill is not an error: the caller asked for "not running", and it is not.
        return killed == 0
            ? ProcessControlResult.Ok with { Error = "no running ollama process was found to stop" }
            : ProcessControlResult.Ok;
    }

    private IEnumerable<Process> CandidateServeProcesses()
    {
        // Ours first — it is the one we know is a server rather than a CLI invocation.
        if (spawned is { } mine)
        {
            yield return mine;
        }

        Process[] byName;

        try
        {
            byName = Process.GetProcessesByName("ollama");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enumerate ollama processes");
            yield break;
        }

        foreach (var process in byName)
        {
            if (spawned is not null && process.Id == spawned.Id)
            {
                process.Dispose();
                continue;
            }

            yield return process;
        }
    }

    private static string? FindExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = SafeCombine(directory.Trim('"'), fileName);

            if (candidate is not null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in WellKnownLocations(fileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> WellKnownLocations(string fileName)
    {
        if (OperatingSystem.IsWindows())
        {
            // The user-scoped installer is the default on Windows and does not always land on the
            // service account's PATH, which is exactly the deployment this phase cares about.
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Programs", "Ollama", fileName);
            }

            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return Path.Combine(programFiles, "Ollama", fileName);
            }

            yield break;
        }

        yield return $"/usr/local/bin/{fileName}";
        yield return $"/usr/bin/{fileName}";
        yield return $"/opt/homebrew/bin/{fileName}";
    }

    private static string? SafeCombine(string directory, string fileName)
    {
        try
        {
            return Path.Combine(directory, fileName);
        }
        catch (ArgumentException)
        {
            // A PATH entry with invalid characters is a machine's problem, not a reason to throw
            // out of a health check.
            return null;
        }
    }

    // ---- running an external command -------------------------------------------------------

    private ProcessControlResult Interpret(CommandResult result, int[]? okExitCodes = null)
    {
        if (!result.Started)
        {
            return ProcessControlResult.Failed(result.Output);
        }

        okExitCodes ??= [0];

        if (okExitCodes.Contains(result.ExitCode))
        {
            return ProcessControlResult.Ok;
        }

        var text = result.Output;

        // 5 = ERROR_ACCESS_DENIED from sc.exe; polkit says it in words. Either way this is the
        // support ticket the phase exists to pre-empt, so it gets its own shape.
        if (result.ExitCode == 5
            || text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Interactive authentication required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication is required", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessControlResult.Denied(
                $"the account this node runs as is not allowed to control the service ({Trim(text)})");
        }

        return ProcessControlResult.Failed($"exit code {result.ExitCode}: {Trim(text)}");
    }

    private static string Trim(string text)
    {
        var collapsed = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length <= 400 ? collapsed : collapsed[..400] + "…";
    }

    private async Task<CommandResult> RunAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"'{fileName}' produced no process.");

            var output = new StringBuilder();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);
            output.Append(await stdout).Append(await stderr);

            return new CommandResult(true, process.ExitCode, output.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return new CommandResult(true, 5, ex.Message);
        }
        catch (Exception ex)
        {
            // A missing systemctl (or a container with no service manager) lands here, and it is
            // information rather than a fault: it just means this box has no service to use.
            logger.LogDebug(ex, "Could not run {FileName}", fileName);
            return new CommandResult(false, -1, $"could not run '{fileName}': {ex.Message}");
        }
    }

    private readonly record struct CommandResult(bool Started, int ExitCode, string Output);
}
