using System.Diagnostics;
using System.Security.Principal;
using InferHub.Node.Update;

namespace InferHub.Node.WindowsService;

/// <summary>
/// Runs a downloaded InferHub node setup over this installation (phase 101, D4/D5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The setup does the work a running process cannot</b>: it stops this service, replaces files that are in
/// use, and starts the service again — on success and on failure alike, so a bad update never leaves the node
/// down. This class only starts it, detached: the setup is not put in a job object (only engines are, 95), so
/// it outlives the service it stops.
/// </para>
/// <para>
/// It refuses where a setup could not succeed: an installation the setup did not make (no
/// <c>unins000.exe</c> beside the exe — a <c>dotnet publish</c> copied by hand), and an account that is not an
/// administrator (a virtual account cannot write <c>Program Files</c> or stop a service).
/// </para>
/// </remarks>
public sealed class InstallerUpdateApplier(bool quiet = true) : IUpdateApplier
{
    public bool CanApply => WhyNot is null;

    public string? WhyNot
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return "updates are applied by the Windows setup, and this is not Windows";
            }

            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")))
            {
                return $"{AppContext.BaseDirectory} was not installed by the InferHub node setup; install it with the setup once and later updates are applied for you";
            }

            using var identity = WindowsIdentity.GetCurrent();

            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return $"the node runs as {identity.Name}, which cannot run a setup; run the service as LocalSystem, or update it by hand with the setup";
            }

            return null;
        }
    }

    public async Task LaunchAsync(string setupPath, string logPath, CancellationToken cancellationToken)
    {
        if (WhyNot is { } reason)
        {
            throw new UpdateException(reason);
        }

        // /VERYSILENT for the service (session 0 has no desktop); /SILENT for an operator at a console, who
        // gets a progress window. Either way the setup keeps the existing directory and every answer it was
        // given last time (it remembers them), and writes no settings — the node's own are untouched (D1).
        var mode = quiet ? "/VERYSILENT" : "/SILENT";
        var start = new ProcessStartInfo(setupPath)
        {
            Arguments = $"{mode} /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=\"{logPath}\"",
            UseShellExecute = false,
            CreateNoWindow = quiet,
            WorkingDirectory = Path.GetDirectoryName(setupPath)!,
        };

        using var process = Process.Start(start)
            ?? throw new UpdateException($"could not start {setupPath}");

        // A setup that refuses at once (a bad argument, a newer version already installed) exits within a
        // second or two; one that is working is still running when it stops this service. So wait briefly,
        // and only a quick non-zero exit is a failure worth reporting from here.
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            return;
        }

        if (process.ExitCode != 0)
        {
            throw new UpdateException($"the setup exited with code {process.ExitCode} before it started; see {logPath}");
        }
    }
}
