using System.Security.Cryptography;
using System.Text;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Phase 127. How a node proves, on every connection and every relay request, that it holds the private key of the
/// certificate its coordinator issued it: the certificate, a time, a fresh nonce, and an ECDSA P-256 / SHA-256 signature
/// over <see cref="Payload"/>. Nothing here is a secret on the wire — a captured set of headers is refused the second time
/// (the nonce) and after the clock window (the time).
/// </summary>
public static class NodeProof
{
    /// <summary>The certificate, DER, base64.</summary>
    public const string CertificateHeader = "X-Node-Certificate";

    /// <summary>The signature over <see cref="Payload"/>, base64.</summary>
    public const string SignatureHeader = "X-Node-Proof";

    /// <summary>Unix seconds, UTC.</summary>
    public const string TimeHeader = "X-Node-Proof-Time";

    /// <summary>16 random bytes, base64url. Never accepted twice.</summary>
    public const string NonceHeader = "X-Node-Proof-Nonce";

    /// <summary>Where a node with a one-time token gets its certificate (anonymous; the token is the credential).</summary>
    public const string EnrollPath = "/node/identity/enroll";

    /// <summary>Where a node swaps its certificate for a new one before it expires (authenticated by the current one).</summary>
    public const string RenewPath = "/node/identity/renew";

    /// <summary>What is signed. The node id is the certificate's subject; the hub checks they agree.</summary>
    public static byte[] Payload(string nodeId, long unixSeconds, string nonce)
        => Encoding.UTF8.GetBytes($"inferhub-node-proof\n{nodeId}\n{unixSeconds}\n{nonce}");

    /// <summary>A fresh set of the four headers. Call it per request: a nonce is good once.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Create(byte[] certificateDer, ECDsa key, string nodeId, DateTimeOffset now)
    {
        var unix = now.ToUnixTimeSeconds();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signature = key.SignData(Payload(nodeId, unix, nonce), HashAlgorithmName.SHA256);

        return
        [
            new(CertificateHeader, Convert.ToBase64String(certificateDer)),
            new(SignatureHeader, Convert.ToBase64String(signature)),
            new(TimeHeader, unix.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(NonceHeader, nonce)
        ];
    }
}

/// <summary>Phase 127. <c>POST /node/identity/enroll</c>: the one-time token, the node's id and name, its public key (SubjectPublicKeyInfo, base64).</summary>
public sealed record NodeEnrollRequest(string Token, string NodeId, string? Name, string PublicKey);

/// <summary>Phase 127. <c>POST /node/identity/renew</c>, sent with a proof from the current certificate: the new public key.</summary>
public sealed record NodeRenewRequest(string PublicKey);

/// <summary>The issued certificate and the coordinator's CA certificate, DER, base64; and when the node's expires.</summary>
public sealed record NodeCertificateResponse(string Certificate, string Authority, DateTimeOffset ExpiresUtc);
