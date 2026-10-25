using System.Text.Json.Nodes;
using InferHub.Desktop;

namespace InferHub.Tests;

/// <summary>Phase 117 in the app: the Models tab's "Shared" box turned into the node's lists, and the Overview line.</summary>
public sealed class SharedModelsTests
{
    [Fact]
    public void WithNoListsKeepingAModelExcludesIt()
    {
        var (include, exclude, error) = SharedModels.Toggle(JsonNode.Parse("""{"include":[],"exclude":[]}"""), "qwen3:32b", shared: false);

        Assert.Null(error);
        Assert.Empty(include);
        Assert.Equal(["qwen3:32b"], exclude);
    }

    [Fact]
    public void SharingAnExcludedModelAgainTakesItOff()
    {
        var (include, exclude, error) = SharedModels.Toggle(JsonNode.Parse("""{"exclude":["Qwen3:32B","gemma3"]}"""), "qwen3:32b", shared: true);

        Assert.Null(error);
        Assert.Empty(include);
        Assert.Equal(["gemma3"], exclude);
    }

    [Fact]
    public void AnOwnersIncludeListIsKeptAndChangedInPlace()
    {
        var sharing = JsonNode.Parse("""{"include":["llama3","gemma3"]}""");

        var (include, exclude, error) = SharedModels.Toggle(sharing, "gemma3", shared: false);
        Assert.Null(error);
        Assert.Equal(["llama3"], include);
        Assert.Empty(exclude);

        (include, _, _) = SharedModels.Toggle(sharing, "qwen3:32b", shared: true);
        Assert.Equal(["llama3", "gemma3", "qwen3:32b"], include);
    }

    [Fact]
    public void TakingTheLastIncludedModelAwayIsRefusedBecauseEmptyMeansAll()
    {
        var (include, _, error) = SharedModels.Toggle(JsonNode.Parse("""{"include":["llama3"]}"""), "llama3", shared: false);

        Assert.Contains("pause sharing", error);
        Assert.Equal(["llama3"], include);
    }

    [Fact]
    public void TheOverviewNamesWhatIsKept()
    {
        Assert.Equal("The network may use all 2 models.",
            SharedModels.Describe(JsonNode.Parse("""{"models":[{"name":"a","shared":true},{"name":"b","shared":true}]}""")));
        Assert.Equal("Kept for this machine: b — the network may use 1 of 2 models.",
            SharedModels.Describe(JsonNode.Parse("""{"models":[{"name":"a","shared":true},{"name":"b","shared":false}]}""")));

        // A 3.77 node says nothing per model: nothing is claimed.
        Assert.Equal("", SharedModels.Describe(JsonNode.Parse("""{"models":[{"name":"a"}]}""")));

        // null: a model Node:Models leaves out, served to nobody, counted on neither side.
        Assert.Equal("Kept for this machine: b — the network may use 1 of 2 models.",
            SharedModels.Describe(JsonNode.Parse("""{"models":[{"name":"a","shared":true},{"name":"b","shared":false},{"name":"c","shared":null}]}""")));
    }
}
