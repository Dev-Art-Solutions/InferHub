// Phase 38 moved the pure retrieval core — the store engine, the retrieval and ingestion pipelines
// and their contracts — out of InferHub.Coordinator and into InferHub.Shared, so a solo node can run
// the same code rather than a second copy of it (D2). These two global usings are why that move
// touched no consuming file: everything that said `using InferHub.Coordinator.Vector;` still
// resolves, and a reader can still find the types by name.
global using InferHub.Shared.Ingestion;
global using InferHub.Shared.Vector;

// Phase 44 moved the Qdrant connector the same way and for the same reason: a node the hub assigns
// a Qdrant corpus runs *this* store, not a second one (D2). `QdrantBootstrapper` stayed behind, so
// `using InferHub.Coordinator.Vector.Qdrant;` still resolves wherever it was already written.
global using InferHub.Shared.Vector.Qdrant;
