using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Configuration;
using InferHub.Node.LocalApi;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Fit;

/// <summary>
/// Phase 120: the coordinator asks this node what the owner's app asks it — the catalogue, the estimate, an install —
/// under the owner's terms (<c>Node:Management</c>, read live). The node is the boundary (43 D1): with
/// <c>AllowFromHub</c> off it answers why and does nothing, whatever the hub decided.
/// </summary>
public sealed class HubManagement(FitService fit, CatalogService catalog, IOptionsMonitor<NodeOptions> node, ILogger<HubManagement> logger)
{
    public ManagementTerms Terms()
    {
        var terms = node.CurrentValue.Management;
        return new ManagementTerms(terms.AllowFromHub, terms.MaxModelGiB, terms.KeepFreeGiB);
    }

    public async Task<ToolResult> RunAsync(ToolJob job, CancellationToken cancellationToken)
    {
        var terms = node.CurrentValue.Management;
        var operation = job.Model?.Trim().ToLowerInvariant();

        if (!terms.AllowFromHub)
        {
            logger.LogInformation("Refused the coordinator's {Operation}: Node:Management:AllowFromHub is off", operation);
            return ToolResult.Refused(
                job.JobId,
                "this node's owner has not let its coordinator manage its models (Node:Management:AllowFromHub is off — the Windows setup asks, or the node's settings file)",
                ManagementJob.NotAllowed);
        }

        ManagementRequest request;

        try
        {
            request = string.IsNullOrWhiteSpace(job.Payload)
                ? new ManagementRequest()
                : JsonSerializer.Deserialize<ManagementRequest>(job.Payload) ?? new ManagementRequest();
        }
        catch (JsonException ex)
        {
            return ToolResult.Refused(job.JobId, $"the request is not JSON this node reads: {ex.Message}", ManagementJob.StatusCode(400));
        }

        try
        {
            var payload = operation switch
            {
                ManagementJob.Catalog => JsonSerializer.Serialize(
                    await catalog.ListAsync(new CatalogService.Request(request.Source, request.Sort, request.Kind, request.Q, request.Limit, request.FitsOnly), cancellationToken),
                    LocalControlEndpoints.Json),
                ManagementJob.Estimate => await EstimateAsync(request, terms, cancellationToken),
                ManagementJob.Install => await InstallAsync(request, terms, cancellationToken),
                _ => null
            };

            if (payload is null)
            {
                return ToolResult.Refused(job.JobId, $"'{operation}' is not a management operation; this node knows {string.Join(", ", ManagementJob.Operations)}", ManagementJob.StatusCode(400));
            }

            logger.LogInformation("Answered the coordinator's {Operation} for {Link}", operation, request.Link ?? request.Q ?? "the catalogue");
            return ToolResult.Succeeded(job.JobId, payload);
        }
        catch (QuotaRefusedException ex)
        {
            logger.LogWarning("Refused the coordinator's install of {Link}: {Reason}", request.Link, ex.Message);
            return ToolResult.Refused(job.JobId, ex.Message, ManagementJob.OverQuota);
        }
        catch (FitRefusedException ex)
        {
            return ToolResult.Refused(job.JobId, ex.Message, ManagementJob.StatusCode(ex.Status));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Refused(job.JobId, $"the model could not be looked up: {ex.Message}", ManagementJob.StatusCode(502));
        }
    }

    /// <summary>The estimate as <c>/fit</c> answers it, with the owner's terms and, per option, whether the hub may install it.</summary>
    private async Task<string> EstimateAsync(ManagementRequest request, ManagementOptions terms, CancellationToken cancellationToken)
    {
        var answer = await fit.EstimateAsync(request.Link, request.Context, cancellationToken);
        var json = JsonSerializer.SerializeToNode(answer, LocalControlEndpoints.Json)!.AsObject();

        json["terms"] = JsonSerializer.SerializeToNode(Terms(), LocalControlEndpoints.Json);
        json["hub"] = new JsonArray(answer.Options
            .Select(option => (JsonNode)new JsonObject
            {
                ["engine"] = option.Engine,
                ["variant"] = option.Variant,
                ["allowed"] = ManagementQuota.Refusal(terms, option, answer.Machine) is null,
                ["why"] = ManagementQuota.Refusal(terms, option, answer.Machine)
            })
            .ToArray());

        return json.ToJsonString(LocalControlEndpoints.Json);
    }

    /// <summary>118's install decision, then the owner's disk terms on the option it chose. The pull itself is the hub's to send.</summary>
    private async Task<string> InstallAsync(ManagementRequest request, ManagementOptions terms, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Engine))
        {
            throw new FitRefusedException(400, "name the engine to install for (engine: ollama, llamacpp, colibri or strata) — the estimate's options say which can");
        }

        var plan = await fit.PlanAsync(request.Link, request.Engine.Trim().ToLowerInvariant(), request.Variant, request.Context, request.Force, cancellationToken);

        if (ManagementQuota.Refusal(terms, plan.Option, plan.Answer.Machine) is { } refusal)
        {
            throw new QuotaRefusedException(refusal);
        }

        logger.LogInformation(
            "The coordinator may install {Variant} of {Link} for {Engine} ({DiskMiB} MiB on disk)",
            plan.Option.Variant,
            plan.Answer.Link,
            plan.Option.Engine,
            plan.Option.DiskMiB);

        return JsonSerializer.Serialize(new ManagementInstall(plan.Option.Engine, plan.Option.Variant, plan.Option.DiskMiB, plan.Command), LocalControlEndpoints.Json);
    }

    private sealed class QuotaRefusedException(string message) : Exception(message);
}

/// <summary>Phase 120 D3: the owner's disk terms for an install from the hub — a largest model, and space left free.</summary>
public static class ManagementQuota
{
    private const long MiBPerGiB = 1024;

    /// <summary>Why the hub may not install this option here, in a sentence; null when it may.</summary>
    public static string? Refusal(ManagementOptions terms, FitOption option, MachineFacts machine)
    {
        if (terms.MaxModelGiB is { } max && option.DiskMiB > max * MiBPerGiB)
        {
            return $"{option.Variant} takes {GiB(option.DiskMiB)} on disk, and this node lets its coordinator install at most {max} GiB per model (Node:Management:MaxModelGiB)";
        }

        if (terms.KeepFreeGiB <= 0)
        {
            return null;
        }

        var engine = machine.Engines.FirstOrDefault(e => e.Kind == option.Engine && (option.EngineName is null || e.Name is null || e.Name == option.EngineName))
                     ?? machine.Engines.FirstOrDefault(e => e.Kind == option.Engine);

        if (engine?.DiskFreeMiB is not { } free)
        {
            return $"this node cannot tell how much disk is free where {option.Engine} keeps its models, and its owner keeps {terms.KeepFreeGiB} GiB free from the coordinator's installs (Node:Management:KeepFreeGiB; 0 lifts it)";
        }

        var left = free - option.DiskMiB;

        return left < terms.KeepFreeGiB * MiBPerGiB
            ? $"{option.Variant} takes {GiB(option.DiskMiB)} and would leave {GiB(Math.Max(0, left))} of {GiB(free)} free where {option.Engine} keeps its models; this node keeps {terms.KeepFreeGiB} GiB free from the coordinator's installs (Node:Management:KeepFreeGiB)"
            : null;
    }

    private static string GiB(long mib) => $"{mib / (double)MiBPerGiB:0.#} GiB";
}
