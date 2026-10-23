using System.Diagnostics;
using System.Globalization;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// Tells <c>coli serve</c> its limits (phase 111, D4) in the words colibri 2.0's own launcher reads, so
/// the engine sizes its caches inside them instead of from the free memory it sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Memory → <c>--ram G</c></b>, 90% of the limit in whole GB. The launcher hands it to each family in
/// its own variable (<c>RAM_GB</c>; GLM-5.3 Flash's expert cache gets <c>GLM53_EXPERT_GB</c> = G − 8),
/// and the engines treat it as the whole process's budget. Unset, they take 88% of what the OS reports
/// free — which a Job Object's memory limit is not part of — and meet the limit as a failed allocation.
/// The tenth left over is for the Python gateway and the prefill workspace.
/// </para>
/// <para>
/// <b>Cores and CPU share → <c>OMP_NUM_THREADS</c></b>. The launcher otherwise sets it to the
/// machine's physical cores, which on four allowed cores of a 32-core box is eight threads per core.
/// </para>
/// <para>
/// <b>GPUs.</b> None → <c>COLI_CUDA=0</c> and <c>COLI_VULKAN=0</c>: 2.0's archives carry the Vulkan
/// backend and a Windows CUDA package, so "no GPU" must be said to colibri, not only to CUDA. A list →
/// <c>COLI_VK_DEV</c> is its first card (Vulkan ignores <c>CUDA_VISIBLE_DEVICES</c>, as llama.cpp's
/// Vulkan build did in 102); a second device the operator asked for (<c>COLI_VK_DEV2</c>) becomes the
/// list's second card, or goes away when the list has one.
/// </para>
/// <para>
/// <b>VRAM → <c>COLI_VK_DEVICE_CAP_MB</c></b> (per device), which every Vulkan budget in the engine
/// reads — the expert tier, the dense chain, the chunk — and <c>CUDA_EXPERT_GB</c> (the limit less 2 GB
/// for the dense weights and scratch) for the CUDA build.
/// </para>
/// <para>
/// <b>Never <c>--gpu</c> or <c>--vram</c>.</b> Those flags make <c>coli</c> exit on a build without
/// CUDA (every Linux archive and the plain Windows one), so a limit would become a runtime that never
/// starts. The variables reach the same switches and are ignored by a build that lacks them.
/// </para>
/// </remarks>
public sealed class ColibriLimitTeller(Func<int>? logicalProcessors = null) : IEngineLimitTeller
{
    private readonly Func<int> logical = logicalProcessors ?? (() => Environment.ProcessorCount);

    public EngineLimits Told(EngineLimits effective) => effective;

    public IReadOnlyList<string> Tell(EngineLimits told, ProcessStartInfo info)
    {
        var lines = new List<string>();

        if (EngineLimitTelling.Threads(told, logical()) is { } threads)
        {
            info.Environment["OMP_NUM_THREADS"] = threads.ToString(CultureInfo.InvariantCulture);
            lines.Add($"OMP_NUM_THREADS={threads}");
        }

        if (told.MemoryMiB is { } memory)
        {
            var gb = EngineLimitTelling.GiB(memory * 9 / 10);
            info.ArgumentList.Add("--ram");
            info.ArgumentList.Add(gb.ToString(CultureInfo.InvariantCulture));
            lines.Add($"--ram {gb}");
        }

        if (told.Gpus is { } gpus)
        {
            if (gpus.Count == 0)
            {
                info.Environment["COLI_CUDA"] = "0";
                info.Environment["COLI_VULKAN"] = "0";
                info.Environment.Remove("COLI_VK_DEV");
                info.Environment.Remove("COLI_VK_DEV2");
                info.Environment.Remove("CUDA_EXPERT_GB");
                lines.Add("COLI_CUDA=0");
                lines.Add("COLI_VULKAN=0");
            }
            else
            {
                info.Environment["COLI_VK_DEV"] = gpus[0].ToString(CultureInfo.InvariantCulture);
                lines.Add($"COLI_VK_DEV={gpus[0]}");

                if (info.Environment.ContainsKey("COLI_VK_DEV2"))
                {
                    if (gpus.Count > 1)
                    {
                        info.Environment["COLI_VK_DEV2"] = gpus[1].ToString(CultureInfo.InvariantCulture);
                        lines.Add($"COLI_VK_DEV2={gpus[1]}");
                    }
                    else
                    {
                        info.Environment.Remove("COLI_VK_DEV2");
                        lines.Add("COLI_VK_DEV2 removed");
                    }
                }
            }
        }

        if (told.VramMiB is { } vram && told.Gpus is not { Count: 0 })
        {
            var devices = info.Environment.ContainsKey("COLI_VK_DEV2") ? 2 : 1;
            var perDevice = Math.Max(1, vram / devices);
            info.Environment["COLI_VK_DEVICE_CAP_MB"] = perDevice.ToString(CultureInfo.InvariantCulture);
            lines.Add($"COLI_VK_DEVICE_CAP_MB={perDevice}");

            var expertGb = Math.Max(1.0, vram / 1024.0 - 2.0);
            info.Environment["CUDA_EXPERT_GB"] = EngineLimitTelling.Invariant(Math.Round(expertGb, 1));
            lines.Add($"CUDA_EXPERT_GB={EngineLimitTelling.Invariant(Math.Round(expertGb, 1))}");
        }

        return lines;
    }
}
