using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Services;

public interface INodeConnectionTracker
{
    void Track(string connectionId, HubCallerContext context);

    void Forget(string connectionId);

    bool Abort(string connectionId);
}

public sealed class NodeConnectionTracker : INodeConnectionTracker
{
    private readonly ConcurrentDictionary<string, HubCallerContext> contexts = new();

    public void Track(string connectionId, HubCallerContext context)
    {
        contexts[connectionId] = context;
    }

    public void Forget(string connectionId)
    {
        contexts.TryRemove(connectionId, out _);
    }

    public bool Abort(string connectionId)
    {
        if (!contexts.TryRemove(connectionId, out var context))
        {
            return false;
        }

        context.Abort();
        return true;
    }
}
