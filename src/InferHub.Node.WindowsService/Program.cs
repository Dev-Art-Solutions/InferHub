using InferHub.Node;
using InferHub.Node.Configuration;
using InferHub.Node.Update;
using InferHub.Node.WindowsService;

// Phase 101: `configure` (the setup writing its answers) and `update` (an operator updating by hand) run and
// exit before any host exists — neither must start a second node beside the service.
if (ServiceCommands.Handles(args))
{
    return await ServiceCommands.RunAsync(args);
}

// Same factory the console host uses (phase 37), so the two cannot drift on host shape any more
// than they can on services: a web host when solo mode is on, the plain worker host otherwise.
// Phase 101, D1: plus the setup's settings file in %ProgramData%, which no update overwrites.
var builder = NodeHostFactory.Create(args, NodeSettingsFile.DefaultPath);

// Windows-service lifetime: sets ContentRoot to AppContext.BaseDirectory when run as a
// service (so appsettings.json and the node-id file resolve next to the exe, not
// C:\Windows\System32) and enables the Windows Event Log logger by default. It no-ops off
// Windows, so this host still builds and runs on the Linux CI matrix.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "InferHub Node";
});

// Give in-flight jobs time to drain on stop / reboot (the SCM's default grace is short).
builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// Phase 101, D5: this host is the one the setup installs, so it is the one that can run the next setup.
// Registered before AddInferHubNode, whose default (NoUpdateApplier) is only a TryAdd.
builder.Services.AddSingleton<IUpdateApplier>(new InstallerUpdateApplier());

builder.AddInferHubNode();

NodeHostFactory.Build(builder).Run();
return 0;
