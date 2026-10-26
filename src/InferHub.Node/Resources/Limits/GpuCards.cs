using System.Runtime.InteropServices;

namespace InferHub.Node.Resources.Limits;

/// <summary>How much memory a card has, by its NVML index (phase 111) — what a teller turns a VRAM limit into a reserve with.</summary>
public interface IGpuCards
{
    /// <summary>The card's total memory in MiB, or null when it cannot be read here (no NVIDIA driver, no such card).</summary>
    long? TotalMiB(int index);

    /// <summary>Every card NVML sees, with its name and its memory now (phase 118) — empty without an NVIDIA driver.</summary>
    IReadOnlyList<InferHub.Shared.Fit.GpuFacts> Cards() => [];
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

        if (NativeLibrary.TryGetExport(library, "nvmlDeviceGetCount_v2", out var count))
        {
            getCount = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetCountV2>(count);
        }

        if (NativeLibrary.TryGetExport(library, "nvmlDeviceGetName", out var name))
        {
            getName = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetName>(name);
        }

        return true;
    }

    private delegate int NvmlDeviceGetCountV2(out uint count);

    private delegate int NvmlDeviceGetName(nint device, byte[] name, uint length);

    private NvmlDeviceGetCountV2? getCount;
    private NvmlDeviceGetName? getName;

    public IReadOnlyList<InferHub.Shared.Fit.GpuFacts> Cards()
    {
        lock (gate)
        {
            try
            {
                if (!Load() || getCount is null || getCount(out var count) != 0)
                {
                    return [];
                }

                var cards = new List<InferHub.Shared.Fit.GpuFacts>();

                for (uint i = 0; i < count; i++)
                {
                    if (getHandle!(i, out var device) != 0 || getMemory!(device, out var memory) != 0)
                    {
                        continue;
                    }

                    var name = $"GPU {i}";
                    var buffer = new byte[96];

                    if (getName is not null && getName(device, buffer, (uint)buffer.Length) == 0)
                    {
                        var end = Array.IndexOf(buffer, (byte)0);
                        name = System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
                    }

                    cards.Add(new InferHub.Shared.Fit.GpuFacts((int)i, name, (long)(memory.Total / (1024 * 1024)), (long)(memory.Free / (1024 * 1024))));
                }

                return cards;
            }
            catch (Exception)
            {
                return [];
            }
        }
    }
}
