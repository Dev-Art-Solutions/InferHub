using System.Runtime.InteropServices;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Strata;
using InferHub.Node.Configuration;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Fit;

namespace InferHub.Node.Fit;

/// <summary>
/// What this machine has for a model (phase 118): the NVIDIA cards NVML sees, the RAM, the cores, and for each kind
/// of engine whether this node can install for it, where, the free disk there and the owner's 102 limits.
/// </summary>
/// <remarks>Read on every call — free VRAM and disk change — and cheap: one NVML pass, one memory call, a few drives.</remarks>
public sealed class MachineProbe(
    IGpuCards cards,
    BackendOptions backend,
    NodeOptions node,
    HuggingFaceOptions huggingFace,
    HuggingFaceTargets? targets,
    ColibriOptions colibri,
    StrataOptions strata,
    bool strataInstalls,
    Func<bool> managesModels)
{
    public MachineFacts Read()
    {
        var notes = new List<string>();
        var gpus = cards.Cards();

        if (gpus.Count == 0)
        {
            notes.Add("no NVIDIA card seen (NVML did not load or lists none); an AMD or Apple GPU is not read, and the estimate is for the CPU");
        }

        var (total, available) = Memory();

        return new MachineFacts(gpus, total, available, Environment.ProcessorCount, Engines(), notes);
    }

    private IReadOnlyList<EngineFacts> Engines()
    {
        var hf = huggingFace.Enabled;
        var hfOff = $"downloads are off on this node ({HuggingFaceOptions.SectionName}:Enabled=false)";
        var list = new List<EngineFacts>();

        // Ollama: pulls by itself; the node asks it to.
        var ollama = EngineOf(BackendOptions.Ollama);
        list.Add(ollama.Present
            ? Facts(FitEngines.Ollama, ollama.Name, managesModels(), managesModels() ? null : "this node does not manage its models", OllamaModels(), OllamaBackend.ParallelFrom(Environment.GetEnvironmentVariable("OLLAMA_NUM_PARALLEL")))
            : Facts(FitEngines.Ollama, null, false, "this node has no Ollama engine", null));

        var llama = EngineOf(BackendOptions.LlamaCpp);
        list.Add(!llama.Present
            ? Facts(FitEngines.LlamaCpp, null, false, "this node has no llama.cpp engine", null)
            : targets?.GgufDirectory is { } gguf
                ? Facts(FitEngines.LlamaCpp, targets.GgufEngine, true, null, gguf)
                : Facts(FitEngines.LlamaCpp, llama.Name, false, hf ? "its llama.cpp engine has no Serve:ModelsDir to download into" : hfOff, null));

        var coli = EngineOf(BackendOptions.Colibri);
        list.Add(!coli.Present
            ? Facts(FitEngines.Colibri, null, false, "this node has no colibri engine", null)
            : targets?.ColibriDirectory is { } dir
                ? Facts(FitEngines.Colibri, coli.Name, true, null, dir)
                : Facts(FitEngines.Colibri, coli.Name, false, hf ? "colibri has no catalogue (Colibri:Serve:ModelsDir) or HuggingFace:Convert is off" : hfOff, colibri.Serve.ModelsDir));

        var stra = EngineOf(BackendOptions.Strata);
        var strataDir = string.IsNullOrWhiteSpace(strata.DataDir) ? strata.Root : strata.DataDir;
        list.Add(!stra.Present
            ? Facts(FitEngines.Strata, null, false, "this node has no Strata install (Strata:Root and a strata engine)", null)
            : Facts(FitEngines.Strata, stra.Name, strataInstalls, strataInstalls ? null : hfOff, strataDir));

        return list;
    }

    private EngineFacts Facts(string kind, string? name, bool installs, string? absent, string? directory, int kvCopies = 1)
    {
        node.ResourceLimits.Engines.TryGetValue(kind, out var limit);

        return new EngineFacts(kind, name, installs, absent, directory, FreeMiB(directory), limit?.VramMiB, limit?.MemoryMiB, kvCopies);
    }

    private (bool Present, string? Name) EngineOf(string type)
    {
        if (backend.IsMulti)
        {
            var hit = backend.Engines
                .Where(pair => pair.Value.NormalizedType() == type)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return hit.Key is null ? (false, null) : (true, hit.Key);
        }

        return (backend.Normalized() == type, null);
    }

    /// <summary>Where Ollama keeps models: its <c>OLLAMA_MODELS</c> as this process sees it, else its default.</summary>
    private static string OllamaModels() =>
        Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } set
            ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

    internal static long? FreeMiB(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            // The directory may not exist yet (a fresh install); its nearest existing parent is on the same drive.
            var path = Path.GetFullPath(directory.Trim());

            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent && parent != path)
            {
                path = parent;
            }

            return new DriveInfo(path).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static (long Total, long? Available) Memory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

                if (GlobalMemoryStatusEx(ref status))
                {
                    return ((long)(status.TotalPhys / (1024 * 1024)), (long)(status.AvailPhys / (1024 * 1024)));
                }
            }
            else if (File.Exists("/proc/meminfo"))
            {
                long? Field(string name) => File.ReadLines("/proc/meminfo")
                    .Where(l => l.StartsWith(name + ":", StringComparison.Ordinal))
                    .Select(l => long.TryParse(l[(name.Length + 1)..].Trim().Split(' ')[0], out var kib) ? kib / 1024 : (long?)null)
                    .FirstOrDefault();

                // A container's limit is what the engine may really use; the GC reads it.
                var limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
                var total = Field("MemTotal") ?? limit;
                return (Math.Min(total, limit), Field("MemAvailable"));
            }
        }
        catch (Exception)
        {
        }

        return (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024), null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
