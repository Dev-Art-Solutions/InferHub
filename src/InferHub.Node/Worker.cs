using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node;

public class Worker(
    IOptions<CoordinatorOptions> coordinatorOptions,
    IOptions<NodeOptions> nodeOptions,
    IOptions<LocalApiOptions> localApiOptions,
    IInferenceBackend backend,
    CoordinatorConnection coordinatorConnection,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly CoordinatorOptions coordinator = coordinatorOptions.Value;
    private readonly NodeOptions node = nodeOptions.Value;
    private readonly LocalApiOptions localApi = localApiOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Node {NodeName} starting, coordinator={CoordinatorUrl}, backend={BackendName}, endpoint={BackendEndpoint}, maxConcurrency={MaxConcurrency}, labels={LabelCount}",
            node.Name,
            coordinator.Enabled ? coordinator.Url : "(disabled)",
            backend.Name,
            backend.Endpoint,
            node.MaxConcurrency,
            node.Labels.Count);

        // Solo (phase 37): no hub to dial, so no connection, no heartbeat, no reconnect loop —
        // and no coordinator URL was ever required. The local API is a hosted service of its own.
        if (!coordinator.Enabled)
        {
            logger.LogInformation(
                "{Key}=false — this node is not joining a mesh and is serving its own clients on {Urls}.",
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.Enabled)}",
                localApi.Urls);
        }
        else
        {
            await coordinatorConnection.StartAsync(stoppingToken);
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (coordinator.Enabled)
        {
            await coordinatorConnection.StopAsync(cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }
}
