using System.Text.Json.Serialization;

namespace InferHub.Shared.Fit;

/// <summary>
/// Phase 120: the hub's model management on a node travels as a tool job to that one node — the capability
/// <see cref="Capability"/>, the operation as the job's model, a <see cref="ManagementRequest"/> as its payload. The answer
/// is the node's own control-API answer for the same question, so the console and the app show one estimate (118's
/// invariant), not a second opinion.
/// </summary>
public static class ManagementJob
{
    public const string Capability = "manage";

    public const string Catalog = "catalog";
    public const string Estimate = "estimate";
    public const string Install = "install";

    public static readonly IReadOnlyList<string> Operations = [Catalog, Estimate, Install];

    /// <summary>The node said no on its owner's behalf: <c>Node:Management:AllowFromHub</c> is off.</summary>
    public const string NotAllowed = "management_not_allowed";

    /// <summary>The owner's disk terms refused this install (<c>MaxModelGiB</c>, <c>KeepFreeGiB</c>).</summary>
    public const string OverQuota = "management_over_quota";

    private const string StatusPrefix = "status_";

    /// <summary>An error code carrying the control API's own status for the same refusal (400, 403, 409, 502).</summary>
    public static string StatusCode(int status) => StatusPrefix + status;

    /// <summary>The status a node's error code stands for; null when it carries none.</summary>
    public static int? StatusOf(string? errorCode) =>
        errorCode switch
        {
            NotAllowed => 403,
            OverQuota => 409,
            not null when errorCode.StartsWith(StatusPrefix, StringComparison.Ordinal)
                          && int.TryParse(errorCode.AsSpan(StatusPrefix.Length), out var status) => status,
            _ => null
        };
}

/// <summary>One question for <see cref="ManagementJob"/>: the fields of <c>/fit</c>, <c>/fit/install</c> and <c>/fit/catalog</c>.</summary>
public sealed record ManagementRequest(
    [property: JsonPropertyName("link")] string? Link = null,
    [property: JsonPropertyName("context")] int? Context = null,
    [property: JsonPropertyName("engine")] string? Engine = null,
    [property: JsonPropertyName("variant")] string? Variant = null,
    [property: JsonPropertyName("force")] bool Force = false,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("sort")] string? Sort = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("q")] string? Q = null,
    [property: JsonPropertyName("limit")] int? Limit = null,
    [property: JsonPropertyName("fitsOnly")] bool FitsOnly = false);

/// <summary><c>Node:Management</c> as the node reports it at registration — what the console shows beside the panel.</summary>
public sealed record ManagementTerms(
    [property: JsonPropertyName("allowFromHub")] bool AllowFromHub,
    [property: JsonPropertyName("maxModelGiB")] int? MaxModelGiB,
    [property: JsonPropertyName("keepFreeGiB")] int KeepFreeGiB);

/// <summary>
/// The node's answer to an install: the pull it agreed to, which the hub then sends as a model command so its progress
/// reaches the console as every other pull's does (26).
/// </summary>
public sealed record ManagementInstall(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("diskMiB")] long DiskMiB,
    [property: JsonPropertyName("command")] Contracts.ModelCommand Command);
