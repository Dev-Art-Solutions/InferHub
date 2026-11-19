using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Users;

/// <summary>
/// <c>Users</c> (phase 154): people, made at runtime, each with a role and their own keys. Off by default; a hub that never
/// sets it keeps <c>Auth:Clients</c> and <c>Auth:AdminApiKeys</c> as its only credentials, byte for byte.
/// </summary>
public sealed class UserOptions
{
    public const string SectionName = "Users";
    public const string PersistenceNone = "none";
    public const string PersistenceFile = "file";

    public bool Enabled { get; set; }

    /// <summary>
    /// <c>file</c> (default): one <c>users.json</c> under <see cref="DataDirectory"/> — rule 4's eleventh exception, because a
    /// person whose account evaporates with a restart was never given one. <c>none</c> keeps them as long as the hub (tests).
    /// </summary>
    public string Persistence { get; set; } = PersistenceFile;

    /// <summary>Relative for a bare-metal hub; the image sets <c>/data/users</c> (the permissions trap, 21 D7).</summary>
    public string DataDirectory { get; set; } = "./data/users";

    /// <summary>Roles beyond the four built in: a name and the permissions it carries (<see cref="Permissions"/>).</summary>
    public Dictionary<string, List<string>> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFile => string.Equals((Persistence ?? "").Trim(), PersistenceFile, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a role may do. Five words, each guarding one surface, so a role is read in one line.</summary>
public static class Permissions
{
    /// <summary>See which models exist: <c>/api/tags</c>, <c>/v1/models</c>, <c>/api/status</c>, MCP <c>list_models</c>.</summary>
    public const string Models = "models";

    /// <summary>Run a model: both client dialects, jobs, media, MCP <c>chat</c> and <c>embed</c>.</summary>
    public const string Inference = "inference";

    /// <summary>Read the admin API (GET/HEAD) and <c>/metrics</c>; MCP <c>fleet_status</c>.</summary>
    public const string AdminRead = "admin.read";

    /// <summary>Change anything the admin API changes, except people.</summary>
    public const string AdminWrite = "admin.write";

    /// <summary>Make, change and remove users and their keys.</summary>
    public const string Users = "users";

    public static readonly IReadOnlyList<string> All = [Models, Inference, AdminRead, AdminWrite, Users];

    public static bool IsKnown(string? permission)
        => permission is not null && All.Contains(permission.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The four roles every hub has. A custom role may add to the list, never redefine one of these (154 D2).</summary>
public static class BuiltInRoles
{
    public const string Viewer = "viewer";
    public const string User = "user";
    public const string Operator = "operator";
    public const string Admin = "admin";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> All =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Viewer] = [Permissions.Models],
            [User] = [Permissions.Models, Permissions.Inference],
            [Operator] = [Permissions.Models, Permissions.Inference, Permissions.AdminRead],
            [Admin] = Permissions.All
        };
}

public sealed partial class UserOptionsValidator : IValidateOptions<UserOptions>
{
    public ValidateOptionsResult Validate(string? name, UserOptions options)
    {
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.Persistence)
            && !options.IsFile
            && !string.Equals(options.Persistence.Trim(), UserOptions.PersistenceNone, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Users:Persistence '{options.Persistence}' is not file or none.");
        }

        foreach (var (role, permissions) in options.Roles)
        {
            if (!IsName(role))
            {
                failures.Add($"Users:Roles — '{role}' is not a role name; use lowercase letters, digits, '.', '_' and '-'.");
                continue;
            }

            if (BuiltInRoles.All.ContainsKey(role))
            {
                failures.Add($"Users:Roles — '{role}' is a built-in role and cannot be redefined; name yours differently.");
            }

            foreach (var permission in permissions ?? [])
            {
                if (!Permissions.IsKnown(permission))
                {
                    failures.Add($"Users:Roles:{role} — unknown permission '{permission}'; use {string.Join(", ", Permissions.All)}.");
                }
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>A user id or role name: what reads well in an audit line and a URL, and nothing that needs escaping.</summary>
    public static bool IsName(string? value) => value is not null && NamePattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();
}
