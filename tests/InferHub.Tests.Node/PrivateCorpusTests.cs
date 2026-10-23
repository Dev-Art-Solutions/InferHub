using InferHub.Node.Retrieval;
using Microsoft.Extensions.DependencyInjection;

namespace InferHub.Tests;

/// <summary>
/// Phase 114: a private corpus beside the hub's replicas. 38 D1 still refuses a meshed node its own corpus unless it
/// is asked for as private, in a directory of its own; and the hub can neither assign nor stop it.
/// </summary>
public sealed class PrivateCorpusTests
{
    private const string Hub = "--Coordinator:Url=http://127.0.0.1:1/";

    [Fact]
    public async Task AMeshedNodeWithLocalRetrievalAndNoPrivateStillRefusesToBoot()
    {
        var failure = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=true", "--Coordinator:Enabled=true", Hub, "--LocalApi:Retrieval:Enabled=true");

        Assert.Contains("LocalApi:Retrieval:Enabled", failure.ToString());
        Assert.Contains("Coordinator:Enabled", failure.ToString());
    }

    [Fact]
    public async Task APrivateCorpusInsideTheReplicaDirectoryIsRefusedNamingBothKeys()
    {
        var dir = Directory.CreateTempSubdirectory("inferhub-private-").FullName;

        var failure = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=true", "--Coordinator:Enabled=true", Hub,
            "--LocalApi:Retrieval:Enabled=true", "--LocalApi:Retrieval:Private=true",
            $"--Vector:ReplicaDirectory={dir}", $"--LocalApi:Retrieval:DataDirectory={Path.Combine(dir, "mine")}");

        Assert.Contains("LocalApi:Retrieval:DataDirectory", failure.ToString());
        Assert.Contains("Vector:ReplicaDirectory", failure.ToString());
    }

    [Fact]
    public async Task AMeshedNodeKeepsAPrivateCorpusAndRefusesTheCoordinatorsOne()
    {
        await using var host = await SoloHost.StartAsync(
            null,
            "--Coordinator:Enabled=true", Hub,
            "--LocalApi:Retrieval:Enabled=true", "--LocalApi:Retrieval:Private=true");

        var retrieval = host.Services.GetRequiredService<RetrievalHost>();
        Assert.True(retrieval.IsPrivate);
        Assert.False(retrieval.ReplicationEnabled);
        Assert.NotNull(retrieval.TryLease()?.Corpus);

        var assigned = await retrieval.StartCorpusAsync(
            new CorpusRequest("local", null, null, ["docs"], null, CorpusRequest.Profile), CancellationToken.None);

        Assert.False(assigned.Started);
        Assert.Equal(RetrievalHost.PrivateRefusal, assigned.Error);
    }
}
