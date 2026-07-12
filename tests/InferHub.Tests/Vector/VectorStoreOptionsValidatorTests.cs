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

    // --- Provider / Postgres (phase 20) ---

    [Fact]
    public void LocalProviderWithNoPostgresSectionPasses()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, Provider = "local" };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void UnknownProviderIsRejected()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions { Enabled = true, Provider = "sqlite" };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(VectorStoreOptions.Provider)));
    }

    [Fact]
    public void ValidPostgresConfigPasses()
    {
        var validator = new VectorStoreOptionsValidator();
        var options = new VectorStoreOptions
        {
            Enabled = true,
            Provider = "postgres",
            DataDirectory = "", // inert under postgres — must not fail
            Postgres = new PostgresStoreOptions
            {
                ConnectionString = "Host=localhost;Database=inferhub;Username=x;Password=y"
            }
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void PostgresRequiresConnectionString()
    {
        var result = ValidatePostgres(pg => pg.ConnectionString = "");

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.ConnectionString)));
    }

    [Theory]
    [InlineData("1bad")]
    [InlineData("Bad")]
    [InlineData("has-dash")]
    public void PostgresRejectsInvalidSchemaIdentifier(string schema)
    {
        var result = ValidatePostgres(pg => pg.Schema = schema);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.Schema)));
    }

    [Fact]
    public void PostgresRejectsInvalidTablePrefix()
    {
        var result = ValidatePostgres(pg => pg.TablePrefix = "Vec-");

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.TablePrefix)));
    }

    [Fact]
    public void PostgresRejectsUnknownIndexKind()
    {
        var result = ValidatePostgres(pg => pg.Index = "annoy");

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.Index)));
    }

    [Fact]
    public void PostgresRejectsHnswMBelowTwo()
    {
        var result = ValidatePostgres(pg => pg.HnswM = 1);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.HnswM)));
    }

    [Fact]
    public void PostgresRejectsEfConstructionBelowM()
    {
        var result = ValidatePostgres(pg => { pg.HnswM = 32; pg.HnswEfConstruction = 16; });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.HnswEfConstruction)));
    }

    [Fact]
    public void PostgresRejectsEfSearchBelowOne()
    {
        var result = ValidatePostgres(pg => pg.EfSearch = 0);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.EfSearch)));
    }

    [Fact]
    public void PostgresRejectsCommandTimeoutBelowOne()
    {
        var result = ValidatePostgres(pg => pg.CommandTimeoutSeconds = 0);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains(nameof(PostgresStoreOptions.CommandTimeoutSeconds)));
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult ValidatePostgres(Action<PostgresStoreOptions> tweak)
    {
        var pg = new PostgresStoreOptions
        {
            ConnectionString = "Host=localhost;Database=inferhub;Username=x;Password=y"
        };
        tweak(pg);
        var options = new VectorStoreOptions { Enabled = true, Provider = "postgres", Postgres = pg };
        return new VectorStoreOptionsValidator().Validate(null, options);
    }
}
