using InferHub.Coordinator.Vector;

namespace InferHub.Tests.Vector;

public class VectorStoreOptionsValidatorTests
{
    [Fact]
    public void DisabledStoreSkipsValidation()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions
        {
            Enabled = false,
            DataDirectory = "",
            Distance = "garbage"
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledStorePassesForDefaults()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Theory]
    [InlineData("cosine")]
    [InlineData("dot")]
    [InlineData("l2")]
    [InlineData("Cosine")]
    public void EnabledStoreAcceptsAllSupportedDistances(string distance)
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, Distance = distance };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledStoreRejectsUnknownDistance()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, Distance = "hamming" };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(VectorStoreOptions.Distance)));
    }

    [Fact]
    public void EnabledStoreRejectsBlankDataDirectory()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, DataDirectory = "  " };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(VectorStoreOptions.DataDirectory)));
    }

    [Fact]
    public void EnabledStoreRejectsZeroReplicationFactor()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, ReplicationFactor = 0 };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(VectorStoreOptions.ReplicationFactor)));
    }

    [Fact]
    public void EnabledStoreRejectsZeroSnapshotEveryOps()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, SnapshotEveryOps = 0 };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(VectorStoreOptions.SnapshotEveryOps)));
    }

    [Fact]
    public void EnabledStoreRejectsRetrievalMaxRecordsBelowDefaultK()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions
        {
            Enabled = true,
            Retrieval = new RetrievalOptions { DefaultK = 5, MaxRecords = 2 }
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(RetrievalOptions.MaxRecords)));
    }

    [Fact]
    public void EnabledStoreRejectsUnknownOnMissing()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions
        {
            Enabled = true,
            Retrieval = new RetrievalOptions { OnMissing = "explode" }
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(RetrievalOptions.OnMissing)));
    }
}
