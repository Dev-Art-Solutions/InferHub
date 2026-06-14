namespace InferHub.Coordinator.Services;

public sealed class Router(INodeRegistry registry) : IRouter
{
    private int cursor;

    public RoutableNode? Route(string model)
    {
        var candidates = registry.FindNodesWithModel(model).ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var current = unchecked(Interlocked.Increment(ref cursor) - 1);
        var index = (int)((uint)current % candidates.Length);
        return candidates[index];
    }
}
