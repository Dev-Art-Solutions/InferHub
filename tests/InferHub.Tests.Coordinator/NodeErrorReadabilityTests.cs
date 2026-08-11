using InferHub.Coordinator.Endpoints;

namespace InferHub.Tests;

/// <summary>
/// What a client actually reads out of <c>error.message</c> when a node refuses. Ollama encodes
/// its own backend's JSON error as a *string* inside its <c>error</c> field, so the sentence a
/// human needs arrives buried under two layers of escaping and ours makes three.
/// </summary>
public class NodeErrorReadabilityTests
{
    [Fact]
    public void TheDoubleEncodedOllamaRefusalUnwrapsToItsSentence()
    {
        // Captured verbatim from a real Ollama node handed an image for llama3.1 (phase 29).
        const string raw =
            """{"error":"{\"error\":{\"code\":400,\"message\":\"Multimodal data provided, but model does not support multimodal requests.\",\"type\":\"invalid_request_error\"}}"}""";

        Assert.Equal(
            "Multimodal data provided, but model does not support multimodal requests.",
            InferenceCore.ReadableNodeError(raw));
    }

    [Fact]
    public void ASingleLevelErrorStringUnwrapsToo()
    {
        Assert.Equal("model not found", InferenceCore.ReadableNodeError("""{"error":"model not found"}"""));
    }

    [Fact]
    public void APlainSentenceIsLeftExactlyAsItIs()
    {
        Assert.Equal("the GPU fell over", InferenceCore.ReadableNodeError("the GPU fell over"));
    }

    [Fact]
    public void JsonThatIsNotAnErrorEnvelopeIsLeftAlone()
    {
        const string raw = """{"status":"weird"}""";
        Assert.Equal(raw, InferenceCore.ReadableNodeError(raw));
    }

    [Fact]
    public void MalformedJsonKeepsWhateverTextWeAlreadyHave()
    {
        // Truncated mid-string: not parseable, so the raw text is still the best on offer.
        const string raw = """{"error":"half a mess""";
        Assert.Equal(raw, InferenceCore.ReadableNodeError(raw));
    }

    [Fact]
    public void NothingUsableFallsBackRatherThanReturningAnEmptyMessage()
    {
        // An SDK surfacing a blank error.message is the "unknown error" this envelope exists
        // to avoid.
        Assert.Equal("node failed to run inference", InferenceCore.ReadableNodeError(null));
        Assert.Equal("node failed to run inference", InferenceCore.ReadableNodeError("   "));
        Assert.Equal("node failed to run inference", InferenceCore.ReadableNodeError("""{"error":""}"""));
    }
}
