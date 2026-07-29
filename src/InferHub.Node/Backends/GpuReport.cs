using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends;

/// <summary>
/// Says out loud, on every boot, whether this process can see a GPU — phase 39, D6.
/// </summary>
/// <remarks>
/// <para>
/// An earlier draft of phase 39 had the bundled image <em>refuse to start</em> without a visible
/// GPU, on the grounds that a silent CPU fallback is the "confident, fluent, wrong" failure this
/// codebase keeps refusing. That was the wrong call. CPU is a legitimate mode rather than a
/// misconfiguration: embedding models, small models and a vector-store-only node all run on it by
/// design, and refusing would have made two of the image's three documented modes impossible.
/// </para>
/// <para>
/// The danger was never the CPU — it was <strong>silence</strong>. Somebody who pulls four
/// gigabytes of CUDA runtime, drops a <c>--gpus all</c> flag, and gets two tokens a second has no
/// signal at all, and spends the afternoon blaming the model. So this logs what the probe saw in
/// both directions, in the first lines of <c>docker logs</c>, and <see cref="OllamaOptions.RequireGpu"/>
/// is there for the operator who wants the guarantee rather than the report.
/// </para>
/// <para>
/// That follows phase-35 D4 (a keyless remote Qdrant warns rather than refusing — overruling an
/// operator about their own deployment is not ours to do) rather than phase-37 D4 (a keyless
/// inference port on a LAN refuses). The line between them is whether the bad outcome is the
/// operator's own: a slow box is theirs, an open GPU is everyone's.
/// </para>
/// <para>
/// The probe runs in <see cref="StartAsync"/> and never in a constructor, so composition stays
/// I/O-free — <c>NodeCompositionTests</c> pins that for every service on the node.
/// </para>
/// </remarks>
public sealed class GpuReport(
    IOptions<OllamaOptions> ollamaOptions,
    ILogger<GpuReport> logger) : IHostedService
{
    private readonly bool required = ollamaOptions.Value.RequireGpu;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var devices = CudaDeviceProbe.Current;

        if (devices.Available)
        {
            logger.LogInformation(
                "CUDA: {Count} device(s) visible to this process — {Devices}.",
                devices.Count,
                string.Join(", ", devices.Names));

            return Task.CompletedTask;
        }

        if (required)
        {
            throw new InvalidOperationException(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.RequireGpu)} is true and no CUDA device is "
                + "visible to this process: libcuda.so.1 did not load, or the driver reported no devices. "
                + "In a container this almost always means the run is missing '--gpus all' (or, under "
                + "compose, a devices reservation). Pass it, or set "
                + $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.RequireGpu)}=false to run on the CPU.");
        }

        logger.LogInformation(
            "CUDA: no devices visible to this process; inference will run on the CPU. In a container, pass '--gpus all' to use a card. Set {Key}=true to make this a startup failure instead.",
            $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.RequireGpu)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
