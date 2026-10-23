using InferHub.Node.Control;
using Microsoft.Extensions.Options;

namespace InferHub.Node.WindowsService;

/// <summary>
/// The service's restart (112 D3): after the drain, the process ends without telling the service manager it stopped,
/// which the manager counts as a failure — and the setup configured the service to restart on one (5 s, 5 s, 30 s;
/// <c>InferHubNode.iss</c>, <c>install-service.ps1</c>). A clean stop would be honoured as a stop, and nothing would
/// start it again. The engines go with it: they are in the node's kill-on-close job (95 D3).
/// </summary>
internal sealed class ServiceRestarter(
    IHostApplicationLifetime lifetime,
    IOptions<ControlOptions> options,
    ILogger<NodeRestarter> logger,
    NodeActivity? activity = null) : NodeRestarter(lifetime, options, logger, activity)
{
    public override RestartSupport Support =>
        new(true, "windows-service", "the Windows service manager restarts it a few seconds after it ends (the setup's recovery actions)");

    protected override void End() => Environment.Exit(ExitCode);
}
