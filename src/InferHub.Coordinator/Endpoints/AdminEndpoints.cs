using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;

namespace InferHub.Coordinator.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/nodes", (INodeRegistry registry, IAuditLog audit, IProfileRegistry profiles) =>
        {
            var nodes = BuildAdminNodes(registry, audit, profiles);
            return Results.Ok(nodes);
        });

        group.MapPost("/nodes/{nodeId}/cordon", (
            string nodeId,
            HttpContext context,
            INodeRegistry registry,
            IAuditLog audit,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");

            if (!registry.Cordon(nodeId))
            {
                return Results.NotFound(new { error = $"node '{nodeId}' not found" });
            }

            audit.Record(nodeId, "cordon", ActorOf(context), DateTimeOffset.UtcNow);
            logger.LogInformation("Cordoned node {NodeId}", nodeId);
            return Results.Ok(new { nodeId, cordoned = true });
        });

        group.MapPost("/nodes/{nodeId}/uncordon", (
            string nodeId,
            HttpContext context,
            INodeRegistry registry,
            IAuditLog audit,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");

            if (!registry.Uncordon(nodeId))
            {
                return Results.NotFound(new { error = $"node '{nodeId}' not found" });
            }

            audit.Record(nodeId, "uncordon", ActorOf(context), DateTimeOffset.UtcNow);
            logger.LogInformation("Uncordoned node {NodeId}", nodeId);
            return Results.Ok(new { nodeId, cordoned = false });
        });

        group.MapPost("/nodes/{nodeId}/deregister", (
            string nodeId,
            HttpContext context,
            INodeRegistry registry,
            INodeConnectionTracker connections,
            IConversationAffinity affinity,
            IAuditLog audit,
            IProfileRegistry profiles,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");
            var connectionId = registry.FindConnectionIdByNodeId(nodeId);

            if (connectionId is null)
            {
                return Results.NotFound(new { error = $"node '{nodeId}' not found" });
            }

            var aborted = connections.Abort(connectionId);
            registry.Remove(connectionId);
            // Explicit deregister is the operator saying this node is gone for good — unlike a
            // transient disconnect, so its warm conversations should not linger pinned to it.
            affinity.ForgetNode(nodeId);
            // Same reasoning for the reported profile state: a node that is gone for good should
            // not leave a stale "refused" hanging on the status page. The profile itself stays —
            // it is fleet configuration, not node state, and the box may come back.
            profiles.Forget(nodeId);
            audit.Record(nodeId, "deregister", ActorOf(context), DateTimeOffset.UtcNow);

            logger.LogInformation(
                "Deregistered node {NodeId} (connection {ConnectionId}, aborted={Aborted})",
                nodeId,
                connectionId,
                aborted);

            return Results.Ok(new { nodeId, deregistered = true });
        });

        // Model management (phase 26). Commands travel down the node's existing outbound
        // connection; progress is relayed on the SSE stream below as `model-progress` events.
        group.MapPost("/nodes/{nodeId}/models/{model}/pull", (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            RunModelCommandAsync(ModelCommand.KindPull, nodeId, model, context, registry, commands, audit, loggerFactory, cancellationToken));

        group.MapDelete("/nodes/{nodeId}/models/{model}", (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            RunModelCommandAsync(ModelCommand.KindDelete, nodeId, model, context, registry, commands, audit, loggerFactory, cancellationToken));

        group.MapPost("/nodes/{nodeId}/models/{model}/warm", (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            RunModelCommandAsync(ModelCommand.KindWarm, nodeId, model, context, registry, commands, audit, loggerFactory, cancellationToken));

        // Fleet-wide model matrix (phase 26): model × node, with sizes and which nodes hold each.
        // The view that makes the whole feature make sense.
        group.MapGet("/models", (INodeRegistry registry) =>
        {
            var inventory = registry.ModelInventory();

            var nodes = inventory
                .Select(n => new
                {
                    nodeId = n.NodeId,
                    name = n.Name,
                    cordoned = n.Cordoned,
                    supportsModelManagement = n.SupportsModelManagement,
                    modelCount = n.Models.Count
                })
                .ToArray();

            var models = inventory
                .SelectMany(n => n.Models.Select(m => new { n.NodeId, Model = m }))
                .GroupBy(x => x.Model.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    name = g.Key,
                    sizeBytes = g.Select(x => x.Model.SizeBytes).Where(s => s.HasValue).Select(s => s!.Value)
                        .DefaultIfEmpty(0).Max() is var mx && mx > 0 ? (long?)mx : null,
                    nodes = g.Select(x => x.NodeId).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                })
                .OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(new { nodes, models });
        });

        // Ensure a model is held by at least N nodes: pull it onto the most suitable
        // capable-and-manageable nodes that don't already have it, skipping cordoned ones.
        group.MapPost("/models/{model}/ensure", async (
            string model, int? replicas, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            await EnsureModelAsync(model, replicas ?? 1, context, registry, commands, audit, loggerFactory, cancellationToken));

        // Node profiles (phase 43). The hub says what a node should be doing; the node clamps it
        // against its own configuration and reports what it refused. Admin key only, like every
        // route in this group.
        group.MapGet("/profiles", (IProfileRegistry profiles) => Results.Ok(profiles.All()));

        group.MapGet("/profiles/{name}", (string name, IProfileRegistry profiles) =>
        {
            var profile = profiles.Get(name);

            return profile is null
                ? Results.NotFound(new { error = $"profile '{name}' not found" })
                : Results.Ok(profile);
        });

        group.MapPut("/profiles/{name}", async (
            string name,
            NodeProfile body,
            HttpContext context,
            IProfileRegistry profiles,
            NodeProfileCoordinator coordinator,
            INodeRegistry registry,
            IAuditLog audit,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "a profile name is required" });
            }

            if (body?.Selector is null || body.Selector.IsEmpty)
            {
                // A selector that names nothing would otherwise read as "everything", and the one
                // thing a fleet-configuration API must not do is apply to more boxes than the
                // author meant.
                return Results.BadRequest(new
                {
                    error = "a selector naming a nodeId or at least one label is required; a profile that selects nothing is refused rather than applied to everything"
                });
            }

            if (body.MaxConcurrency is { } concurrency and < 1)
            {
                return Results.BadRequest(new { error = $"maxConcurrency {concurrency} is not a usable limit; it must be at least 1" });
            }

            var stored = profiles.Put(name, body);
            var pushes = await coordinator.ReassertAsync(cancellationToken);
            var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");

            foreach (var push in pushes.Where(p => string.Equals(p.Profile, stored.Name, StringComparison.OrdinalIgnoreCase)))
            {
                audit.Record(push.NodeId, $"profile.apply:{stored.Name}@{stored.Revision}", ActorOf(context), DateTimeOffset.UtcNow);
            }

            logger.LogInformation(
                "Profile '{Profile}' revision {Revision} written; {Matched} of {Nodes} connected node(s) matched",
                stored.Name,
                stored.Revision,
                pushes.Count(p => p.Profile is not null),
                pushes.Count);

            return Results.Ok(new
            {
                profile = stored,
                applied = pushes.Where(p => p.Profile is not null).Select(p => p.NodeId).ToArray(),
                conflicts = pushes
                    .Where(p => p.Conflicts is not null)
                    .Select(p => new { nodeId = p.NodeId, profiles = p.Conflicts })
                    .ToArray()
            });
        });

        group.MapDelete("/profiles/{name}", async (
            string name,
            IProfileRegistry profiles,
            NodeProfileCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!profiles.Delete(name))
            {
                return Results.NotFound(new { error = $"profile '{name}' not found" });
            }

            // Re-assert rather than just forgetting it: every node it used to match has to be told
            // to revert to its own configuration, or it stays narrowed with nothing explaining why.
            var pushes = await coordinator.ReassertAsync(cancellationToken);

            return Results.Ok(new
            {
                deleted = name,
                reverted = pushes.Where(p => p.Profile is null && p.Conflicts is null).Select(p => p.NodeId).ToArray()
            });
        });

        // What one node is actually running, and what it would not do. The question an operator
        // asks after writing a profile and finding a box still serving what it served before.
        group.MapGet("/nodes/{nodeId}/profile", (
            string nodeId,
            INodeRegistry registry,
            IProfileRegistry profiles) =>
        {
            var node = registry.Snapshot(DateTimeOffset.UtcNow)
                .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

            if (node is null)
            {
                return Results.NotFound(new { error = $"node '{nodeId}' not found" });
            }

            var assignment = profiles.MatchFor(node.NodeId, node.Labels);
            var state = profiles.StateOf(node.NodeId);

            return Results.Ok(new
            {
                nodeId = node.NodeId,
                name = node.Name,
                assigned = assignment.Profile?.Name,
                revision = assignment.Profile?.Revision,
                conflicts = assignment.Conflicts,
                status = assignment.IsConflict ? "conflict" : state?.Status() ?? "none",
                effective = new
                {
                    capabilities = (node.Capabilities ?? []).Select(c => c.Kind).ToArray(),
                    maxConcurrency = node.MaxConcurrency
                },
                applied = state?.Applied ?? Array.Empty<string>(),
                refusals = state?.Refusals ?? Array.Empty<NodeProfileRefusal>(),
                pending = state?.Pending ?? Array.Empty<string>(),
                reportedAtUtc = state?.AtUtc
            });
        });

        group.MapGet("/stream", StreamAsync);

        // Usage accounting (phase 25). Aggregates only — the ledger holds counts, never text,
        // so this endpoint could not leak a prompt even if asked nicely.
        group.MapGet("/usage", async (
            IUsageLedger ledger,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? clientId,
            string? model,
            CancellationToken cancellationToken) =>
        {
            var rows = await ledger.QueryAsync(new UsageQuery(from, to, clientId, model), cancellationToken);

            return Results.Ok(new
            {
                from,
                to,
                rows = rows.Select(r => new
                {
                    clientId = r.ClientId,
                    model = r.Model,
                    requests = r.Requests,
                    promptTokens = r.PromptTokens,
                    completionTokens = r.CompletionTokens,
                    totalTokens = r.TotalTokens,
                    fallbackRequests = r.FallbackRequests
                })
            });
        });

        // Configured clients with live window consumption. Ids and limits — never keys.
        group.MapGet("/clients", (IClientRegistry clients, AdmissionControl admission) =>
        {
            var rows = clients.NamedClients
                .Where(client => !string.IsNullOrWhiteSpace(client.Id))
                .Select(client =>
                {
                    var live = admission.LiveUsageOf(client.Id);
                    return new
                    {
                        id = client.Id,
                        limits = client.Limits is { } limits
                            ? new
                            {
                                maxConcurrent = limits.MaxConcurrent,
                                requestsPerMinute = limits.RequestsPerMinute,
                                tokensPerMinute = limits.TokensPerMinute,
                                tokensPerDay = limits.TokensPerDay,
                                allowedModels = limits.AllowedModels
                            }
                            : null,
                        live = new
                        {
                            inFlight = live.InFlight,
                            requestsLastMinute = live.RequestsLastMinute,
                            tokensLastMinute = live.TokensLastMinute,
                            tokensToday = live.TokensToday
                        }
                    };
                })
                .OrderBy(row => row.id, StringComparer.OrdinalIgnoreCase);

            return Results.Ok(rows);
        });

        return app;
    }

    private static async Task<IResult> RunModelCommandAsync(
        string kind,
        string nodeId,
        string model,
        HttpContext context,
        INodeRegistry registry,
        ModelCommandCoordinator commands,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");
        model = (model ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.BadRequest(new { error = "a model name is required" });
        }

        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' not found" });
        }

        // A backend that cannot manage models refuses cleanly here, not with a 500 from the node.
        if (!node.SupportsModelManagement)
        {
            return Results.BadRequest(new
            {
                error = $"node '{nodeId}' runs a backend that cannot manage models"
            });
        }

        var result = await commands.SendAsync(node.NodeId, kind, model, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' is no longer connected" });
        }

        audit.Record(node.NodeId, $"model.{kind}", ActorOf(context), DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Model command {Kind} '{Model}' on node {NodeId} → command {CommandId} (reused={Reused})",
            kind, model, node.NodeId, result.CommandId, result.Reused);

        return Results.Accepted($"/api/admin/nodes/{node.NodeId}/models", new
        {
            nodeId = node.NodeId,
            model,
            kind,
            commandId = result.CommandId,
            reused = result.Reused
        });
    }

    private static async Task<IResult> EnsureModelAsync(
        string model,
        int replicas,
        HttpContext context,
        INodeRegistry registry,
        ModelCommandCoordinator commands,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");
        model = (model ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.BadRequest(new { error = "a model name is required" });
        }

        if (replicas < 1)
        {
            return Results.BadRequest(new { error = "replicas must be >= 1" });
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = registry.Snapshot(now);
        var holders = registry.FindNodesWithModel(model); // non-cordoned nodes already holding it
        var holderConns = holders.Select(h => h.ConnectionId).ToHashSet(StringComparer.Ordinal);
        var byConn = snapshot.ToDictionary(n => n.ConnectionId, StringComparer.Ordinal);

        var plan = ModelPlacement.Choose(snapshot, holderConns, replicas);

        var pulling = new List<object>();
        foreach (var connId in plan.PullConnectionIds)
        {
            if (!byConn.TryGetValue(connId, out var node)) continue;
            var result = await commands.SendAsync(node.NodeId, ModelCommand.KindPull, model, cancellationToken);
            if (result is null) continue;
            audit.Record(node.NodeId, "model.pull", ActorOf(context), now);
            pulling.Add(new { nodeId = node.NodeId, name = node.Name, commandId = result.CommandId, reused = result.Reused });
        }

        var cordonedHolders = snapshot.Where(n => n.Cordoned).Select(n => n.NodeId).ToArray();

        logger.LogInformation(
            "Ensure '{Model}' replicas={Replicas}: {Holders} already present, pulling onto {Pulling}, satisfied={Satisfied}",
            model, replicas, holders.Count, pulling.Count, plan.Satisfied);

        return Results.Ok(new
        {
            model,
            requestedReplicas = replicas,
            alreadyPresent = holders.Select(h => h.NodeId).ToArray(),
            pulling,
            satisfied = plan.Satisfied,
            // The "why": what was and wasn't eligible, so an operator can trust the decision.
            decision = new
            {
                effectiveTarget = plan.EffectiveTarget,
                nonManageableHolders = plan.NonManageableHolders,
                eligibleCandidates = plan.EligibleCandidates,
                cordonedNodesSkipped = cordonedHolders,
                shortfall = plan.Shortfall,
                note = plan.Satisfied
                    ? "target met (already-present holders plus new pulls cover the requested replicas)"
                    : "not enough eligible manageable nodes to reach the requested replica count"
            }
        });
    }

    private static async Task StreamAsync(
        HttpContext context,
        INodeRegistry registry,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin.Stream");
        var vectorEvents = context.RequestServices.GetService<VectorEvents>();
        var commands = context.RequestServices.GetService<ModelCommandCoordinator>();
        var profiles = context.RequestServices.GetService<IProfileRegistry>();

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.Body.FlushAsync(cancellationToken);

        // signal: 0 = snapshot due (fleet change), 1 = vector event ready.
        var signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var vectorQueue = Channel.CreateBounded<VectorEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var modelQueue = Channel.CreateBounded<ModelCommandProgress>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        void OnChanged() => signal.Writer.TryWrite(0);
        void OnVector(VectorEvent ev)
        {
            if (vectorQueue.Writer.TryWrite(ev)) signal.Writer.TryWrite(1);
        }
        void OnModelProgress(ModelCommandProgress progress)
        {
            if (modelQueue.Writer.TryWrite(progress)) signal.Writer.TryWrite(2);
        }

        registry.Changed += OnChanged;
        IDisposable? vectorSub = vectorEvents?.Subscribe(OnVector);
        if (commands is not null) commands.ProgressReceived += OnModelProgress;

        try
        {
            await WriteSnapshotAsync(context.Response, registry, audit, profiles, cancellationToken);

            var keepalive = TimeSpan.FromSeconds(10);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(keepalive);

                try
                {
                    await signal.Reader.ReadAsync(timeoutCts.Token);
                    var needSnapshot = false;
                    while (signal.Reader.TryRead(out var kind))
                    {
                        if (kind == 0) needSnapshot = true;
                    }
                    // Drain any queued vector events first — order-preserving within the queue.
                    while (vectorQueue.Reader.TryRead(out var ev))
                    {
                        await WriteVectorEventAsync(context.Response, ev, cancellationToken);
                    }
                    // Then model-command progress frames, likewise order-preserving.
                    while (modelQueue.Reader.TryRead(out var progress))
                    {
                        await WriteModelProgressAsync(context.Response, progress, cancellationToken);
                    }
                    // A fleet change (or the very first wake) always warrants a fresh snapshot.
                    if (needSnapshot)
                    {
                        await WriteSnapshotAsync(context.Response, registry, audit, profiles, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Idle keepalive — refresh ages/in-flight counts even when nothing happened.
                    await WriteSnapshotAsync(context.Response, registry, audit, profiles, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Admin stream ended");
        }
        finally
        {
            registry.Changed -= OnChanged;
            vectorSub?.Dispose();
            if (commands is not null) commands.ProgressReceived -= OnModelProgress;
        }
    }

    private static async Task WriteModelProgressAsync(
        HttpResponse response,
        ModelCommandProgress progress,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(progress, StreamJsonOptions);
        await response.WriteAsync("event: model-progress\n", cancellationToken);
        await response.WriteAsync($"data: {payload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteSnapshotAsync(
        HttpResponse response,
        INodeRegistry registry,
        IAuditLog audit,
        IProfileRegistry? profiles,
        CancellationToken cancellationToken)
    {
        var nodes = BuildAdminNodes(registry, audit, profiles);
        var payload = JsonSerializer.Serialize(new { nodes }, StreamJsonOptions);
        await response.WriteAsync("event: snapshot\n", cancellationToken);
        await response.WriteAsync($"data: {payload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteVectorEventAsync(
        HttpResponse response,
        VectorEvent ev,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sequence = ev.Sequence,
            kind = ev.Kind,
            collection = ev.Collection,
            atUtc = ev.AtUtc,
            data = ev.Data
        }, StreamJsonOptions);
        await response.WriteAsync($"event: {ev.Kind}\n", cancellationToken);
        await response.WriteAsync($"data: {payload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static AdminNode[] BuildAdminNodes(
        INodeRegistry registry,
        IAuditLog audit,
        IProfileRegistry? profiles = null)
    {
        return registry.Snapshot(DateTimeOffset.UtcNow)
            .Select(node => AdminNode.From(
                node,
                audit.Get(node.NodeId),
                ProfileBlock(profiles, node)))
            .ToArray();
    }

    /// <summary>
    /// What the console needs to render a node's profile in one column: which one, which revision,
    /// and whether it took (phase 43). Null when profiles are not in play at all, so a deployment
    /// that never writes one sees no new key.
    /// </summary>
    private static AdminNodeProfile? ProfileBlock(IProfileRegistry? profiles, Services.NodeSnapshot node)
    {
        if (profiles is null)
        {
            return null;
        }

        var assignment = profiles.MatchFor(node.NodeId, node.Labels);
        var state = profiles.StateOf(node.NodeId);

        if (assignment.Profile is null && !assignment.IsConflict && state?.ProfileName is null)
        {
            return null;
        }

        return new AdminNodeProfile(
            assignment.Profile?.Name ?? state?.ProfileName,
            assignment.Profile?.Revision ?? state?.Revision ?? 0,
            assignment.IsConflict ? "conflict" : state?.Status() ?? "pending",
            assignment.Conflicts,
            state?.Refusals ?? Array.Empty<NodeProfileRefusal>());
    }

    private static string ActorOf(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "admin";
        }

        return System.Net.IPAddress.IsLoopback(ip) ? "local" : ip.ToString();
    }

    private sealed record AdminNode(
        string ConnectionId,
        string NodeId,
        string Name,
        string OllamaEndpoint,
        string Version,
        DateTimeOffset LastSeenUtc,
        double AgeSeconds,
        int InFlight,
        int LocalInFlight,
        int ModelCount,
        IReadOnlyDictionary<string, string> Labels,
        int? MaxConcurrency,
        bool Cordoned,
        bool SupportsModelManagement,
        // Resolved capabilities (phase 40) — what this node will actually be routed for, which is
        // the question an operator staring at a node that is "up but idle" is really asking.
        IReadOnlyList<string> Capabilities,
        AdminNodeAction? LastAction,
        // Null unless a profile applies to this node (phase 43), so a fleet that defines none sees
        // exactly the v3.10 payload.
        AdminNodeProfile? Profile = null)
    {
        public static AdminNode From(NodeSnapshot node, AuditEntry? lastAction, AdminNodeProfile? profile = null)
        {
            return new AdminNode(
                node.ConnectionId,
                node.NodeId,
                node.Name,
                node.OllamaEndpoint,
                node.Version,
                node.LastSeenUtc,
                node.AgeSeconds,
                node.InFlight,
                node.LocalInFlight,
                node.ModelCount,
                node.Labels,
                node.MaxConcurrency,
                node.Cordoned,
                node.SupportsModelManagement,
                (node.Capabilities ?? []).Select(capability => capability.Kind).ToArray(),
                lastAction is null
                    ? null
                    : new AdminNodeAction(lastAction.Action, lastAction.AtUtc, lastAction.By),
                profile);
        }
    }

    private sealed record AdminNodeAction(string Action, DateTimeOffset AtUtc, string By);

    private sealed record AdminNodeProfile(
        string? Name,
        long Revision,
        string Status,
        IReadOnlyList<string>? Conflicts,
        IReadOnlyList<NodeProfileRefusal> Refusals);
}
