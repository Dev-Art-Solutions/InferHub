namespace InferHub.Coordinator.Services;

public interface IConversationAffinity
{
    string? GetNodeFor(string conversationKey);

    void Record(string conversationKey, string connectionId);

    void Forget(string conversationKey);

    int ForgetConnection(string connectionId);
}
