namespace InferHub.Node.Update;

/// <summary>
/// Runs a downloaded setup (phase 101, D5). Registered by the host that was installed by one — the Windows
/// service; every other host gets <see cref="NoUpdateApplier"/>, which says why not.
/// </summary>
public interface IUpdateApplier
{
    /// <summary>Whether this box can apply an update at all. Asked on every report, so it must be cheap.</summary>
    bool CanApply { get; }

    /// <summary>The sentence that goes with <c>CanApply == false</c>.</summary>
    string? WhyNot { get; }

    /// <summary>
    /// Starts the setup and returns once it is running. The setup stops this process; nothing after this call
    /// is guaranteed to run.
    /// </summary>
    Task LaunchAsync(string setupPath, string logPath, CancellationToken cancellationToken);
}

/// <summary>The default: a node that was not installed by the Windows setup is updated the way it was installed.</summary>
public sealed class NoUpdateApplier : IUpdateApplier
{
    public bool CanApply => false;

    public string? WhyNot =>
        "this node was not installed by the Windows setup; update it the way it was installed (docker pull for an image, the setup for a Windows service)";

    public Task LaunchAsync(string setupPath, string logPath, CancellationToken cancellationToken)
        => throw new UpdateException(WhyNot!);
}
