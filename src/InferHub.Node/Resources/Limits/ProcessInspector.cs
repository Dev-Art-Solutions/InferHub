using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InferHub.Node.Resources.Limits;

/// <summary>One process's reading: CPU time so far and the memory it holds.</summary>
public readonly record struct ProcessSample(TimeSpan CpuTime, long MemoryBytes);

/// <summary>
/// What the limiter needs to know about processes (phase 102), behind a seam so its decisions — over
/// a limit, stop it, report it — are tested with scripted readings rather than real engines.
/// </summary>
public interface IProcessInspector
{
    /// <summary><paramref name="roots"/> and every live descendant.</summary>
    IReadOnlyList<int> WithDescendants(IReadOnlyCollection<int> roots);

    /// <summary>Null once the process is gone.</summary>
    ProcessSample? Sample(int pid);

    /// <summary>Per process, the dedicated GPU memory in bytes; null when this box gives no such reading.</summary>
    IReadOnlyDictionary<int, long>? GpuMemory();

    /// <summary>Whether <see cref="GpuMemory"/> can ever answer here — decides how the VRAM limit is reported.</summary>
    bool CanReadGpuMemory { get; }
}

/// <summary>The real one: <c>/proc</c> on Linux, the process table on Windows, and the GPU probe of each.</summary>
public sealed class SystemProcessInspector : IProcessInspector, IDisposable
{
    private readonly IGpuProcessMemory gpu = GpuProcessMemory.Create();

    public bool CanReadGpuMemory => gpu.Available;

    public IReadOnlyList<int> WithDescendants(IReadOnlyCollection<int> roots)
    {
        var result = new HashSet<int>(roots);

        if (roots.Count == 0)
        {
            return result.ToArray();
        }

        var parents = ParentMap();

        // Breadth-first over the parent map; a pid reused by an unrelated process cannot sneak in
        // because only children of a pid already in the set are added.
        var frontier = new Queue<int>(roots);

        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();

            foreach (var (pid, ppid) in parents)
            {
                if (ppid == parent && result.Add(pid))
                {
                    frontier.Enqueue(pid);
                }
            }
        }

        return result.ToArray();
    }

    public ProcessSample? Sample(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            if (process.HasExited)
            {
                return null;
            }

            // Windows counts commit against a Job Object's limit, so the reading matches the limit;
            // Linux's watchdog compares resident memory, which is what an OOM kill would weigh.
            var memory = OperatingSystem.IsWindows() ? process.PrivateMemorySize64 : process.WorkingSet64;
            return new ProcessSample(process.TotalProcessorTime, memory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IReadOnlyDictionary<int, long>? GpuMemory() => gpu.Read();

    public void Dispose() => gpu.Dispose();

    private static List<(int Pid, int Parent)> ParentMap()
    {
        var map = new List<(int, int)>();

        if (OperatingSystem.IsLinux())
        {
            foreach (var directory in SafeEnumerate("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid))
                {
                    continue;
                }

                try
                {
                    // "pid (comm) state ppid …" — comm may hold spaces and parentheses, so read after the last ')'.
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');

                    if (fields.Length > 1 && int.TryParse(fields[1], out var ppid))
                    {
                        map.Add((pid, ppid));
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            map.AddRange(WindowsProcessTable.ParentMap());
        }

        return map;
    }

    private static IEnumerable<string> SafeEnumerate(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>Windows parent links from a Toolhelp snapshot — for a runtime whose Job Object could not be made.</summary>
internal static class WindowsProcessTable
{
    public static IEnumerable<(int Pid, int Parent)> ParentMap()
    {
        var result = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);

        if (snapshot == -1)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (!Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private const uint SnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
