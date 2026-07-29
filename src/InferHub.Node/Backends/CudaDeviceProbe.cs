using System.Runtime.InteropServices;
using System.Text;

namespace InferHub.Node.Backends;

/// <summary>What this process can see of the machine's NVIDIA GPUs.</summary>
/// <param name="Available">True only when CUDA initialised <em>and</em> reported at least one device.</param>
/// <param name="Names">One entry per device, in CUDA ordinal order. Empty when <paramref name="Available"/> is false.</param>
public readonly record struct CudaDevices(bool Available, IReadOnlyList<string> Names)
{
    public static readonly CudaDevices None = new(false, []);

    public int Count => Names.Count;
}

/// <summary>
/// Answers "can this process use a GPU?" by loading the driver and asking it — phase 39, D5.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Do not replace this with a check for <c>/dev/nvidia*</c>.</strong> Every recipe on the
/// internet does that and it is wrong on the most common GPU-with-Docker setup there is: under
/// WSL2 — Docker Desktop on Windows — those device nodes <em>do not exist</em>. The GPU arrives
/// through <c>/dev/dxg</c> and the driver libraries are injected from <c>/usr/lib/wsl/lib</c>.
/// A device-node check reports "no GPU" on a machine that is about to happily run CUDA.
/// </para>
/// <para>
/// So this asks the only question that matters, the same one the inference engine will ask a few
/// seconds later: does <c>libcuda.so.1</c> load, does <c>cuInit</c> succeed, and does the driver
/// report a device? That library is the <em>driver</em>, injected at <c>docker run</c> by the
/// NVIDIA container runtime when <c>--gpus</c> is passed; the CUDA <em>runtime</em> that Ollama
/// links against ships inside Ollama itself.
/// </para>
/// <para>
/// Every failure path — no library, no symbol, a non-zero CUDA status, a hostile P/Invoke — is
/// "no devices". This is diagnostic information on a path that must never be the reason a node
/// fails to start, so it does not throw, and resolution goes through <see cref="NativeLibrary"/>
/// rather than a class-scope <c>DllImport</c>, which would fault the first time anything touched
/// this type on a machine with no driver.
/// </para>
/// </remarks>
public static class CudaDeviceProbe
{
    private const string DriverLibrary = "libcuda.so.1";

    /// <summary>Long enough for any real name; the driver truncates and NUL-terminates into it.</summary>
    private const int NameBufferLength = 256;

    private delegate int CuInit(uint flags);

    private delegate int CuDeviceGetCount(ref int count);

    private delegate int CuDeviceGetName(byte[] name, int length, int device);

    private static readonly Lazy<CudaDevices> Cached = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The boot-time answer, computed once. Nothing hot-plugs a GPU into a running container, so
    /// re-loading the driver on every <c>/api/status</c> would buy nothing.
    /// </summary>
    public static CudaDevices Current => Cached.Value;

    /// <summary>
    /// Never throws. Returns <see cref="CudaDevices.None"/> on any platform without the NVIDIA
    /// driver, which includes every Windows and macOS host — the interface this uses is the Linux
    /// driver's, and a node on Windows reaches its GPU through an Ollama that is not in a
    /// container to begin with.
    /// </summary>
    public static CudaDevices Detect()
    {
        if (!OperatingSystem.IsLinux())
        {
            return CudaDevices.None;
        }

        nint handle = 0;

        try
        {
            if (!NativeLibrary.TryLoad(DriverLibrary, out handle))
            {
                return CudaDevices.None;
            }

            return Query(handle);
        }
        catch (Exception)
        {
            // A missing symbol, a signature mismatch, a driver that faults on init: all of them
            // mean "no usable GPU here", and none of them is worth a stack trace on a probe.
            return CudaDevices.None;
        }
        finally
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }
        }
    }

    private static CudaDevices Query(nint library)
    {
        if (!TryGet<CuInit>(library, "cuInit", out var init)
            || !TryGet<CuDeviceGetCount>(library, "cuDeviceGetCount", out var getCount))
        {
            return CudaDevices.None;
        }

        if (init!(0) != 0)
        {
            return CudaDevices.None;
        }

        var count = 0;

        if (getCount!(ref count) != 0 || count <= 0)
        {
            return CudaDevices.None;
        }

        // The name is a nicety — a device we cannot name still counts, so a failure here degrades
        // to an ordinal rather than discarding the device.
        TryGet<CuDeviceGetName>(library, "cuDeviceGetName", out var getName);

        var names = new List<string>(count);

        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            names.Add(ReadName(getName, ordinal));
        }

        return new CudaDevices(true, names);
    }

    private static string ReadName(CuDeviceGetName? getName, int ordinal)
    {
        var fallback = $"CUDA device {ordinal}";

        if (getName is null)
        {
            return fallback;
        }

        var buffer = new byte[NameBufferLength];

        if (getName(buffer, buffer.Length, ordinal) != 0)
        {
            return fallback;
        }

        var end = Array.IndexOf(buffer, (byte)0);
        var name = Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();

        return name.Length == 0 ? fallback : name;
    }

    private static bool TryGet<T>(nint library, string symbol, out T? function) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(library, symbol, out var address))
        {
            function = Marshal.GetDelegateForFunctionPointer<T>(address);
            return true;
        }

        function = null;
        return false;
    }
}
