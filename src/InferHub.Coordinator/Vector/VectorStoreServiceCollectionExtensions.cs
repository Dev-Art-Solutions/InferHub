using InferHub.Coordinator.Ingestion;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Vector.Postgres;
using InferHub.Coordinator.Vector.Qdrant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Composition root for the vector store. Both <c>Program.cs</c> and the DI-shape composition
/// test wire the vector feature through here, so the two can never drift — and the test that
/// guards "the mesh services are absent under postgres" holds the real registration path.
/// </summary>
public static class VectorStoreServiceCollectionExtensions
{
    public static IServiceCollection AddInferHubVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(VectorStoreOptions.SectionName);
        services.Configure<VectorStoreOptions>(section);
        services.AddSingleton<IValidateOptions<VectorStoreOptions>, VectorStoreOptionsValidator>();
        services.AddOptions<VectorStoreOptions>().ValidateOnStart();

        var enabled = section.GetValue<bool>(nameof(VectorStoreOptions.Enabled));
        if (!enabled)
        {
            return services;
        }

        var provider = section.GetValue<string>(nameof(VectorStoreOptions.Provider)) ?? VectorStoreProviderExtensions.Local;

        services.AddSingleton<VectorEvents>();
        services.AddSingleton<ReplicaRegistry>();   // stays empty under postgres

        if (VectorStoreProviderExtensions.IsPostgres(provider))
        {
            services.AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value.Postgres;
                var dsb = new NpgsqlDataSourceBuilder(opts.ConnectionString);
                if (opts.MaxPoolSize > 0 &&
                    !opts.ConnectionString.Contains("Pool Size", StringComparison.OrdinalIgnoreCase))
                {
                    dsb.ConnectionStringBuilder.MaxPoolSize = opts.MaxPoolSize;
                }
                dsb.UseVector();
                return dsb.Build();
            });
            services.AddSingleton<PostgresVectorStore>();
            services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<PostgresVectorStore>());
            services.AddSingleton<IVectorQueryRouter, NullVectorQueryRouter>();
            // Registered under its own seam as well as IHostedService: the coordinator runs it as a
            // hosted service, and inferhub-migrate resolves *only* this one (see IVectorStoreBootstrapper).
            services.AddSingleton<IVectorStoreBootstrapper, PostgresBootstrapper>();
            services.AddHostedService(sp => sp.GetRequiredService<IVectorStoreBootstrapper>());
        }
        else if (VectorStoreProviderExtensions.IsQdrant(provider))
        {
            // Qdrant speaks JSON over HTTP, so the connector is a hand-rolled HttpClient — no client
            // package, no gRPC (rule 5 held for a third backend). Same "external provider" shape as
            // postgres: NullVectorQueryRouter, no replication / healing services.
            var qdrant = section.GetSection(nameof(VectorStoreOptions.Qdrant));
            services.AddHttpClient(QdrantClient.HttpClientName, http => QdrantClient.Configure(
                http,
                qdrant.GetValue<string>(nameof(QdrantStoreOptions.Url)) ?? "",
                qdrant.GetValue<string>(nameof(QdrantStoreOptions.ApiKey)),
                qdrant.GetValue<int?>(nameof(QdrantStoreOptions.TimeoutSeconds)) ?? 30));
            services.AddSingleton(sp => new QdrantClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(QdrantClient.HttpClientName)));

            // Constructed by hand for the same reason LocalVectorStore is: phase 44 moved this store
            // into InferHub.Shared, which cannot see IOptions<T> or ILogger<T> (D2). The lifecycle
            // events it used to publish through VectorEvents itself are now plain events the host
            // forwards — same kinds, same data, so the admin SSE feed is unchanged.
            services.AddSingleton(sp =>
            {
                var store = new QdrantVectorStore(
                    sp.GetRequiredService<QdrantClient>(),
                    sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value,
                    new VectorLog<QdrantVectorStore>(sp.GetRequiredService<ILogger<QdrantVectorStore>>()));

                var events = sp.GetRequiredService<VectorEvents>();
                store.CollectionCreated += info => events.Publish("vector.collection.created", info.Name,
                    new Dictionary<string, object?>
                    {
                        ["dimension"] = info.Dimension,
                        ["distance"] = info.Distance
                    });
                store.CollectionDropped += name => events.Publish("vector.collection.dropped", name);

                return store;
            });
            services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<QdrantVectorStore>());
            services.AddSingleton<IVectorQueryRouter, NullVectorQueryRouter>();
            services.AddSingleton<IVectorStoreBootstrapper, QdrantBootstrapper>();
            services.AddHostedService(sp => sp.GetRequiredService<IVectorStoreBootstrapper>());
        }
        else
        {
            // Constructed by hand because the store moved to InferHub.Shared in phase 38 and a plain
            // class library cannot see IOptions<T> or ILogger<T> (D3). The seams are one line each.
            services.AddSingleton(sp => new LocalVectorStore(
                sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value,
                new VectorLog<LocalVectorStore>(sp.GetRequiredService<ILogger<LocalVectorStore>>())));
            services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<LocalVectorStore>());
            services.AddSingleton<ReplicationCoordinator>();
            services.AddHostedService(sp => sp.GetRequiredService<ReplicationCoordinator>());
            services.AddSingleton<IVectorQueryRouter, VectorQueryRouter>();
            services.AddSingleton<HealingService>();
            services.AddHostedService(sp => sp.GetRequiredService<HealingService>());
        }

        // RAG works in both modes; keep it outside the provider branch.
        services.AddSingleton<IReranker, LlmReranker>();
        services.AddSingleton(sp => new RetrievalPipeline(
            sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value,
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IEmbeddingDispatcher>(),
            sp.GetRequiredService<IVectorQueryRouter>(),
            sp.GetRequiredService<IReranker>(),
            sp.GetRequiredService<Metrics>(),
            new VectorLog<RetrievalPipeline>(sp.GetRequiredService<ILogger<RetrievalPipeline>>())));

        AddIngestion(services, configuration);
        return services;
    }

    /// <summary>
    /// Ingestion (phase 23) lives inside the vector-store branch on purpose: it writes to the
    /// vector store and nowhere else (D1), so with no store there is nothing for it to do and no
    /// reason for its services to exist. It is provider-agnostic — everything it touches is behind
    /// <see cref="IVectorStore"/> and <c>IEmbeddingDispatcher</c>.
    /// </summary>
    private static void AddIngestion(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.SectionName));
        services.AddSingleton<IValidateOptions<IngestionOptions>, IngestionOptionsValidator>();
        services.AddOptions<IngestionOptions>().ValidateOnStart();

        // The one place in the solution that references the PDF package (rule 5, phase-23 D3). It is
        // deliberately absent on a solo node, which answers a PDF upload with a 415 (phase-38 D5).
        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddSingleton<TextExtractor>();
        services.AddSingleton<DocumentIndex>();
        services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<DocumentIndex>(),
            sp.GetRequiredService<TextExtractor>(),
            sp.GetRequiredService<IEmbeddingDispatcher>(),
            sp.GetRequiredService<IOptions<IngestionOptions>>().Value,
            sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value,
            sp.GetRequiredService<Metrics>(),
            new VectorLog<IngestionPipeline>(sp.GetRequiredService<ILogger<IngestionPipeline>>())));
    }
}
