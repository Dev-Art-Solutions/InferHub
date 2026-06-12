namespace InferHub.Node;

public class Worker(
    IConfiguration configuration,
    CoordinatorConnection coordinatorConnection,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var coordinatorUrl = configuration["Coordinator:Url"] ?? "http://localhost:5080/";
        var nodeName = configuration["Node:Name"] ?? Environment.MachineName;
        var ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://localhost:11434/";

        logger.LogInformation(
            "Node {NodeName} starting, coordinator={CoordinatorUrl}, ollama={OllamaEndpoint}",
            nodeName,
            coordinatorUrl,
            ollamaEndpoint);

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
