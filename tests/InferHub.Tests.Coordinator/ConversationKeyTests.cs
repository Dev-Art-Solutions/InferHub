using InferHub.Coordinator.Endpoints;
using InferHub.Shared.Ollama;

namespace InferHub.Tests;

public class ConversationKeyTests
{
    [Fact]
    public void DeriveReturnsNullForEmptyMessages()
    {
        Assert.Null(InferenceEndpoints.DeriveConversationKey(null));
        Assert.Null(InferenceEndpoints.DeriveConversationKey(Array.Empty<ChatMessage>()));
    }

    [Fact]
    public void DeriveIsStableAcrossTurnsOfTheSameThread()
    {
        var turnOne = new List<ChatMessage>
        {
            new() { Role = "system", Content = "You are a helpful assistant." },
            new() { Role = "user", Content = "What is the capital of Bulgaria?" }
        };

        var turnTwo = new List<ChatMessage>
        {
            new() { Role = "system", Content = "You are a helpful assistant." },
            new() { Role = "user", Content = "What is the capital of Bulgaria?" },
            new() { Role = "assistant", Content = "Sofia." },
            new() { Role = "user", Content = "And of France?" }
        };

        var keyOne = InferenceEndpoints.DeriveConversationKey(turnOne);
        var keyTwo = InferenceEndpoints.DeriveConversationKey(turnTwo);

        Assert.NotNull(keyOne);
        Assert.Equal(keyOne, keyTwo);
    }

    [Fact]
    public void DeriveDiffersForDifferentOpeningMessages()
    {
        var threadOne = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Tell me about cats." }
        };

        var threadTwo = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Tell me about dogs." }
        };

        var keyOne = InferenceEndpoints.DeriveConversationKey(threadOne);
        var keyTwo = InferenceEndpoints.DeriveConversationKey(threadTwo);

        Assert.NotNull(keyOne);
        Assert.NotNull(keyTwo);
        Assert.NotEqual(keyOne, keyTwo);
    }

    [Fact]
    public void DeriveIgnoresLaterUserAndAssistantTurns()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "First topic" },
            new() { Role = "assistant", Content = "Reply" },
            new() { Role = "user", Content = "Second message" }
        };

        var sameOpening = new List<ChatMessage>
        {
            new() { Role = "user", Content = "First topic" }
        };

        Assert.Equal(
            InferenceEndpoints.DeriveConversationKey(sameOpening),
            InferenceEndpoints.DeriveConversationKey(messages));
    }
}
