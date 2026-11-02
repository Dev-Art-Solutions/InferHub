using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Identity;

/// <summary>
/// Phase 127. This node's own key and the certificate its coordinator issued for it, under
/// <c>&lt;Node:DataDirectory&gt;/identity/</c>. The key is generated here and never leaves the machine; only its public half
/// goes to the coordinator. With no certificate and no <c>Coordinator:EnrollmentToken</c> this does nothing and the node
/// uses the shared secret as before.
/// </summary>
public sealed class NodeCredential(
    IOptions<CoordinatorOptions> coordinatorOptions,
    IOptions<NodeOptions> nodeOptions,
    IHostEnvironment environment,
    INodeIdentity identity,
    ILogger<NodeCredential> logger,
    TimeProvider? clock = null)
{
    public const string KeyFile = "node.key";
    public const string CertificateFile = "node.crt";

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly object gate = new();
    private ECDsa? key;
    private byte[]? certificate;
    private DateTimeOffset notBefore;
    private DateTimeOffset notAfter;
    private bool loaded;

    public string Directory => Path.Combine(
        string.IsNullOrWhiteSpace(nodeOptions.Value.DataDirectory) ? environment.ContentRootPath : nodeOptions.Value.DataDirectory,
        "identity");

    /// <summary>Whether this node holds a certificate to prove itself with.</summary>
    public bool HasCertificate
    {
        get
        {
            EnsureLoaded();
            return certificate is not null;
        }
    }

    public DateTimeOffset? ExpiresUtc
    {
        get
        {
            EnsureLoaded();
            return certificate is null ? null : notAfter;
        }
    }

    /// <summary>A fresh proof for one request, or nothing when this node has no certificate. A nonce is good once (D3).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Proof()
    {
        EnsureLoaded();

        lock (gate)
        {
            return certificate is null || key is null
                ? []
                : NodeProof.Create(certificate, key, identity.GetOrCreateNodeId(), time.GetUtcNow());
        }
    }

    /// <summary>
    /// Before a connection: enroll if there is a token and no certificate; renew once two thirds of the certificate's life
    /// has passed. A failed renewal keeps the current certificate and says so — it is still valid until it expires.
    /// </summary>
    public async Task EnsureAsync(string coordinatorUrl, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        var token = coordinatorOptions.Value.EnrollmentToken;

        if (certificate is null && string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        await refresh.WaitAsync(cancellationToken);

        try
        {
            if (certificate is null)
            {
                await EnrollAsync(coordinatorUrl, token!.Trim(), cancellationToken);
                return;
            }

            var now = time.GetUtcNow();

            // Offline past its certificate's end, it cannot renew; a new token lets it enroll again.
            if (now >= notAfter && !string.IsNullOrWhiteSpace(token))
            {
                logger.LogWarning("This node's certificate expired at {Expires:u}; enrolling again with Coordinator:EnrollmentToken", notAfter);
                await EnrollAsync(coordinatorUrl, token.Trim(), cancellationToken);
                return;
            }

            if (now < notBefore + (notAfter - notBefore) * 2 / 3)
            {
                return;
            }

            try
            {
                await RenewAsync(coordinatorUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not renew this node's certificate (valid until {Expires:u}); trying again at the next connection", notAfter);
            }
        }
        finally
        {
            refresh.Release();
        }
    }

    private async Task EnrollAsync(string coordinatorUrl, string token, CancellationToken cancellationToken)
    {
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new NodeEnrollRequest(token, identity.GetOrCreateNodeId(), nodeOptions.Value.Name, Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo()));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.PostAsJsonAsync(new Uri(Base(coordinatorUrl), NodeProof.EnrollPath.TrimStart('/')), request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the coordinator refused this node's enrollment ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        var issued = await response.Content.ReadFromJsonAsync<NodeCertificateResponse>(cancellationToken)
            ?? throw new InvalidOperationException("the coordinator answered the enrollment with no certificate");
        Store(fresh, issued);
        logger.LogInformation(
            "Enrolled with the coordinator: certificate until {Expires:u}, CA {Authority}. Coordinator:EnrollmentToken is spent; it can be removed",
            issued.ExpiresUtc,
            Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(issued.Authority))));
    }

    private async Task RenewAsync(string coordinatorUrl, CancellationToken cancellationToken)
    {
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Base(coordinatorUrl), NodeProof.RenewPath.TrimStart('/')))
        {
            Content = JsonContent.Create(new NodeRenewRequest(Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo())))
        };

        foreach (var (name, value) in Proof())
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the coordinator refused this node's renewal ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        var issued = await response.Content.ReadFromJsonAsync<NodeCertificateResponse>(cancellationToken)
            ?? throw new InvalidOperationException("the coordinator answered the renewal with no certificate");
        Store(fresh, issued);
        logger.LogInformation("Renewed this node's certificate: valid until {Expires:u}", issued.ExpiresUtc);
    }

    private void Store(ECDsa fresh, NodeCertificateResponse issued)
    {
        var der = Convert.FromBase64String(issued.Certificate);
        using var parsed = X509CertificateLoader.LoadCertificate(der);
        using var publicKey = parsed.GetECDsaPublicKey();

        if (publicKey is null || !publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(fresh.ExportSubjectPublicKeyInfo()))
        {
            throw new InvalidOperationException("the coordinator returned a certificate for a different key");
        }

        System.IO.Directory.CreateDirectory(Directory);
        WriteOwnerOnly(Path.Combine(Directory, KeyFile), fresh.ExportPkcs8PrivateKeyPem());
        WriteOwnerOnly(Path.Combine(Directory, CertificateFile), parsed.ExportCertificatePem());

        lock (gate)
        {
            key?.Dispose();
            key = ECDsa.Create();
            key.ImportFromPem(fresh.ExportPkcs8PrivateKeyPem());
            certificate = der;
            notBefore = parsed.NotBefore.ToUniversalTime();
            notAfter = parsed.NotAfter.ToUniversalTime();
        }
    }

    private void EnsureLoaded()
    {
        if (loaded)
        {
            return;
        }

        lock (gate)
        {
            if (loaded)
            {
                return;
            }

            loaded = true;
            var keyPath = Path.Combine(Directory, KeyFile);
            var certPath = Path.Combine(Directory, CertificateFile);

            if (!File.Exists(keyPath) || !File.Exists(certPath))
            {
                return;
            }

            try
            {
                var loadedKey = ECDsa.Create();
                loadedKey.ImportFromPem(File.ReadAllText(keyPath));
                using var parsed = X509CertificateLoader.LoadCertificate(PemToDer(File.ReadAllText(certPath)));
                key = loadedKey;
                certificate = parsed.RawData;
                notBefore = parsed.NotBefore.ToUniversalTime();
                notAfter = parsed.NotAfter.ToUniversalTime();
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
            {
                logger.LogError(ex, "This node's certificate in {Directory} could not be read; enroll it again with a new token", Directory);
            }
        }
    }

    private static Uri Base(string coordinatorUrl) => new(coordinatorUrl.TrimEnd('/') + "/");

    private static byte[] PemToDer(string pem)
        => Convert.FromBase64String(string.Concat(pem.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal))));

    private static void WriteOwnerOnly(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Phase 127: on the hub connection's negotiate and on a sealed node's relay requests, a fresh proof each time.</summary>
public sealed class NodeProofHandler(NodeCredential credential) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(NodeProof.CertificateHeader))
        {
            foreach (var (name, value) in credential.Proof())
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
