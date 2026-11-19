using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Auth;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Users;

/// <summary>One person on this hub, as stored. Keys are kept as SHA-256 only; the key itself is shown once, when made.</summary>
public sealed class UserRecord
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = BuiltInRoles.User;
    public bool Disabled { get; set; }
    public string? Organisation { get; set; }
    public string? Priority { get; set; }
    public List<string>? Collections { get; set; }
    public ClientLimits? Limits { get; set; }
    public List<UserKey> Keys { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class UserKey
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }

    /// <summary>Lowercase hex SHA-256 of the key. Nothing a key can be recovered from.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The key's last four characters, so a person can tell their keys apart in a list.</summary>
    public string Hint { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>A key that resolved to a user who may use it: who, with what role and permissions, as which client.</summary>
public sealed record AuthenticatedUser(string Id, string Role, IReadOnlySet<string> Permissions, ResolvedClient Client, string KeyId)
{
    public bool Can(string permission) => Permissions.Contains(permission);

    /// <summary>How the audit trail names a request this user made.</summary>
    public string Who => "user:" + Id;
}

/// <summary>What a caller asked to set on a user. Null leaves a field as it is.</summary>
public sealed record UserChange(
    string? DisplayName = null,
    string? Role = null,
    bool? Disabled = null,
    string? Organisation = null,
    string? Priority = null,
    List<string>? Collections = null,
    ClientLimits? Limits = null);

public sealed class UserDirectoryException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The hub's people (phase 154). Reads take an immutable snapshot — a key lookup on every request must never wait for an
/// admin's write — and writes rebuild it under one lock and save the file whole.
/// </summary>
public sealed class UserDirectory
{
    public const string KeyPrefix = "ihu_";
    public const int MaxUsers = 10_000;
    public const int MaxKeysPerUser = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly object writeLock = new();
    private readonly string? file;
    private readonly IOptionsMonitor<UserOptions> options;
    private readonly IOptionsMonitor<ApiKeyOptions>? auth;
    private readonly ILogger<UserDirectory> logger;
    private readonly TimeProvider time;

    private Dictionary<string, UserRecord> users = new(StringComparer.Ordinal);

    /// <summary>Key hash → (user id, key id). Rebuilt on each write; read without a lock.</summary>
    private FrozenDictionary<string, (string User, string Key)> byHash = FrozenDictionary<string, (string, string)>.Empty;

    public UserDirectory(
        IOptionsMonitor<UserOptions> options,
        ILogger<UserDirectory> logger,
        IOptionsMonitor<ApiKeyOptions>? auth = null,
        TimeProvider? time = null)
    {
        this.options = options;
        this.auth = auth;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;

        var value = options.CurrentValue;
        if (!value.IsFile)
        {
            return;
        }

        var directory = string.IsNullOrWhiteSpace(value.DataDirectory) ? "./data/users" : value.DataDirectory;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "users.json");

        if (File.Exists(file))
        {
            // A file someone broke by hand stops the hub rather than starting with nobody and overwriting it on the next
            // save — 152 D2's reasoning, and here it would also lock every person out.
            var stored = JsonSerializer.Deserialize<UserRecord[]>(File.ReadAllText(file), JsonOptions) ?? [];
            users = stored.Where(u => !string.IsNullOrWhiteSpace(u.Id)).ToDictionary(u => u.Id, StringComparer.Ordinal);
            Rebuild();
            logger.LogInformation("Loaded {Count} user(s) from {File}", users.Count, file);
        }
    }

    public string Persistence => file is null ? UserOptions.PersistenceNone : UserOptions.PersistenceFile;

    public int Count => users.Count;

    public IReadOnlyList<UserRecord> All() => users.Values.OrderBy(u => u.Id, StringComparer.Ordinal).ToArray();

    public UserRecord? Get(string id) => users.GetValueOrDefault(id?.Trim().ToLowerInvariant() ?? string.Empty);

    /// <summary>Every role this hub knows, built-in first, with the permissions each carries.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Roles()
    {
        var roles = new Dictionary<string, IReadOnlyList<string>>(BuiltInRoles.All, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, permissions) in options.CurrentValue.Roles)
        {
            if (!BuiltInRoles.All.ContainsKey(name))
            {
                roles[name.Trim().ToLowerInvariant()] = (permissions ?? [])
                    .Where(Permissions.IsKnown)
                    .Select(p => p.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToArray();
            }
        }

        return roles;
    }

    /// <summary>
    /// A presented token, if it is one of this hub's user keys and its user may sign in. Null otherwise — including a key
    /// whose user is disabled or whose role a config change removed, which then signs in as nobody rather than as less.
    /// </summary>
    public AuthenticatedUser? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // 154 D3: a dictionary lookup by the key's SHA-256. Its timing tells a caller something about the hash of what they
        // sent — which they can compute themselves — and nothing about a key they do not hold.
        if (!byHash.TryGetValue(Hash(token), out var entry))
        {
            return null;
        }

        return Authenticate(entry.User, entry.Key);
    }

    /// <summary>A user by id who may still sign in with the given key (an OAuth grant remembers the key it was made with).</summary>
    public AuthenticatedUser? Authenticate(string userId, string keyId)
    {
        if (!users.TryGetValue(userId, out var user)
            || user.Disabled
            || !user.Keys.Any(k => string.Equals(k.Id, keyId, StringComparison.Ordinal))
            || !Roles().TryGetValue(user.Role, out var permissions))
        {
            return null;
        }

        return new AuthenticatedUser(
            user.Id,
            user.Role,
            permissions.ToHashSet(StringComparer.Ordinal),
            new ResolvedClient(user.Id, user.Limits, user.Collections, user.Priority, user.Organisation),
            keyId);
    }

    public (UserRecord User, string Key) Create(string id, string? displayName, string? role, UserChange? rest = null, string? keyName = null)
    {
        id = (id ?? string.Empty).Trim().ToLowerInvariant();
        if (!UserOptionsValidator.IsName(id))
        {
            throw new UserDirectoryException("id must be 1–64 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit");
        }

        // A user and an Auth:Clients entry with one id would share an admission window and a usage line, and the audit
        // trail could not say which one asked.
        if (auth?.CurrentValue.Clients.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new UserDirectoryException($"'{id}' is already a client in Auth:Clients; pick another id", StatusCodes.Status409Conflict);
        }

        lock (writeLock)
        {
            if (users.ContainsKey(id))
            {
                throw new UserDirectoryException($"user '{id}' already exists", StatusCodes.Status409Conflict);
            }

            if (users.Count >= MaxUsers)
            {
                throw new UserDirectoryException($"this hub holds at most {MaxUsers} users");
            }

            var user = new UserRecord { Id = id, Role = BuiltInRoles.User, CreatedUtc = time.GetUtcNow() };
            Apply(user, (rest ?? new UserChange()) with { DisplayName = displayName ?? rest?.DisplayName, Role = role ?? rest?.Role });
            var key = NewKey(user, keyName);

            var next = new Dictionary<string, UserRecord>(users, StringComparer.Ordinal) { [id] = user };
            Commit(next);
            logger.LogInformation("Created user {User} with role {Role}", id, user.Role);
            return (user, key);
        }
    }

    public UserRecord Update(string id, UserChange change)
    {
        lock (writeLock)
        {
            var user = Clone(Require(id));
            Apply(user, change);
            Commit(new Dictionary<string, UserRecord>(users, StringComparer.Ordinal) { [user.Id] = user });
            logger.LogInformation("Changed user {User}", user.Id);
            return user;
        }
    }

    public bool Delete(string id)
    {
        lock (writeLock)
        {
            var user = Get(id);
            if (user is null)
            {
                return false;
            }

            var next = new Dictionary<string, UserRecord>(users, StringComparer.Ordinal);
            next.Remove(user.Id);
            Commit(next);
            logger.LogInformation("Deleted user {User}", user.Id);
            return true;
        }
    }

    public (UserKey Key, string Secret) AddKey(string id, string? name)
    {
        lock (writeLock)
        {
            var user = Clone(Require(id));
            if (user.Keys.Count >= MaxKeysPerUser)
            {
                throw new UserDirectoryException($"user '{user.Id}' already has {MaxKeysPerUser} keys; remove one first");
            }

            var secret = NewKey(user, name);
            Commit(new Dictionary<string, UserRecord>(users, StringComparer.Ordinal) { [user.Id] = user });
            return (user.Keys[^1], secret);
        }
    }

    public bool RemoveKey(string id, string keyId)
    {
        lock (writeLock)
        {
            var user = Clone(Require(id));
            if (user.Keys.RemoveAll(k => string.Equals(k.Id, keyId, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            Commit(new Dictionary<string, UserRecord>(users, StringComparer.Ordinal) { [user.Id] = user });
            return true;
        }
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private UserRecord Require(string id)
        => Get(id) ?? throw new UserDirectoryException($"user '{id}' not found", StatusCodes.Status404NotFound);

    private void Apply(UserRecord user, UserChange change)
    {
        if (change.Role is { } role)
        {
            role = role.Trim().ToLowerInvariant();
            if (!Roles().ContainsKey(role))
            {
                throw new UserDirectoryException($"unknown role '{role}'; this hub has {string.Join(", ", Roles().Keys)}");
            }

            user.Role = role;
        }

        if (change.DisplayName is { } display)
        {
            user.DisplayName = string.IsNullOrWhiteSpace(display) ? null : display.Trim()[..Math.Min(display.Trim().Length, 120)];
        }

        if (change.Disabled is { } disabled)
        {
            user.Disabled = disabled;
        }

        if (change.Organisation is { } organisation)
        {
            organisation = organisation.Trim().ToLowerInvariant();
            if (organisation.Length > 0 && !Services.OrganisationPolicy.IsName(organisation))
            {
                throw new UserDirectoryException($"organisation '{organisation}' — use lowercase letters, digits and '-'");
            }

            user.Organisation = organisation.Length == 0 ? null : organisation;
        }

        if (change.Priority is { } priority)
        {
            priority = priority.Trim().ToLowerInvariant();
            if (priority.Length > 0 && !Services.PriorityClasses.IsKnown(priority))
            {
                throw new UserDirectoryException($"priority '{priority}' — use essential, normal or background");
            }

            user.Priority = priority.Length == 0 ? null : priority;
        }

        if (change.Collections is { } collections)
        {
            user.Collections = collections.Count == 0 ? null : collections.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        }

        if (change.Limits is { } limits)
        {
            user.Limits = limits.HasAny ? limits : null;
        }
    }

    private string NewKey(UserRecord user, string? name)
    {
        var secret = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        user.Keys.Add(new UserKey
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 80)],
            Hash = Hash(secret),
            Hint = secret[^4..],
            CreatedUtc = time.GetUtcNow()
        });
        return secret;
    }

    private void Commit(Dictionary<string, UserRecord> next)
    {
        // Save before publishing: a key the file does not hold must not work for a moment and then vanish on restart.
        if (file is not null)
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next.Values.OrderBy(u => u.Id, StringComparer.Ordinal), JsonOptions));
            File.Move(temp, file, overwrite: true);
        }

        users = next;
        Rebuild();
    }

    private void Rebuild()
        => byHash = users.Values
            .SelectMany(u => u.Keys.Select(k => (k.Hash, Entry: (u.Id, k.Id))))
            .GroupBy(x => x.Hash, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First().Entry, StringComparer.Ordinal);

    private static UserRecord Clone(UserRecord user)
        => JsonSerializer.Deserialize<UserRecord>(JsonSerializer.Serialize(user, JsonOptions), JsonOptions)!;

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
