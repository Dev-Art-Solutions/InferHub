namespace InferHub.Coordinator.Services;

public interface IRouter
{
    RoutableNode? Route(string model, string? conversationKey = null);
}
