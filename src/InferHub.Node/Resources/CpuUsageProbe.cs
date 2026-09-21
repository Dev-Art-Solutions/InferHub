using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace InferHub.Node.Resources;

/// <summary>
/// This machine's overall CPU utilization, 0-100, sampled as a delta between two calls (phase 82).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-rolled against each platform's own idle/total counters, not a package</b> — rule 5.
/// <c>System.Diagnostics.PerformanceCounter</c> is a separate NuGet package on modern .NET and would
/// have been the obvious shortcut on Windows; instead this calls <c>GetSystemTimes</c> directly, the
/// same "the driver already knows the answer, ask it" posture <see cref="Backends.CudaDeviceProbe"/>
/// takes with <c>libcuda.so.1</c>. On Linux it reads <c>/proc/stat</c>, which is the same file every
/// <c>top</c> and <c>htop</c> reads.
/// </para>
/// <para>
/// The first call after construction (and the first call after any gap where the machine's counters
/// could have wrapped or a probe was skipped) has nothing to diff against, so it returns null rather
/// than a fabricated number — the same "an absent number is never treated as over the cap" rule
/// <c>VramBudget</c> uses for a recipe with no declared VRAM figure.
/// </para>
/// </remarks>
internal sealed class CpuUsageProbe
{
    private (ulong Idle, ulong Total)? last;

    /// <summary>Never throws. Null on any platform this cannot read, or on the first call.</summary>
    public double? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows();
            }

            if (OperatingSystem.IsLinux())
            {
                return ReadLinux();
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    private double? ReadWindows()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return null;
        }

        var idle = ToTicks(idleFt);

        // Windows' "kernel time" already includes idle time, so kernel + user is the true total —
        // there is no third counter to add.
        var total = ToTicks(kernelFt) + ToTicks(userFt);

        return Delta(idle, total);
    }

    private double? ReadLinux()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));

        if (line is null)
        {
            return null;
        }

        var fields = line
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(part => ulong.TryParse(part, out var value) ? value : 0UL)
            .ToArray();

        if (fields.Length < 4)
        {
            return null;
        }

        // user, nice, system, idle, iowait, irq, softirq, steal, guest, guest_nice — idle and iowait
        // both count as "not doing work", the same pairing procfs's own accounting treats as idle.
        var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0UL);
        var total = fields.Aggregate(0UL, (sum, value) => sum + value);

        return Delta(idle, total);
    }

    private double? Delta(ulong idle, ulong total)
    {
        if (last is not { } previous)
        {
            last = (idle, total);
            return null;
        }

        last = (idle, total);

        var deltaTotal = total - previous.Total;

        if (deltaTotal <= 0)
        {
            return null;
        }

        var deltaIdle = idle - previous.Idle;

        return 100.0 * (1.0 - (double)deltaIdle / deltaTotal);
    }

    private static ulong ToTicks(FILETIME ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
}
