using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using InferHub.Shared.HuggingFace;

namespace InferHub.Node.Backends.HuggingFace;

/// <summary>One file in a repository at one commit, as the Hub's tree listing reports it.</summary>
public sealed record HfFile(string Path, long Size, string? Sha256);

/// <summary>
/// The three Hugging Face calls a download needs (phase 98, D2): the commit a revision points at, the
/// file tree at that commit, and one file's bytes. Hand-rolled over <see cref="HttpClient"/>, as every
/// other remote API here is (rule 5 — no <c>huggingface_hub</c> in a .NET process).
/// </summary>
public sealed class HuggingFaceClient(HttpClient http, Uri endpoint, string? token)
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The commit sha a branch or tag points at — downloads pin to it, so a push mid-download cannot mix two versions.</summary>
    public async Task<string> CommitAsync(HfReference reference, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"api/models/{reference.RepoId}/revision/{Uri.EscapeDataString(reference.Revision)}", reference, cancellationToken);

        return document.RootElement.TryGetProperty("sha", out var sha) && sha.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Hugging Face did not say which commit '{reference.RepoId}@{reference.Revision}' is");
    }

    public async Task<IReadOnlyList<HfFile>> TreeAsync(HfReference reference, string commit, CancellationToken cancellationToken)
    {
        var files = new List<HfFile>();
        string? next = $"api/models/{reference.RepoId}/tree/{commit}?recursive=true";

        // The tree is paged past a thousand entries, with the next page in a Link header.
        while (next is not null)
        {
            using var request = Request(HttpMethod.Get, next);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ApiTimeout);
            using var response = await http.SendAsync(request, timeout.Token);
            await EnsureAsync(response, reference);

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type) && type.GetString() == "file"
                    && entry.TryGetProperty("path", out var path) && path.GetString() is { } value)
                {
                    var size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    string? sha = null;

                    if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                    {
                        sha = lfs.TryGetProperty("oid", out var oid) ? oid.GetString() : null;

                        if (lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var big))
                        {
                            size = big;
                        }
                    }

                    files.Add(new HfFile(value, size, sha));
                }
            }

            next = NextPage(response);
        }

        return files;
    }

    /// <summary>
    /// Downloads one file to <paramref name="destination"/>, resuming a <c>.partial</c> that an
    /// interrupted run left, checking the size and (for an LFS file) the sha256, and renaming last —
    /// so a file under its real name is always a whole one.
    /// </summary>
    public async Task DownloadAsync(
        HfReference reference,
        string commit,
        HfFile file,
        string destination,
        Action<long> progress,
        CancellationToken cancellationToken)
    {
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        if (have > file.Size)
        {
            File.Delete(partial);
            have = 0;
        }

        if (have < file.Size || file.Size == 0)
        {
            var path = string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
            using var request = Request(HttpMethod.Get, $"{reference.RepoId}/resolve/{commit}/{path}");

            if (have > 0)
            {
                request.Headers.Range = new RangeHeaderValue(have, null);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureAsync(response, reference);

            // A server that ignores Range sends the whole file: start over rather than append it.
            var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;

            if (!append)
            {
                have = 0;
            }

            progress(have);

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    have += read;
                    progress(have);
                }
            }
        }

        var length = new FileInfo(partial).Length;

        if (length != file.Size)
        {
            throw new InvalidOperationException($"'{file.Path}' arrived with {length} bytes, Hugging Face lists {file.Size}; run the pull again to resume");
        }

        if (file.Sha256 is { Length: 64 } expected)
        {
            await using var stream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                stream.Close();
                File.Delete(partial);
                throw new InvalidOperationException($"'{file.Path}' does not match its sha256 on Hugging Face (got {actual[..12]}…, expected {expected[..12]}…); it was deleted, run the pull again");
            }
        }

        File.Move(partial, destination, overwrite: true);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, HfReference reference, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        using var response = await http.SendAsync(request, timeout.Token);
        await EnsureAsync(response, reference);

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
    }

    private HttpRequestMessage Request(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, relative.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(relative)
            : new Uri(endpoint, relative));

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, HfReference reference)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new InvalidOperationException(
                $"Hugging Face refused '{reference.RepoId}' ({(int)response.StatusCode}): it is gated or private. Accept its licence on huggingface.co and give this node a token in {HuggingFaceOptions.SectionName}:Token."),
            HttpStatusCode.NotFound => new InvalidOperationException(
                $"Hugging Face has no '{reference.RepoId}' at '{reference.Revision}' (404), or it is private and this node has no token for it."),
            _ => new InvalidOperationException(
                $"Hugging Face answered {(int)response.StatusCode} for '{reference.RepoId}': {Trim(await response.Content.ReadAsStringAsync())}")
        };
    }

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var link in links.SelectMany(l => l.Split(',')))
        {
            if (link.Contains("rel=\"next\"", StringComparison.Ordinal))
            {
                var start = link.IndexOf('<');
                var end = link.IndexOf('>');
                return start >= 0 && end > start ? link[(start + 1)..end] : null;
            }
        }

        return null;
    }

    private static string Trim(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
