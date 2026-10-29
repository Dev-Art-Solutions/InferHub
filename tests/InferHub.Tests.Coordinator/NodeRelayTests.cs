using InferHub.Coordinator.Services;

namespace InferHub.Tests;

/// <summary>Phase 122 D3: the relay fetches from three upstreams by name, and a path cannot move it anywhere else.</summary>
public class NodeRelayTests
{
    private static readonly RelayOptions Relay = new() { Enabled = true };

    [Theory]
    [InlineData("hf", "api/models/a/b", "?expand[]=gguf", "https://huggingface.co/api/models/a/b?expand[]=gguf")]
    [InlineData("ollama-registry", "v2/library/qwen3/manifests/8b", null, "https://registry.ollama.ai/v2/library/qwen3/manifests/8b")]
    [InlineData("ollama-library", "library", "?sort=popular", "https://ollama.com/library?sort=popular")]
    public void APathLandsOnItsUpstream(string upstream, string path, string? query, string expected)
        => Assert.Equal(expected, NodeRelayEndpoints.Target(Relay, upstream, path, query)!.ToString());

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("http:evil.example")]
    public void APathCannotMoveTheHost(string path)
    {
        var target = NodeRelayEndpoints.Target(Relay, "hf", path, null);
        Assert.True(target is null || target.Host == "huggingface.co", target?.ToString());
    }

    [Fact]
    public void AnUpstreamItDoesNotKnowIsNothing()
        => Assert.Null(NodeRelayEndpoints.Target(Relay, "github", "repos/x", null));

    [Fact]
    public void AMirrorKeepsItsPathBase()
    {
        var mirror = new RelayOptions { HuggingFace = "http://hf-mirror.internal/hf" };
        Assert.Equal("http://hf-mirror.internal/hf/api/models/a/b", NodeRelayEndpoints.Target(mirror, "hf", "api/models/a/b", null)!.ToString());
    }
}
