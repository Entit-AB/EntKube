using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for ComponentScanService — verifies Helm release decoding, discovery
/// logic, and import behavior. Uses SQLite in-memory for isolated database tests.
/// </summary>
public class ComponentScanServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly ComponentScanService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly KubernetesCluster testCluster;

    public ComponentScanServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        VaultEncryptionService encryption = new(TestRootKey);
        VaultService vaultService = new(dbFactory, encryption);
        IKubernetesClientFactory k8sFactory = new Mock<IKubernetesClientFactory>().Object;
        EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate = new(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance);
        KyvernoPolicyService kyvernoService = new(dbFactory, k8sFactory, gate, NullLogger<KyvernoPolicyService>.Instance);
        RabbitMQService rabbitMQService = new(dbFactory, k8sFactory, vaultService);
        sut = new ComponentScanService(dbFactory, vaultService, kyvernoService, rabbitMQService);

        // Seed tenant, environment, and cluster.

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestTenant", Slug = "test" });
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = "dev" });

        testCluster = new KubernetesCluster
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Name = "test-cluster",
            ApiServerUrl = "https://k8s.example.com"
        };

        db.KubernetesClusters.Add(testCluster);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ──────── DecodeHelmRelease Tests ────────

    [Fact]
    public void DecodeHelmRelease_ValidPayload_ReturnsRelease()
    {
        // Arrange — create a realistic Helm release JSON, gzip it, base64 it.

        HelmReleaseJson releaseJson = new()
        {
            Name = "my-app",
            Namespace = "production",
            Chart = new HelmChartJson
            {
                Metadata = new HelmChartMetadataJson
                {
                    Name = "nginx",
                    Version = "1.2.3",
                    AppVersion = "1.25.0"
                }
            },
            Info = new HelmInfoJson
            {
                Status = "deployed",
                LastDeployed = "2025-01-15T10:30:00Z"
            },
            Config = JsonDocument.Parse("{\"replicaCount\":3,\"image\":{\"tag\":\"latest\"}}").RootElement
        };

        byte[] secretData = EncodeHelmRelease(releaseJson);

        // Act — invoke the private method via the public ScanHelmReleasesAsync,
        // but since we can't easily mock K8s client, test the decode logic directly.

        DiscoveredHelmRelease? result = InvokeDecodeHelmRelease(secretData, 5);

        // Assert

        result.Should().NotBeNull();
        result!.Name.Should().Be("my-app");
        result.Namespace.Should().Be("production");
        result.ChartName.Should().Be("nginx");
        result.ChartVersion.Should().Be("1.2.3");
        result.AppVersion.Should().Be("1.25.0");
        result.Status.Should().Be("deployed");
        result.Revision.Should().Be(5);
        result.Values.Should().Contain("replicaCount");
        result.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void DecodeHelmRelease_EmptyConfig_ReturnsNullValues()
    {
        // Arrange — release with empty config {}.

        HelmReleaseJson releaseJson = new()
        {
            Name = "minimal",
            Namespace = "default",
            Chart = new HelmChartJson
            {
                Metadata = new HelmChartMetadataJson { Name = "redis", Version = "0.1.0" }
            },
            Info = new HelmInfoJson { Status = "deployed" },
            Config = JsonDocument.Parse("{}").RootElement
        };

        byte[] secretData = EncodeHelmRelease(releaseJson);

        // Act

        DiscoveredHelmRelease? result = InvokeDecodeHelmRelease(secretData, 1);

        // Assert — empty config means no custom values.

        result.Should().NotBeNull();
        result!.Values.Should().BeNull();
    }

    [Fact]
    public void DecodeHelmRelease_InvalidData_ReturnsFallbackFromLabels()
    {
        // Arrange — garbage data that isn't valid base64 of gzipped content.

        byte[] secretData = Encoding.UTF8.GetBytes("not-valid-base64!!!");

        // Act — the service gracefully falls back to label-based metadata.

        DiscoveredHelmRelease? result = InvokeDecodeHelmRelease(secretData, 1);

        // Assert — returns a minimal release from secret labels.

        result.Should().NotBeNull();
        result!.Name.Should().Be("test");
        result.Namespace.Should().Be("default");
        result.Revision.Should().Be(1);
    }

    // ──────── ImportReleaseAsync Tests ────────

    [Fact]
    public async Task ImportReleaseAsync_NewRelease_CreatesComponent()
    {
        // Arrange

        DiscoveredHelmRelease release = new()
        {
            Name = "grafana",
            Namespace = "monitoring",
            ChartName = "grafana",
            ChartVersion = "7.0.0",
            Status = "deployed",
            Revision = 3,
            Values = "{\"persistence\":{\"enabled\":true}}",
            UpdatedAt = DateTime.UtcNow
        };

        // Act

        ClusterComponent result = await sut.ImportReleaseAsync(testCluster, release);

        // Assert

        result.Name.Should().Be("grafana");
        result.Namespace.Should().Be("monitoring");
        result.HelmChartName.Should().Be("grafana");
        result.HelmChartVersion.Should().Be("7.0.0");
        result.Status.Should().Be(ComponentStatus.Installed);
        result.HelmValues.Should().Contain("persistence");
        result.ClusterId.Should().Be(clusterId);

        // Verify persisted in DB.

        ClusterComponent? persisted = await db.ClusterComponents.FindAsync(result.Id);
        persisted.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportReleaseAsync_ExistingComponent_UpdatesIt()
    {
        // Arrange — pre-create a component with outdated info.

        ClusterComponent existing = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = "prometheus",
            ComponentType = "HelmChart",
            Namespace = "old-namespace",
            HelmChartVersion = "1.0.0",
            Status = ComponentStatus.NotInstalled
        };
        db.ClusterComponents.Add(existing);
        await db.SaveChangesAsync();

        DiscoveredHelmRelease release = new()
        {
            Name = "prometheus",
            Namespace = "monitoring",
            ChartName = "kube-prometheus-stack",
            ChartVersion = "55.0.0",
            Status = "deployed",
            Revision = 12,
            Values = "{\"alertmanager\":{\"enabled\":false}}"
        };

        // Act

        ClusterComponent result = await sut.ImportReleaseAsync(testCluster, release);

        // Assert — should update existing, not create new.

        result.Id.Should().Be(existing.Id);
        result.Namespace.Should().Be("monitoring");
        result.HelmChartVersion.Should().Be("55.0.0");
        result.HelmChartName.Should().Be("kube-prometheus-stack");
        result.Status.Should().Be(ComponentStatus.Installed);

        int count = await db.ClusterComponents.CountAsync(c => c.ClusterId == clusterId && c.Name == "prometheus");
        count.Should().Be(1);
    }

    [Fact]
    public async Task ImportAllNewReleasesAsync_SkipsAlreadyTracked()
    {
        // Arrange — mix of tracked and new releases.

        List<DiscoveredHelmRelease> releases =
        [
            new() { Name = "existing-one", Namespace = "ns1", Status = "deployed", Revision = 1, AlreadyTracked = true },
            new() { Name = "new-one", Namespace = "ns2", ChartName = "new-chart", ChartVersion = "1.0.0", Status = "deployed", Revision = 1, AlreadyTracked = false },
            new() { Name = "new-two", Namespace = "ns3", ChartName = "other-chart", ChartVersion = "2.0.0", Status = "deployed", Revision = 1, AlreadyTracked = false }
        ];

        // Act

        int imported = await sut.ImportAllNewReleasesAsync(testCluster, releases);

        // Assert — only non-tracked releases should be imported.

        imported.Should().Be(2);
        int total = await db.ClusterComponents.CountAsync(c => c.ClusterId == clusterId);
        total.Should().Be(2);
    }

    // ──────── MapStatus Tests ────────

    [Theory]
    [InlineData("deployed", ComponentStatus.Installed)]
    [InlineData("failed", ComponentStatus.Failed)]
    [InlineData("pending-install", ComponentStatus.Installing)]
    [InlineData("pending-upgrade", ComponentStatus.Installing)]
    [InlineData("pending-rollback", ComponentStatus.Installing)]
    [InlineData("uninstalling", ComponentStatus.Uninstalling)]
    [InlineData("unknown-status", ComponentStatus.NotInstalled)]
    [InlineData(null, ComponentStatus.NotInstalled)]
    public void MapStatus_MapsHelmStatusToComponentStatus(string? helmStatus, ComponentStatus expected)
    {
        // Use reflection to test the private static method.

        System.Reflection.MethodInfo? method = typeof(ComponentScanService)
            .GetMethod("MapStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull();

        ComponentStatus result = (ComponentStatus)method!.Invoke(null, [helmStatus])!;

        result.Should().Be(expected);
    }

    // ──────── Helpers ────────

    /// <summary>
    /// Encodes a Helm release JSON object into the format stored in K8s secrets:
    /// JSON → gzip → base64 (Helm). The K8s layer handles the outer base64.
    /// </summary>
    private static byte[] EncodeHelmRelease(HelmReleaseJson release)
    {
        string json = JsonSerializer.Serialize(release, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

        using MemoryStream compressedStream = new();
        using (GZipStream gzipStream = new(compressedStream, CompressionMode.Compress))
        {
            gzipStream.Write(jsonBytes);
        }

        // Helm base64 encoding.

        string helmBase64 = Convert.ToBase64String(compressedStream.ToArray());
        return Encoding.UTF8.GetBytes(helmBase64);
    }

    /// <summary>
    /// Invokes the private DecodeHelmRelease method via reflection for unit testing.
    /// </summary>
    private static DiscoveredHelmRelease? InvokeDecodeHelmRelease(byte[] secretData, int revision)
    {
        System.Reflection.MethodInfo? method = typeof(ComponentScanService)
            .GetMethod("DecodeHelmRelease", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (method is null)
        {
            throw new InvalidOperationException("DecodeHelmRelease method not found");
        }

        // Build a minimal V1Secret with the release data.

        k8s.Models.V1Secret secret = new()
        {
            Metadata = new k8s.Models.V1ObjectMeta
            {
                Name = "sh.helm.release.v1.test.v1",
                NamespaceProperty = "default",
                Labels = new Dictionary<string, string>
                {
                    ["name"] = "test",
                    ["owner"] = "helm",
                    ["version"] = revision.ToString()
                }
            },
            Data = new Dictionary<string, byte[]>
            {
                ["release"] = secretData
            }
        };

        return method.Invoke(null, [secret, revision]) as DiscoveredHelmRelease;
    }

    // ──────── Test DTOs for creating Helm release JSON ────────

    private class HelmReleaseJson
    {
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public HelmChartJson? Chart { get; set; }
        public HelmInfoJson? Info { get; set; }
        public JsonElement? Config { get; set; }
    }

    private class HelmChartJson
    {
        public HelmChartMetadataJson? Metadata { get; set; }
    }

    private class HelmChartMetadataJson
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? AppVersion { get; set; }
    }

    private class HelmInfoJson
    {
        public string? Status { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("last_deployed")]
        public string? LastDeployed { get; set; }
    }
}
