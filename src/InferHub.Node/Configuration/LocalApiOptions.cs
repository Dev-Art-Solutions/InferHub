namespace InferHub.Node.Configuration;

/// <summary>
/// Solo mode (phase 37): the node serves the coordinator's client-facing API itself, so a
/// deployment of one machine needs one process.
/// </summary>
/// <remarks>
/// Off by default and loopback when on. Two deliberate choices: a node that acquired an open
/// inference port because somebody ran an upgrade would be a betrayal rather than a feature, and an
/// unauthenticated endpoint on a LAN hands arbitrary compute on somebody's GPU to anyone who can
/// reach the port.
/// </remarks>
public sealed class LocalApiOptions
{
    public const string SectionName = "LocalApi";

    public bool Enabled { get; set; }

    /// <summary>
    /// Where to listen. Loopback by default, and deliberately <em>not</em> 5080 — a laptop running
    /// a hub and a node together for a demo must not collide.
    /// </summary>
    public string Urls { get; set; } = "http://localhost:5081";

    /// <summary>
    /// Bearer tokens accepted on <c>/api</c> and <c>/v1</c>. The node's own list; it has nothing to
    /// do with <c>Coordinator:EnrollmentSecret</c>, which authenticates this node <em>to</em> a hub.
    /// </summary>
    public IList<string> ApiKeys { get; set; } = new List<string>();

    /// <summary>
    /// The explicit consent to serve a non-loopback address with no keys. Off by default, and the
    /// node warns on every boot when it is on.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Matches the coordinator's <c>Auth:RequireAuthForLoopback</c>: local curl just works, exactly
    /// as the quickstart promises for the hub.
    /// </summary>
    public bool RequireAuthForLoopback { get; set; }

    /// <summary>
    /// How long a request waits for a concurrency slot before <c>503</c> (phase-37 D9). Only bites
    /// when <c>Node:MaxConcurrency</c> is set.
    /// </summary>
    public int MaxWaitSeconds { get; set; } = 30;

    /// <summary>
    /// True when every configured listen address is loopback. This is what decides whether the node
    /// may serve without keys — see <c>LocalApiOptionsValidator</c>.
    /// </summary>
    public bool BindsLoopbackOnly()
    {
        var addresses = SplitUrls();

        // No parseable address is not "loopback"; it is a broken config, and the validator says so.
        // Answering true here would let it through as if it were safe.
        return addresses.Count > 0 && addresses.All(IsLoopback);
    }

    public IReadOnlyList<string> SplitUrls()
        => Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Kestrel's wildcards mean "every interface on this box", which is the opposite of loopback
        // and is exactly the container case. Uri.IsLoopback does not recognise them as hosts, so
        // they have to be named.
        return uri.Host is not ("+" or "*" or "0.0.0.0" or "[::]") && uri.IsLoopback;
    }
}
