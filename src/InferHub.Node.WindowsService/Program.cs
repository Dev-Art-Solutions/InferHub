using InferHub.Node;

var builder = Host.CreateApplicationBuilder(args);

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

builder.Build().Run();
