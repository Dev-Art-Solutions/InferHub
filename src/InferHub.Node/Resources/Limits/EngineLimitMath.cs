using InferHub.Shared.Contracts;

namespace InferHub.Node.Resources.Limits;

/// <summary>
/// The pure arithmetic of phase 102: a coordinator's limit against the node's own ceiling. Same shape
/// as <c>NodeProfileClamp</c> (43 D1) — no I/O, nothing to mock — because this is the half of the
/// phase a hostile profile is aimed at.
/// </summary>
public static class EngineLimitMath
{
    /// <summary>
    /// What survives of <paramref name="hub"/> against <paramref name="node"/>: each field at or below
    /// the node's is kept, each above is refused naming the key and both numbers, and the node's own
    /// value stays in force. Never throws.
    /// </summary>
    public static EngineLimits? Narrow(
        string group,
        EngineLimits? node,
        EngineLimits? hub,
        ICollection<string> applied,
        ICollection<NodeProfileRefusal> refusals)
    {
        if (hub is null)
        {
            return null;
        }

        var key = $"Node:ResourceLimits:Engines:{group}";

        var cpu = Scalar(group, "cpuPercent", $"{key}:CpuPercent", node?.CpuPercent, hub.CpuPercent, 1, 100, "%", applied, refusals);
        var cores = Scalar(group, "cores", $"{key}:Cores", node?.Cores, hub.Cores, 1, int.MaxValue, " core(s)", applied, refusals);
        var memory = Scalar(group, "memoryMiB", $"{key}:MemoryMiB", node?.MemoryMiB, hub.MemoryMiB, 1, int.MaxValue, " MiB", applied, refusals);
        var vram = Scalar(group, "vramMiB", $"{key}:VramMiB", node?.VramMiB, hub.VramMiB, 1, int.MaxValue, " MiB", applied, refusals);
        var gpus = Devices(group, $"{key}:Gpus", node?.Gpus, hub.Gpus, applied, refusals);

        var kept = new EngineLimits(cpu, cores, memory, vram, gpus);
        return kept.IsEmpty ? null : kept;
    }

    /// <summary>Per field, the smaller of the two; a GPU list is the intersection. What the node enforces.</summary>
    public static EngineLimits Effective(EngineLimits? node, EngineLimits? hub) => new(
        Min(node?.CpuPercent, hub?.CpuPercent),
        Min(node?.Cores, hub?.Cores),
        Min(node?.MemoryMiB, hub?.MemoryMiB),
        Min(node?.VramMiB, hub?.VramMiB),
        (node?.Gpus, hub?.Gpus) switch
        {
            (null, null) => null,
            ({ } n, null) => Sorted(n),
            (null, { } h) => Sorted(h),
            ({ } n, { } h) => Sorted(n.Intersect(h))
        });

    private static int? Scalar(
        string group,
        string field,
        string key,
        int? ceiling,
        int? wanted,
        int min,
        int max,
        string unit,
        ICollection<string> applied,
        ICollection<NodeProfileRefusal> refusals)
    {
        if (wanted is not { } value)
        {
            return null;
        }

        if (value < min || value > max)
        {
            refusals.Add(new NodeProfileRefusal(
                $"limits:{group}:{field}",
                max == int.MaxValue
                    ? $"{field} {value} is not a usable limit; it must be at least {min}"
                    : $"{field} {value} is not a usable limit; it must be {min}–{max}"));

            return null;
        }

        if (ceiling is { } cap && value > cap)
        {
            refusals.Add(new NodeProfileRefusal(
                $"limits:{group}:{field}",
                $"{key} is {cap}{unit} on this node; a profile can lower it, never raise it to {value}{unit}"));

            return null;
        }

        applied.Add($"{group} {field} {value}{unit}");
        return value;
    }

    private static IReadOnlyList<int>? Devices(
        string group,
        string key,
        IReadOnlyList<int>? ceiling,
        IReadOnlyList<int>? wanted,
        ICollection<string> applied,
        ICollection<NodeProfileRefusal> refusals)
    {
        if (wanted is null)
        {
            return null;
        }

        if (wanted.Any(index => index < 0))
        {
            refusals.Add(new NodeProfileRefusal($"limits:{group}:gpus", "a GPU index cannot be negative"));
            return null;
        }

        var asked = Sorted(wanted);

        if (ceiling is not null)
        {
            var outside = asked.Where(index => !ceiling.Contains(index)).ToArray();

            if (outside.Length > 0)
            {
                // The whole list, not the part that fits: a profile that said "0 and 2" and got "0"
                // would be running on a card nobody chose for it.
                refusals.Add(new NodeProfileRefusal(
                    $"limits:{group}:gpus",
                    $"{key} on this node is [{string.Join(", ", ceiling)}]; a profile can pick from it, never add GPU {string.Join(", ", outside)}"));

                return null;
            }
        }

        applied.Add($"{group} gpus [{string.Join(", ", asked)}]");
        return asked;
    }

    private static int? Min(int? a, int? b) => (a, b) switch
    {
        (null, null) => null,
        ({ } x, null) => x,
        (null, { } y) => y,
        ({ } x, { } y) => Math.Min(x, y)
    };

    private static IReadOnlyList<int> Sorted(IEnumerable<int> devices) => devices.Distinct().Order().ToArray();
}
