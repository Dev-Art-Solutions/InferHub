using InferHub.Node;
using InferHub.Node.Backends.ChatGpt;

// Phase 103: `chatgpt login|status|logout|use|pair` runs and exits before any host exists.
if (ChatGptCommands.Handles(args))
{
    return await ChatGptCommands.RunAsync(args, Directory.GetCurrentDirectory(), null, Console.Out, Console.Error);
}

// NodeHostFactory picks a web host when solo mode is on and the plain worker host otherwise
// (phase 37). AddInferHubNode is unchanged either way — it takes IHostApplicationBuilder, which
// WebApplicationBuilder also implements.
var builder = NodeHostFactory.Create(args);
builder.AddInferHubNode();
NodeHostFactory.Build(builder).Run();
return 0;
