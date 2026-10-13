using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// One host's ChatGPT registrations, on disk (phase 103): the issued client ids, the verified
/// identities and the tokens, <b>encrypted at rest</b> with AES-GCM under a key file beside them,
/// written atomically and owner-only. The fifth recorded exception to rule 4 — see the root
/// <c>CLAUDE.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The coordinator and every node each own one of these, and never share one</b> (103 D1). The
/// store has no idea which host it belongs to — the host decides the directory — and that is what
/// keeps "the coordinator never receives a node's tokens" a property of where files are rather
/// than of code that remembers not to.
/// </para>
/// <para>
/// The key file is the same protection the vendor asks for (owner-only, never committed, never
/// logged) plus one: a copy of <c>accounts.bin</c> on its own — a backup, a support bundle, a
/// volume snapshot somebody attached to a ticket — is ciphertext. It is not a defence against an
/// attacker who can read the directory, and the notes say so rather than implying one.
/// </para>
/// </remarks>
public sealed class ChatGptCredentialStore
{
    public const string AccountsFileName = "accounts.bin";

    public const string KeyFileName = "chatgpt.key";

    public const string HostFileName = "host.json";

    private static readonly byte[] Magic = "IHCG1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string hostFile;

    /// <param name="directory">Where this host's registrations live.</param>
    /// <param name="hostFile">
    /// The host's <c>ext_agent_host_id</c> file. The coordinator keeps one for the whole host and a
    /// store per provider; a node keeps one of each. Defaults to <see cref="HostFileName"/> inside
    /// <paramref name="directory"/>.
    /// </param>
    public ChatGptCredentialStore(string directory, string? hostFile = null)
    {
        Directory = directory;
        this.hostFile = hostFile ?? Path.Combine(directory, HostFileName);
    }

    public string Directory { get; }

    public string AccountsPath => Path.Combine(Directory, AccountsFileName);

    /// <summary>When <see cref="AccountsPath"/> was last written, so a reader can notice a sign-in made by another process.</summary>
    public DateTime? LastWriteUtc => File.Exists(AccountsPath) ? File.GetLastWriteTimeUtc(AccountsPath) : null;

    /// <summary>
    /// This host's stable, opaque <c>ext_agent_host_id</c> — created once, before the first sign-in,
    /// and never regenerated. Not a secret and not proof of anything; it is what OpenAI attributes
    /// a registration to.
    /// </summary>
    public string HostId()
    {
        if (File.Exists(hostFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hostFile));

                if (document.RootElement.TryGetProperty("ext_agent_host_id", out var id)
                    && id.GetString() is { Length: > 0 } existing)
                {
                    return existing;
                }
            }
            catch (JsonException)
            {
                // Rewritten below. A host id that cannot be read is replaced rather than guessed at,
                // and the sign-in after it is a new registration — loud, not wrong.
            }
        }

        var created = $"urn:uuid:{Guid.NewGuid()}";
        WriteAtomic(hostFile, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["ext_agent_host_id"] = created,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true }));

        return created;
    }

    public ChatGptAccountsFile Load()
    {
        if (!File.Exists(AccountsPath))
        {
            return new ChatGptAccountsFile();
        }

        var sealedBytes = File.ReadAllBytes(AccountsPath);

        if (sealedBytes.Length < Magic.Length + 12 + 16 || !sealedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new ChatGptStoreException($"{AccountsPath} is not a ChatGPT credential file this release can read.");
        }

        var nonce = sealedBytes.AsSpan(Magic.Length, 12);
        var tag = sealedBytes.AsSpan(Magic.Length + 12, 16);
        var cipher = sealedBytes.AsSpan(Magic.Length + 28);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(Key(create: false), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FileNotFoundException)
        {
            throw new ChatGptStoreException(
                $"{AccountsPath} cannot be decrypted with {Path.Combine(Directory, KeyFileName)} — the key file is missing "
                + "or belongs to another copy. Sign in again; the old file is left where it is.");
        }

        return JsonSerializer.Deserialize<ChatGptAccountsFile>(plain, JsonOptions) ?? new ChatGptAccountsFile();
    }

    public void Save(ChatGptAccountsFile accounts)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];

        using (var aes = new AesGcm(Key(create: true), 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(plain);

        WriteAtomic(AccountsPath, [.. Magic, .. nonce, .. tag, .. cipher]);
    }

    private byte[] Key(bool create)
    {
        var path = Path.Combine(Directory, KeyFileName);

        if (File.Exists(path))
        {
            var key = Convert.FromBase64String(File.ReadAllText(path).Trim());

            if (key.Length == 32)
            {
                return key;
            }

            throw new ChatGptStoreException($"{path} is not a 256-bit key.");
        }

        if (!create)
        {
            throw new FileNotFoundException(path);
        }

        var created = RandomNumberGenerator.GetBytes(32);
        WriteAtomic(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(created)));
        return created;
    }

    /// <summary>
    /// Temp file, owner-only, then a rename over the old one — a reader sees the old file or the
    /// new one and never half of a rotated refresh token.
    /// </summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

/// <summary>Everything one host knows about its ChatGPT accounts.</summary>
public sealed class ChatGptAccountsFile
{
    /// <summary>The registration inference uses. Null until the first sign-in completes.</summary>
    public string? Active { get; set; }

    public List<ChatGptRegistration> Registrations { get; set; } = [];

    public ChatGptRegistration? ActiveRegistration()
        => Registrations.FirstOrDefault(registration => registration.Id == Active);
}

/// <summary>
/// One ChatGPT account's registration on this host: its issued client id and verified identity,
/// which survive a sign-out, and its tokens, which do not.
/// </summary>
public sealed class ChatGptRegistration
{
    /// <summary>This host's own stable key for the registration — not the vendor's.</summary>
    public string Id { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Name { get; set; }

    public string? IdToken { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset? AccessExpiresAtUtc { get; set; }

    public List<string> Scopes { get; set; } = [];

    public DateTimeOffset? SavedAtUtc { get; set; }

    /// <summary>The model the console or the node's page picked, for a <c>@selected</c> mapping.</summary>
    public string? SelectedModel { get; set; }

    public bool SignedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

    public bool PlanUsage => Scopes.Contains(ChatGptOAuth.PlanUsageScope, StringComparer.Ordinal);

    public string Label => string.IsNullOrWhiteSpace(Email) ? Id : Email!;
}

public sealed class ChatGptStoreException(string message) : Exception(message);
