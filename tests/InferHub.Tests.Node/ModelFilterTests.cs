using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

public class ModelFilterTests
{
    [Fact]
    public void EmptyFilterReturnsAllModels()
    {
        var models = Models("llama3", "qwen2", "mistral");
        var filter = new ModelFilterOptions();

        var result = ModelFilter.Apply(models, filter);

        Assert.Equal(models.Select(m => m.Name), result.Select(m => m.Name));
    }

    [Fact]
    public void IncludeKeepsOnlyMatchingModels()
    {
        var models = Models("llama3", "qwen2", "mistral");
        var filter = new ModelFilterOptions { Include = { "qwen2", "mistral" } };

        var result = ModelFilter.Apply(models, filter);

        Assert.Equal(new[] { "qwen2", "mistral" }, result.Select(m => m.Name));
    }

    [Fact]
    public void IncludeIsCaseInsensitive()
    {
        var models = Models("Llama3", "Qwen2");
        var filter = new ModelFilterOptions { Include = { "llama3" } };

        var result = ModelFilter.Apply(models, filter);

        var only = Assert.Single(result);
        Assert.Equal("Llama3", only.Name);
    }

    [Fact]
    public void ExcludeRemovesMatchingModels()
    {
        var models = Models("llama3", "qwen2", "mistral");
        var filter = new ModelFilterOptions { Exclude = { "qwen2" } };

        var result = ModelFilter.Apply(models, filter);

        Assert.Equal(new[] { "llama3", "mistral" }, result.Select(m => m.Name));
    }

    [Fact]
    public void ExcludeTakesPrecedenceOverInclude()
    {
        var models = Models("llama3", "qwen2", "mistral");
        var filter = new ModelFilterOptions
        {
            Include = { "llama3", "qwen2" },
            Exclude = { "qwen2" }
        };

        var result = ModelFilter.Apply(models, filter);

        var only = Assert.Single(result);
        Assert.Equal("llama3", only.Name);
    }

    [Fact]
    public void BlankEntriesAreIgnored()
    {
        var models = Models("llama3", "qwen2");
        var filter = new ModelFilterOptions
        {
            Include = { "", "   " },
            Exclude = { "  " }
        };

        var result = ModelFilter.Apply(models, filter);

        Assert.Equal(new[] { "llama3", "qwen2" }, result.Select(m => m.Name));
    }

    private static IReadOnlyList<ModelInfo> Models(params string[] names)
    {
        return names.Select(name => new ModelInfo(name, null, null)).ToArray();
    }
}
