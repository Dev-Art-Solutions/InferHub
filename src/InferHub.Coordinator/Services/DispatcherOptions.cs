using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class DispatcherOptions
{
    public const string SectionName = "Dispatcher";

    /// <summary>
    /// The deadline for every job whose capability has no entry in <see cref="Deadlines"/> — which,
    /// with the shipped config, is every job. A value below 1 is read as 1, as it always was.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Phase 89. Per-capability deadlines in seconds, keyed by the capability a job was routed on
    /// (<c>chat</c>, <c>embed</c>, <c>image</c>, <c>video</c>, or a custom tool's own name). Empty
    /// by default, so a deployment that sets nothing gets <see cref="TimeoutSeconds"/> for every
    /// job, exactly as before (89 D1). Keys are case-insensitive; values must be &gt;= 1.
    /// </summary>
    public Dictionary<string, int> Deadlines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The deadline for a job routed on <paramref name="capability"/>, and the config key it came
    /// from — the key is what the timeout message names, so an operator reading a failed job knows
    /// which number to raise (89 D3).
    /// </summary>
    public DispatchDeadline DeadlineFor(string? capability)
    {
        if (capability is not null)
        {
            // The binder may have replaced the dictionary with one using the default comparer, so a
            // case-insensitive lookup is done by hand rather than trusted to the instance.
            foreach (var (key, seconds) in Deadlines)
            {
                if (string.Equals(key, capability, StringComparison.OrdinalIgnoreCase))
                {
                    return new DispatchDeadline(
                        capability,
                        TimeSpan.FromSeconds(Math.Max(1, seconds)),
                        $"{SectionName}:Deadlines:{key}");
                }
            }
        }

        return new DispatchDeadline(
            capability,
            TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds)),
            $"{SectionName}:{nameof(TimeoutSeconds)}");
    }

    /// <summary>
    /// The capability an <see cref="InferenceJob"/> is routed on — the router's own reading
    /// (<see cref="CapabilityKinds.ForJobKind"/>), not a second one. <c>generate</c> is <c>chat</c>;
    /// the hub's internal kinds (<c>vector-query</c>, the corpus jobs) have no capability and always
    /// take <see cref="TimeoutSeconds"/> (89 D2).
    /// </summary>
    public DispatchDeadline DeadlineFor(InferenceJob job) => DeadlineFor(CapabilityKinds.ForJobKind(job.Kind));
}

/// <summary>How long the hub waits for one dispatched job, and which config key said so.</summary>
public readonly record struct DispatchDeadline(string? Capability, TimeSpan Timeout, string Source)
{
    /// <summary>
    /// The message a job that ran out of time ends with. Names the capability, the number and the
    /// key, because "The operation has timed out." after 302 seconds at progress 0 is what v3.28's
    /// F5 had to reverse-engineer three times.
    /// </summary>
    public TimeoutException Expired() => new(
        $"the hub's dispatch deadline for {Capability ?? "this job"} ({Timeout.TotalSeconds:0} s, {Source}) expired before the node answered");
}

/// <summary>
/// Refuses a deadline that cannot be meant. Unknown capability names are <em>allowed</em>: a custom
/// tool declares whatever capability it likes (phase-40 D1), and a hub that refused its name would
/// be deciding which tools may exist (89 D4).
/// </summary>
public sealed class DispatcherOptionsValidator : IValidateOptions<DispatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, DispatcherOptions options)
    {
        var failures = new List<string>();

        foreach (var (key, seconds) in options.Deadlines)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                failures.Add($"{DispatcherOptions.SectionName}:Deadlines has an empty capability name.");
            }
            else if (seconds < 1)
            {
                // Unlike TimeoutSeconds (clamped to 1 since v1.x, and kept that way), a new key can be
                // strict: a 0 here is a typo, and clamping it would give that capability one second.
                failures.Add($"{DispatcherOptions.SectionName}:Deadlines:{key} must be >= 1 (got {seconds}).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
