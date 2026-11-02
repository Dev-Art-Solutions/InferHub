using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 127: the authority's tokens, certificates and the proof check, one refusal at a time.</summary>
public sealed class NodeIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-identity-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AnEnrolledNodesProofIsAccepted()
    {
        var authority = Authority();
        var (key, der) = Enroll(authority, "node-1");

        var result = authority.Verify(Headers(der, key, "node-1"));

        Assert.True(result!.Valid);
        Assert.Equal("node-1", result.NodeId);
        Assert.Null(authority.Verify(_ => null));
    }

    [Fact]
    public void ATokenIsSpentOnFirstUseAndExpires()
    {
        var authority = Authority();
        var (token, _) = authority.CreateToken("gpu", "admin:x");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        authority.Enroll(new NodeEnrollRequest(token, "node-1", null, publicKey));
        Assert.Equal(401, Assert.Throws<NodeIdentityException>(() => authority.Enroll(new NodeEnrollRequest(token, "node-2", null, publicKey))).Status);

        var (late, _) = authority.CreateToken(null, "admin:x");
        clock.Now = clock.Now.AddMinutes(61);
        Assert.Contains("expired", Assert.Throws<NodeIdentityException>(() => authority.Enroll(new NodeEnrollRequest(late, "node-3", null, publicKey))).Message);
        Assert.Equal(400, Assert.Throws<NodeIdentityException>(() => authority.Enroll(new NodeEnrollRequest("x", "node-4", null, "not a key"))).Status);
    }

    [Fact]
    public void AProofIsGoodOnceAndOnlyNearTheHubsClock()
    {
        var authority = Authority();
        var (key, der) = Enroll(authority, "node-1");
        var headers = Headers(der, key, "node-1");

        Assert.True(authority.Verify(headers)!.Valid);
        Assert.Contains("already used", authority.Verify(headers)!.Reason);

        var old = Headers(der, key, "node-1", clock.Now.AddMinutes(-6));
        Assert.Contains("Identity:ClockSkewSeconds", authority.Verify(old)!.Reason);
    }

    [Fact]
    public void AForgedOrForeignCertificateIsRefused()
    {
        var authority = Authority();
        var (key, der) = Enroll(authority, "node-1");
        var (otherKey, otherDer) = Enroll(authority, "node-2");

        // node-2's key signing with node-1's certificate.
        Assert.Contains("not signed by node node-1", authority.Verify(Headers(der, otherKey, "node-1"))!.Reason);

        // A certificate this hub never issued, even for a known node id.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var self = new CertificateRequest("CN=node-1", stranger, HashAlgorithmName.SHA256).CreateSelfSigned(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        Assert.Contains("not issued by this coordinator", authority.Verify(Headers(self.RawData, stranger, "node-1"))!.Reason);

        // Claiming another node's id in the signed payload: the hub signs nothing in, it checks against the record.
        Assert.False(authority.Verify(Headers(otherDer, otherKey, "node-1"))!.Valid);
        Assert.True(authority.Verify(Headers(otherDer, otherKey, "node-2"))!.Valid);
    }

    [Fact]
    public void RevokedSupersededAndExpiredCertificatesAreRefusedByName()
    {
        var authority = Authority();
        var (first, firstDer) = Enroll(authority, "node-1");
        var (second, secondDer) = Enroll(authority, "node-1");

        Assert.Contains("superseded", authority.Verify(Headers(firstDer, first, "node-1"))!.Reason);
        Assert.True(authority.Verify(Headers(secondDer, second, "node-1"))!.Valid);

        Assert.Equal(1, authority.Revoke("node-1", "admin:x", "laptop stolen"));
        Assert.Contains("laptop stolen", authority.Verify(Headers(secondDer, second, "node-1"))!.Reason);
        Assert.Equal(0, authority.Revoke("node-1", "admin:x", null));

        var (third, thirdDer) = Enroll(authority, "node-3");
        clock.Now = clock.Now.AddDays(91);
        Assert.Contains("expired", authority.Verify(Headers(thirdDer, third, "node-3"))!.Reason);

        var snapshot = authority.Snapshot()!;
        Assert.Equal((0, 2, 1), (snapshot.Active, snapshot.Revoked, snapshot.Expired));
    }

    [Fact]
    public void RenewalIssuesANewCertificateAndRetiresTheOld()
    {
        var authority = Authority();
        var (old, oldDer) = Enroll(authority, "node-1");
        using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var renewed = authority.Renew("node-1", new NodeRenewRequest(Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo())));

        Assert.Contains("renewed", authority.Verify(Headers(oldDer, old, "node-1"))!.Reason);
        Assert.True(authority.Verify(Headers(Convert.FromBase64String(renewed.Certificate), fresh, "node-1"))!.Valid);
    }

    [Fact]
    public void TheAuthorityAndTheRecordSurviveARestart()
    {
        var first = Authority();
        var (key, der) = Enroll(first, "node-1");
        var fingerprint = first.AuthorityFingerprint();

        var second = Authority();

        Assert.Equal(fingerprint, second.AuthorityFingerprint());
        Assert.True(second.Verify(Headers(der, key, "node-1"))!.Valid);

        // The issued certificate chains to that CA, so any X.509 tool reads it as this hub's.
        using var issued = X509CertificateLoader.LoadCertificate(der);
        Assert.Contains("node-1", issued.Subject);
        Assert.StartsWith("CN=InferHub coordinator CA", issued.Issuer);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory), path => File.ReadAllText(path).Contains("ihn_"));
    }

    [Fact]
    public void FilterRequireRefusesTheSecretAndAFailedCertificateNeverFallsBackToIt()
    {
        var authority = Authority(require: true);
        var filter = new NodeAuthFilter(
            new Monitor(new ApiKeyOptions { NodeEnrollmentSecret = "shared" }),
            NullLogger<NodeAuthFilter>.Instance,
            authority);

        var secretOnly = new DefaultHttpContext();
        secretOnly.Request.Headers[NodeAuthFilter.EnrollmentSecretHeader] = "shared";
        Assert.False(filter.IsAuthorized(secretOnly));

        var (key, der) = Enroll(authority, "node-1");
        var proved = new DefaultHttpContext();
        foreach (var (name, value) in NodeProof.Create(der, key, "node-1", clock.Now))
        {
            proved.Request.Headers[name] = value;
        }

        Assert.True(filter.IsAuthorized(proved));
        Assert.Equal("node-1", proved.Items[NodeAuthFilter.CertifiedNodeKey]);

        authority.Revoke("node-1", "admin:x", null);
        var revoked = new DefaultHttpContext();
        revoked.Request.Headers[NodeAuthFilter.EnrollmentSecretHeader] = "shared";
        foreach (var (name, value) in NodeProof.Create(der, key, "node-1", clock.Now))
        {
            revoked.Request.Headers[name] = value;
        }

        // Not Require: the secret alone works, but a presented certificate that fails is not rescued by it.
        var lenient = new NodeAuthFilter(new Monitor(new ApiKeyOptions { NodeEnrollmentSecret = "shared" }), NullLogger<NodeAuthFilter>.Instance, Authority());
        Assert.True(lenient.IsAuthorized(secretOnly));
        Assert.False(filter.IsAuthorized(revoked));
    }

    [Fact]
    public void OffItIsSilentAndRequireAloneFailsStartup()
    {
        Assert.Null(Authority(enabled: false).Snapshot());
        Assert.False(Directory.Exists(directory));

        var validator = new NodeIdentityOptionsValidator();
        Assert.Contains("Identity:Require", validator.Validate(null, new NodeIdentityOptions { Require = true }).FailureMessage);
        Assert.Contains("Identity:CertificateDays", validator.Validate(null, new NodeIdentityOptions { Enabled = true, CertificateDays = 0 }).FailureMessage);
    }

    [Fact]
    public void TheScrapeCountsCertificatesByState()
    {
        var authority = Authority();
        Enroll(authority, "node-1");
        authority.CreateToken(null, "admin:x");

        var text = PrometheusFormatter.Format(new PrometheusScrape(
            "test", new Metrics().Snapshot(DateTimeOffset.UtcNow), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], 0,
            Identity: authority.Snapshot()));

        Assert.Contains("inferhub_node_certificates{state=\"active\"} 1", text);
        Assert.Contains("inferhub_node_enrollment_tokens 1", text);
        Assert.Contains("inferhub_node_identity_refused_total 0", text);
    }

    private NodeCertificateAuthority Authority(bool enabled = true, bool require = false)
        => new(
            Options.Create(new NodeIdentityOptions { Enabled = enabled, Require = require, DataDirectory = directory }),
            clock,
            NullLogger<NodeCertificateAuthority>.Instance);

    private (ECDsa Key, byte[] Der) Enroll(NodeCertificateAuthority authority, string nodeId)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (token, _) = authority.CreateToken(nodeId, "admin:x");
        var response = authority.Enroll(new NodeEnrollRequest(token, nodeId, nodeId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        return (key, Convert.FromBase64String(response.Certificate));
    }

    private Func<string, string?> Headers(byte[] der, ECDsa key, string nodeId, DateTimeOffset? at = null)
    {
        var headers = NodeProof.Create(der, key, nodeId, at ?? clock.Now).ToDictionary(h => h.Key, h => h.Value);
        return name => headers.GetValueOrDefault(name);
    }

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

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue => value;

        public ApiKeyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
