using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Moq;
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
    private readonly TestDbContextFactory dbFactory;
    private readonly CertificateDistributionService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    /// <summary>
    /// A certificate in the tenant's vault. The tests used to point distributions at a
    /// fabricated VaultSecretId, which worked only because nothing checked it — so these
    /// seeded rows are what make the create path's check reachable rather than vacuous.
    /// </summary>
    private readonly Guid certSecretId = Guid.NewGuid();
    private readonly Guid otherCertSecretId = Guid.NewGuid();
    private readonly Guid strangerTenantId = Guid.NewGuid();
    private readonly Guid strangerCertSecretId = Guid.NewGuid();

    public CertificateDistributionServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            Guid ourVault = SeedCertificate(db, tenantId, certSecretId, "corp");
            SeedSecret(db, ourVault, otherCertSecretId, "corp-alt");
            SeedCertificate(db, strangerTenantId, strangerCertSecretId, "annan");
            db.SaveChanges();
        }

        VaultEncryptionService encryption = new(TestRootKey);
        VaultService vault = new(dbFactory, encryption, TestServices.NoClusterAccess);

        // No gate here any more: this service reaches it only through IClusterClient, which
        // supplies the credential the gate needs. See PlannedChangeSeamTests.
        sut = new CertificateDistributionService(
            dbFactory, vault,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                dbFactory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.DeliveryApi(dbFactory),
            NullLogger<CertificateDistributionService>.Instance);
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
        CertificateDistribution created = await sut.CreateDistributionAsync(tenantId, new CertificateDistribution
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            Name = "corp-tls",
            VaultSecretId = certSecretId,
            TargetSecretName = "Corp TLS Secret",
            Scope = TrustDistributionScope.TenantNamespaces,
        });

        // Name is sanitised to a valid Secret name.
        created.TargetSecretName.Should().Be("corp-tls-secret");

        List<CertificateDistribution> list = await sut.GetDistributionsAsync(tenantId, clusterId);
        list.Should().ContainSingle();

        await sut.UpdateDistributionAsync(tenantId, created.Id, d => d.IncludeKey = false);
        (await sut.GetDistributionAsync(tenantId, created.Id))!.IncludeKey.Should().BeFalse();

        // No cluster row exists → delete just removes the DB row (no kubectl).
        await sut.DeleteDistributionAsync(tenantId, created.Id);
        (await sut.GetDistributionsAsync(tenantId, clusterId)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetDistributionsForCert_ReturnsAppAndTenantTargets()
    {
        // Both certificates are real now: the create path checks the certificate exists and is
        // this tenant's, so a fabricated id here would be refused for that reason instead.
        Guid certId = otherCertSecretId;

        await sut.CreateDistributionAsync(tenantId, new CertificateDistribution
        {
            TenantId = tenantId, ClusterId = null, VaultSecretId = certId,
            Name = "to-app", TargetSecretName = "tls", IncludeKey = true,
            Scope = TrustDistributionScope.App, AppId = Guid.NewGuid(),
        });
        await sut.CreateDistributionAsync(tenantId, new CertificateDistribution
        {
            TenantId = tenantId, ClusterId = null, VaultSecretId = certId,
            Name = "tenant-wide", TargetSecretName = "tls", IncludeKey = false,
            Scope = TrustDistributionScope.AllNamespaces,
        });
        // A target for a different cert must not leak in.
        await sut.CreateDistributionAsync(tenantId, new CertificateDistribution
        {
            TenantId = tenantId, ClusterId = null, VaultSecretId = certSecretId,
            Name = "other", TargetSecretName = "tls", Scope = TrustDistributionScope.AllNamespaces,
        });

        List<CertificateDistribution> targets = await sut.GetDistributionsForCertAsync(tenantId, certId);

        targets.Should().HaveCount(2);
        targets.Should().Contain(t => t.Scope == TrustDistributionScope.App && t.AppId != null);
        targets.Should().Contain(t => t.Scope == TrustDistributionScope.AllNamespaces);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A tenant, its vault and one certificate in it — the row the create path checks.</summary>
    private static Guid SeedCertificate(
        ApplicationDbContext db, Guid tenant, Guid secretId, string label)
    {
        Guid vaultId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenant, Name = label, Slug = $"{label}-{tenant:N}" });
        db.SecretVaults.Add(new SecretVault
        {
            Id = vaultId, TenantId = tenant, EncryptedDataKey = [], Nonce = [],
        });
        db.Set<VaultSecret>().Add(new VaultSecret
        {
            Id = secretId,
            VaultId = vaultId,
            Name = $"{label}-tls",
            SecretType = VaultSecretType.Certificate,
            EncryptedValue = [],
            Nonce = [],
        });

        return vaultId;
    }

    /// <summary>A second certificate in a tenant's existing vault.</summary>
    private static void SeedSecret(ApplicationDbContext db, Guid vaultId, Guid secretId, string name)
    {
        db.Set<VaultSecret>().Add(new VaultSecret
        {
            Id = secretId, VaultId = vaultId, Name = name,
            SecretType = VaultSecretType.Certificate, EncryptedValue = [], Nonce = [],
        });
    }

    private CertificateDistribution NewDist(bool includeKey) => new()
    {
        TenantId = tenantId,
        ClusterId = clusterId,
        Name = "corp-tls",
        VaultSecretId = certSecretId,
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
    // ── Somebody else's certificate ───────────────────────────────────────────

    /// <summary>
    /// ⚠️ <b>A distribution row says which certificate is copied into which namespaces, and
    /// IncludeKey defaults to true</b> — so the row names a private key and where to put it.
    /// Both the TenantId and the VaultSecretId arrived on the caller's entity and neither was
    /// checked.
    /// </summary>
    [Fact]
    public async Task A_distribution_cannot_name_another_tenants_certificate()
    {
        Func<Task> act = () => sut.CreateDistributionAsync(tenantId, new CertificateDistribution
        {
            TenantId = tenantId,
            VaultSecretId = strangerCertSecretId,
            Name = "theirs",
            TargetSecretName = "tls",
            Scope = TrustDistributionScope.AllNamespaces,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Certificate not found, or it is not this tenant's.");
    }

    /// <summary>
    /// The create path files the row under the parameter. Otherwise a reconciler entry lands
    /// under somebody else's tenant, and the background sweep then acts on it as them.
    /// </summary>
    [Fact]
    public async Task A_distribution_is_filed_under_the_tenant_that_created_it()
    {
        CertificateDistribution created = await sut.CreateDistributionAsync(
            tenantId, new CertificateDistribution
            {
                TenantId = strangerTenantId,   // the lie
                VaultSecretId = certSecretId,  // ours, so the certificate check passes
                Name = "ours",
                TargetSecretName = "tls",
                Scope = TrustDistributionScope.AllNamespaces,
            });

        created.TenantId.Should().Be(tenantId);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Set<CertificateDistribution>().First(d => d.Id == created.Id)
            .TenantId.Should().Be(tenantId);
    }

    /// <summary>
    /// ⚠️ The mutate delegate is handed the row, so it can set anything on it — including the
    /// column that says whose it is. And by id alone it could repoint another tenant's
    /// distribution at a different certificate or different namespaces, which the background
    /// reconciler would then carry out as them.
    /// </summary>
    [Fact]
    public async Task Another_tenants_distribution_cannot_be_read_or_repointed()
    {
        CertificateDistribution theirs;
        using (ApplicationDbContext seed = dbFactory.CreateDbContext())
        {
            theirs = new CertificateDistribution
            {
                Id = Guid.NewGuid(),
                TenantId = strangerTenantId,
                VaultSecretId = strangerCertSecretId,
                Name = "their-tls",
                TargetSecretName = "their-secret",
                Scope = TrustDistributionScope.AllNamespaces,
            };
            seed.Set<CertificateDistribution>().Add(theirs);
            seed.SaveChanges();
        }

        (await sut.GetDistributionAsync(tenantId, theirs.Id)).Should().BeNull();

        Func<Task> repoint = () => sut.UpdateDistributionAsync(
            tenantId, theirs.Id, d => d.TargetSecretName = "ours");

        await repoint.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Certificate distribution not found, or it is not this tenant's.");

        using ApplicationDbContext after = dbFactory.CreateDbContext();
        after.Set<CertificateDistribution>().First(d => d.Id == theirs.Id)
            .TargetSecretName.Should().Be("their-secret");
    }

    /// <summary>
    /// And our own edit cannot hand the row away — the delegate setting TenantId is overruled
    /// after it runs.
    /// </summary>
    [Fact]
    public async Task Our_own_distribution_cannot_be_moved_to_another_tenant()
    {
        CertificateDistribution ours = await sut.CreateDistributionAsync(
            tenantId, NewDist(includeKey: true));

        CertificateDistribution edited = await sut.UpdateDistributionAsync(
            tenantId, ours.Id, d =>
            {
                d.TenantId = strangerTenantId;
                d.TargetSecretName = "renamed";
            });

        edited.TenantId.Should().Be(tenantId);
        edited.TargetSecretName.Should().Be("renamed");

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Set<CertificateDistribution>().First(d => d.Id == ours.Id)
            .TenantId.Should().Be(tenantId);
    }
}
