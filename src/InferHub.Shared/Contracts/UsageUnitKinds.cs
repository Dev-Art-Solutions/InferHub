namespace InferHub.Shared.Contracts;

/// <summary>
/// The units fleet work is measured in, as they appear in a usage row (phase 42, D7).
/// </summary>
/// <remarks>
/// <para>
/// They live in <c>InferHub.Shared</c> rather than beside the ledger because the node's audio edge
/// decides which unit a request is in, and the coordinator's ledger writes it — two projects, one
/// spelling. A solo node and a hub that disagreed about whether the string is
/// <c>audio_seconds</c> or <c>audioSeconds</c> would produce two ledgers nobody can add together.
/// </para>
/// <para>
/// Rule 7 is unchanged by their existence: these name a <em>count</em>, and there is deliberately no
/// unit whose value is text.
/// </para>
/// </remarks>
public static class UsageUnitKinds
{
    public const string Tokens = "tokens";

    public const string AudioSeconds = "audio_seconds";

    public const string Characters = "characters";
}
