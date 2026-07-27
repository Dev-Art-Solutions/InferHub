using System.ComponentModel;
using System.Diagnostics;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// Downloads and runs the official Ollama installer. Reached only when discovery found neither a
/// service nor a binary, only when <c>AutoInstall</c> is on, and only once per process lifetime.
/// </summary>
/// <remarks>
/// <strong>The command and its source are logged before they run.</strong> An operator reading
/// the node's log must be able to see exactly what was executed on their machine — this is
/// software installing itself, and doing it quietly would be the wrong kind of convenience.
/// </remarks>
public sealed class OllamaInstaller(
    IOptions<OllamaSupervisorOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaInstaller> logger) : IOllamaInstaller
{
    public const string HttpClientName = "ollama-supervisor-install";

    private const string LinuxInstallScript = "https://ollama.com/install.sh";
    private const string WindowsInstaller = "https://ollama.com/download/OllamaSetup.exe";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

    private readonly OllamaSupervisorOptions supervisor = options.Value;

    public async Task<ProcessControlResult> InstallAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            return await InstallOnLinuxAsync(cancellationToken);
        }

        if (OperatingSystem.IsWindows())
        {
            return await InstallOnWindowsAsync(cancellationToken);
        }

        return ProcessControlResult.Failed(
            $"automatic installation is not supported on {RuntimeName()}; install Ollama yourself and restart the node.");
    }

    private async Task<ProcessControlResult> InstallOnLinuxAsync(CancellationToken cancellationToken)
    {
        var url = Source(LinuxInstallScript);
        var command = $"curl -fsSL {url} | sh";

        logger.LogInformation(
            "Installing Ollama from {InstallUrl}. The exact command is: /bin/sh -c \"{Command}\"",
            url,
            command);

        return await RunAsync("/bin/sh", ["-c", command], cancellationToken);
    }

    private async Task<ProcessControlResult> InstallOnWindowsAsync(CancellationToken cancellationToken)
    {
        var url = Source(WindowsInstaller);
        var target = Path.Combine(Path.GetTempPath(), $"OllamaSetup-{Guid.NewGuid():N}.exe");
        var arguments = new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" };

        logger.LogInformation(
            "Installing Ollama from {InstallUrl}. It will be downloaded to {Target} and the exact command is: {Target} {Arguments}",
            url,
            target,
            target,
            string.Join(' ', arguments));

        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var file = File.Create(target))
            {
                await response.Content.CopyToAsync(file, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProcessControlResult.Failed($"could not download the installer from {url}: {ex.Message}");
        }

        try
        {
            return await RunAsync(target, arguments, cancellationToken);
        }
        finally
        {
            TryDelete(target);
        }
    }

    private string Source(string official)
        => string.IsNullOrWhiteSpace(supervisor.InstallUrl) ? official : supervisor.InstallUrl;

    private async Task<ProcessControlResult> RunAsync(
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
        timeout.CancelAfter(InstallTimeout);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"'{fileName}' produced no process.");

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout) + (await stderr);

            if (process.ExitCode == 0)
            {
                logger.LogInformation("Ollama installer finished successfully.");
                return ProcessControlResult.Ok;
            }

            return ProcessControlResult.Failed($"the installer exited with code {process.ExitCode}: {output}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return ProcessControlResult.Denied(
                "the account this node runs as is not allowed to run the Ollama installer.");
        }
        catch (Exception ex)
        {
            return ProcessControlResult.Failed($"could not run the installer: {ex.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete the downloaded installer {Path}", path);
        }
    }

    private static string RuntimeName()
        => OperatingSystem.IsMacOS() ? "macOS" : Environment.OSVersion.Platform.ToString();
}
