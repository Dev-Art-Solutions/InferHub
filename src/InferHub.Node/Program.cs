using InferHub.Node;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<INodeIdentity, FileNodeIdentity>();
builder.Services.AddSingleton<CoordinatorConnection>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
