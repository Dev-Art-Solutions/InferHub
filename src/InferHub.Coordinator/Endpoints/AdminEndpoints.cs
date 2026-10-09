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
            NodeCorpusRegistry corpora,
            NodeToolRegistry tools,
            NodeBackendRegistry backends,
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
            // Same for the two phase-44/45 mailboxes: what a node that is gone for good last said
            // about its corpus and its tools is not something the hub should keep answering with.
            corpora.Forget(nodeId);
            tools.Forget(nodeId);
            backends.Forget(nodeId);
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

        // Phase 98. A Hugging Face link, downloaded by the node into the engine that can serve it.
        // The phase-26 pull with engine "huggingface": same progress, same coalescing.
        group.MapPost("/nodes/{nodeId}/huggingface", async (
            string nodeId, HuggingFacePullRequest body, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (!InferHub.Shared.HuggingFace.HfReference.TryParse(body?.Url, body?.Quant, out var reference, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var node = registry.Snapshot(DateTimeOffset.UtcNow)
                .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

            if (node is null)
            {
                return Results.NotFound(new { error = $"node '{nodeId}' not found" });
            }

            var model = reference!.ToString();
            var result = await commands.SendAsync(node.NodeId, ModelCommand.KindPull, model, cancellationToken, engine: ModelCommand.EngineHuggingFace);

            if (result is null)
            {
                return Results.NotFound(new { error = $"node '{nodeId}' is no longer connected" });
            }

            audit.Record(node.NodeId, "model.pull.huggingface", ActorOf(context), DateTimeOffset.UtcNow);
            loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin").LogInformation(
                "Hugging Face pull '{Model}' on node {NodeId} → command {CommandId} (reused={Reused})",
                model, node.NodeId, result.CommandId, result.Reused);

            return Results.Accepted($"/api/admin/nodes/{node.NodeId}/models", new
            {
                nodeId = node.NodeId,
                model,
                kind = ModelCommand.KindPull,
                engine = ModelCommand.EngineHuggingFace,
                commandId = result.CommandId,
                reused = result.Reused
            });
        });

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

        // Phase 96 (D3): warm's opposite — a llama.cpp router's /models/unload, Ollama's keep_alive 0.
        group.MapPost("/nodes/{nodeId}/models/{model}/unload", (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            RunModelCommandAsync(ModelCommand.KindUnload, nodeId, model, context, registry, commands, audit, loggerFactory, cancellationToken));

        // Tool models (phase 48, D4). The SAME channel, the same coalescing, the same SSE relay —
        // what changes is that the command names a tool, so the node runs it against that tool's
        // catalogue instead of its inference backend. Weights measured in tens of gigabytes take
        // longer to fetch than any request timeout should tolerate, which is why this is an operator
        // action and not something a generation request does on the caller's behalf.
        group.MapPost("/nodes/{nodeId}/tools/{tool}/models/{model}/pull", (
            string nodeId, string tool, string model, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            RunToolModelCommandAsync(ModelCommand.KindPull, nodeId, tool, model, context, registry, commands, audit, loggerFactory, cancellationToken));

        group.MapDelete("/nodes/{nodeId}/tools/{tool}/models/{model}", (
            string nodeId, string tool, string model, HttpContext context,
            INodeRegistry registry, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            RunToolModelCommandAsync(ModelCommand.KindDelete, nodeId, tool, model, context, registry, commands, audit, loggerFactory, cancellationToken));

        // Fleet-wide model matrix (phase 26): model × node, with sizes and which nodes hold each.
        // The view that makes the whole feature make sense.
        group.MapGet("/models", (INodeRegistry registry, IProfileRegistry profiles) =>
        {
            var inventory = registry.ModelInventory();

            // Phase 74. A model a profile disabled is still held (and shown as such — the operator
            // needs to see it to re-enable it) but is no longer routed to on that node. Sourced from
            // each node's currently-*assigned* profile rather than a state report, same as every
            // other "effective" field this endpoint already computes from the profile book.
            var disabledByNode = registry.Snapshot(DateTimeOffset.UtcNow)
                .ToDictionary(
                    n => n.NodeId,
                    n => (IReadOnlySet<string>)new HashSet<string>(
                        profiles.MatchFor(n.NodeId, n.Labels).Profile?.Models?.Disabled ?? Array.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            IReadOnlySet<string> DisabledOn(string nodeId) =>
                disabledByNode.TryGetValue(nodeId, out var set) ? set : (IReadOnlySet<string>)Array.Empty<string>().ToHashSet();

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
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    disabledOn = g.Select(x => x.NodeId).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(nodeId => DisabledOn(nodeId).Contains(g.Key))
                        .OrderBy(nodeId => nodeId, StringComparer.OrdinalIgnoreCase).ToArray()
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

        // Per-(node, model) routing toggle and per-node vector-collection assignment (phase 74).
        // Both are ergonomic wrappers around the profile mechanism above: they read-modify-write
        // whichever named profile already matches the node (creating a fresh `node:{id}` one if
        // none does) rather than asking the operator to hand-edit the raw profile JSON for a single
        // model or collection. Every write still goes through IProfileRegistry.Put +
        // NodeProfileCoordinator.ReassertAsync, so CollectionOwnership stays derived, never a second
        // source of truth.
        group.MapPost("/nodes/{nodeId}/models/{model}/enable", async (
            string nodeId, string model, bool? force, HttpContext context,
            INodeRegistry registry, NodeModelToggle toggle, CancellationToken cancellationToken) =>
            await SetModelEnabledAsync(nodeId, model, enabled: true, force ?? false, context, registry, toggle, cancellationToken));

        group.MapPost("/nodes/{nodeId}/models/{model}/disable", async (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, NodeModelToggle toggle, CancellationToken cancellationToken) =>
            await SetModelEnabledAsync(nodeId, model, enabled: false, force: true, context, registry, toggle, cancellationToken));

        // Phase 95. Start or stop one engine on a node that runs several — the same profile
        // read-modify-write as the two above, so the engine stays stopped across a reboot of either
        // side and the node's clamp (Backend:Engines is the grant) stays the authority.
        group.MapPost("/nodes/{nodeId}/backends/{backend}/start", async (
            string nodeId, string backend, HttpContext context,
            INodeRegistry registry, NodeBackendToggle toggle, CancellationToken cancellationToken) =>
            await SetBackendRunningAsync(nodeId, backend, running: true, context, registry, toggle, cancellationToken));

        group.MapPost("/nodes/{nodeId}/backends/{backend}/stop", async (
            string nodeId, string backend, HttpContext context,
            INodeRegistry registry, NodeBackendToggle toggle, CancellationToken cancellationToken) =>
            await SetBackendRunningAsync(nodeId, backend, running: false, context, registry, toggle, cancellationToken));

        // Phase 97. Pick which colibri catalogue models stay loaded, and switch on-demand — the same
        // profile read-modify-write, so a pinned model is loaded again after a reboot of either side.
        group.MapPost("/nodes/{nodeId}/colibri/models/{model}/load", async (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, NodeColibriToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetLoadedAsync(node, model, loaded: true, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/colibri/models/{model}/unload", async (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, NodeColibriToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetLoadedAsync(node, model, loaded: false, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/colibri/on-demand/enable", async (
            string nodeId, HttpContext context,
            INodeRegistry registry, NodeColibriToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetOnDemandAsync(node, onDemand: true, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/colibri/on-demand/disable", async (
            string nodeId, HttpContext context,
            INodeRegistry registry, NodeColibriToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetOnDemandAsync(node, onDemand: false, by, cancellationToken)));

        // Phase 99. The same four for a Strata catalogue, and an install: a model from the node's own
        // `installable` list (or a link to one of Strata's repos), sent as the phase-98 Hugging Face pull.
        group.MapPost("/nodes/{nodeId}/strata/models/{model}/load", async (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, NodeStrataToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetLoadedAsync(node, model, loaded: true, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/strata/models/{model}/unload", async (
            string nodeId, string model, HttpContext context,
            INodeRegistry registry, NodeStrataToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetLoadedAsync(node, model, loaded: false, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/strata/on-demand/enable", async (
            string nodeId, HttpContext context,
            INodeRegistry registry, NodeStrataToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetOnDemandAsync(node, onDemand: true, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/strata/on-demand/disable", async (
            string nodeId, HttpContext context,
            INodeRegistry registry, NodeStrataToggle toggle, CancellationToken cancellationToken) =>
            await SetCatalogAsync(nodeId, context, registry, (node, by) => toggle.SetOnDemandAsync(node, onDemand: false, by, cancellationToken)));

        group.MapPost("/nodes/{nodeId}/strata/install", async (
            string nodeId, StrataInstallRequest body, HttpContext context,
            INodeRegistry registry, NodeStrataRegistry strata, ModelCommandCoordinator commands, IAuditLog audit,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            await InstallStrataAsync(nodeId, body, context, registry, strata, commands, audit, loggerFactory, cancellationToken));

        group.MapPost("/nodes/{nodeId}/collections/{collection}/assign", async (
            string nodeId, string collection, HttpContext context,
            INodeRegistry registry, IProfileRegistry profiles, NodeProfileCoordinator coordinator,
            CollectionOwnership ownership, IAuditLog audit, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            await SetCollectionAssignedAsync(nodeId, collection, assigned: true, context, registry, profiles, coordinator, ownership, audit, loggerFactory, cancellationToken));

        group.MapPost("/nodes/{nodeId}/collections/{collection}/unassign", async (
            string nodeId, string collection, HttpContext context,
            INodeRegistry registry, IProfileRegistry profiles, NodeProfileCoordinator coordinator,
            CollectionOwnership ownership, IAuditLog audit, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            await SetCollectionAssignedAsync(nodeId, collection, assigned: false, context, registry, profiles, coordinator, ownership, audit, loggerFactory, cancellationToken));

        // Node-corpus replication (phase 77): a standby for a node-owned collection, so the
        // collection survives its owning node's permanent loss. The hub relays snapshot+tail traffic
        // between the two nodes and never stores a copy itself (NodeCorpusReplicator's own D1).
        group.MapPost("/collections/{collection}/standby/{standbyNodeId}", async (
            string collection, string standbyNodeId, HttpContext context,
            NodeCorpusReplicator replicator, CancellationToken cancellationToken) =>
        {
            collection = (collection ?? string.Empty).Trim();
            standbyNodeId = (standbyNodeId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(standbyNodeId))
            {
                return Results.BadRequest(new { error = "a collection name and a standby node id are required" });
            }

            var (ok, error) = await replicator.AssignStandbyAsync(collection, standbyNodeId, ActorOf(context), cancellationToken);

            return ok
                ? Results.Ok(new { collection, standbyNodeId })
                : Results.Conflict(new { error });
        });

        group.MapDelete("/collections/{collection}/standby", (
            string collection, HttpContext context, NodeCorpusReplicator replicator) =>
        {
            collection = (collection ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(collection))
            {
                return Results.BadRequest(new { error = "a collection name is required" });
            }

            replicator.ClearStandby(collection, ActorOf(context));
            return Results.Ok(new { collection, standbyNodeId = (string?)null });
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

        // A Hugging Face repo is owner/repo (96 D3), and a '/' in a route segment arrives as %2F —
        // routing leaves that one escape alone. A model name never contains a literal '%'.
        model = Uri.UnescapeDataString(model ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.BadRequest(new { error = "a model name is required" });
        }

        // 96 D3: which engine on a Backend:Engines node. A token, as an engine name is (95 D4).
        string? engine = context.Request.Query["engine"].ToString().Trim();

        if (engine.Length == 0)
        {
            engine = null;
        }
        else if (!System.Text.RegularExpressions.Regex.IsMatch(engine, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$"))
        {
            return Results.BadRequest(new { error = "engine is a node's engine name: letters, digits, '.', '_' and '-'" });
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

        var result = await commands.SendAsync(node.NodeId, kind, model, cancellationToken, engine: engine);
        if (result is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' is no longer connected" });
        }

        audit.Record(node.NodeId, $"model.{kind}", ActorOf(context), DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Model command {Kind} '{Model}' on node {NodeId}{Engine} → command {CommandId} (reused={Reused})",
            kind, model, node.NodeId, engine is null ? "" : $" engine {engine}", result.CommandId, result.Reused);

        return Results.Accepted($"/api/admin/nodes/{node.NodeId}/models", new
        {
            nodeId = node.NodeId,
            model,
            kind,
            engine,
            commandId = result.CommandId,
            reused = result.Reused
        });
    }

    /// <summary>
    /// Pull or delete a <em>tool's</em> model on one node (phase 48, D4).
    /// </summary>
    /// <remarks>
    /// It deliberately does <b>not</b> check <c>SupportsModelManagement</c>: that flag is about the
    /// node's inference backend (phase-26 D3), and an OpenAI-backed node reports false while running
    /// a diffusion tool it manages perfectly well. Whether the tool exists and is allowed is the
    /// node's own answer, and it arrives as a terminal error frame naming the tool — the same shape
    /// a backend that cannot manage models already gives.
    /// </remarks>
    private static async Task<IResult> RunToolModelCommandAsync(
        string kind,
        string nodeId,
        string tool,
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
        tool = (tool ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(tool))
        {
            return Results.BadRequest(new { error = "a tool id and a model name are both required" });
        }

        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' not found" });
        }

        var result = await commands.SendAsync(node.NodeId, kind, model, cancellationToken, tool);

        if (result is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' is no longer connected" });
        }

        audit.Record(node.NodeId, $"tool.{tool}.model.{kind}", ActorOf(context), DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Tool model command {Kind} '{Model}' on tool '{Tool}' at node {NodeId} → command {CommandId} (reused={Reused})",
            kind, model, tool, node.NodeId, result.CommandId, result.Reused);

        return Results.Accepted($"/api/admin/nodes/{node.NodeId}/tools/{tool}/models", new
        {
            nodeId = node.NodeId,
            tool,
            model,
            kind,
            commandId = result.CommandId,
            reused = result.Reused
        });
    }

    /// <summary>
    /// Which named profile a per-node admin action should patch: whatever already matches the
    /// node, or a fresh <c>node:{id}</c> skeleton if none does. Mirrors <see cref="NodeProfileCoordinator.ReassertAsync"/>'s
    /// own match so a conflicted node is refused here exactly as it is refused a push.
    /// </summary>
    private static (IResult? Error, string Name, NodeProfile Profile) ResolveProfileForNode(
        NodeSnapshot node,
        IProfileRegistry profiles)
    {
        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return (Results.Conflict(new
            {
                error = $"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles ({string.Join(", ", assignment.Conflicts)}); resolve the conflict in the raw profile editor before toggling a model or collection here"
            }), string.Empty, null!);
        }

        if (assignment.Profile is { } existing)
        {
            return (null, existing.Name, existing);
        }

        var name = $"node:{node.NodeId}";
        return (null, name, new NodeProfile(name, 0, new NodeProfileSelector(NodeId: node.NodeId)));
    }

    /// <summary>
    /// Enables or disables one model on one node. Thin HTTP wrapper (phase 76) around
    /// <see cref="NodeModelToggle"/>, which does the profile read-modify-write and VRAM precheck —
    /// the same path the auto-scaler calls, so a human's <c>force=true</c> override and the
    /// scaler's decision can never disagree about what "fits" means.
    /// </summary>
    private static async Task<IResult> SetModelEnabledAsync(
        string nodeId,
        string model,
        bool enabled,
        bool force,
        HttpContext context,
        INodeRegistry registry,
        NodeModelToggle toggle,
        CancellationToken cancellationToken)
    {
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

        var outcome = await toggle.SetEnabledAsync(node, model, enabled, force, ActorOf(context), cancellationToken);

        if (outcome.Conflict)
        {
            return Results.Conflict(new { error = outcome.ConflictError });
        }

        if (!outcome.Applied)
        {
            var precheck = outcome.Precheck!;
            return Results.Conflict(new
            {
                error = precheck.Reason,
                nodeId = node.NodeId,
                model,
                precheck = new
                {
                    ok = false,
                    estimatedMiB = precheck.EstimatedMiB,
                    estimate = precheck.IsEstimate,
                    budgetMiB = precheck.BudgetMiB,
                    reserveMiB = precheck.ReserveMiB
                },
                hint = "retry with ?force=true to enable anyway"
            });
        }

        return Results.Ok(new
        {
            nodeId = node.NodeId,
            model,
            enabled,
            profile = outcome.Profile,
            precheck = outcome.Precheck is null
                ? null
                : new
                {
                    ok = outcome.Precheck.Ok,
                    overridden = !outcome.Precheck.Ok && force,
                    estimatedMiB = outcome.Precheck.EstimatedMiB,
                    estimate = outcome.Precheck.IsEstimate,
                    budgetMiB = outcome.Precheck.BudgetMiB,
                    reserveMiB = outcome.Precheck.ReserveMiB,
                    reason = outcome.Precheck.Reason
                }
        });
    }

    /// <summary>
    /// Assigns or releases one vector collection on one node (phase 74), through the same profile
    /// read-modify-write as the model toggle. Never touches Provider/Url/CredentialRef/EmbeddingModel
    /// — those stay a raw-profile-editor decision, since a structured per-collection control has no
    /// good UI for picking a vector engine.
    /// </summary>
    private static async Task<IResult> SetCollectionAssignedAsync(
        string nodeId,
        string collection,
        bool assigned,
        HttpContext context,
        INodeRegistry registry,
        IProfileRegistry profiles,
        NodeProfileCoordinator coordinator,
        CollectionOwnership ownership,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        collection = (collection ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(collection))
        {
            return Results.BadRequest(new { error = "a collection name is required" });
        }

        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' not found" });
        }

        if (assigned)
        {
            var currentOwner = ownership.OwnerOfCollection(collection);
            var thisNodeOwner = $"node:{node.NodeId}";

            if (!string.Equals(currentOwner, CollectionOwnership.Hub, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentOwner, thisNodeOwner, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = ownership.RefusalFor(collection) });
            }
        }

        var (error, name, existing) = ResolveProfileForNode(node, profiles);

        if (error is not null)
        {
            return error;
        }

        var currentCollections = (existing.Retrieval?.Collections ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Where(c => !string.Equals(c, collection, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (assigned)
        {
            currentCollections.Add(collection);
        }

        var updatedRetrieval = existing.Retrieval is { } retrieval
            ? retrieval with { Enabled = true, Collections = currentCollections }
            : new RetrievalProfile(Enabled: true, Collections: currentCollections);

        var updated = existing with { Retrieval = updatedRetrieval };
        var stored = profiles.Put(name, updated);
        await coordinator.ReassertAsync(cancellationToken);

        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin");
        audit.Record(node.NodeId, $"collection.{(assigned ? "assign" : "unassign")}:{collection}", ActorOf(context), DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Collection '{Collection}' {State} on node {NodeId} via profile '{Profile}' revision {Revision}",
            collection, assigned ? "assigned" : "unassigned", node.NodeId, stored.Name, stored.Revision);

        return Results.Ok(new
        {
            nodeId = node.NodeId,
            collection,
            assigned,
            profile = stored,
            owner = ownership.OwnerOfCollection(collection)
        });
    }

    /// <summary>
    /// Starts or stops one engine on one node (phase 95). Thin HTTP wrapper around
    /// <see cref="NodeBackendToggle"/>, which does the profile read-modify-write.
    /// </summary>
    private static async Task<IResult> SetBackendRunningAsync(
        string nodeId,
        string backend,
        bool running,
        HttpContext context,
        INodeRegistry registry,
        NodeBackendToggle toggle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return Results.BadRequest(new { error = "an engine name is required" });
        }

        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' not found" });
        }

        var outcome = await toggle.SetRunningAsync(node, backend, running, ActorOf(context), cancellationToken);

        return outcome.Refusal switch
        {
            null => Results.Ok(new { nodeId = node.NodeId, backend = outcome.Engine, running, profile = outcome.Profile }),
            BackendToggleOutcome.UnknownEngine => Results.NotFound(new { error = outcome.Error }),
            _ => Results.Conflict(new { error = outcome.Error })
        };
    }

    /// <summary>
    /// Phase 99 D4. A name from the node's own <c>installable</c> list is its link; anything else must be
    /// a link to one of Strata's repos. Either way it travels as the phase-98 pull with engine
    /// <c>huggingface</c>, and the node hands a Strata repo to Strata's setup.
    /// </summary>
    private static async Task<IResult> InstallStrataAsync(
        string nodeId,
        StrataInstallRequest body,
        HttpContext context,
        INodeRegistry registry,
        NodeStrataRegistry strata,
        ModelCommandCoordinator commands,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' not found" });
        }

        if (strata.Of(node.NodeId) is not { } state)
        {
            return Results.Conflict(new { error = $"node '{node.NodeId}' reports no Strata catalogue: it runs no strata backend or engine with Strata:Root, or a release before v3.64." });
        }

        if (state.Installable is null)
        {
            return Results.Conflict(new { error = $"node '{node.NodeId}' does not install from Hugging Face; set HuggingFace:Enabled=true on it." });
        }

        var wanted = (body?.Model ?? string.Empty).Trim();
        var link = state.Installable.FirstOrDefault(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase))?.Link ?? wanted;

        if (!InferHub.Shared.HuggingFace.HfReference.TryParse(link, null, out var reference, out var error))
        {
            return Results.BadRequest(new { error = $"'{wanted}' is neither a model this node can install ({string.Join(", ", state.Installable.Select(i => i.Name))}) nor a link: {error}" });
        }

        if (!InferHub.Shared.Strata.StrataModels.TryMatch(reference!, out _, out _, out error))
        {
            return Results.BadRequest(new { error });
        }

        // Phase 100: setup's --vision for this install; omitted, the node's Strata:Install:Vision decides.
        var vision = string.IsNullOrWhiteSpace(body?.Vision) ? null : body.Vision.Trim().ToLowerInvariant();

        if (!ModelCommand.IsKnownVision(vision))
        {
            return Results.BadRequest(new { error = $"vision '{body!.Vision}' is not setup.py's --vision: yes (the image encoder on the GPU), cpu, or no" });
        }

        var model = reference!.ToString();
        var result = await commands.SendAsync(node.NodeId, ModelCommand.KindPull, model, cancellationToken, engine: ModelCommand.EngineHuggingFace, vision: vision);

        if (result is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' is no longer connected" });
        }

        audit.Record(node.NodeId, "model.install.strata", ActorOf(context), DateTimeOffset.UtcNow);
        loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin").LogInformation(
            "Strata install '{Model}' on node {NodeId} (vision {Vision}) -> command {CommandId} (reused={Reused})",
            model, node.NodeId, vision ?? "node default", result.CommandId, result.Reused);

        return Results.Accepted($"/api/admin/nodes/{node.NodeId}/models", new
        {
            nodeId = node.NodeId,
            model,
            kind = ModelCommand.KindPull,
            engine = ModelCommand.EngineHuggingFace,
            vision,
            commandId = result.CommandId,
            reused = result.Reused
        });
    }

    /// <summary>Phase 97 (99: either catalogue). Thin HTTP wrapper around a <see cref="NodeCatalogToggle"/>.</summary>
    private static async Task<IResult> SetCatalogAsync(
        string nodeId,
        HttpContext context,
        INodeRegistry registry,
        Func<NodeSnapshot, string, Task<CatalogToggleOutcome>> toggle)
    {
        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return Results.NotFound(new { error = $"node '{nodeId}' not found" });
        }

        var outcome = await toggle(node, ActorOf(context));

        return outcome.Refusal switch
        {
            null => Results.Ok(new { nodeId = node.NodeId, colibri = outcome.Profile?.Colibri, strata = outcome.Profile?.Strata, profile = outcome.Profile }),
            CatalogToggleOutcome.UnknownModel => Results.NotFound(new { error = outcome.Error }),
            _ => Results.Conflict(new { error = outcome.Error })
        };
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
        // Possession, not serviceability (69 D2): a sick node still HAS the model on disk, and
        // counting it as absent would pull twenty gigabytes onto another box to replace one that
        // is already there and will be back in a minute.
        var holders = registry.FindNodesWithModel(model, includeUnserviceable: true);
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

    /// <summary>
    /// Phase 99: <c>POST /api/admin/nodes/{id}/strata/install</c> — a name from the node's <c>installable</c>, or a link.
    /// Phase 100: <c>vision</c> — <c>yes</c>, <c>cpu</c> or <c>no</c>; <c>yes</c> on a size installed without pictures adds them.
    /// </summary>
    internal sealed record StrataInstallRequest(string? Model, string? Vision = null);

    /// <summary>Phase 98: <c>POST /api/admin/nodes/{id}/huggingface</c>. The quant is a separate field so a form can carry it.</summary>
    internal sealed record HuggingFacePullRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("quant")] string? Quant = null);
}
