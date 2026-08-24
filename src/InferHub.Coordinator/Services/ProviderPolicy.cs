namespace InferHub.Coordinator.Services;

/// <summary>
/// When a provider serves (phase 65, D1). One word rather than two: <c>Trigger</c>'s two values are
/// the first two of these, so a v3.29–v3.32 configuration says exactly what it said before, and the
/// two new ones are the ones the track exists for — a provider that is a <em>choice</em> rather than
/// the thing that happens after routing failed.
/// </summary>
/// <remarks>
/// The field is named <c>Policy</c> and not <c>Trigger</c> because a trigger whose value is "always,
/// first" is a lie in a configuration file. <see cref="ProviderOptionsValidator"/> refuses a
/// deployment that writes both and makes them disagree — precedence is not a thing this hub uses to
/// decide whose servers see a prompt (61 D1).
/// </remarks>
public static class ProviderPolicy
{
    /// <summary>The default, and what every release since v2.4 has done: only when no node holds it.</summary>
    public const string NoNode = FallbackOptions.TriggerNoNode;

    /// <summary>Also when every node holding it is at its declared cap.</summary>
    public const string NoNodeOrSaturated = FallbackOptions.TriggerNoNodeOrSaturated;

    /// <summary>
    /// Asked first, with the fleet as the backstop when the call fails (65 D3). Falling back to a
    /// local node is not a disclosure, which is why this one may do it quietly.
    /// </summary>
    public const string Prefer = "prefer";

    /// <summary>
    /// Asked always, and a node holding the same name never serves it — which is what makes this
    /// the answer to a name collision rather than a stronger <see cref="Prefer"/>. A failure here is
    /// a 502 naming the provider, never a quiet answer from different weights.
    /// </summary>
    public const string Only = "only";

    public static readonly IReadOnlyList<string> All = [NoNode, NoNodeOrSaturated, Prefer, Only];

    public static bool IsKnown(string? value) => Normalize(value) is not null;

    /// <summary>The canonical spelling, or null when the value is not one of the four.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return All.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for the two policies that ask the provider before the fleet has failed.</summary>
    public static bool IsFirstChoice(string policy)
        => policy is Prefer or Only;
}
