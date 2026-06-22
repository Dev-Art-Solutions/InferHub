using System.Text.Json;
using System.Threading.Channels;
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

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.Body.FlushAsync(cancellationToken);

        var signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

        void OnChanged() => signal.Writer.TryWrite(1);
        registry.Changed += OnChanged;

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
                    while (signal.Reader.TryRead(out _)) { }
                    await WriteSnapshotAsync(context.Response, registry, audit, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Idle keepalive — also pushes fresh ages and in-flight counts to the client.
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
