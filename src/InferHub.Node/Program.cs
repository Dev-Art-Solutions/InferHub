using InferHub.Node;

// NodeHostFactory picks a web host when solo mode is on and the plain worker host otherwise
// (phase 37). AddInferHubNode is unchanged either way — it takes IHostApplicationBuilder, which
// WebApplicationBuilder also implements.
var builder = NodeHostFactory.Create(args);
builder.AddInferHubNode();
NodeHostFactory.Build(builder).Run();
