namespace InferHub.Node;

public class Worker(IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatDelay = TimeSpan.FromSeconds(30);

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

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Node {NodeName} heartbeat, coordinator={CoordinatorUrl}, ollama={OllamaEndpoint}",
                nodeName,
                coordinatorUrl,
                ollamaEndpoint);

            await Task.Delay(HeartbeatDelay, stoppingToken);
        }
    }
}
