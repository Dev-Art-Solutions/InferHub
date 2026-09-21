using System.Runtime.InteropServices;

namespace InferHub.Node.Resources;

/// <summary>
/// This box's live GPU utilization, re-sampled on every poll (phase 82). Distinct from
/// <see cref="Backends.CudaDeviceProbe"/>, which answers "is a device visible at all" exactly once
/// at boot — this asks a different library (NVML rather than the CUDA driver API) a question that
/// changes every second, and is deliberately never cached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same platform scope and the same never-throw posture as <c>CudaDeviceProbe</c></b>
/// (phase-39 D5): <c>libnvidia-ml.so.1</c> is a Linux-only library, resolved through
/// <see cref="NativeLibrary"/> rather than a class-scope <c>DllImport</c> so a machine with no
/// NVIDIA driver never faults on the first touch of this type. Every failure — no library, no
/// symbol, a non-zero NVML status — is "no reading", never an exception a poll loop has to guard.
/// </para>
/// <para>
/// Reports the busiest visible device's compute utilization rather than an average: a cap exists to
/// stop this box being pushed past what an operator will tolerate, and a second idle card should not
/// dilute a first one already pegged at 100%.
/// </para>
/// </remarks>
internal sealed class NvmlProbe : IDisposable
{
    private const string Library = "libnvidia-ml.so.1";

    private delegate int NvmlInitV2();

    private delegate int NvmlDeviceGetCountV2(ref uint count);

    private delegate int NvmlDeviceGetHandleByIndexV2(uint index, out nint device);

    private delegate int NvmlDeviceGetUtilizationRates(nint device, out Utilization utilization);

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private nint library;
    private NvmlDeviceGetCountV2? getCount;
    private NvmlDeviceGetHandleByIndexV2? getHandle;
    private NvmlDeviceGetUtilizationRates? getUtilization;

    public bool Available { get; private set; }

    /// <summary>Loads the driver and initialises NVML. Never throws; failure just leaves <see cref="Available"/> false.</summary>
    public void Initialize()
    {
        if (!OperatingSystem.IsLinux() || Available)
        {
            return;
        }

        try
        {
            if (!NativeLibrary.TryLoad(Library, out library))
            {
                return;
            }

            if (!TryGet<NvmlInitV2>("nvmlInit_v2", out var init) || init!() != 0)
            {
                return;
            }

            if (!TryGet("nvmlDeviceGetCount_v2", out getCount)
                || !TryGet("nvmlDeviceGetHandleByIndex_v2", out getHandle)
                || !TryGet("nvmlDeviceGetUtilizationRates", out getUtilization))
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

    /// <summary>The busiest visible device's compute utilization, 0-100, or null when NVML has nothing to say.</summary>
    public double? Read()
    {
        if (!Available)
        {
            return null;
        }

        try
        {
            uint count = 0;

            if (getCount!(ref count) != 0 || count == 0)
            {
                return null;
            }

            double? busiest = null;

            for (uint index = 0; index < count; index++)
            {
                if (getHandle!(index, out var device) != 0)
                {
                    continue;
                }

                if (getUtilization!(device, out var utilization) != 0)
                {
                    continue;
                }

                if (busiest is null || utilization.Gpu > busiest)
                {
                    busiest = utilization.Gpu;
                }
            }

            return busiest;
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
