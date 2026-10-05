using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 94: what the edge reads of a <c>/v1/brio</c> body, and how a node's answer is rendered —
/// the one copy both hosts call.
/// </summary>
public class BrioContractTests
{
    private const string Answer = """
    {"object":"brio.choice","answer":"request changes","entropy":0.41,"normalize":"mean",
     "choices":[{"option":"request changes","p":0.78},{"option":"merge","p":0.22}],
     "id":"brio-1","created":0,"model":"olmoe",
     "usage":{"prompt_tokens":40,"completion_tokens":0,"read_tokens":21,"total_tokens":61}}
    """;

    [Theory]
    [InlineData("""{"model":"olmoe","state":"s","options":["a","b","c"]}""", "options", 3)]
    [InlineData("""{"model":"olmoe","state":"s","questions":[{"question":"q","options":["a","b"]}]}""", "questions", 1)]
    [InlineData("""{"model":"olmoe","state":"s","schema":{"lang":["en","bg"],"tone":["calm","angry"]}}""", "schema", 2)]
    [InlineData("""{"model":"olmoe","state":"s","options":["a","b"],"schema":null}""", "options", 2)]
    public void TheEdgeReadsTheModelTheFormAndTheCount(string raw, string form, int count)
    {
        var request = BrioRequest.TryParse(raw, out var error);

        Assert.NotNull(request);
        Assert.Equal("", error);
        Assert.Equal("olmoe", request.Model);
        Assert.Equal(form, request.Form);
        Assert.Equal(count, request.Count);

        // D2: the body goes on exactly as it came.
        Assert.Same(raw, request.Raw);
    }

    [Theory]
    [InlineData("not json", "JSON object")]
    [InlineData("[1,2]", "JSON object")]
    [InlineData("""{"state":"s","options":["a","b"]}""", "model is required")]
    [InlineData("""{"model":"  ","options":["a","b"]}""", "model is required")]
    [InlineData("""{"model":"olmoe","state":"s"}""", "exactly one of")]
    [InlineData("""{"model":"olmoe","options":["a","b"],"questions":[]}""", "exactly one of")]
    public void ARequestTheEngineCouldNotReadIsRefusedAtTheEdge(string raw, string expected)
    {
        Assert.Null(BrioRequest.TryParse(raw, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void AnAnswerIsPassedOnVerbatimAndBilledForEverythingTheEngineRead()
    {
        var outcome = BrioRenderer.Render(ToolResult.Succeeded(Guid.NewGuid(), Answer));

        Assert.Equal(200, outcome.Status);
        Assert.Same(Answer, outcome.Json);
        Assert.Equal(61, outcome.Tokens);
    }

    [Fact]
    public void WithoutATotalThePrefixAndTheOptionsAreAdded()
    {
        Assert.Equal(61, BrioRenderer.TokensRead("""{"usage":{"prompt_tokens":40,"read_tokens":21}}"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"answer":"merge"}""")]
    [InlineData("<html>")]
    public void ASuccessThatIsNotABrioAnswerIsA502RatherThanAFreeAnswer(string payload)
    {
        var outcome = BrioRenderer.Render(ToolResult.Succeeded(Guid.NewGuid(), payload));

        Assert.Equal(502, outcome.Status);
        Assert.Equal(0, outcome.Tokens);
    }

    [Fact]
    public void TheNodeStatesTheKindOfFailureAndTheEdgeRendersIt()
    {
        var id = Guid.NewGuid();

        var busy = BrioRenderer.Render(ToolResult.Retry(id, "queue full", 2));
        Assert.Equal(503, busy.Status);
        Assert.Equal(2, busy.RetryAfterSeconds);

        var model = BrioRenderer.Render(ToolResult.Refused(id, "The model `x` does not exist.", BrioErrorCodes.ModelNotFound));
        Assert.Equal(404, model.Status);
        Assert.Equal("model_not_found", model.ErrorCode);

        var invalid = BrioRenderer.Render(ToolResult.Refused(id, "Duplicate option in `options`: 'a'.", ToolErrorCodes.InvalidRequest));
        Assert.Equal(400, invalid.Status);
        Assert.Contains("Duplicate option", invalid.Error);

        var broken = BrioRenderer.Render(ToolResult.Failed(id, "engine_error"));
        Assert.Equal(502, broken.Status);

        Assert.All(new[] { busy, model, invalid, broken }, outcome => Assert.Equal(0, outcome.Tokens));
    }

    [Fact]
    public void ScoreIsAKindThisReleaseKnows()
    {
        Assert.Equal("score", CapabilityKinds.Score);
        Assert.True(CapabilityKinds.IsWellKnown(CapabilityKinds.Score));

        // Not an Ollama job kind: nothing routes an InferenceJob to it (40 D3).
        Assert.Null(CapabilityKinds.ForJobKind("score"));
    }
}
