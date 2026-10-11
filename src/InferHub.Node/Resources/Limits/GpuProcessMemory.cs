using System.Globalization;
using System.Runtime.InteropServices;

namespace InferHub.Node.Resources.Limits;

/// <summary>Dedicated GPU memory per process (phase 102). No OS can cap it, so the watchdog has to read it.</summary>
public interface IGpuProcessMemory : IDisposable
{
    bool Available { get; }

    /// <summary>pid → bytes of dedicated GPU memory; null when there is no reading this time.</summary>
    IReadOnlyDictionary<int, long>? Read();
}

internal static class GpuProcessMemory
{
    public static IGpuProcessMemory Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsGpuProcessMemory();
        }

        if (OperatingSystem.IsLinux())
        {
            return new NvmlGpuProcessMemory();
        }

        return new NoGpuProcessMemory();
    }
}

internal sealed class NoGpuProcessMemory : IGpuProcessMemory
{
    public bool Available => false;

    public IReadOnlyDictionary<int, long>? Read() => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Windows: the <c>GPU Process Memory(pid_N_…)\Dedicated Usage</c> performance counters, the numbers
/// Task Manager shows — through PDH, hand-rolled (rule 5: no package for a counter). NVML cannot answer
/// this on Windows: under WDDM its per-process figure is "not available".
/// </summary>
internal sealed class WindowsGpuProcessMemory : IGpuProcessMemory
{
    private const string CounterPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhMoreData = 0x800007D2;

    private nint query;
    private nint counter;

    public WindowsGpuProcessMemory()
    {
        try
        {
            if (PdhOpenQueryW(null, 0, out query) != 0)
            {
                query = 0;
                return;
            }

            if (PdhAddEnglishCounterW(query, CounterPath, 0, out counter) != 0)
            {
                PdhCloseQuery(query);
                query = 0;
                return;
            }

            Available = true;
        }
        catch (Exception)
        {
            Available = false;
        }
    }

    public bool Available { get; }

    public IReadOnlyDictionary<int, long>? Read()
    {
        if (!Available)
        {
            return null;
        }

        try
        {
            if (PdhCollectQueryData(query) != 0)
            {
                return null;
            }

            uint size = 0;
            uint count = 0;

            if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, ref count, 0) != PdhMoreData || size == 0)
            {
                // No instance yet is "nobody uses the card", a reading in its own right.
                return new Dictionary<int, long>();
            }

            var buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, ref count, buffer) != 0)
                {
                    return null;
                }

                var result = new Dictionary<int, long>();
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();

                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + (i * itemSize));

                    if (item.CStatus is not (0 or 1) || ParsePid(Marshal.PtrToStringUni(item.Name)) is not { } pid)
                    {
                        continue;
                    }

                    // One instance per adapter a process touches; a process on two cards holds both.
                    result[pid] = result.GetValueOrDefault(pid) + item.LargeValue;
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary><c>pid_1234_luid_0x00000000_0x0000D1B5_phys_0</c> → 1234.</summary>
    internal static int? ParsePid(string? instance)
    {
        if (instance is null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = instance.IndexOf('_', 4);
        var digits = end < 0 ? instance[4..] : instance[4..end];

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    public void Dispose()
    {
        if (query != 0)
        {
            PdhCloseQuery(query);
            query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public uint CStatus;
        public long LargeValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}

/// <summary>
/// Linux: NVML's running compute processes, per device. Resolved like <see cref="NvmlProbe"/> — through
/// <see cref="NativeLibrary"/>, never a class-scope import — so a box without the driver never faults.
/// </summary>
/// <remarks>
/// Inside a container NVML may report the host's pids, which match nothing the node started; the
/// runtime's VRAM then reads 0 and the limit cannot trip. The release notes say so.
/// </remarks>
internal sealed class NvmlGpuProcessMemory : IGpuProcessMemory
{
    private delegate int NvmlInitV2();

    private delegate int NvmlDeviceGetCountV2(ref uint count);

    private delegate int NvmlDeviceGetHandleByIndexV2(uint index, out nint device);

    private delegate int NvmlDeviceGetComputeRunningProcesses(nint device, ref uint count, [Out] ProcessInfo[]? infos);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    private const int NvmlErrorInsufficientSize = 7;

    private nint library;
    private NvmlDeviceGetCountV2? getCount;
    private NvmlDeviceGetHandleByIndexV2? getHandle;
    private NvmlDeviceGetComputeRunningProcesses? getProcesses;

    public NvmlGpuProcessMemory()
    {
        try
        {
            if (!NativeLibrary.TryLoad("libnvidia-ml.so.1", out library))
            {
                return;
            }

            if (!TryGet<NvmlInitV2>("nvmlInit_v2", out var init) || init!() != 0)
            {
                return;
            }

            if (!TryGet("nvmlDeviceGetCount_v2", out getCount)
                || !TryGet("nvmlDeviceGetHandleByIndex_v2", out getHandle)
                || !(TryGet("nvmlDeviceGetComputeRunningProcesses_v3", out getProcesses)
                     || TryGet("nvmlDeviceGetComputeRunningProcesses_v2", out getProcesses)))
            {
                return;
            }

            Available = true;
        }
        catch (Exception)
        {
            Available = false;
        }
    }

    public bool Available { get; }

    public IReadOnlyDictionary<int, long>? Read()
    {
        if (!Available)
        {
            return null;
        }

        try
        {
            uint devices = 0;

            if (getCount!(ref devices) != 0)
            {
                return null;
            }

            var result = new Dictionary<int, long>();

            for (uint index = 0; index < devices; index++)
            {
                if (getHandle!(index, out var device) != 0)
                {
                    continue;
                }

                uint count = 0;
                var status = getProcesses!(device, ref count, null);

                if (status != 0 && status != NvmlErrorInsufficientSize)
                {
                    continue;
                }

                if (count == 0)
                {
                    continue;
                }

                // Room for a process that started between the two calls.
                count += 8;
                var infos = new ProcessInfo[count];

                if (getProcesses(device, ref count, infos) != 0)
                {
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    if (infos[i].UsedGpuMemory is ulong.MaxValue)
                    {
                        continue;
                    }

                    var pid = (int)infos[i].Pid;
                    result[pid] = result.GetValueOrDefault(pid) + (long)infos[i].UsedGpuMemory;
                }
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool TryGet<T>(string symbol, out T? function) where T : Delegate
    {
        if (library != 0 && NativeLibrary.TryGetExport(library, symbol, out var address))
        {
            function = Marshal.GetDelegateForFunctionPointer<T>(address);
            return true;
        }

        function = null;
        return false;
    }

    public void Dispose()
    {
        if (library != 0)
        {
            NativeLibrary.Free(library);
            library = 0;
        }
    }
}
