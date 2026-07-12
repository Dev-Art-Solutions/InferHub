namespace InferHub.Tests.Vector;

/// <summary>
/// Groups the database-backed vector tests into one non-parallel collection. They share the
/// <c>inferhub_test</c> schema and each run bootstraps <c>CREATE EXTENSION</c> / <c>CREATE SCHEMA</c>;
/// Postgres can't do those concurrently (tuple-concurrently-updated on the catalog), so the
/// classes must not run in parallel with each other or the rest of the suite.
/// </summary>
[CollectionDefinition("postgres", DisableParallelization = true)]
public sealed class PostgresCollection;
