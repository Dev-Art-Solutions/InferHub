namespace InferHub.Coordinator.Vector;

/// <summary>
/// The startup step an <b>external</b> vector provider needs before its store can answer anything:
/// create the schema, warm the metadata cache, and prove the store is reachable with an actionable
/// message rather than 500-ing on every later call. The <c>local</c> provider has none — its store
/// loads from disk in its own constructor.
/// <para>
/// It is an <see cref="IHostedService"/> because the coordinator runs it as one. It is also a
/// <em>named</em> seam because <c>inferhub-migrate</c> (phase 35) has to run exactly this and nothing
/// else out of the hosted-service list: the tool composes a store, not a hub, and resolving every
/// <see cref="IHostedService"/> would drag in the replication and healing services, which want a node
/// registry and a dispatcher that a console tool has no business owning.
/// </para>
/// </summary>
public interface IVectorStoreBootstrapper : IHostedService;
