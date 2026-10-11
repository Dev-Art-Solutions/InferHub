using System.Diagnostics;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Resources.Limits;

/// <summary>
/// The OS object that holds one runtime's processes to its limits (phase 102): a Windows Job Object,
/// a Linux cgroup, or nothing at all. What it cannot enforce, it says — the limiter then watches that
/// resource itself and stops the runtime above it, or reports that nothing holds it.
/// </summary>
public interface IProcessContainer : IDisposable
{
    /// <summary>Puts a freshly started process (and so everything it starts later) under the limits. Never throws.</summary>
    void Add(Process process);

    /// <summary>
    /// Sets the limits in force, live, on processes already inside. Returns resource → how it is held
    /// (<c>os …</c>) for each limit this container enforces itself; the rest are the limiter's.
    /// </summary>
    IReadOnlyDictionary<string, string> Apply(EngineLimits limits);

    /// <summary>Every process inside, children included; null when this container cannot list them.</summary>
    IReadOnlyList<int>? ProcessIds();
}

/// <summary>Builds one container per runtime. A seam so the limiter's decisions are tested without an OS object.</summary>
public interface IProcessContainerFactory
{
    IProcessContainer Create(string group);
}

/// <summary>Holds nothing: every limit falls to the watchdog, or to nobody.</summary>
public sealed class NoProcessContainer : IProcessContainer
{
    private static readonly IReadOnlyDictionary<string, string> Nothing = new Dictionary<string, string>();

    public void Add(Process process)
    {
    }

    public IReadOnlyDictionary<string, string> Apply(EngineLimits limits) => Nothing;

    public IReadOnlyList<int>? ProcessIds() => null;

    public void Dispose()
    {
    }
}

/// <summary>Windows: a Job Object per runtime. Linux: a cgroup when this process may make one, and affinity either way.</summary>
public sealed class PlatformProcessContainerFactory(ILogger logger) : IProcessContainerFactory
{
    public IProcessContainer Create(string group)
    {
        if (OperatingSystem.IsWindows())
        {
            return (IProcessContainer?)WindowsJobContainer.TryCreate(group, logger) ?? new NoProcessContainer();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxProcessContainer(group, logger);
        }

        return new NoProcessContainer();
    }
}

/// <summary>The names the report uses for each limited resource.</summary>
public static class LimitResources
{
    public const string Cpu = "cpuPercent";
    public const string Cores = "cores";
    public const string Memory = "memoryMiB";
    public const string Vram = "vramMiB";
    public const string Gpus = "gpus";
}
