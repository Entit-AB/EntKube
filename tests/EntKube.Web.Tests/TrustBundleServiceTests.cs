using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// Covers the trust-manager Bundle manifest generation (scopes, targets, inline PEM,
/// default CAs) and the CRUD + validity parsing of <see cref="TrustBundleService"/>.
/// Apply-to-cluster paths (which shell kubectl) are not exercised here.
/// </summary>
public sealed class TrustBundleServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory dbFactory;
    private readonly TrustBundleService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public TrustBundleServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();

            // ⚠️ The tenant's own cluster, seeded for every test rather than in the one that
            // needed it. CreateBundleAsync now checks that the ClusterId on the posted entity
            // belongs to the caller's tenant — a bundle names the cluster it is distributed to,
            // so a bundle pointed at somebody else's cluster is the thing being prevented. Three
            // tests failed on this: they created bundles against a cluster id that existed
            // nowhere, which the guard is right to refuse.
            Guid envId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = $"acme-{tenantId:N}" });
            db.Environments.Add(new EntKube.Web.Data.Environment
            {
                Id = envId, TenantId = tenantId, Name = "prod",
            });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                Name = "prod", ApiServerUrl = "https://api",
            });
            db.SaveChanges();
        }

        // No gate here any more: this service reaches it only through IClusterClient.
        sut = new TrustBundleService(
            dbFactory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                dbFactory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.DeliveryApi(dbFactory),
            NullLogger<TrustBundleService>.Instance);
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
        CaTrustBundle created = await sut.CreateBundleAsync(tenantId, NewBundle(TrustDistributionScope.TenantNamespaces));
        await sut.AddSourceAsync(tenantId, created.Id, "Root CA", NewSelfSignedCertPem());

        CaTrustBundle? loaded = await sut.GetBundleAsync(tenantId, created.Id);
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
        CaTrustBundle created = await sut.CreateBundleAsync(tenantId, NewBundle(TrustDistributionScope.AllNamespaces));

        Func<Task> act = () => sut.AddSourceAsync(tenantId, created.Id, "bad", "not a certificate");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RemoveSource_Removes()
    {
        CaTrustBundle created = await sut.CreateBundleAsync(tenantId, NewBundle(TrustDistributionScope.AllNamespaces));
        CaTrustBundleSource src = await sut.AddSourceAsync(tenantId, created.Id, "Root CA", NewSelfSignedCertPem());

        await sut.RemoveSourceAsync(tenantId, src.Id);

        CaTrustBundle? loaded = await sut.GetBundleAsync(tenantId, created.Id);
        loaded!.Sources.Should().BeEmpty();
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsTrustManagerAvailable_FalseWhenAbsent_TrueWhenInstalled()
    {
        (await sut.IsTrustManagerAvailableAsync(tenantId, clusterId)).Should().BeFalse();

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            // Tenant, environment and cluster come from the constructor now; this test only
            // needs the component that makes trust-manager "available".
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

        (await sut.IsTrustManagerAvailableAsync(tenantId, clusterId)).Should().BeTrue();
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

    // ──────── Whose trust ────────

    /// <summary>
    /// ⚠️ What a CA trust bundle <em>is</em> makes this the sharpest kind of scoping bug: the
    /// bundle is distributed into namespaces as a Secret that workloads trust for outbound TLS.
    /// A source added to it is a CA those workloads will accept. With a bare bundle id, one
    /// tenant could add their own CA to another's bundle and then present a certificate that
    /// every workload the bundle reaches would believe.
    ///
    /// <para><c>DeleteBundleAsync</c> was already scoped. Everything else here — the read, the
    /// create, the mutate-by-action update, add-source and remove-source — was not.</para>
    /// </summary>
    [Fact]
    public async Task A_trust_bundle_and_its_sources_are_confined_to_their_tenant()
    {
        Guid stranger = Guid.NewGuid(), strangerCluster = Guid.NewGuid();

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            Guid envId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
            db.Environments.Add(new EntKube.Web.Data.Environment
            {
                Id = envId, TenantId = stranger, Name = "their-prod",
            });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = strangerCluster, TenantId = stranger, EnvironmentId = envId,
                Name = "their-prod", ApiServerUrl = "https://their-api",
            });
            db.SaveChanges();
        }

        // Their bundle, with a CA of their own in it.
        CaTrustBundle theirs = await sut.CreateBundleAsync(stranger, new CaTrustBundle
        {
            TenantId = stranger, ClusterId = strangerCluster,
            Name = "Their Corporate Trust", Scope = TrustDistributionScope.AllNamespaces,
        });
        CaTrustBundleSource theirSource =
            await sut.AddSourceAsync(stranger, theirs.Id, "Their Root CA", NewSelfSignedCertPem());

        // We cannot read it.
        (await sut.GetBundleAsync(tenantId, theirs.Id)).Should().BeNull(
            "the bundle exists, so this is a refusal");
        (await sut.GetBundleAsync(stranger, theirs.Id)).Should().NotBeNull("the owner still sees it");

        // ⚠️ Nor add a CA of ours to it — the whole point.
        Func<Task> plantCa = () => sut.AddSourceAsync(tenantId, theirs.Id, "Our CA", NewSelfSignedCertPem());
        await plantCa.Should().ThrowAsync<InvalidOperationException>();

        // Nor remove theirs, nor edit the bundle.
        await sut.RemoveSourceAsync(tenantId, theirSource.Id);
        Func<Task> edit = () => sut.UpdateBundleAsync(tenantId, theirs.Id, b => b.Name = "hijacked");
        await edit.Should().ThrowAsync<InvalidOperationException>();

        CaTrustBundle intact = (await sut.GetBundleAsync(stranger, theirs.Id))!;
        intact.Name.Should().Be("Their Corporate Trust");
        intact.Sources.Should().ContainSingle("their CA is still there and ours never arrived")
            .Which.Name.Should().Be("Their Root CA");

        // And a bundle cannot be created against their cluster either.
        Func<Task> ontoTheirCluster = () => sut.CreateBundleAsync(tenantId, new CaTrustBundle
        {
            TenantId = tenantId, ClusterId = strangerCluster,
            Name = "Ours on their cluster", Scope = TrustDistributionScope.AllNamespaces,
        });
        await ontoTheirCluster.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// ⚠️ <c>UpdateBundleAsync</c> takes an <c>Action&lt;CaTrustBundle&gt;</c> over the loaded
    /// entity, so a caller can write any column on it — including the two that decide whose it is
    /// and where it goes. Loading the row scoped is not enough on its own; the tenant and cluster
    /// are pinned back after the action runs.
    /// </summary>
    [Fact]
    public async Task An_update_action_cannot_move_a_bundle_to_another_tenant_or_cluster()
    {
        Guid stranger = Guid.NewGuid();
        CaTrustBundle ours = await sut.CreateBundleAsync(
            tenantId, NewBundle(TrustDistributionScope.AllNamespaces));

        CaTrustBundle after = await sut.UpdateBundleAsync(tenantId, ours.Id, b =>
        {
            b.Name = "renamed";
            b.TenantId = stranger;
            b.ClusterId = Guid.NewGuid();
        });

        after.Name.Should().Be("renamed", "the edit itself still applies");
        after.TenantId.Should().Be(tenantId, "a caller may edit a bundle, not give it away");
        after.ClusterId.Should().Be(clusterId, "nor redirect where it is distributed");
    }
}
