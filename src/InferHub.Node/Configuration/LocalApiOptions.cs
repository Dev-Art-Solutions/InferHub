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
        return addresses.Count > 0 && addresses.All(url => TryParse(url, out var uri, out var wildcard) && !wildcard && uri!.IsLoopback);
    }

    public IReadOnlyList<string> SplitUrls()
        => Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parses a listen address the way <em>Kestrel</em> accepts one, not the way <see cref="Uri"/>
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>Uri.TryCreate</c> rejects <c>http://+:8080</c> and <c>http://*:8080</c>.</strong>
    /// Kestrel accepts both — they mean "every interface on this box" and are the standard
    /// container form, which is exactly what the node image sets. Validating with <c>Uri</c> alone
    /// therefore refused to start the shipped container with a bogus "must be an absolute http(s)
    /// URL" message, and solo mode was dead on arrival in Docker for the whole of v3.5.0. Found by
    /// running the published image, which is the only way this class of bug is ever found.
    /// </para>
    /// <para>
    /// So the wildcard host is swapped for a placeholder before parsing, and reported back through
    /// <paramref name="isWildcard"/> — because "did this parse?" and "is this loopback?" are two
    /// different questions and conflating them is how the bug happened.
    /// </para>
    /// </remarks>
    public static bool TryParse(string url, out Uri? parsed, out bool isWildcard)
    {
        isWildcard = false;
        parsed = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var candidate = url.Trim();

        foreach (var wildcard in (ReadOnlySpan<string>)["://+:", "://*:"])
        {
            var index = candidate.IndexOf(wildcard, StringComparison.Ordinal);

            if (index >= 0)
            {
                isWildcard = true;
                candidate = string.Concat(candidate.AsSpan(0, index), "://0.0.0.0:", candidate.AsSpan(index + wildcard.Length));
                break;
            }
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        // 0.0.0.0 and [::] are wildcards spelled out, and mean the same exposure.
        isWildcard |= uri.Host is "0.0.0.0" or "[::]" or "::";

        parsed = uri;
        return true;
    }
}
