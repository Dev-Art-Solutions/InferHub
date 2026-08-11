using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Retrieval;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Node-profile helpers for the suites that build a <see cref="InferHub.Node.CoordinatorConnection"/>
/// by hand (phase 43).
/// </summary>
internal static class TestProfiles
{
    /// <summary>An applier over a node that has no profile — the shape every pre-43 fixture wants.</summary>
    public static NodeProfileApplier Applier(
        IInferenceBackend backend,
        IToolRuntime runtime,
        NodeOptions? node = null,
        ToolOptions? tools = null,
        RetrievalHost? retrieval = null)
        => new(
            Options.Create(node ?? new NodeOptions()),
            Options.Create(tools ?? new ToolOptions()),
            backend,
            runtime,
            retrieval ?? IdleRetrieval(),
            NullLogger<NodeProfileApplier>.Instance);

    /// <summary>
    /// A retrieval host with no corpus and nothing to build one from (phase 44). It is what every
    /// fixture that predates the phase wants: registered, inert, and never asked to start anything.
    /// </summary>
    public static RetrievalHost IdleRetrieval(LocalRetrievalOptions? options = null)
        => new(
            new EmptyServiceProvider(),
            Options.Create(options ?? new LocalRetrievalOptions()),
            NullLogger<RetrievalHost>.Instance);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>The ceiling a plain node presents: nothing disabled, no tools, no cap.</summary>
    public static LocalCeiling OpenCeiling(
        IReadOnlyList<string>? disabled = null,
        bool toolsEnabled = false,
        IReadOnlyList<string>? allowedTools = null,
        int? maxConcurrency = null,
        bool supportsModelManagement = true)
        => new(
            disabled ?? Array.Empty<string>(),
            toolsEnabled,
            allowedTools ?? Array.Empty<string>(),
            maxConcurrency,
            supportsModelManagement);

    public static NodeProfile Profile(
        string name = "test-profile",
        long revision = 1,
        NodeProfileSelector? selector = null,
        IReadOnlyDictionary<string, bool>? capabilities = null,
        IReadOnlyDictionary<string, bool>? tools = null,
        NodeProfileModels? models = null,
        int? maxConcurrency = null)
        => new(
            name,
            revision,
            selector ?? new NodeProfileSelector(NodeId: "node-1"),
            capabilities,
            tools,
            models,
            maxConcurrency);
}
