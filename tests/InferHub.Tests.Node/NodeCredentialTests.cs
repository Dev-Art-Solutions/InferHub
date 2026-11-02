using System.Security.Cryptography;
using InferHub.Node;
using InferHub.Node.Configuration;
using InferHub.Node.Identity;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 127, the node's half without a hub: inert without a certificate, and what it signs when it has one.</summary>
public sealed class NodeCredentialTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-credential-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WithNoCertificateAndNoTokenItDoesNothing()
    {
        var credential = Credential(token: null);

        await credential.EnsureAsync("http://127.0.0.1:9/", CancellationToken.None);

        Assert.False(credential.HasCertificate);
        Assert.Empty(credential.Proof());
        Assert.False(Directory.Exists(credential.Directory));
    }

    [Fact]
    public void AStoredCertificateIsLoadedAndEachProofIsFreshAndVerifiable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ca = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=test CA", ca, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var caCert = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var issued = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=node-1", key, HashAlgorithmName.SHA256)
            .Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(9), [1, 2, 3]);

        var credential = Credential(token: null);
        Directory.CreateDirectory(credential.Directory);
        File.WriteAllText(Path.Combine(credential.Directory, NodeCredential.KeyFile), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(credential.Directory, NodeCredential.CertificateFile), issued.ExportCertificatePem());

        Assert.True(credential.HasCertificate);
        var first = credential.Proof().ToDictionary(h => h.Key, h => h.Value);
        var second = credential.Proof().ToDictionary(h => h.Key, h => h.Value);

        Assert.NotEqual(first[NodeProof.NonceHeader], second[NodeProof.NonceHeader]);
        Assert.Equal(Convert.ToBase64String(issued.RawData), first[NodeProof.CertificateHeader]);
        Assert.True(key.VerifyData(
            NodeProof.Payload("node-1", long.Parse(first[NodeProof.TimeHeader]), first[NodeProof.NonceHeader]),
            Convert.FromBase64String(first[NodeProof.SignatureHeader]),
            HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task AnEnrollmentTheHubRefusesSaysWhy()
    {
        var credential = Credential(token: "ihn_wrong");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => credential.EnsureAsync("http://127.0.0.1:9/", CancellationToken.None));

        Assert.False(credential.HasCertificate);
        Assert.IsAssignableFrom<HttpRequestException>(error);
    }

    private NodeCredential Credential(string? token)
        => new(
            Options.Create(new CoordinatorOptions { EnrollmentToken = token }),
            Options.Create(new NodeOptions { DataDirectory = directory }),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = directory },
            new Fixed("node-1"),
            NullLogger<NodeCredential>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class Fixed(string id) : INodeIdentity
    {
        public string GetOrCreateNodeId() => id;
    }
}
