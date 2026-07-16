namespace InferHub.Coordinator.Services;

/// <summary>
/// One completed piece of fleet work, attributed to a client. This record is the entire data
/// model of usage accounting: a client id, a model name, two integers, a kind, a flag and a
/// timestamp. It does not contain the prompt, the completion, a hash of either, or a "sample" —
/// and there is no flag to add one, because a flag is an invitation (rule 7).
/// </summary>
public sealed record UsageRecord(
    string ClientId,
    string Model,
    string Kind,
    long PromptTokens,
    long CompletionTokens,
    bool Fallback,
    DateTimeOffset AtUtc)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

public sealed record UsageQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? ClientId = null,
    string? Model = null);

/// <summary>One row of the aggregate a billing question actually needs.</summary>
public sealed record UsageAggregate(
    string ClientId,
    string Model,
    long Requests,
    long PromptTokens,
    long CompletionTokens,
    long FallbackRequests)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

public interface IUsageLedger
{
    /// <summary>Append-only. Never throws into the request path — a metering failure must not fail the request it meters.</summary>
    ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageAggregate>> QueryAsync(UsageQuery query, CancellationToken cancellationToken = default);
}
