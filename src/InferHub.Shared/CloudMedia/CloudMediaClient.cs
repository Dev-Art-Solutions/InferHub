using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// A picture, a voice or a transcript from a cloud vendor, in the shape a local worker answers with
/// (104 D1): a <see cref="ToolJob"/> in, a <see cref="ToolResult"/> out.
/// </summary>
/// <remarks>
/// <para>
/// Both hosts hold one of these over one <see cref="HttpClient"/> with no base address and no
/// default headers: the route carries the vendor's URL and the key, set per request, so one client
/// serves every provider and no header set for one vendor ever reaches another.
/// </para>
/// <para>
/// <b>Nothing here logs</b> — not the prompt, not the audio, not the picture, not the key (rule 7,
/// 104 D6). The caller logs the capability, the model, the provider and the outcome.
/// </para>
/// </remarks>
/// <param name="maxDownloadBytes">
/// The largest clip a vendor URI may hand back (105 D5) — the host's <c>Tools:MaxAttachmentBytes</c>, like
/// any worker's output.
/// </param>
/// <param name="pollInterval">How often a vendor's long-running operation is asked about (105 D1). Ten seconds when null.</param>
public sealed class CloudMediaClient(HttpClient http, long maxDownloadBytes = ToolAttachmentLimits.DefaultMaxBytes, TimeSpan? pollInterval = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="attachments">
    /// The job's bytes. Null means <see cref="ToolJob.Attachments"/>; a node that pulled a streamed
    /// upload passes what it read.
    /// </param>
    public async Task<ToolResult> RunAsync(
        ToolJob job,
        CloudMediaRoute route,
        IReadOnlyList<ToolAttachment>? attachments,
        CancellationToken cancellationToken)
        => (await RunReportedAsync(job, route, attachments, cancellationToken)).Result;

    /// <summary>
    /// The same call, saying whether anything reached the vendor. A request refused before the call
    /// (104 D4) is the caller's mistake and not the provider's failure, and a host's counters must not
    /// say a vendor failed — or was even asked — when nothing was sent (found by the v3.69 live run).
    /// </summary>
    /// <param name="progress">
    /// Where a clip's polling reports itself (105 D1), as the <c>progress</c> frames a worker sends, so
    /// <c>/api/videos/*/events</c> moves for a cloud clip too. Null for everything that is one request.
    /// </param>
    public async Task<CloudMediaOutcome> RunReportedAsync(
        ToolJob job,
        CloudMediaRoute route,
        IReadOnlyList<ToolAttachment>? attachments,
        CancellationToken cancellationToken,
        IProgress<ToolChunk>? progress = null)
    {
        // A clip takes minutes (105 D4): a provider's default of 300 s would cut Veo off at its own peak.
        var seconds = string.Equals(route.Capability, CapabilityKinds.Video, StringComparison.OrdinalIgnoreCase)
            ? Math.Max(route.TimeoutSeconds, GeminiVideo.MinTimeoutSeconds)
            : route.TimeoutSeconds;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, seconds)));

        var call = new CloudMediaCall(http, job, route, attachments ?? job.Attachments ?? [], timeout.Token)
        {
            Progress = progress,
            PollInterval = pollInterval ?? TimeSpan.FromSeconds(10),
            MaxDownloadBytes = maxDownloadBytes,
            TimeoutSeconds = seconds
        };
        var result = await RunCoreAsync(call, job, route, cancellationToken);
        return new CloudMediaOutcome(result, call.Sent);
    }

    private static async Task<ToolResult> RunCoreAsync(
        CloudMediaCall call,
        ToolJob job,
        CloudMediaRoute route,
        CancellationToken cancellationToken)
    {
        try
        {
            using var payload = ParsePayload(job.Payload);
            var root = payload.RootElement;

            return (route.Dialect, route.Capability) switch
            {
                (CloudMediaDialects.OpenAi, CapabilityKinds.Image) => await OpenAiMedia.GenerateAsync(call, root),
                (CloudMediaDialects.OpenAi, CapabilityKinds.ImageEdit) => await OpenAiMedia.EditAsync(call, root),
                (CloudMediaDialects.OpenAi, CapabilityKinds.Speak) => await OpenAiMedia.SpeakAsync(call, root),
                (CloudMediaDialects.OpenAi, CapabilityKinds.Transcribe) => await OpenAiMedia.TranscribeAsync(call, root),
                (CloudMediaDialects.Gemini, CapabilityKinds.Image) => await GeminiMedia.GenerateAsync(call, root),
                (CloudMediaDialects.Gemini, CapabilityKinds.ImageEdit) => await GeminiMedia.EditAsync(call, root),
                (CloudMediaDialects.Gemini, CapabilityKinds.Speak) => await GeminiMedia.SpeakAsync(call, root),
                (CloudMediaDialects.Gemini, CapabilityKinds.Transcribe) => await GeminiMedia.TranscribeAsync(call, root),
                (CloudMediaDialects.Gemini, CapabilityKinds.Video) => await GeminiVideo.GenerateAsync(call, root),
                (CloudMediaDialects.OpenAi, CapabilityKinds.Video) => throw new CloudMediaRefusal(CloudMediaDialects.NoOpenAiVideo),
                _ => ToolResult.Failed(job.JobId, $"provider '{route.ProviderId}' ({route.Dialect}) does not serve '{route.Capability}'")
            };
        }
        catch (CloudMediaRefusal refusal)
        {
            return ToolResult.Refused(job.JobId, refusal.Message, refusal.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Refused(job.JobId, "the job was cancelled", ToolErrorCodes.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Failed(
                job.JobId,
                $"provider '{route.ProviderId}' did not answer within {call.TimeoutSeconds}s (its TimeoutSeconds)");
        }
        catch (HttpRequestException ex)
        {
            // The message names a host and a socket error, never a header — it cannot carry the key.
            return ToolResult.Failed(job.JobId, $"could not reach provider '{route.ProviderId}': {ex.Message}");
        }
        catch (JsonException)
        {
            return ToolResult.Failed(job.JobId, $"provider '{route.ProviderId}' answered with something that is not JSON");
        }
        catch (InvalidDataException ex)
        {
            // A 200 whose body is not the answer it should be: the vendor's fault, a 502.
            return ToolResult.Failed(job.JobId, ex.Message);
        }
    }

    private static JsonDocument ParsePayload(string? payload)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        }
        catch (JsonException)
        {
            throw new CloudMediaRefusal("the tool payload is not valid JSON");
        }
    }
}

/// <summary>
/// How often a vendor's long-running operation is polled (105 D1). Registered by nobody in production —
/// the client's ten seconds is the answer there — and by a test that cannot wait for it.
/// </summary>
public sealed record CloudMediaTiming(TimeSpan PollInterval);

/// <summary>A cloud job's answer, and whether any request reached the vendor to produce it.</summary>
public sealed record CloudMediaOutcome(ToolResult Result, bool Sent);

/// <summary>A request refused before the vendor is called (104 D4) — rendered as a 400.</summary>
internal sealed class CloudMediaRefusal(string message, string code = ToolErrorCodes.InvalidRequest) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Everything one vendor call needs, so the dialect files stay a list of requests and readings.</summary>
internal sealed class CloudMediaCall(
    HttpClient http,
    ToolJob job,
    CloudMediaRoute route,
    IReadOnlyList<ToolAttachment> attachments,
    CancellationToken cancellationToken)
{
    public ToolJob Job { get; } = job;

    public CloudMediaRoute Route { get; } = route;

    public IReadOnlyList<ToolAttachment> Attachments { get; } = attachments;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>True once a request has been handed to the vendor's socket.</summary>
    public bool Sent { get; private set; }

    public IProgress<ToolChunk>? Progress { get; init; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    public long MaxDownloadBytes { get; init; } = ToolAttachmentLimits.DefaultMaxBytes;

    /// <summary>The whole call's allowance, which a clip's progress is measured against.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>
    /// A clip's progress as a worker's frame: seconds elapsed of the seconds this provider is allowed
    /// (105 D1). A vendor's operation reports no percentage, and this is the one fraction that is true.
    /// </summary>
    public void ReportProgress(DateTimeOffset started)
    {
        if (Progress is null)
        {
            return;
        }

        var elapsed = (int)Math.Max(0, (DateTimeOffset.UtcNow - started).TotalSeconds);
        Progress.Report(new ToolChunk(
            Job.JobId,
            JsonSerializer.Serialize(new { type = "progress", step = elapsed, totalSteps = Math.Max(1, TimeoutSeconds) }),
            Done: false));
    }

    /// <summary>
    /// Fetches a URI the vendor chose (105 D5). The key goes only to the vendor's own host: a URI on any
    /// other host is refused before anything is sent, and a redirect is followed here, by hand, without
    /// the key — the hosts build this client with redirects off, because .NET strips only
    /// <c>Authorization</c> on a redirect and Google's key travels in <c>x-goog-api-key</c>.
    /// </summary>
    public async Task<byte[]> DownloadAsync(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidDataException($"provider '{Route.ProviderId}' named a download that is not an http(s) URL");
        }

        var home = new Uri(Route.BaseUrl, UriKind.Absolute);

        if (!string.Equals(target.Host, home.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"provider '{Route.ProviderId}' named a download on '{target.Host}', not its own host '{home.Host}'; the key is not sent there");
        }

        var withKey = true;

        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);

            if (withKey && !string.IsNullOrWhiteSpace(Route.ApiKey))
            {
                if (Route.Dialect == CloudMediaDialects.Gemini)
                {
                    request.Headers.TryAddWithoutValidation("x-goog-api-key", Route.ApiKey.Trim());
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Route.ApiKey.Trim());
                }
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                target = location.IsAbsoluteUri ? location : new Uri(target, location);
                withKey = string.Equals(target.Host, home.Host, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException($"downloading the clip from provider '{Route.ProviderId}' answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxDownloadBytes)
            {
                throw new InvalidDataException($"the clip from provider '{Route.ProviderId}' is {length} bytes, over {MaxDownloadBytes} (Tools:MaxAttachmentBytes)");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, CancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    throw new InvalidDataException($"the clip from provider '{Route.ProviderId}' is over {MaxDownloadBytes} bytes (Tools:MaxAttachmentBytes)");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        throw new InvalidDataException($"downloading the clip from provider '{Route.ProviderId}' redirected more than five times");
    }

    public ToolAttachment? Attachment(string name) =>
        Attachments.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public Uri Url(string relative) =>
        new(Route.BaseUrl.TrimEnd('/') + "/" + relative.TrimStart('/'), UriKind.Absolute);

    /// <summary>Sends one request with the dialect's credential header, and answers the vendor's error as a result.</summary>
    public async Task<(HttpResponseMessage? Response, ToolResult? Failure)> SendAsync(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(Route.ApiKey))
        {
            if (Route.Dialect == CloudMediaDialects.Gemini)
            {
                // Never an Authorization header for Gemini — a Bearer token there is a 401 that reads like a bad key (64).
                request.Headers.TryAddWithoutValidation("x-goog-api-key", Route.ApiKey.Trim());
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Route.ApiKey.Trim());
            }
        }

        Sent = true;
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, CancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (response, null);
        }

        using (response)
        {
            return (null, await VendorFailureAsync(response));
        }
    }

    public async Task<(JsonDocument? Document, ToolResult? Failure)> SendJsonAsync(HttpRequestMessage request)
    {
        var (response, failure) = await SendAsync(request);

        if (response is null)
        {
            return (null, failure);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(CancellationToken);
            return (JsonDocument.Parse(text), null);
        }
    }

    public HttpRequestMessage PostJson(string relative, object body) =>
        new(HttpMethod.Post, Url(relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, CloudMediaClient.Json), System.Text.Encoding.UTF8, "application/json")
        };

    /// <summary>
    /// 104 D6: the vendor's status becomes the kind of failure the edges already render, and its own
    /// message travels — it is the vendor's sentence about the request, and nothing here rewrites it.
    /// </summary>
    private async Task<ToolResult> VendorFailureAsync(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var message = await VendorMessageAsync(response);
        var who = $"provider '{Route.ProviderId}' ({Route.UpstreamModel})";

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
                ? (int)Math.Ceiling(delta.TotalSeconds)
                : 30;

            return ToolResult.Retry(Job.JobId, $"{who} is rate-limiting this key (429): {message}", retryAfter);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The caller cannot fix the operator's key, so this is not the 400 a vendor 4xx otherwise is.
            return ToolResult.Failed(Job.JobId, $"{who} refused the configured API key ({status}): {message}");
        }

        if (status is >= 400 and < 500)
        {
            return ToolResult.Refused(Job.JobId, $"{who} refused the request ({status}): {message}", ToolErrorCodes.InvalidRequest);
        }

        return ToolResult.Failed(Job.JobId, $"{who} failed ({status}): {message}");
    }

    private async Task<string> VendorMessageAsync(HttpResponseMessage response)
    {
        string text;

        try
        {
            text = await response.Content.ReadAsStringAsync(CancellationToken);
        }
        catch (Exception)
        {
            return "no body";
        }

        try
        {
            using var document = JsonDocument.Parse(text);

            // OpenAI and Gemini agree on this much: {"error": {"message": "..."}}.
            if (document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind is JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind is JsonValueKind.String)
                {
                    return message.GetString() ?? "no message";
                }

                if (error.ValueKind is JsonValueKind.String)
                {
                    return error.GetString() ?? "no message";
                }
            }
        }
        catch (JsonException)
        {
        }

        return text.Length > 300 ? text[..300] + "…" : string.IsNullOrWhiteSpace(text) ? "no body" : text;
    }
}

/// <summary>The fields of a worker payload the dialects read, in one place.</summary>
internal static class Payload
{
    public static string? Text(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Int(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    public static double? Number(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    public static bool True(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    public static bool Present(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null;
}
