namespace InferHub.Node.Backends.Supervision;

public enum OllamaInstallKind
{
    /// <summary>No service and no binary — there is nothing to supervise.</summary>
    Missing,

    /// <summary>A Windows service or a systemd unit. Restarted through its manager, never respawned.</summary>
    Service,

    /// <summary>An <c>ollama</c> executable. Started detached as <c>ollama serve</c>.</summary>
    Binary
}

/// <summary>
/// What discovery found, and the name or path it found it under — carried so every log line can
/// say which thing it is about.
/// </summary>
public readonly record struct OllamaInstallation(OllamaInstallKind Kind, string Target)
{
    public static readonly OllamaInstallation Missing = new(OllamaInstallKind.Missing, string.Empty);

    public static OllamaInstallation Service(string name) => new(OllamaInstallKind.Service, name);

    public static OllamaInstallation Binary(string path) => new(OllamaInstallKind.Binary, path);
}

/// <summary>
/// The outcome of a platform action. Failures are returned, not thrown, because the one that
/// matters most — a node running as a restricted user trying to restart a machine-wide service —
/// must reach the operator as a sentence naming the privilege, not as "Access is denied" from a
/// <c>Process.Start</c> deep inside a hosted service.
/// </summary>
public sealed record ProcessControlResult(bool Success, string? Error = null, bool AccessDenied = false)
{
    public static readonly ProcessControlResult Ok = new(true);

    public static ProcessControlResult Failed(string error) => new(false, error);

    public static ProcessControlResult Denied(string error) => new(false, error, AccessDenied: true);
}

/// <summary>
/// The one class in this phase that touches <c>Process</c>, <c>sc.exe</c>, <c>systemctl</c> or
/// the filesystem. Everything else — classification, thresholds, the restart budget, the
/// one-shot install rule — is a state machine over this seam, which is what makes it testable
/// without killing anything on a build agent.
/// </summary>
public interface IOllamaProcessControl
{
    Task<OllamaInstallation> DiscoverAsync(CancellationToken cancellationToken);

    Task<ProcessControlResult> StartAsync(OllamaInstallation installation, CancellationToken cancellationToken);

    Task<ProcessControlResult> StopAsync(OllamaInstallation installation, CancellationToken cancellationToken);

    Task<bool> IsInstalledAsync(CancellationToken cancellationToken);
}
