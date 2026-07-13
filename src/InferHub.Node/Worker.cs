using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node;

public class Worker(
    IOptions<CoordinatorOptions> coordinatorOptions,
    IOptions<NodeOptions> nodeOptions,
    IInferenceBackend backend,
    CoordinatorConnection coordinatorConnection,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly CoordinatorOptions coordinator = coordinatorOptions.Value;
    private readonly NodeOptions node = nodeOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Node {NodeName} starting, coordinator={CoordinatorUrl}, backend={BackendName}, endpoint={BackendEndpoint}, maxConcurrency={MaxConcurrency}, labels={LabelCount}",
            node.Name,
            coordinator.Url,
            backend.Name,
            backend.Endpoint,
            node.MaxConcurrency,
            node.Labels.Count);

        await coordinatorConnection.StartAsync(stoppingToken);

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
        await coordinatorConnection.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
