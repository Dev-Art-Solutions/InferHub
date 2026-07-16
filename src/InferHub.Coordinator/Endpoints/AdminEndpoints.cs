using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;

namespace InferHub.Coordinator.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/nodes", (INodeRegistry registry, IAuditLog audit) =>
        {
            var nodes = BuildAdminNodes(registry, audit);
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
            IAuditLog audit,
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
            audit.Record(nodeId, "deregister", ActorOf(context), DateTimeOffset.UtcNow);

            logger.LogInformation(
                "Deregistered node {NodeId} (connection {ConnectionId}, aborted={Aborted})",
                nodeId,
                connectionId,
                aborted);

            return Results.Ok(new { nodeId, deregistered = true });
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

    private static async Task StreamAsync(
        HttpContext context,
        INodeRegistry registry,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin.Stream");
        var vectorEvents = context.RequestServices.GetService<VectorEvents>();

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

        void OnChanged() => signal.Writer.TryWrite(0);
        void OnVector(VectorEvent ev)
        {
            if (vectorQueue.Writer.TryWrite(ev)) signal.Writer.TryWrite(1);
        }

        registry.Changed += OnChanged;
        IDisposable? vectorSub = vectorEvents?.Subscribe(OnVector);

        try
        {
            await WriteSnapshotAsync(context.Response, registry, audit, cancellationToken);

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
                    // A fleet change (or the very first wake) always warrants a fresh snapshot.
                    if (needSnapshot)
                    {
                        await WriteSnapshotAsync(context.Response, registry, audit, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Idle keepalive — refresh ages/in-flight counts even when nothing happened.
                    await WriteSnapshotAsync(context.Response, registry, audit, cancellationToken);
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
        }
    }

    private static async Task WriteSnapshotAsync(
        HttpResponse response,
        INodeRegistry registry,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var nodes = BuildAdminNodes(registry, audit);
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

    private static AdminNode[] BuildAdminNodes(INodeRegistry registry, IAuditLog audit)
    {
        return registry.Snapshot(DateTimeOffset.UtcNow)
            .Select(node => AdminNode.From(node, audit.Get(node.NodeId)))
            .ToArray();
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
        AdminNodeAction? LastAction)
    {
        public static AdminNode From(NodeSnapshot node, AuditEntry? lastAction)
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
                lastAction is null
                    ? null
                    : new AdminNodeAction(lastAction.Action, lastAction.AtUtc, lastAction.By));
        }
    }

    private sealed record AdminNodeAction(string Action, DateTimeOffset AtUtc, string By);
}
