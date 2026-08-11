// Phase 52. The replication fake moved here from ReplicationCoordinatorTests because two suites
// need it: the coordinator's replication tests and the mesh's node-owned-collection tests. A
// fixture used by two projects lives in the fixture library вЂ” the alternative is a second copy,
// and a fake that exists twice is two fakes that drift.

using InferHub.Coordinator.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Tests;

/// <summary>One message the coordinator asked SignalR to send, captured rather than sent.</summary>
internal sealed record SentMessage(string ConnectionId, string Method, object?[] Args);

internal sealed class RecordingHubContext : IHubContext<NodeHub>
{
    public List<SentMessage> Sends { get; } = new();

    public IHubClients Clients { get; }

    public IGroupManager Groups => throw new NotImplementedException();

    public RecordingHubContext()
    {
        Clients = new RecordingHubClients(this);
    }
}

internal sealed class RecordingHubClients(RecordingHubContext parent) : IHubClients
{
    public IClientProxy All => throw new NotImplementedException();
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => new RecordingClientProxy(parent, connectionId);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Group(string groupName) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
}

internal sealed class RecordingClientProxy(RecordingHubContext parent, string connectionId) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        lock (parent.Sends)
        {
            parent.Sends.Add(new SentMessage(connectionId, method, args));
        }
        return Task.CompletedTask;
    }
}
