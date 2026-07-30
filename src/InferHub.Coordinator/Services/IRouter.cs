namespace InferHub.Coordinator.Services;

public interface IRouter
{
    /// <summary>
    /// Picks a node for a model. <paramref name="capability"/> (phase 40) narrows the candidates
    /// to nodes that declare they can do this *kind* of work with that model; null keeps the
    /// pre-v3.8 question, "who holds it".
    /// </summary>
    RoutableNode? Route(
        string model,
        string? conversationKey = null,
        string? excludeConnectionId = null,
        string? capability = null);
}
