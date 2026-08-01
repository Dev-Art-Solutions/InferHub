using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The units fleet work is measured in (phase 42, D7), as the coordinator names them.
/// </summary>
/// <remarks>
/// The strings themselves are <see cref="UsageUnitKinds"/> in <c>InferHub.Shared</c>, because the
/// node's audio edge picks the unit and this ledger writes it — aliased rather than re-spelled, so
/// there is one definition and no way for the two ends to drift.
///
/// <para>A transcription has no token count and never will: the unit the work is actually in is
/// <em>seconds of audio</em>, and metering it as tokens would mean inventing a number. Phase-25 D3
/// is unchanged and is why adding a unit is safe — these are counts, computed from what was
/// processed, and there is still deliberately no field anywhere that could hold a sample of it.</para>
/// </remarks>
public static class UsageUnits
{
    public const string Tokens = UsageUnitKinds.Tokens;

    public const string AudioSeconds = UsageUnitKinds.AudioSeconds;

    public const string Characters = UsageUnitKinds.Characters;

    public static bool IsKnown(string? kind) =>
        kind is Tokens or AudioSeconds or Characters;
}

/// <summary>
/// One completed piece of fleet work, attributed to a client. This record is the entire data
/// model of usage accounting: a client id, a model name, a kind, some counts, a flag and a
/// timestamp. It does not contain the prompt, the completion, the audio, the transcript, a hash
/// of any of them, or a "sample" — and there is no flag to add one, because a flag is an
/// invitation (rule 7).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Units"/> and <see cref="UnitKind"/> were appended in phase 42 with defaults that
/// describe today's rows, so every existing consumer, every existing test and every row already in
/// a Postgres ledger keeps meaning exactly what it meant. The token fields are untouched.
/// </para>
/// <para>
/// For token work <see cref="Units"/> is the total token count — the same number, in the general
/// field — so a consumer that wants "how much work" without a switch on the kind can sum one
/// column per kind. Summing <em>across</em> kinds is meaningless and the aggregate below is shaped
/// so that nothing accidentally does it.
/// </para>
/// </remarks>
public sealed record UsageRecord(
    string ClientId,
    string Model,
    string Kind,
    long PromptTokens,
    long CompletionTokens,
    bool Fallback,
    DateTimeOffset AtUtc,
    double Units = 0,
    string UnitKind = UsageUnits.Tokens)
{
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>Token-metered work: the two counts, and the total mirrored into the general field.</summary>
    public static UsageRecord ForTokens(
        string clientId,
        string model,
        string kind,
        long promptTokens,
        long completionTokens,
        bool fallback,
        DateTimeOffset atUtc)
        => new(
            clientId,
            model,
            kind,
            promptTokens,
            completionTokens,
            fallback,
            atUtc,
            promptTokens + completionTokens,
            UsageUnits.Tokens);

    /// <summary>Work measured in something other than tokens. The token columns stay zero.</summary>
    public static UsageRecord ForUnits(
        string clientId,
        string model,
        string kind,
        double units,
        string unitKind,
        DateTimeOffset atUtc)
        => new(clientId, model, kind, 0, 0, false, atUtc, units, unitKind);
}

public sealed record UsageQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? ClientId = null,
    string? Model = null);

/// <summary>
/// One row of the aggregate a billing question actually needs.
/// </summary>
/// <remarks>
/// <b>The unit totals are separate columns, not one <c>units</c> sum</b> (phase 42). A client that
/// chatted and transcribed has rows in two units under the same model grouping, and a single sum
/// would add seconds to tokens and produce a number that is wrong in a way no reader can detect.
/// Three columns, each unambiguous, and a zero means zero rather than "some other unit".
/// </remarks>
public sealed record UsageAggregate(
    string ClientId,
    string Model,
    long Requests,
    long PromptTokens,
    long CompletionTokens,
    long FallbackRequests,
    double AudioSeconds = 0,
    double Characters = 0)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

public interface IUsageLedger
{
    /// <summary>Append-only. Never throws into the request path — a metering failure must not fail the request it meters.</summary>
    ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageAggregate>> QueryAsync(UsageQuery query, CancellationToken cancellationToken = default);
}
