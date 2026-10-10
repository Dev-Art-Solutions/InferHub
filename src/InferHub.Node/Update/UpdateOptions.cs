using Microsoft.Extensions.Options;

namespace InferHub.Node.Update;

/// <summary>
/// <c>Update:</c> — whether this node looks for a newer release, applies one by itself, and lets an admin tell
/// it to (phase 101, D2). All three are off in the shipped <c>appsettings.json</c>: a Docker or dev node phones
/// nobody. The Windows setup writes them from its one question.
/// </summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>The repository's release list. A mirror serving the same JSON shape is one line.</summary>
    public const string DefaultSource = "https://api.github.com/repos/Dev-Art-Solutions/InferHub/releases";

    /// <summary>Look for a newer release every <see cref="Interval"/> and report it to the hub.</summary>
    public bool Check { get; set; }

    /// <summary>Apply a newer release by itself, once idle. Needs <see cref="Check"/>.</summary>
    public bool Auto { get; set; }

    /// <summary>Let an admin apply one from the hub's console or <c>POST /api/admin/nodes/{id}/update/apply</c>.</summary>
    public bool AllowFromHub { get; set; }

    public string Source { get; set; } = DefaultSource;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long after start the first check runs — long enough for the node to have registered.</summary>
    public TimeSpan FirstCheckDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an automatic update waits for this node to be idle before it goes anyway (D4): a node that is
    /// never idle must still update. The service's own shutdown grace drains what is left.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class UpdateOptionsValidator : IValidateOptions<UpdateOptions>
{
    public ValidateOptionsResult Validate(string? name, UpdateOptions options)
    {
        var failures = new List<string>();
        var prefix = $"{UpdateOptions.SectionName}:";

        if (options.Auto && !options.Check)
        {
            failures.Add($"{prefix}{nameof(UpdateOptions.Auto)}=true needs {prefix}{nameof(UpdateOptions.Check)}=true: a node cannot apply a release it never looks for.");
        }

        if ((options.Check || options.AllowFromHub)
            && (!Uri.TryCreate(options.Source, UriKind.Absolute, out var source) || source.Scheme is not ("https" or "http")))
        {
            failures.Add($"{prefix}{nameof(UpdateOptions.Source)} must be an absolute http(s) URL (got '{options.Source}').");
        }

        if (options.Interval < TimeSpan.FromMinutes(5))
        {
            failures.Add($"{prefix}{nameof(UpdateOptions.Interval)} must be at least 5 minutes (got {options.Interval}); the release feed is rate-limited.");
        }

        if (options.FirstCheckDelay < TimeSpan.Zero || options.DrainTimeout < TimeSpan.Zero)
        {
            failures.Add($"{prefix}{nameof(UpdateOptions.FirstCheckDelay)} and {prefix}{nameof(UpdateOptions.DrainTimeout)} cannot be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
