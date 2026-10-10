using System.Reflection;
using System.Text.Json;

namespace InferHub.Node.Update;

/// <summary>A release this node could move to: its version, its setup, and the checksum beside it.</summary>
public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string SetupName,
    Uri SetupUrl,
    Uri ChecksumUrl,
    string? PageUrl);

/// <summary>Where releases come from. The one seam the tests replace.</summary>
public interface IReleaseFeed
{
    /// <summary>The newest release above <paramref name="current"/> with a setup attached, or null.</summary>
    Task<UpdateRelease?> NewestAboveAsync(Version current, CancellationToken cancellationToken);
}

/// <summary>
/// GitHub's release list (or a mirror of its shape), phase 101 D3: the highest non-draft, non-prerelease
/// <c>vX.Y.Z</c> above the running version <b>with its setup and checksum attached</b>. The setup is built
/// by a workflow minutes after the tag, so a release without one is "not yet", never an error.
/// </summary>
public sealed class GitHubReleaseFeed(HttpClient http, Uri source) : IReleaseFeed
{
    public const string HttpClientName = "InferHub.Node.Update";

    public async Task<UpdateRelease?> NewestAboveAsync(Version current, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        // GitHub refuses a request without one, with a 403 that reads like a rate limit.
        request.Headers.UserAgent.ParseAdd($"InferHub-Node/{NodeVersion.Current}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"the release feed {source} answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return Pick(document.RootElement, current);
    }

    /// <summary>The choice itself, pure, so the tests can hand it the feed's own JSON.</summary>
    public static UpdateRelease? Pick(JsonElement releases, Version current)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            throw new UpdateException("the release feed did not answer with a list of releases");
        }

        UpdateRelease? best = null;

        foreach (var release in releases.EnumerateArray())
        {
            if (Bool(release, "draft") || Bool(release, "prerelease")
                || !release.TryGetProperty("tag_name", out var tagElement)
                || tagElement.GetString() is not { } tag
                || !NodeVersion.TryParseTag(tag, out var version)
                || version <= current
                || (best is not null && version <= best.Version))
            {
                continue;
            }

            var setupName = SetupName(version);
            Uri? setup = null;
            Uri? checksum = null;

            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;

                    if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        continue;
                    }

                    if (string.Equals(name, setupName, StringComparison.OrdinalIgnoreCase))
                    {
                        setup = uri;
                    }
                    else if (string.Equals(name, setupName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        checksum = uri;
                    }
                }
            }

            if (setup is null || checksum is null)
            {
                continue;
            }

            var page = release.TryGetProperty("html_url", out var html) ? html.GetString() : null;
            best = new UpdateRelease(version, tag, setupName, setup, checksum, page);
        }

        return best;
    }

    /// <summary>The setup's file name for a version — what the workflow attaches and what the feed looks for.</summary>
    public static string SetupName(Version version) => $"InferHub-Node-Setup-{NodeVersion.Format(version)}-win-x64.exe";

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>The running version, as the release feed compares it.</summary>
public static class NodeVersion
{
    /// <summary>
    /// <c>AssemblyInformationalVersion</c> without its <c>+commit</c> suffix — what <c>&lt;Version&gt;</c> in
    /// <c>Directory.Build.props</c> said when this was built.
    /// </summary>
    public static string Current { get; } = Read();

    public static Version Parsed => TryParseTag(Current, out var version) ? version : new Version(0, 0, 0);

    /// <summary><c>v3.66.0</c> or <c>3.66.0</c> → 3.66.0. A pre-release (<c>3.66.0-rc1</c>) is not one.</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = (tag ?? string.Empty).Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+');

        if (plus >= 0)
        {
            text = text[..plus];
        }

        if (text.Contains('-') || !Version.TryParse(text, out var parsed) || parsed.Build < 0)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, parsed.Build);
        return true;
    }

    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static string Read()
    {
        var informational = typeof(NodeVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (TryParseTag(informational, out var version))
        {
            return Format(version);
        }

        return typeof(NodeVersion).Assembly.GetName().Version is { } assembly
            ? Format(assembly)
            : "0.0.0";
    }
}

/// <summary>An update step that failed, with a sentence an operator can act on.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
