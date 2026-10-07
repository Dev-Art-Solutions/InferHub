using InferHub.Shared.HuggingFace;

namespace InferHub.Tests;

/// <summary>Phase 98: the links an operator pastes, read the same way on the hub and on the node.</summary>
public class HfReferenceTests
{
    [Theory]
    [InlineData("https://huggingface.co/bartowski/SmolLM2-135M-Instruct-GGUF", null, "bartowski/SmolLM2-135M-Instruct-GGUF")]
    [InlineData("https://huggingface.co/bartowski/SmolLM2-135M-Instruct-GGUF/", "Q4_K_M", "bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M")]
    [InlineData("hf.co/bartowski/SmolLM2-135M-Instruct-GGUF:Q8_0", null, "bartowski/SmolLM2-135M-Instruct-GGUF:Q8_0")]
    [InlineData("bartowski/SmolLM2-135M-Instruct-GGUF:Q8_0", "Q4_K_M", "bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M")]
    [InlineData("https://huggingface.co/allenai/OLMoE-1B-7B-0924/tree/main", null, "allenai/OLMoE-1B-7B-0924")]
    [InlineData("https://huggingface.co/owner/repo/tree/v1.0", null, "owner/repo@v1.0")]
    [InlineData("https://huggingface.co/owner/repo/blob/main/sub/model-Q4_K_M.gguf?download=true", null, "owner/repo//sub/model-Q4_K_M.gguf")]
    [InlineData("https://huggingface.co/owner/repo/resolve/abc123/model.gguf", "Q8_0", "owner/repo@abc123//model.gguf")]
    [InlineData("owner/repo@abc123//model.gguf", null, "owner/repo@abc123//model.gguf")]
    public void ALinkReadsAsTheRepoRevisionAndFileItNames(string link, string? quant, string canonical)
    {
        Assert.True(HfReference.TryParse(link, quant, out var reference, out var error), error);
        Assert.Equal(canonical, reference!.ToString());

        // The canonical form is what travels to the node; it must read back as itself.
        Assert.True(HfReference.TryParse(reference.ToString(), null, out var again, out _));
        Assert.Equal(reference, again);
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("https://evil.example/owner/repo", "not a Hugging Face link")]
    [InlineData("file:///etc/passwd", "not a Hugging Face link")]
    [InlineData("https://huggingface.co/owner", "names no repository")]
    [InlineData("https://huggingface.co/owner/repo/blob/main/../../etc/passwd", "not a path inside a repository")]
    [InlineData("owner/repo//a\\b.gguf", "not a path inside a repository")]
    [InlineData("owner/re po", "not a Hugging Face repository name")]
    [InlineData("https://huggingface.co/owner/repo/discussions/1", "not a repository or a file")]
    [InlineData("owner/repo:Q4 K", "not a quantization name")]
    public void AnythingElseIsRefusedInASentence(string link, string expected)
    {
        Assert.False(HfReference.TryParse(link, null, out _, out var error));
        Assert.Contains(expected, error);
    }
}
