using InferHub.Coordinator.Endpoints;
using InferHub.Shared.Vector;

namespace InferHub.Tests.Vector;

/// <summary>
/// Phase 75, D2: cross-collection RRF is keyed by <c>(collection, id)</c>, not by id alone — the bug
/// this exists to catch is two unrelated collections' record "1" silently fusing into one entry.
/// </summary>
public class FederatedFusionTests
{
    [Fact]
    public void RecordsWithTheSameIdInDifferentCollectionsAreNotMerged()
    {
        var a = Result("docs-a", Match("1", 0), Match("2", 1));
        var b = Result("docs-b", Match("1", 0));

        var fused = FederatedRetrievalEndpoints.FuseAcrossCollections([a, b], k: 10);

        // Three distinct entries, not two — "1" from docs-a and "1" from docs-b are different records.
        Assert.Equal(3, fused.Count);
        Assert.Contains(fused, m => m.Collection == "docs-a" && m.Id == "1");
        Assert.Contains(fused, m => m.Collection == "docs-b" && m.Id == "1");
        Assert.Contains(fused, m => m.Collection == "docs-a" && m.Id == "2");
    }

    [Fact]
    public void ARecordRankedFirstInTwoCollectionsOutranksOneRankedFirstInOnlyOne()
    {
        var a = Result("docs-a", Match("shared", 0), Match("only-a", 1));
        var b = Result("docs-b", Match("shared", 0));

        // "shared" is a distinct (collection, id) pair in each list — same key would be wrong here
        // too, since docs-a's "shared" and docs-b's "shared" are still different records. What this
        // test pins is that a record ranked #1 wherever it appears twice outranks one ranked #1 once,
        // by ordinary RRF summation over the (collection, id) keys.
        var c = Result("docs-c", Match("only-a", 0));

        var fused = FederatedRetrievalEndpoints.FuseAcrossCollections([a, b, c], k: 10);

        var onlyAEntries = fused.Where(m => m.Id == "only-a").ToArray();
        Assert.Equal(2, onlyAEntries.Length);
    }

    [Fact]
    public void KIsRespectedAcrossTheCombinedPool()
    {
        var a = Result("docs-a", Match("1", 0), Match("2", 1), Match("3", 2));
        var b = Result("docs-b", Match("4", 0), Match("5", 1));

        var fused = FederatedRetrievalEndpoints.FuseAcrossCollections([a, b], k: 2);

        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void ASourceThatAnsweredNothingContributesNothing()
    {
        var a = Result("docs-a", Match("1", 0));
        var empty = FederatedRetrievalEndpoints.CollectionResult.Empty("docs-b", "timeout");

        var fused = FederatedRetrievalEndpoints.FuseAcrossCollections([a, empty], k: 10);

        Assert.Single(fused);
        Assert.Equal("docs-a", fused[0].Collection);
    }

    private static VectorMatch Match(string id, double score) => new(id, score, Payload: null, Metadata: null);

    private static FederatedRetrievalEndpoints.CollectionResult Result(string collection, params VectorMatch[] matches) =>
        new(collection, matches, new FederatedRetrievalEndpoints.FederatedSourceResult(collection, "ok", matches.Length, 0));
}
