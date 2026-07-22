using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the per-namespace Secret manifest generation (tls vs opaque, key handling,
/// base64, multi-namespace fan-out) and CRUD of <see cref="CertificateDistributionService"/>.
/// Cluster-touching paths (kubectl) are not exercised here.
/// </summary>
public sealed class CertificateDistributionServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly CertificateDistributionService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public CertificateDistributionServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
            db.Database.EnsureCreated();

        VaultEncryptionService encryption = new(TestRootKey);
        VaultService vault = new(dbFactory, encryption);
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        sut = new CertificateDistributionService(dbFactory, vault, gate, NullLogger<CertificateDistributionService>.Instance);
    }

    // ── Secret manifest generation ────────────────────────────────────────────

    [Fact]
    public void BuildSecretManifest_WithKey_ProducesTlsSecretIncludingKey()
    {
        (string certPem, string keyPem) = NewCertWithKey();
        CertificateBundle bundle = new() { Certificate = certPem, PrivateKey = keyPem };
        CertificateDistribution dist = NewDist(includeKey: true);

        string yaml = CertificateDistributionService.BuildSecretManifest(dist, bundle, ["team-a"]);

        yaml.Should().Contain("type: kubernetes.io/tls");
        yaml.Should().Contain("tls.crt:");
        yaml.Should().Contain("tls.key:");
        yaml.Should().Contain("namespace: team-a");
        yaml.Should().Contain($"{VaultService.ManagedByLabelKey}: {VaultService.ManagedByLabelValue}");
    }

    [Fact]
    public void BuildSecretManifest_CertOnly_ProducesOpaqueWithoutKey()
    {
        (string certPem, _) = NewCertWithKey();
        CertificateBundle bundle = new() { Certificate = certPem }; // no private key
        CertificateDistribution dist = NewDist(includeKey: false);

        string yaml = CertificateDistributionService.BuildSecretManifest(dist, bundle, ["team-a"]);

        yaml.Should().Contain("type: Opaque");
        yaml.Should().Contain("tls.crt:");
        yaml.Should().NotContain("tls.key:");
    }

    [Fact]
    public void BuildSecretManifest_IncludeKeyButNoKey_FallsBackToOpaque()
    {
        (string certPem, _) = NewCertWithKey();
        CertificateBundle bundle = new() { Certificate = certPem }; // no key present
        CertificateDistribution dist = NewDist(includeKey: true);

        string yaml = CertificateDistributionService.BuildSecretManifest(dist, bundle, ["team-a"]);

        // Requested key inclusion, but the bundle has none → must not emit a broken tls Secret.
        yaml.Should().Contain("type: Opaque");
        yaml.Should().NotContain("tls.key:");
    }

    [Fact]
    public void BuildSecretManifest_MultipleNamespaces_ProducesOneDocEach()
    {
        (string certPem, string keyPem) = NewCertWithKey();
        CertificateBundle bundle = new() { Certificate = certPem, PrivateKey = keyPem };
        CertificateDistribution dist = NewDist(includeKey: true);

        string yaml = CertificateDistributionService.BuildSecretManifest(dist, bundle, ["a", "b", "c"]);

        yaml.Split("\n---\n").Should().HaveCount(3);
        yaml.Should().Contain("namespace: a");
        yaml.Should().Contain("namespace: b");
        yaml.Should().Contain("namespace: c");
    }

    [Fact]
    public void BuildSecretManifest_Base64EncodesCertificate()
    {
        (string certPem, string keyPem) = NewCertWithKey();
        CertificateBundle bundle = new() { Certificate = certPem, PrivateKey = keyPem };
        CertificateDistribution dist = NewDist(includeKey: true);

        string yaml = CertificateDistributionService.BuildSecretManifest(dist, bundle, ["team-a"]);

        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(bundle.CombinedCertificateChain));
        yaml.Should().Contain($"tls.crt: {expected}");
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Get_Update_Delete_RoundTrips()
    {
        CertificateDistribution created = await sut.CreateDistributionAsync(new CertificateDistribution
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            Name = "corp-tls",
            VaultSecretId = Guid.NewGuid(),
            TargetSecretName = "Corp TLS Secret",
            Scope = TrustDistributionScope.TenantNamespaces,
        });

        // Name is sanitised to a valid Secret name.
        created.TargetSecretName.Should().Be("corp-tls-secret");

        List<CertificateDistribution> list = await sut.GetDistributionsAsync(tenantId, clusterId);
        list.Should().ContainSingle();

        await sut.UpdateDistributionAsync(created.Id, d => d.IncludeKey = false);
        (await sut.GetDistributionAsync(created.Id))!.IncludeKey.Should().BeFalse();

        // No cluster row exists → delete just removes the DB row (no kubectl).
        await sut.DeleteDistributionAsync(created.Id);
        (await sut.GetDistributionsAsync(tenantId, clusterId)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetDistributionsForCert_ReturnsAppAndTenantTargets()
    {
        Guid certId = Guid.NewGuid();

        await sut.CreateDistributionAsync(new CertificateDistribution
        {
            TenantId = tenantId, ClusterId = null, VaultSecretId = certId,
            Name = "to-app", TargetSecretName = "tls", IncludeKey = true,
            Scope = TrustDistributionScope.App, AppId = Guid.NewGuid(),
        });
        await sut.CreateDistributionAsync(new CertificateDistribution
        {
            TenantId = tenantId, ClusterId = null, VaultSecretId = certId,
            Name = "tenant-wide", TargetSecretName = "tls", IncludeKey = false,
            Scope = TrustDistributionScope.AllNamespaces,
        });
        // A target for a different cert must not leak in.
        await sut.CreateDistributionAsync(new CertificateDistribution
        {
            TenantId = tenantId, ClusterId = null, VaultSecretId = Guid.NewGuid(),
            Name = "other", TargetSecretName = "tls", Scope = TrustDistributionScope.AllNamespaces,
        });

        List<CertificateDistribution> targets = await sut.GetDistributionsForCertAsync(tenantId, certId);

        targets.Should().HaveCount(2);
        targets.Should().Contain(t => t.Scope == TrustDistributionScope.App && t.AppId != null);
        targets.Should().Contain(t => t.Scope == TrustDistributionScope.AllNamespaces);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private CertificateDistribution NewDist(bool includeKey) => new()
    {
        TenantId = tenantId,
        ClusterId = clusterId,
        Name = "corp-tls",
        VaultSecretId = Guid.NewGuid(),
        TargetSecretName = "entkube-cert",
        IncludeKey = includeKey,
        Scope = TrustDistributionScope.TenantNamespaces,
    };

    private static (string CertPem, string KeyPem) NewCertWithKey()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest req = new("CN=example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose() => connection.Dispose();
}
