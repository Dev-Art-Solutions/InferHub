using Microsoft.Extensions.Options;

namespace InferHub.Node.Control;

/// <summary>What will start this node again after it ends — the precondition for a restart (112 D3).</summary>
public sealed record RestartSupport(bool Supported, string Supervisor, string Why);

/// <summary>
/// Ends this process so its supervisor starts it again (112 D3). Hosts that know better register their own — the
/// Windows service's ends in a way the service manager's recovery acts on.
/// </summary>
public interface INodeRestarter
{
    RestartSupport Support { get; }

    /// <summary>Ends the process once running work drains (bounded); returns immediately.</summary>
    void Restart(string reason);
}

/// <summary>
/// The default: drain, then stop the host with exit code 75, which systemd (<c>Restart=on-failure</c>), a container's
/// restart policy and the desktop app (<c>INFERHUB_SUPERVISOR=desktop</c>) all treat as "start it again".
/// </summary>
public class NodeRestarter(
    IHostApplicationLifetime lifetime,
    IOptions<ControlOptions> options,
    ILogger<NodeRestarter> logger,
    NodeActivity? activity = null) : INodeRestarter
{
    public const int ExitCode = 75;

    public const string SupervisorVariable = "INFERHUB_SUPERVISOR";

    private int requested;

    public virtual RestartSupport Support => Detect();

    public void Restart(string reason)
    {
        if (Interlocked.Exchange(ref requested, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            logger.LogWarning("Restart requested ({Reason}); waiting up to {Drain} for running jobs.", reason, options.Value.DrainTimeout);
            var deadline = DateTimeOffset.UtcNow + options.Value.DrainTimeout;

            while (activity is not null && activity.Running.Count > 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            logger.LogWarning("Restarting now (exit code {Code}): {Reason}.", ExitCode, reason);
            End();
        });
    }

    /// <summary>The way this host ends; the Windows service overrides it.</summary>
    protected virtual void End()
    {
        Environment.ExitCode = ExitCode;
        lifetime.StopApplication();
    }

    internal static RestartSupport Detect(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (environment(SupervisorVariable) is { Length: > 0 } named)
        {
            return new RestartSupport(true, named, $"{named} starts the node again when it exits with {ExitCode}");
        }

        if (!string.IsNullOrEmpty(environment("INVOCATION_ID")))
        {
            return new RestartSupport(true, "systemd", $"systemd starts it again if the unit has Restart=on-failure or Restart=always (exit code {ExitCode})");
        }

        if (string.Equals(environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new RestartSupport(true, "container", $"the container starts again only if it runs with a restart policy (--restart on-failure or unless-stopped); exit code {ExitCode}");
        }

        return new RestartSupport(false, "none", "nothing on this machine would start the node again: it runs in a console. Run it as a service, under systemd, in a container with a restart policy, or from InferHub Desktop");
    }
}
