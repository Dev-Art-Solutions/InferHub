using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

/// <summary>
/// Phase 127. <c>Identity:</c> — a certificate per node, issued by this coordinator, instead of one enrollment secret shared
/// by all of them. Off by default; rule 4's ninth exception (127 D6).
/// </summary>
public sealed class NodeIdentityOptions
{
    public const string SectionName = "Identity";

    public bool Enabled { get; set; }

    /// <summary>Refuse a node that presents no certificate, even with the right shared secret (D4). Revocation means something only then.</summary>
    public bool Require { get; set; }

    /// <summary>The CA key and certificate and the record of what was issued. The image sets <c>/data/identity</c>.</summary>
    public string DataDirectory { get; set; } = "./data/identity";

    /// <summary>How long a node certificate is valid. The node renews it once two thirds of that has passed.</summary>
    public int CertificateDays { get; set; } = 90;

    /// <summary>How long a one-time enrollment token may wait to be used.</summary>
    public int TokenMinutes { get; set; } = 60;

    /// <summary>How far a proof's time may be from the hub's clock.</summary>
    public int ClockSkewSeconds { get; set; } = 300;
}

public sealed class NodeIdentityOptionsValidator : IValidateOptions<NodeIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, NodeIdentityOptions options)
    {
        if (!options.Enabled)
        {
            return options.Require
                ? ValidateOptionsResult.Fail("Identity:Require=true needs Identity:Enabled=true: there would be no way for any node to connect.")
                : ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add("Identity:DataDirectory must be set when Identity:Enabled=true.");
        }

        if (options.CertificateDays is < 1 or > 3650)
        {
            failures.Add($"Identity:CertificateDays {options.CertificateDays} is out of range; use 1 to 3650.");
        }

        if (options.TokenMinutes is < 1 or > 10_080)
        {
            failures.Add($"Identity:TokenMinutes {options.TokenMinutes} is out of range; use 1 to 10080 (a week).");
        }

        if (options.ClockSkewSeconds is < 10 or > 3600)
        {
            failures.Add($"Identity:ClockSkewSeconds {options.ClockSkewSeconds} is out of range; use 10 to 3600.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}

/// <summary>One certificate this coordinator issued. The thumbprint is what a presented certificate is matched by (D3).</summary>
public sealed record IssuedCertificate(
    string NodeId,
    string? Name,
    string Serial,
    string Thumbprint,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? RevokedUtc = null,
    string? RevokedBy = null,
    string? Reason = null,
    string? Organisation = null)
{
    public string State(DateTimeOffset now) => RevokedUtc is not null ? "revoked" : ExpiresUtc <= now ? "expired" : "active";
}

/// <summary>A one-time enrollment token, kept as its SHA-256 only.</summary>
public sealed record EnrollmentToken(string Hash, string? Label, DateTimeOffset ExpiresUtc, string CreatedBy, string? Organisation = null);

/// <summary>What <c>/api/status</c> says while identity is on. Counts and the CA's fingerprint, never a token.</summary>
public sealed record NodeIdentitySnapshot(bool Require, string Authority, int Active, int Revoked, int Expired, int PendingTokens, long Refused);

public sealed record ProofResult(bool Valid, string? NodeId, string? Reason)
{
    public static ProofResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// Phase 127. The coordinator as a small certificate authority for its own nodes: an ECDSA P-256 CA created on first use,
/// one-time enrollment tokens, a certificate per node for its own key, renewal, revocation, and the check every node
/// connection and relay request passes (D1–D5).
/// </summary>
public sealed class NodeCertificateAuthority
{
    private const string CaKeyFile = "ca.key";
    private const string CaCertFile = "ca.crt";
    private const string StateFile = "issued.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly NodeIdentityOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<NodeCertificateAuthority> logger;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> nonces = new(StringComparer.Ordinal);

    private readonly INodeIdentityStore store;
    private readonly Cluster.IClusterMembership? membership;
    private X509Certificate2? authority;
    private State? state;
    private long stateVersion;
    private long loadedFence = -1;
    private long refused;

    public NodeCertificateAuthority(
        IOptions<NodeIdentityOptions> options,
        TimeProvider clock,
        ILogger<NodeCertificateAuthority> logger,
        INodeIdentityStore? store = null,
        Cluster.IClusterMembership? membership = null)
    {
        this.options = options.Value;
        this.clock = clock;
        this.logger = logger;
        this.store = store ?? new FileNodeIdentityStore(options.Value.DataDirectory);
        this.membership = membership;
    }

    /// <summary>Phase 131: where the CA and the record live — a directory, or the cluster's Postgres.</summary>
    public string Location => store.Describe;

    public bool Enabled => options.Enabled;

    public bool Require => options.Enabled && options.Require;

    public string Directory => Path.GetFullPath(options.DataDirectory);

    /// <summary>Creates a token an admin hands to one node's operator. Shown once; only its hash is kept.</summary>
    public (string Token, DateTimeOffset ExpiresUtc) CreateToken(string? label, string createdBy, string? organisation = null)
    {
        if (organisation is not null && !Services.OrganisationPolicy.IsName(organisation))
        {
            throw new NodeIdentityException(400, "organisation must be lowercase letters, digits and '-'");
        }

        var token = "ihn_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow().AddMinutes(options.TokenMinutes);

        Retrying(() =>
        {
            var current = Load();
            Save(current with
            {
                Tokens = current.Tokens.Where(t => t.ExpiresUtc > clock.GetUtcNow()).Append(new EnrollmentToken(Hash(token), Clean(label), expires, createdBy, organisation)).ToList()
            });
            return 0;
        });

        logger.LogInformation("Enrollment token created by {By} ({Label}), valid until {Expires:u}", createdBy, label ?? "no label", expires);
        return (token, expires);
    }

    /// <summary>
    /// A node with a valid one-time token gets a certificate for the public key it sent. The token is spent whatever the
    /// outcome after it matched; an earlier certificate for the same node id is revoked as superseded (D2: one per node).
    /// </summary>
    public NodeCertificateResponse Enroll(NodeEnrollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodeId) || request.NodeId.Length > 128 || request.NodeId.Any(char.IsControl))
        {
            throw new NodeIdentityException(400, "nodeId is required");
        }

        var publicKey = ImportPublicKey(request.PublicKey);

        return Retrying(() =>
        {
            var current = Load();
            var now = clock.GetUtcNow();
            var hash = Hash(request.Token ?? string.Empty);
            var token = current.Tokens.FirstOrDefault(t => FixedEquals(t.Hash, hash));

            if (token is null || token.ExpiresUtc <= now)
            {
                Interlocked.Increment(ref refused);
                throw new NodeIdentityException(401, "the enrollment token is not valid: unknown, already used or expired. An admin creates one with POST /api/admin/identity/tokens");
            }

            var tokens = current.Tokens.Where(t => !ReferenceEquals(t, token)).ToList();
            var (response, issued) = Issue(request.NodeId.Trim(), Clean(request.Name) ?? token.Label, publicKey, now);
            // Phase 130 D4: the organisation is the token's — the admin's word, never the node's.
            issued = issued with { Organisation = token.Organisation };
            Save(new State(Supersede(current.Certificates, issued.NodeId, now).Append(issued).ToList(), tokens));

            logger.LogInformation("Node {NodeId} ({Name}) enrolled: certificate {Serial} until {Expires:u}", issued.NodeId, issued.Name, issued.Serial, issued.ExpiresUtc);
            return response;
        });
    }

    /// <summary>A node that proved its current certificate gets a new one for a new key; the old one is revoked as renewed.</summary>
    public NodeCertificateResponse Renew(string nodeId, NodeRenewRequest request)
    {
        var publicKey = ImportPublicKey(request.PublicKey);

        return Retrying(() =>
        {
            var current = Load();
            var now = clock.GetUtcNow();
            var previous = current.Certificates.LastOrDefault(c => c.NodeId == nodeId);
            var (response, issued) = Issue(nodeId, previous?.Name, publicKey, now);
            issued = issued with { Organisation = previous?.Organisation };
            Save(current with { Certificates = Supersede(current.Certificates, nodeId, now, "renewed").Append(issued).ToList() });

            logger.LogInformation("Node {NodeId} renewed its certificate: {Serial} until {Expires:u}", nodeId, issued.Serial, issued.ExpiresUtc);
            return response;
        });
    }

    /// <summary>Revokes every certificate a node id holds. Returns how many were active.</summary>
    public int Revoke(string nodeId, string by, string? reason)
    {
        return Retrying(() =>
        {
            var current = Load();
            var now = clock.GetUtcNow();
            var count = 0;
            var certificates = current.Certificates.Select(c =>
            {
                if (c.NodeId != nodeId || c.RevokedUtc is not null)
                {
                    return c;
                }

                count++;
                return c with { RevokedUtc = now, RevokedBy = by, Reason = Clean(reason) ?? "revoked by an admin" };
            }).ToList();

            if (count > 0)
            {
                Save(current with { Certificates = certificates });
                logger.LogWarning("Node {NodeId}: {Count} certificate(s) revoked by {By}", nodeId, count, by);
            }

            return count;
        });
    }

    /// <summary>
    /// The check (D3). The certificate must be one this coordinator issued (by SHA-256 thumbprint), not revoked, within its
    /// dates; the time within <c>ClockSkewSeconds</c>; the nonce new; the signature made by the certificate's key.
    /// Null result means no certificate was presented at all.
    /// </summary>
    public ProofResult? Verify(Func<string, string?> header)
    {
        var certificateText = header(NodeProof.CertificateHeader);

        if (string.IsNullOrWhiteSpace(certificateText))
        {
            return null;
        }

        var result = VerifyCore(certificateText, header(NodeProof.SignatureHeader), header(NodeProof.TimeHeader), header(NodeProof.NonceHeader));

        if (!result.Valid)
        {
            Interlocked.Increment(ref refused);
        }

        return result;
    }

    public void CountRefused() => Interlocked.Increment(ref refused);

    /// <summary>Phase 130: the organisation recorded with this node's active certificate, if it has one.</summary>
    public (bool Certified, string? Organisation) OrganisationOf(string nodeId)
    {
        var now = clock.GetUtcNow();

        lock (gate)
        {
            var active = Load().Certificates.LastOrDefault(c => c.NodeId == nodeId && c.State(now) == "active");
            return (active is not null, active?.Organisation);
        }
    }

    public IReadOnlyList<IssuedCertificate> Certificates()
    {
        lock (gate)
        {
            return Load().Certificates.ToArray();
        }
    }

    /// <summary>The CA certificate's SHA-256 fingerprint: what an operator compares when they check a node trusts the right hub.</summary>
    public string AuthorityFingerprint()
    {
        lock (gate)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Authority().RawData));
        }
    }

    /// <summary>Null when off, so a hub without identity reports v3.86's payload.</summary>
    public NodeIdentitySnapshot? Snapshot()
    {
        if (!Enabled)
        {
            return null;
        }

        lock (gate)
        {
            var current = Load();
            var now = clock.GetUtcNow();
            return new NodeIdentitySnapshot(
                Require,
                Convert.ToHexStringLower(SHA256.HashData(Authority().RawData)),
                current.Certificates.Count(c => c.State(now) == "active"),
                current.Certificates.Count(c => c.State(now) == "revoked"),
                current.Certificates.Count(c => c.State(now) == "expired"),
                current.Tokens.Count(t => t.ExpiresUtc > now),
                Interlocked.Read(ref refused));
        }
    }

    private ProofResult VerifyCore(string certificateText, string? signatureText, string? timeText, string? nonce)
    {
        byte[] der;
        byte[] signature;

        try
        {
            der = Convert.FromBase64String(certificateText.Trim());
            signature = Convert.FromBase64String((signatureText ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            return ProofResult.Fail("the certificate or the proof is not base64");
        }

        if (!long.TryParse(timeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 64)
        {
            return ProofResult.Fail("the proof has no time or no nonce");
        }

        var now = clock.GetUtcNow();

        if (Math.Abs(now.ToUnixTimeSeconds() - unix) > options.ClockSkewSeconds)
        {
            return ProofResult.Fail($"the proof's time is more than {options.ClockSkewSeconds}s from the coordinator's clock (Identity:ClockSkewSeconds)");
        }

        var thumbprint = Convert.ToHexStringLower(SHA256.HashData(der));
        IssuedCertificate? issued;

        lock (gate)
        {
            issued = Load().Certificates.FirstOrDefault(c => c.Thumbprint == thumbprint);
        }

        if (issued is null)
        {
            return ProofResult.Fail("the certificate was not issued by this coordinator");
        }

        if (issued.RevokedUtc is not null)
        {
            return ProofResult.Fail($"node {issued.NodeId}'s certificate {issued.Serial} was revoked ({issued.Reason})");
        }

        if (issued.ExpiresUtc <= now)
        {
            return ProofResult.Fail($"node {issued.NodeId}'s certificate {issued.Serial} expired at {issued.ExpiresUtc:u}; enroll it again with a new token");
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            using var key = certificate.GetECDsaPublicKey();

            if (key is null || !key.VerifyData(NodeProof.Payload(issued.NodeId, unix, nonce), signature, HashAlgorithmName.SHA256))
            {
                return ProofResult.Fail($"the proof is not signed by node {issued.NodeId}'s key");
            }
        }
        catch (CryptographicException)
        {
            return ProofResult.Fail("the certificate could not be read");
        }

        // The nonce last, so a bad signature cannot burn one a real node is about to use.
        if (!nonces.TryAdd(issued.NodeId + "|" + nonce, now))
        {
            return ProofResult.Fail("the proof was already used (a replayed nonce)");
        }

        SweepNonces(now);
        return new ProofResult(true, issued.NodeId, null);
    }

    private void SweepNonces(DateTimeOffset now)
    {
        if (nonces.Count < 4096)
        {
            return;
        }

        var cutoff = now.AddSeconds(-2 * options.ClockSkewSeconds);

        foreach (var entry in nonces)
        {
            if (entry.Value < cutoff)
            {
                nonces.TryRemove(entry.Key, out _);
            }
        }
    }

    private (NodeCertificateResponse Response, IssuedCertificate Issued) Issue(string nodeId, string? name, ECDsa publicKey, DateTimeOffset now)
    {
        var issuer = Authority();
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=" + nodeId.Replace(",", "").Replace("=", "") + ", O=InferHub node"),
            new PublicKey(publicKey),
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notAfter = now.AddDays(options.CertificateDays);

        using var certificate = request.Create(issuer, now.AddMinutes(-5), notAfter, serial);
        var der = certificate.RawData;
        var issued = new IssuedCertificate(
            nodeId,
            name,
            Convert.ToHexStringLower(serial),
            Convert.ToHexStringLower(SHA256.HashData(der)),
            now,
            notAfter);

        return (new NodeCertificateResponse(Convert.ToBase64String(der), Convert.ToBase64String(issuer.RawData), notAfter), issued);
    }

    private static IEnumerable<IssuedCertificate> Supersede(IEnumerable<IssuedCertificate> certificates, string nodeId, DateTimeOffset now, string reason = "superseded by a new enrollment")
        => certificates.Select(c => c.NodeId == nodeId && c.RevokedUtc is null ? c with { RevokedUtc = now, RevokedBy = "coordinator", Reason = reason } : c);

    private static ECDsa ImportPublicKey(string? text)
    {
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(text ?? string.Empty), out _);

            if (key.KeySize != 256)
            {
                throw new NodeIdentityException(400, "publicKey must be an ECDSA P-256 key");
            }

            return key;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new NodeIdentityException(400, "publicKey must be a base64 SubjectPublicKeyInfo of an ECDSA P-256 key");
        }
    }

    // The CA: created once, on first use, in DataDirectory; its key never leaves this machine (D1).
    private X509Certificate2 Authority()
    {
        if (authority is not null)
        {
            return authority;
        }

        if (store.Read(INodeIdentityStore.Authority) is { Body: { } existing })
        {
            authority = Parse(existing);
            return authority;
        }

        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var request = new CertificateRequest(new X500DistinguishedName($"CN=InferHub coordinator CA {Environment.MachineName}, O=InferHub"), key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            var now = clock.GetUtcNow();
            var created = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));

            // 131 D2: two coordinators of a cluster can boot at the same instant. The first insert wins; the other drops
            // the CA it just made and uses the winner's, so a cluster never has two.
            if (!store.TryWrite(INodeIdentityStore.Authority, key.ExportPkcs8PrivateKeyPem() + "\n" + created.ExportCertificatePem(), 0))
            {
                created.Dispose();
                authority = Parse(store.Read(INodeIdentityStore.Authority).Body
                    ?? throw new InvalidOperationException("the node CA was being created by another coordinator and could not be read"));
                return authority;
            }

            authority = created; // CreateSelfSigned already holds the key.
        }

        logger.LogInformation(
            "Created the node CA in {Location} (SHA-256 {Fingerprint}). Keep it: losing it means every node enrolls again",
            store.Describe,
            Convert.ToHexStringLower(SHA256.HashData(authority.RawData)));
        return authority;
    }

    private State Load()
    {
        // 131 D3: in a cluster the record is shared. A standby never trusts a copy it holds (the active writes), and an
        // instance that became active again reads afresh: leadership moving is the one moment another hub may have written.
        if (store.Shared && membership is { Enabled: true } cluster && (!cluster.IsActive || cluster.Fence != loadedFence))
        {
            state = null;
            loadedFence = cluster.IsActive ? cluster.Fence : -1;
        }

        if (state is not null)
        {
            return state;
        }

        var (body, version) = store.Read(INodeIdentityStore.Record);
        state = body is null ? new State([], []) : JsonSerializer.Deserialize<State>(body, Json) ?? new State([], []);
        stateVersion = version;
        return state;
    }

    private void Save(State next)
    {
        if (!store.TryWrite(INodeIdentityStore.Record, JsonSerializer.Serialize(next, Json), stateVersion))
        {
            state = null;
            throw new StaleRecord();
        }

        state = next;
        stateVersion = Math.Max(stateVersion + 1, 1);
    }

    /// <summary>131 D2: a write that lost to another coordinator's is redone on what that one wrote.</summary>
    private T Retrying<T>(Func<T> change)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                lock (gate)
                {
                    return change();
                }
            }
            catch (StaleRecord) when (attempt < 5)
            {
                logger.LogInformation("The node identity record changed under this write (attempt {Attempt}); reading it again", attempt);
            }
        }
    }

    private static X509Certificate2 Parse(string bundle)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(Pem.Block(bundle, "PRIVATE KEY"));
        using var certificate = X509CertificateLoader.LoadCertificate(Pem.Der(Pem.Block(bundle, "CERTIFICATE")));
        return certificate.CopyWithPrivateKey(key);
    }

    private sealed class StaleRecord : Exception;

    private static byte[] PemToDer(string pem)
        => Convert.FromBase64String(string.Concat(pem.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal))));

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }

    private sealed record State(List<IssuedCertificate> Certificates, List<EnrollmentToken> Tokens);
}

public sealed class NodeIdentityException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
