using InferHub.Node.Backends;
using InferHub.Node.Configuration;

namespace InferHub.Tests;

/// <summary>
/// Phase 108 D2. What a node declares as one model's parallelism: an Ollama's own variable, parsed the
/// way Ollama parses it, and <c>Node:ModelSlots</c> refused below one.
/// </summary>
public class ModelSlotsTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("4", 4)]
    [InlineData(" 2 ", 2)]
    // Ollama's Uint falls back to its default on anything unparseable, and its scheduler treats 0 as 1.
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("four", 1)]
    public void OllamasParallelismIsItsOwnVariableAndItsDefaultIsOne(string? value, int expected)
    {
        Assert.Equal(expected, OllamaBackend.ParallelFrom(value));
    }

    [Fact]
    public void ABackendThatSaysNothingDeclaresNothing()
    {
        IInferenceBackend backend = new SilentBackend();

        Assert.Null(backend.ModelSlots);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]
    public void ModelSlotsBelowOneIsRefused(int? slots, bool valid)
    {
        var result = new NodeOptionsValidator().Validate(null, new NodeOptions { Name = "n", ModelSlots = slots });

        Assert.Equal(valid, result.Succeeded);

        if (!valid)
        {
            Assert.Contains("Node:ModelSlots", result.FailureMessage);
        }
    }

    private sealed class SilentBackend : IInferenceBackend
    {
        public string Name => "silent";

        public string Endpoint => "http://127.0.0.1:0/";

        public IReadOnlyList<string> Kinds { get; } = ["chat"];

        public bool SupportsModelManagement => false;

        public Task<IReadOnlyList<InferHub.Shared.Contracts.ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InferHub.Shared.Contracts.ModelInfo>?>([]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
