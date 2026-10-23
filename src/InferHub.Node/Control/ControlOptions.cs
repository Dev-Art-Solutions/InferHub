using System.Security.Cryptography;
using System.Text;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Control;

/// <summary><c>Control:*</c> — the node's control API (phase 112). It exists only where the local API does.</summary>
public sealed class ControlOptions
{
    public const string SectionName = "Control";

    public const string TokenFileName = "control.token";

    /// <summary>The control routes; on by default wherever <c>LocalApi:Enabled</c> is, and never anonymous (D1).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The bearer token. Unset: generated once into <c>&lt;DataDirectory&gt;/control.token</c>, owner-only.</summary>
    public string? Token { get; set; }

    /// <summary>Answer a caller that is not on this machine (still with the token). Off: a non-loopback caller is a 403.</summary>
    public bool AllowRemote { get; set; }

    /// <summary>How many log lines the node keeps for <c>/api/control/logs</c>.</summary>
    public int LogLines { get; set; } = 2000;

    /// <summary>How long a restart waits for running jobs before it goes anyway.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The control API's one secret (112 D1): configured, or generated on first start into the node's data directory
/// where an app on the same box — and nobody without access to that directory — can read it.
/// </summary>
public sealed class ControlToken
{
    private readonly byte[] hash;

    public ControlToken(IOptions<ControlOptions> options, IOptions<NodeOptions> node, IHostEnvironment environment, ILogger<ControlToken> logger)
    {
        var configured = options.Value.Token?.Trim();
        var directory = string.IsNullOrWhiteSpace(node.Value.DataDirectory) ? environment.ContentRootPath : node.Value.DataDirectory;
        FilePath = Path.Combine(directory, ControlOptions.TokenFileName);

        if (!string.IsNullOrEmpty(configured))
        {
            Source = "configured";
            hash = Hash(configured);
            return;
        }

        Source = "file";
        hash = Hash(ReadOrCreate(FilePath, logger));
    }

    /// <summary>Where the generated token is (or would be) — what the status tells an app to read.</summary>
    public string FilePath { get; }

    /// <summary><c>configured</c> or <c>file</c>.</summary>
    public string Source { get; }

    /// <summary>Fixed-time comparison against the presented bearer token.</summary>
    public bool Matches(string? presented) =>
        !string.IsNullOrEmpty(presented) && CryptographicOperations.FixedTimeEquals(Hash(presented.Trim()), hash);

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string ReadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        // Owner-only where the platform says so; on Windows the data directory's own ACL decides (101 D1's
        // %ProgramData% directory is SYSTEM + Administrators; a console node's is the user's).
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(token);
        }

        logger.LogInformation("Generated the control API token in {Path}; an app on this machine reads it from there.", path);
        return token;
    }
}
