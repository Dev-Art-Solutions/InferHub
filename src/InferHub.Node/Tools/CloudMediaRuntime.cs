using InferHub.Node.Configuration;
using InferHub.Shared.CloudMedia;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Tools;

/// <summary>
/// This node's cloud pictures, voices and transcripts (phase 104, D8) — asked by
/// <see cref="ToolExecutor"/> before the worker pool, and declared beside the tool runtime's own
/// capabilities, so the hub routes to it like any tool and a profile narrows it like one.
/// </summary>
/// <remarks>
/// Always registered; with no <c>CloudMedia</c> section its catalogue is empty, it serves nothing and
/// declares nothing.
/// </remarks>
public sealed class CloudMediaRuntime
{
    public const string HttpClientName = "inferhub-node-cloud-media";

    private readonly CloudMediaClient client;
    private readonly ILogger<CloudMediaRuntime> logger;

    public CloudMediaRuntime(
        IOptions<CloudMediaOptions> options,
        IHttpClientFactory httpClients,
        ILogger<CloudMediaRuntime> logger)
    {
        this.logger = logger;
        Catalog = new CloudMediaCatalog(options.Value.Providers);

        var http = httpClients.CreateClient(HttpClientName);
        http.Timeout = Timeout.InfiniteTimeSpan;
        client = new CloudMediaClient(http);
        Capabilities = Catalog.Capabilities();

        if (!Catalog.IsEmpty)
        {
            // Which names go where — never a key.
            foreach (var route in Catalog.Routes)
            {
                logger.LogInformation("Cloud media: {Route}", route.ToString());
            }
        }
    }

    public CloudMediaCatalog Catalog { get; }

    public IReadOnlyList<NodeCapability> Capabilities { get; }

    public bool Serves(string capability, string model) => Catalog.TryResolve(capability, model, out _);

    /// <summary>The tool runtime's live list with this one's beside it — what the node declares.</summary>
    public IReadOnlyList<NodeCapability> Merge(IReadOnlyList<NodeCapability> tools)
    {
        if (Capabilities.Count == 0)
        {
            return tools;
        }

        return tools.Concat(Capabilities)
            .GroupBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NodeCapability(
                group.Key,
                group.SelectMany(capability => capability.Models).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    /// <param name="upload">
    /// Where a streamed attachment's bytes come from (phase 53). A vendor takes a body, not a
    /// stream, so it is read into memory here — up to <paramref name="maxBytes"/>, the node's own
    /// ceiling — and a body over it is refused before the vendor is called.
    /// </param>
    public async Task<ToolResult> RunAsync(
        ToolJob job,
        IStreamedAttachmentSource? upload,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (!Catalog.TryResolve(job.Capability, job.Model, out var route))
        {
            return ToolResult.Retry(job.JobId, $"this node does not provide '{job.Capability}' for model '{job.Model}'", ToolExecutor.CapabilityRetryAfterSeconds);
        }

        IReadOnlyList<ToolAttachment>? attachments = null;

        if (job.HasStreamedAttachments && upload is not null)
        {
            attachments = await ReadAsync(job, upload, maxBytes, cancellationToken);

            if (attachments is null)
            {
                return ToolResult.Refused(
                    job.JobId,
                    $"the upload is over {maxBytes} bytes, and a cloud provider takes no streamed upload; send a smaller file",
                    ToolErrorCodes.InvalidRequest);
            }
        }

        var result = await client.RunAsync(job, route, attachments, cancellationToken);

        // The capability, the model, the provider and the outcome — never the prompt, the audio or
        // the picture (rule 7), and never the key.
        logger.LogInformation(
            "{Capability} job {JobId} ({Model}) on provider {ProviderId} ({Upstream}): success={Success}",
            route.Capability,
            job.JobId,
            route.LocalModel,
            route.ProviderId,
            route.UpstreamModel,
            result.Success);

        return result;
    }

    private static async Task<IReadOnlyList<ToolAttachment>?> ReadAsync(
        ToolJob job,
        IStreamedAttachmentSource upload,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var attachments = new List<ToolAttachment>();
        MemoryStream? current = null;
        string? name = null;
        string? mediaType = null;

        await foreach (var chunk in upload.ReadAsync(job.JobId, cancellationToken))
        {
            switch (chunk.Kind)
            {
                case AttachmentChunkKinds.Start:
                    current = new MemoryStream();
                    name = chunk.Name ?? $"file{chunk.Index}";
                    mediaType = chunk.MediaType ?? "application/octet-stream";
                    break;

                case AttachmentChunkKinds.Data when current is not null && chunk.Bytes is { } bytes:
                    if (current.Length + bytes.Length > maxBytes)
                    {
                        return null;
                    }

                    current.Write(bytes);
                    break;

                case AttachmentChunkKinds.End when current is not null:
                    attachments.Add(new ToolAttachment(name!, mediaType!, current.ToArray()));
                    current = null;
                    break;
            }
        }

        return attachments;
    }
}
