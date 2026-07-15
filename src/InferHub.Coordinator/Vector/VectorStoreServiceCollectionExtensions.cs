using InferHub.Coordinator.Ingestion;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Vector.Postgres;
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
            services.AddHostedService<PostgresBootstrapper>();
        }
        else
        {
            services.AddSingleton<LocalVectorStore>();
            services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<LocalVectorStore>());
            services.AddSingleton<ReplicationCoordinator>();
            services.AddHostedService(sp => sp.GetRequiredService<ReplicationCoordinator>());
            services.AddSingleton<IVectorQueryRouter, VectorQueryRouter>();
            services.AddSingleton<HealingService>();
            services.AddHostedService(sp => sp.GetRequiredService<HealingService>());
        }

        // RAG works in both modes; keep it outside the provider branch.
        services.AddSingleton<IReranker, LlmReranker>();
        services.AddSingleton<RetrievalPipeline>();

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

        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddSingleton<TextExtractor>();
        services.AddSingleton<DocumentIndex>();
        services.AddSingleton<IngestionPipeline>();
    }
}
