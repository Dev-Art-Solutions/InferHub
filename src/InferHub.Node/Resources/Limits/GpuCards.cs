using System.Runtime.InteropServices;

namespace InferHub.Node.Resources.Limits;

/// <summary>How much memory a card has, by its NVML index (phase 111) — what a teller turns a VRAM limit into a reserve with.</summary>
public interface IGpuCards
{
    /// <summary>The card's total memory in MiB, or null when it cannot be read here (no NVIDIA driver, no such card).</summary>
    long? TotalMiB(int index);
}

/// <summary>
/// NVML's <c>nvmlDeviceGetMemoryInfo</c>, loaded through <see cref="NativeLibrary"/> like
/// <see cref="NvmlGpuProcessMemory"/> — on Windows too: 102 D4's "N/A" is per <em>process</em> under
/// WDDM; the card's own total is read fine. Asked once per card and remembered: a card does not grow.
/// </summary>
internal sealed class NvmlGpuCards : IGpuCards
{
    private delegate int NvmlInitV2();

    private delegate int NvmlDeviceGetHandleByIndexV2(uint index, out nint device);

    private delegate int NvmlDeviceGetMemoryInfo(nint device, out Memory memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private readonly Dictionary<int, long?> known = new();
    private readonly object gate = new();
    private nint library;
    private bool loaded;
    private NvmlDeviceGetHandleByIndexV2? getHandle;
    private NvmlDeviceGetMemoryInfo? getMemory;

    public long? TotalMiB(int index)
    {
        if (index < 0)
        {
            return null;
        }

        lock (gate)
        {
            if (known.TryGetValue(index, out var cached))
            {
                return cached;
            }

            long? total = null;

            try
            {
                if (Load() && getHandle!((uint)index, out var device) == 0 && getMemory!(device, out var memory) == 0)
                {
                    total = (long)(memory.Total / (1024 * 1024));
                }
            }
            catch (Exception)
            {
                total = null;
            }

            known[index] = total;
            return total;
        }
    }

    private bool Load()
    {
        if (loaded)
        {
            return getHandle is not null && getMemory is not null;
        }

        loaded = true;

        if (!NativeLibrary.TryLoad(OperatingSystem.IsWindows() ? "nvml.dll" : "libnvidia-ml.so.1", out library)
            && !(OperatingSystem.IsWindows() && NativeLibrary.TryLoad(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
                out library)))
        {
            return false;
        }

        if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init)
            || Marshal.GetDelegateForFunctionPointer<NvmlInitV2>(init)() != 0
            || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out var handle)
            || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetMemoryInfo", out var memory))
        {
            return false;
        }

        getHandle = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetHandleByIndexV2>(handle);
        getMemory = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetMemoryInfo>(memory);
        return true;
    }
}
