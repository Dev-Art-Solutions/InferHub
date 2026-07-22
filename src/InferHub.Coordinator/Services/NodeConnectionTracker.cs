using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Services;

public interface INodeConnectionTracker
{
    void Track(string connectionId, HubCallerContext context);

    void Forget(string connectionId);

    bool Abort(string connectionId);

    /// <summary>
    /// Drop every node connection and report how many. Used on demotion (phase 32): a node left
    /// attached to a coordinator that has stopped leading is a node the mesh has silently lost.
    /// </summary>
    int AbortAll();
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

    public int AbortAll()
    {
        var aborted = 0;

        foreach (var connectionId in contexts.Keys)
        {
            if (Abort(connectionId))
            {
                aborted++;
            }
        }

        return aborted;
    }
}
