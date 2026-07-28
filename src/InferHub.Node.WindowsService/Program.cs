using InferHub.Node;

// Same factory the console host uses (phase 37), so the two cannot drift on host shape any more
// than they can on services: a web host when solo mode is on, the plain worker host otherwise.
var builder = NodeHostFactory.Create(args);

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

builder.AddInferHubNode();

NodeHostFactory.Build(builder).Run();
