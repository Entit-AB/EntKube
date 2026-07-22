using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// Covers the trust-manager Bundle manifest generation (scopes, targets, inline PEM,
/// default CAs) and the CRUD + validity parsing of <see cref="TrustBundleService"/>.
/// Apply-to-cluster paths (which shell kubectl) are not exercised here.
/// </summary>
public sealed class TrustBundleServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly TrustBundleService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public TrustBundleServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
            db.Database.EnsureCreated();

        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        sut = new TrustBundleService(dbFactory, gate, NullLogger<TrustBundleService>.Instance);
    }

    // ── Manifest generation ──────────────────────────────────────────────────

    [Fact]
    public void BuildManifest_AllNamespaces_UsesEmptySelector()
    {
        CaTrustBundle bundle = NewBundle(TrustDistributionScope.AllNamespaces);
        bundle.Sources.Add(NewSource("Root CA"));

        string yaml = TrustBundleService.BuildBundleManifest(bundle, []);

        yaml.Should().Contain("apiVersion: trust.cert-manager.io/v1alpha1");
        yaml.Should().Contain("kind: Bundle");
        yaml.Should().Contain("namespaceSelector: {}");
        yaml.Should().Contain("configMap:");
        yaml.Should().Contain("key: \"ca-certificates.crt\"");
        yaml.Should().Contain("-----BEGIN CERTIFICATE-----");
    }

    [Fact]
    public void BuildManifest_TenantNamespaces_EmitsMatchExpressionsWithNames()
    {
        CaTrustBundle bundle = NewBundle(TrustDistributionScope.TenantNamespaces);
        bundle.Sources.Add(NewSource("Root CA"));

        string yaml = TrustBundleService.BuildBundleManifest(bundle, ["team-a", "team-b"]);

        yaml.Should().Contain("matchExpressions:");
        yaml.Should().Contain("key: kubernetes.io/metadata.name");
        yaml.Should().Contain("operator: In");
        yaml.Should().Contain("- team-a");
        yaml.Should().Contain("- team-b");
    }

    [Fact]
    public void BuildManifest_MatchLabels_EmitsMatchLabels()
    {
        CaTrustBundle bundle = NewBundle(TrustDistributionScope.MatchLabels);
        bundle.NamespaceSelectorJson = TrustBundleService.SerializeSelector(
            new Dictionary<string, string> { ["team"] = "payments" });
        bundle.Sources.Add(NewSource("Root CA"));

        string yaml = TrustBundleService.BuildBundleManifest(bundle, []);

        yaml.Should().Contain("matchLabels:");
        yaml.Should().Contain("team: \"payments\"");
    }

    [Fact]
    public void BuildManifest_SecretTarget_UsesSecretBlock()
    {
        CaTrustBundle bundle = NewBundle(TrustDistributionScope.AllNamespaces);
        bundle.TargetKind = TrustBundleTargetKind.Secret;
        bundle.Sources.Add(NewSource("Root CA"));

        string yaml = TrustBundleService.BuildBundleManifest(bundle, []);

        yaml.Should().Contain("target:");
        yaml.Should().Contain("secret:");
        yaml.Should().NotContain("configMap:");
    }

    [Fact]
    public void BuildManifest_IncludeDefaultCAs_AddsUseDefaultCAs()
    {
        CaTrustBundle bundle = NewBundle(TrustDistributionScope.AllNamespaces);
        bundle.IncludeDefaultCAs = true;

        string yaml = TrustBundleService.BuildBundleManifest(bundle, []);

        yaml.Should().Contain("useDefaultCAs: true");
    }

    [Theory]
    [InlineData("Corporate Trust Store", "corporate-trust-store")]
    [InlineData("  UPPER Case  ", "upper-case")]
    [InlineData("weird__name!!", "weird-name")]
    [InlineData("", "fallback")]
    public void SanitizeName_ProducesValidDnsLabel(string input, string expected)
    {
        TrustBundleService.SanitizeName(input, "fallback").Should().Be(expected);
    }

    [Fact]
    public void Selector_RoundTrips()
    {
        Dictionary<string, string> labels = new() { ["a"] = "1", ["b"] = "2" };
        string json = TrustBundleService.SerializeSelector(labels);
        TrustBundleService.ParseSelector(json).Should().BeEquivalentTo(labels);
    }

    // ── CRUD + validity ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAndAddSource_ParsesValidity()
    {
        CaTrustBundle created = await sut.CreateBundleAsync(NewBundle(TrustDistributionScope.TenantNamespaces));
        await sut.AddSourceAsync(created.Id, "Root CA", NewSelfSignedCertPem());

        CaTrustBundle? loaded = await sut.GetBundleAsync(created.Id);
        loaded.Should().NotBeNull();
        loaded!.Sources.Should().HaveCount(1);

        IReadOnlyList<TrustSourceView> views = TrustBundleService.ViewSources(loaded);
        views.Should().HaveCount(1);
        views[0].Info.Should().NotBeNull();
        views[0].Info!.NotAfter.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task AddSource_RejectsInvalidPem()
    {
        CaTrustBundle created = await sut.CreateBundleAsync(NewBundle(TrustDistributionScope.AllNamespaces));

        Func<Task> act = () => sut.AddSourceAsync(created.Id, "bad", "not a certificate");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RemoveSource_Removes()
    {
        CaTrustBundle created = await sut.CreateBundleAsync(NewBundle(TrustDistributionScope.AllNamespaces));
        CaTrustBundleSource src = await sut.AddSourceAsync(created.Id, "Root CA", NewSelfSignedCertPem());

        await sut.RemoveSourceAsync(src.Id);

        CaTrustBundle? loaded = await sut.GetBundleAsync(created.Id);
        loaded!.Sources.Should().BeEmpty();
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsTrustManagerAvailable_FalseWhenAbsent_TrueWhenInstalled()
    {
        (await sut.IsTrustManagerAvailableAsync(clusterId)).Should().BeFalse();

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            Guid envId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
            db.Environments.Add(new EntKube.Web.Data.Environment { Id = envId, TenantId = tenantId, Name = "prod" });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = "prod", ApiServerUrl = "https://api",
            });
            db.ClusterComponents.Add(new ClusterComponent
            {
                Id = Guid.NewGuid(),
                ClusterId = clusterId,
                Name = "trust-manager",
                ComponentType = "HelmChart",
                HelmChartName = "trust-manager",
                ReleaseName = "trust-manager",
                Status = ComponentStatus.Installed,
            });
            await db.SaveChangesAsync();
        }

        (await sut.IsTrustManagerAvailableAsync(clusterId)).Should().BeTrue();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private CaTrustBundle NewBundle(TrustDistributionScope scope) => new()
    {
        TenantId = tenantId,
        ClusterId = clusterId,
        Name = "Corporate Trust",
        Scope = scope,
    };

    private static CaTrustBundleSource NewSource(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Pem = NewSelfSignedCertPem(),
    };

    internal static string NewSelfSignedCertPem()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest req = new("CN=Test Root CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return cert.ExportCertificatePem();
    }

    public void Dispose() => connection.Dispose();
}
