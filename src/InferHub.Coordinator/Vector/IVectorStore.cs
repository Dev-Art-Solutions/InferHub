using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

public interface IVectorStore
{
    Task<CollectionInfo> CreateCollectionAsync(string name, int dimension, string? distance, CancellationToken cancellationToken = default);

    Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    Task<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken = default);

    Task<VectorRecord> UpsertAsync(string collection, VectorUpsert upsert, CancellationToken cancellationToken = default);

    Task<VectorRecord?> GetAsync(string collection, string id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorMatch>> QueryAsync(string collection, VectorQuery query, CancellationToken cancellationToken = default);
}
