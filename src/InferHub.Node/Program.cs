using InferHub.Node;

var builder = Host.CreateApplicationBuilder(args);
builder.AddInferHubNode();
builder.Build().Run();
