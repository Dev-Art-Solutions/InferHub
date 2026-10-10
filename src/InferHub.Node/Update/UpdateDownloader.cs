using System.Security.Cryptography;

namespace InferHub.Node.Update;

/// <summary>
/// Fetches a release's setup into the node's data directory and checks it against the SHA-256 published
/// beside it (phase 101, D4). The checksum comes from the same release, so it catches a broken or truncated
/// download — not a hostile release, and the README says so.
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    /// <summary>Downloads (or reuses a verified copy of) the setup and returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateRelease release, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        var expected = ParseChecksum(await GetStringAsync(release.ChecksumUrl, cancellationToken), release.SetupName);
        var target = Path.Combine(directory, release.SetupName);

        if (File.Exists(target) && string.Equals(await HashAsync(target, cancellationToken), expected, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var partial = target + ".part";

        using (var request = new HttpRequestMessage(HttpMethod.Get, release.SetupUrl))
        {
            request.Headers.UserAgent.ParseAdd($"InferHub-Node/{NodeVersion.Current}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"downloading {release.SetupName} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(file, cancellationToken);
        }

        var actual = await HashAsync(partial, cancellationToken);

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new UpdateException($"{release.SetupName} does not match its published SHA-256 (expected {expected}, got {actual}); it was deleted and nothing was run");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// <c>sha256sum</c>'s shape (<c>&lt;hex&gt;  &lt;name&gt;</c>), PowerShell's <c>Get-FileHash</c> hex alone, or
    /// either in upper case. A file listing several names is matched by the setup's.
    /// </summary>
    public static string ParseChecksum(string text, string setupName)
    {
        string? first = null;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || !IsHex(parts[0]))
            {
                continue;
            }

            if (parts.Length > 1 && string.Equals(parts[^1], setupName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }

            first ??= parts[0].ToLowerInvariant();
        }

        return first ?? throw new UpdateException($"the checksum published for {setupName} holds no SHA-256");
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"InferHub-Node/{NodeVersion.Current}");
        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"downloading the checksum {uri} answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }

    private static bool IsHex(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
}
