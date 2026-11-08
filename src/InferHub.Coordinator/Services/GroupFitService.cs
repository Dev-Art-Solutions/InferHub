using System.Text.Json;
using InferHub.Shared.Fit;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 133: would a model fit across several of this hub's nodes. One node reads the model (its own estimate, 118's
/// reader and cache); every node is asked only for its machine (<see cref="ManagementJob.Machine"/>); the plan is
/// <see cref="GroupFit"/>'s arithmetic over what they said (D1). Each node answers by its owner's terms (120) — one
/// that does not is listed with its reason, never guessed at.
/// </summary>
public sealed class GroupFitService(INodeRegistry registry, NodeManagementControl control)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public delegate Task<NodeManagementControl.Answer> Ask(string nodeId, string operation, ManagementRequest request, CancellationToken cancellationToken);

    public sealed record Candidate(string NodeId, string Name);

    /// <summary>What a node said when asked: its capacity for a group, or why it is not in one.</summary>
    public sealed record Asked(string NodeId, string Name, bool Ok, long? CapacityMiB, string? Device, string? Why);

    public sealed record Answer(
        int Status,
        string? Error,
        string? Link = null,
        int Context = 0,
        string? Model = null,
        string? Architecture = null,
        int? Layers = null,
        string? ReadBy = null,
        string? Refused = null,
        IReadOnlyList<Asked>? Asked = null,
        IReadOnlyList<GroupPlan>? Plans = null);

    public Task<Answer> PlanAsync(string? link, int? context, IReadOnlyList<string>? only, CancellationToken cancellationToken)
    {
        var nodes = registry.Snapshot(DateTimeOffset.UtcNow)
            .Where(n => only is not { Count: > 0 } || only.Contains(n.NodeId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(n => n.NodeId, StringComparer.OrdinalIgnoreCase)
            .Select(n => new Candidate(n.NodeId, n.Name))
            .ToArray();

        return ComposeAsync(nodes, control.AskAsync, link, context, cancellationToken);
    }

    /// <summary>The asking and the composing, apart from the registry — what the tests drive.</summary>
    public static async Task<Answer> ComposeAsync(IReadOnlyList<Candidate> nodes, Ask ask, string? link, int? context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return new Answer(StatusCodes.Status400BadRequest, "name the model: a link (owner/repo, hf.co/…) — the group runs llama.cpp over a GGUF");
        }

        if (nodes.Count == 0)
        {
            return new Answer(StatusCodes.Status409Conflict, "no node is connected to this hub");
        }

        var asked = context is > 0 ? context.Value : FitEstimator.DefaultContext;
        var request = new ManagementRequest(link.Trim(), asked);
        var machines = new Dictionary<string, MachineFacts>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ModelFacts? model = null;
        string? canonical = null, readBy = null;

        // One node reads the model; the first that can. A link the node calls bad is the link's fault, not the node's.
        foreach (var node in nodes)
        {
            var answer = await ask(node.NodeId, ManagementJob.Estimate, request, cancellationToken);

            if (answer.Status == StatusCodes.Status400BadRequest)
            {
                return new Answer(StatusCodes.Status400BadRequest, answer.Error);
            }

            if (!answer.Ok || Read<EstimateReply>(answer.Payload) is not { Model: not null } reply)
            {
                reasons[node.NodeId] = answer.Error ?? "its estimate could not be read";
                continue;
            }

            (model, canonical, readBy) = (reply.Model, reply.Link ?? link, node.NodeId);

            if (reply.Machine is not null)
            {
                machines[node.NodeId] = reply.Machine;
            }

            break;
        }

        if (model is null)
        {
            return new Answer(StatusCodes.Status409Conflict, "no node could read the model for this hub", link, asked,
                Asked: nodes.Select(n => new Asked(n.NodeId, n.Name, false, null, null, reasons.GetValueOrDefault(n.NodeId))).ToArray());
        }

        // Every other node, its machine only — side by side, each failing alone.
        var rest = nodes.Where(n => !machines.ContainsKey(n.NodeId)).ToArray();
        var answers = await Task.WhenAll(rest.Select(n => ask(n.NodeId, ManagementJob.Machine, new ManagementRequest(), cancellationToken)));

        foreach (var (node, answer) in rest.Zip(answers))
        {
            if (answer.Ok && Read<MachineFacts>(answer.Payload) is { } facts)
            {
                machines[node.NodeId] = facts;
                reasons.Remove(node.NodeId);
            }
            else
            {
                reasons[node.NodeId] = answer.Status == StatusCodes.Status400BadRequest && answer.Error?.Contains("not a management operation") == true
                    ? "older than 3.93: it cannot say what it would give a group"
                    : answer.Error ?? "its machine could not be read";
            }
        }

        var candidates = nodes.Where(n => machines.ContainsKey(n.NodeId)).Select(n => new GroupCandidate(n.NodeId, n.Name, machines[n.NodeId])).ToArray();
        var report = nodes.Select(n =>
            {
                if (!machines.TryGetValue(n.NodeId, out var machine))
                {
                    return new Asked(n.NodeId, n.Name, false, null, null, reasons.GetValueOrDefault(n.NodeId));
                }

                var (mib, device) = GroupFit.Capacity(machine);
                return new Asked(n.NodeId, n.Name, true, mib, device, null);
            })
            .ToArray();

        return new Answer(
            StatusCodes.Status200OK,
            null,
            canonical,
            asked,
            model.Name,
            model.Architecture,
            model.Layers,
            readBy,
            GroupFit.CanSplit(model),
            report,
            GroupFit.PlanAll(model, candidates, asked));
    }

    private sealed record EstimateReply(string? Link, ModelFacts? Model, MachineFacts? Machine);

    private static T? Read<T>(string? payload) where T : class
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
