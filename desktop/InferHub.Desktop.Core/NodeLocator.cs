namespace InferHub.Desktop;

/// <summary>One place a node's control token may be, and the node it would open (phase 115 D2).</summary>
public sealed record TokenCandidate(string Path, string Source);

/// <summary>What looking for a node found.</summary>
/// <param name="Endpoint">The node that answered, or null.</param>
/// <param name="Unreadable">Token files that exist but could not be read (the Windows service's is for administrators).</param>
public sealed record LocateResult(NodeEndpoint? Endpoint, IReadOnlyList<string> Tried, IReadOnlyList<string> Unreadable);

/// <summary>
/// Finds a node on this machine to attach to (115 D2): the one the user configured, then the app's own, then an installed
/// service's — each by its control token where that platform's setup keeps the node's data.
/// </summary>
public sealed class NodeLocator(Func<NodeEndpoint, CancellationToken, Task<bool>>? probe = null)
{
    public const string DefaultUrl = "http://localhost:5081";

    private readonly Func<NodeEndpoint, CancellationToken, Task<bool>> answers = probe ?? ProbeAsync;

    /// <summary>Where a token may be on this platform, most specific first.</summary>
    public static IReadOnlyList<TokenCandidate> Candidates()
    {
        var list = new List<TokenCandidate>
        {
            new(Path.Combine(AppPaths.NodeData, "control.token"), "this app's own node")
        };

        if (OperatingSystem.IsWindows())
        {
            // 101 D1: the Windows setup's data directory.
            list.Add(new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InferHub", "Node", "control.token"), "the InferHub Node service"));
            list.Add(new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "InferHub Node", "control.token"), "the InferHub Node service (program folder)"));
        }
        else
        {
            list.Add(new("/var/lib/inferhub-node/control.token", "a systemd node"));
            list.Add(new("/opt/inferhub-node/control.token", "a node in /opt"));
        }

        return list;
    }

    public async Task<LocateResult> LocateAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        var url = new Uri(string.IsNullOrWhiteSpace(settings.Url) ? DefaultUrl : settings.Url.Trim());
        var tried = new List<string>();
        var unreadable = new List<string>();

        if (settings.Token is { Length: > 0 } pasted)
        {
            var configured = new NodeEndpoint(url, pasted.Trim(), "the token in this app's settings");
            tried.Add(configured.Source);

            if (await answers(configured, cancellationToken))
            {
                return new LocateResult(configured, tried, unreadable);
            }
        }

        foreach (var candidate in Candidates())
        {
            string? token;

            try
            {
                token = File.Exists(candidate.Path) ? File.ReadAllText(candidate.Path).Trim() : null;
            }
            catch (UnauthorizedAccessException)
            {
                unreadable.Add(candidate.Path);
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var endpoint = new NodeEndpoint(url, token, candidate.Source);
            tried.Add($"{candidate.Source} ({candidate.Path})");

            if (await answers(endpoint, cancellationToken))
            {
                return new LocateResult(endpoint, tried, unreadable);
            }
        }

        return new LocateResult(null, tried, unreadable);
    }

    /// <summary>A node answers when its control status comes back with this token.</summary>
    public static async Task<bool> ProbeAsync(NodeEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NodeControlClient(endpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.StatusAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or NodeControlException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
