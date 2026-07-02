using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the VaultService which manages per-tenant vaults, secrets for apps
/// and components, and Kubernetes sync configuration. Uses SQLite in-memory
/// for fast, isolated database tests.
/// </summary>
public class VaultServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly VaultService sut;

    public VaultServiceTests()
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
        sut = new VaultService(dbFactory, encryption);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Tenant CreateTenant(string name = "TestCo", string slug = "testco")
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = name, Slug = slug };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant;
    }

    private (Tenant tenant, KubernetesCluster cluster) CreateTenantWithCluster()
    {
        Tenant tenant = CreateTenant();
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "production" };
        db.Environments.Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com"
        };

        db.KubernetesClusters.Add(cluster);
        db.SaveChanges();
        return (tenant, cluster);
    }

    private (Tenant tenant, App app) CreateTenantWithApp()
    {
        Tenant tenant = CreateTenant();
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "CustomerA" };
        db.Customers.Add(customer);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "MyApp" };
        db.Apps.Add(app);
        db.SaveChanges();
        return (tenant, app);
    }

    // --- Vault Initialization ---

    [Fact]
    public async Task InitializeVaultAsync_CreatesVaultForTenant()
    {
        // When a tenant is first set up, we create a vault with a sealed DEK.

        Tenant tenant = CreateTenant();

        SecretVault vault = await sut.InitializeVaultAsync(tenant.Id);

        vault.Should().NotBeNull();
        vault.TenantId.Should().Be(tenant.Id);
        vault.EncryptedDataKey.Should().NotBeEmpty();
        vault.Nonce.Should().NotBeEmpty();
    }

    [Fact]
    public async Task InitializeVaultAsync_CalledTwice_ReturnsSameVault()
    {
        // Idempotent — if the vault already exists, return it.

        Tenant tenant = CreateTenant();

        SecretVault first = await sut.InitializeVaultAsync(tenant.Id);
        SecretVault second = await sut.InitializeVaultAsync(tenant.Id);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task GetVaultAsync_ReturnsNullWhenNoVaultExists()
    {
        SecretVault? vault = await sut.GetVaultAsync(Guid.NewGuid());

        vault.Should().BeNull();
    }

    // --- App Secrets ---

    [Fact]
    public async Task SetAppSecretAsync_CreatesNewSecret()
    {
        // Store a secret for a customer app.

        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "DB_PASSWORD", "s3cret!");

        secret.Name.Should().Be("DB_PASSWORD");
        secret.AppId.Should().Be(app.Id);
        secret.EncryptedValue.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SetAppSecretAsync_SameNameUpdatesExisting()
    {
        // Setting a secret with the same name should update rather than create a duplicate.

        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        await sut.SetAppSecretAsync(tenant.Id, app.Id, "API_KEY", "old-value");
        VaultSecret updated = await sut.SetAppSecretAsync(tenant.Id, app.Id, "API_KEY", "new-value");

        // Should still be just one secret with that name.
        List<VaultSecret> secrets = await sut.GetAppSecretsAsync(tenant.Id, app.Id);
        secrets.Should().HaveCount(1);
        secrets[0].Id.Should().Be(updated.Id);
    }

    [Fact]
    public async Task GetAppSecretValueAsync_DecryptsCorrectly()
    {
        // The decrypted value should match what was originally stored.

        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        await sut.SetAppSecretAsync(tenant.Id, app.Id, "TOKEN", "my-secret-token-123");

        string? value = await sut.GetAppSecretValueAsync(tenant.Id, app.Id, "TOKEN");

        value.Should().Be("my-secret-token-123");
    }

    [Fact]
    public async Task GetAppSecretsAsync_ReturnsAllSecretsForApp()
    {
        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        await sut.SetAppSecretAsync(tenant.Id, app.Id, "SECRET_A", "val-a");
        await sut.SetAppSecretAsync(tenant.Id, app.Id, "SECRET_B", "val-b");

        List<VaultSecret> secrets = await sut.GetAppSecretsAsync(tenant.Id, app.Id);

        secrets.Should().HaveCount(2);
        secrets.Select(s => s.Name).Should().Contain(["SECRET_A", "SECRET_B"]);
    }

    [Fact]
    public async Task DeleteAppSecretAsync_RemovesSecret()
    {
        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "TEMP", "delete-me");
        bool deleted = await sut.DeleteSecretAsync(secret.Id);

        deleted.Should().BeTrue();
        List<VaultSecret> remaining = await sut.GetAppSecretsAsync(tenant.Id, app.Id);
        remaining.Should().BeEmpty();
    }

    // --- Component Secrets ---

    [Fact]
    public async Task SetComponentSecretAsync_CreatesNewSecret()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        await sut.InitializeVaultAsync(tenant.Id);

        ClusterComponent component = await sut.CreateComponentAsync(
            cluster.Id, "minio", "HelmChart");

        VaultSecret secret = await sut.SetComponentSecretAsync(
            tenant.Id, component.Id, "MINIO_ROOT_PASSWORD", "admin123");

        secret.Name.Should().Be("MINIO_ROOT_PASSWORD");
        secret.ComponentId.Should().Be(component.Id);
    }

    [Fact]
    public async Task GetComponentSecretValueAsync_DecryptsCorrectly()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        await sut.InitializeVaultAsync(tenant.Id);

        ClusterComponent component = await sut.CreateComponentAsync(
            cluster.Id, "postgres", "HelmChart");

        await sut.SetComponentSecretAsync(
            tenant.Id, component.Id, "PG_PASSWORD", "postgres-secret-pw");

        string? value = await sut.GetComponentSecretValueAsync(
            tenant.Id, component.Id, "PG_PASSWORD");

        value.Should().Be("postgres-secret-pw");
    }

    [Fact]
    public async Task GetComponentSecretsAsync_ReturnsOnlyComponentSecrets()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        await sut.InitializeVaultAsync(tenant.Id);

        ClusterComponent comp = await sut.CreateComponentAsync(cluster.Id, "redis", "HelmChart");

        await sut.SetComponentSecretAsync(tenant.Id, comp.Id, "REDIS_PASSWORD", "r3dis");
        await sut.SetComponentSecretAsync(tenant.Id, comp.Id, "REDIS_TLS_CERT", "cert-data");

        List<VaultSecret> secrets = await sut.GetComponentSecretsAsync(tenant.Id, comp.Id);

        secrets.Should().HaveCount(2);
    }

    // --- Kubernetes Sync Configuration ---

    [Fact]
    public async Task ConfigureKubernetesSyncAsync_SetsFields()
    {
        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "DB_URL", "postgres://...");

        await sut.ConfigureKubernetesSyncAsync(
            secret.Id, syncEnabled: true, secretName: "myapp-db", ns: "production");

        VaultSecret? reloaded = await db.VaultSecrets.FindAsync(secret.Id);
        reloaded!.SyncToKubernetes.Should().BeTrue();
        reloaded.KubernetesSecretName.Should().Be("myapp-db");
        reloaded.KubernetesNamespace.Should().Be("production");
    }

    [Fact]
    public async Task ConfigureKubernetesSyncAsync_DisablesSync()
    {
        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);

        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "KEY", "val");
        await sut.ConfigureKubernetesSyncAsync(secret.Id, syncEnabled: true, secretName: "s", ns: "ns");
        await sut.ConfigureKubernetesSyncAsync(secret.Id, syncEnabled: false, secretName: null, ns: null);

        VaultSecret? reloaded = await db.VaultSecrets.FindAsync(secret.Id);
        reloaded!.SyncToKubernetes.Should().BeFalse();
    }

    // --- Cluster Components ---

    [Fact]
    public async Task CreateComponentAsync_CreatesComponent()
    {
        (_, KubernetesCluster cluster) = CreateTenantWithCluster();

        ClusterComponent component = await sut.CreateComponentAsync(
            cluster.Id, "keycloak", "HelmChart");

        component.Name.Should().Be("keycloak");
        component.ComponentType.Should().Be("HelmChart");
        component.ClusterId.Should().Be(cluster.Id);
    }

    [Fact]
    public async Task GetComponentsAsync_ReturnsComponentsForCluster()
    {
        (_, KubernetesCluster cluster) = CreateTenantWithCluster();

        await sut.CreateComponentAsync(cluster.Id, "minio", "HelmChart");
        await sut.CreateComponentAsync(cluster.Id, "cnpg", "Operator");

        List<ClusterComponent> components = await sut.GetComponentsAsync(cluster.Id);

        components.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteComponentAsync_RemovesComponent()
    {
        (_, KubernetesCluster cluster) = CreateTenantWithCluster();
        ClusterComponent component = await sut.CreateComponentAsync(cluster.Id, "temp", "Deployment");

        bool deleted = await sut.DeleteComponentAsync(component.Id);

        deleted.Should().BeTrue();
        List<ClusterComponent> remaining = await sut.GetComponentsAsync(cluster.Id);
        remaining.Should().BeEmpty();
    }

    // ──────── GetSecretValueByIdAsync ────────

    [Fact]
    public async Task GetSecretValueByIdAsync_ReturnsDecryptedValue()
    {
        // Arrange

        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);
        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "API_KEY", "my-secret-value");

        // Act

        string? value = await sut.GetSecretValueByIdAsync(secret.Id);

        // Assert

        value.Should().Be("my-secret-value");
    }

    [Fact]
    public async Task GetSecretValueByIdAsync_NonExistent_ReturnsNull()
    {
        // Act

        string? value = await sut.GetSecretValueByIdAsync(Guid.NewGuid());

        // Assert

        value.Should().BeNull();
    }

    // ──────── UpdateSecretValueAsync ────────

    [Fact]
    public async Task UpdateSecretValueAsync_UpdatesValue()
    {
        // Arrange

        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);
        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "DB_PASS", "old-password");

        // Act

        bool updated = await sut.UpdateSecretValueAsync(secret.Id, "new-password");

        // Assert

        updated.Should().BeTrue();
        string? value = await sut.GetSecretValueByIdAsync(secret.Id);
        value.Should().Be("new-password");
    }

    [Fact]
    public async Task UpdateSecretValueAsync_NonExistent_ReturnsFalse()
    {
        // Act

        bool updated = await sut.UpdateSecretValueAsync(Guid.NewGuid(), "anything");

        // Assert

        updated.Should().BeFalse();
    }

    // ──────── CanDeleteSecretAsync ────────

    [Fact]
    public async Task CanDeleteSecretAsync_NoBindings_ReturnsTrue()
    {
        // Arrange

        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);
        VaultSecret secret = await sut.SetAppSecretAsync(tenant.Id, app.Id, "TEMP", "value");

        // Act

        (bool canDelete, string? reason) = await sut.CanDeleteSecretAsync(secret.Id);

        // Assert

        canDelete.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task CanDeleteSecretAsync_WithActiveStorageBinding_ReturnsFalse()
    {
        // Arrange — a storage link secret that has an active binding.

        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        await sut.InitializeVaultAsync(tenant.Id);

        Data.Environment env = await db.Environments.FirstAsync(e => e.TenantId == tenant.Id);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.AwsS3,
            Name = "Backups"
        };
        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        VaultSecret secret = await sut.SetStorageLinkSecretAsync(tenant.Id, link.Id, "ACCESS_KEY", "AKIA...");

        // Create an app deployment that binds to this storage.

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Acme" };
        db.Customers.Add(customer);
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "api" };
        db.Apps.Add(app);

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Name = "api-prod",
            Type = DeploymentType.HelmChart,
            EnvironmentId = env.Id,
            ClusterId = cluster.Id,
            Namespace = "default"
        };
        db.AppDeployments.Add(deployment);

        StorageBinding binding = new()
        {
            Id = Guid.NewGuid(),
            StorageLinkId = link.Id,
            AppDeploymentId = deployment.Id,
            KubernetesSecretName = "backup-creds",
            SyncEnabled = true
        };
        db.Set<StorageBinding>().Add(binding);
        await db.SaveChangesAsync();

        // Act

        (bool canDelete, string? reason) = await sut.CanDeleteSecretAsync(secret.Id);

        // Assert

        canDelete.Should().BeFalse();
        reason.Should().Contain("storage binding");
    }

    [Fact]
    public async Task CanDeleteSecretAsync_WithDisabledBinding_ReturnsTrue()
    {
        // Arrange — same setup but binding has SyncEnabled = false.

        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        await sut.InitializeVaultAsync(tenant.Id);

        Data.Environment env = await db.Environments.FirstAsync(e => e.TenantId == tenant.Id);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.AwsS3,
            Name = "Archives"
        };
        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        VaultSecret secret = await sut.SetStorageLinkSecretAsync(tenant.Id, link.Id, "SECRET_KEY", "s3cr3t");

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Acme2" };
        db.Customers.Add(customer);
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "worker" };
        db.Apps.Add(app);

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Name = "worker-prod",
            Type = DeploymentType.Yaml,
            EnvironmentId = env.Id,
            ClusterId = cluster.Id,
            Namespace = "workers"
        };
        db.AppDeployments.Add(deployment);

        StorageBinding binding = new()
        {
            Id = Guid.NewGuid(),
            StorageLinkId = link.Id,
            AppDeploymentId = deployment.Id,
            KubernetesSecretName = "archive-creds",
            SyncEnabled = false
        };
        db.Set<StorageBinding>().Add(binding);
        await db.SaveChangesAsync();

        // Act

        (bool canDelete, string? reason) = await sut.CanDeleteSecretAsync(secret.Id);

        // Assert — disabled binding should not block deletion.

        canDelete.Should().BeTrue();
        reason.Should().BeNull();
    }

    // --- Cluster Kubeconfigs ---

    private const string SampleKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: prod
          cluster:
            server: https://k8s.example.com
        contexts:
        - name: prod
          context:
            cluster: prod
            user: admin
        users:
        - name: admin
          user:
            token: abc123
        """;

    [Fact]
    public async Task SetClusterKubeconfigAsync_StoresEncryptedAndLinksCluster()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        DateTime expiry = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        KubeconfigBundle bundle = new()
        {
            ConfigYaml = SampleKubeconfig,
            ContextName = "prod",
            ApiServerUrl = "https://k8s.example.com",
            ExpiresAt = expiry,
        };

        (bool ok, string? error, Guid? secretId) =
            await sut.SetClusterKubeconfigAsync(tenant.Id, cluster.Id, bundle, "admin@example.com");

        ok.Should().BeTrue();
        error.Should().BeNull();
        secretId.Should().NotBeNull();

        // The cluster is linked to the secret.
        KubernetesCluster reloaded = await db.KubernetesClusters.AsNoTracking().FirstAsync(c => c.Id == cluster.Id);
        reloaded.KubeconfigSecretId.Should().Be(secretId);

        // The secret is a cluster-scoped Kubeconfig secret, never synced to Kubernetes.
        VaultSecret stored = await db.VaultSecrets.AsNoTracking().FirstAsync(s => s.Id == secretId);
        stored.SecretType.Should().Be(VaultSecretType.Kubeconfig);
        stored.OwnerClusterId.Should().Be(cluster.Id);
        stored.SyncToKubernetes.Should().BeFalse();

        // Round-trips the YAML and expiry.
        KubeconfigBundle? got = await sut.GetKubeconfigBundleByIdAsync(secretId!.Value);
        got.Should().NotBeNull();
        got!.ConfigYaml.Should().Be(SampleKubeconfig);
        got.ExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public async Task SetClusterKubeconfigAsync_Update_ArchivesPreviousVersion()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();

        KubeconfigBundle first = new() { ConfigYaml = SampleKubeconfig, ContextName = "prod" };
        (bool ok1, _, Guid? secretId) = await sut.SetClusterKubeconfigAsync(tenant.Id, cluster.Id, first, "admin");
        ok1.Should().BeTrue();

        KubeconfigBundle second = new()
        {
            ConfigYaml = SampleKubeconfig.Replace("abc123", "xyz789"),
            ContextName = "prod",
        };
        (bool ok2, _, Guid? secretId2) = await sut.SetClusterKubeconfigAsync(tenant.Id, cluster.Id, second, "admin");

        ok2.Should().BeTrue();
        secretId2.Should().Be(secretId); // upsert keeps the same secret

        // The previous value was archived as a version.
        int versions = await db.VaultSecretVersions.CountAsync(v => v.SecretId == secretId);
        versions.Should().Be(1);

        KubeconfigBundle? latest = await sut.GetKubeconfigBundleByIdAsync(secretId!.Value);
        latest!.ConfigYaml.Should().Contain("xyz789");
    }

    [Fact]
    public async Task SetClusterKubeconfigAsync_RejectsInvalidKubeconfig()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();

        KubeconfigBundle bundle = new() { ConfigYaml = "this is not a kubeconfig" };

        (bool ok, string? error, Guid? secretId) =
            await sut.SetClusterKubeconfigAsync(tenant.Id, cluster.Id, bundle, "admin");

        ok.Should().BeFalse();
        error.Should().NotBeNull();
        secretId.Should().BeNull();
    }

    [Fact]
    public async Task GetClusterKubeconfigSecretsAsync_ListsKubeconfigsWithOwnerCluster()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();

        await sut.SetClusterKubeconfigAsync(
            tenant.Id, cluster.Id, new KubeconfigBundle { ConfigYaml = SampleKubeconfig }, "admin");

        List<VaultSecret> list = await sut.GetClusterKubeconfigSecretsAsync(tenant.Id);

        list.Should().ContainSingle();
        list[0].SecretType.Should().Be(VaultSecretType.Kubeconfig);
        list[0].OwnerClusterId.Should().Be(cluster.Id);
        list[0].OwnerCluster.Should().NotBeNull();
        list[0].OwnerCluster!.Name.Should().Be(cluster.Name);
    }

    [Fact]
    public async Task GetExpiringSecretCandidatesAsync_IncludesClusterKubeconfig()
    {
        (Tenant tenant, KubernetesCluster cluster) = CreateTenantWithCluster();
        DateTime expiry = DateTime.UtcNow.AddDays(10);

        KubeconfigBundle bundle = new()
        {
            ConfigYaml = SampleKubeconfig,
            ContextName = "prod",
            ExpiresAt = expiry,
        };
        await sut.SetClusterKubeconfigAsync(tenant.Id, cluster.Id, bundle, "admin");

        List<ExpiringSecretInfo> candidates = await sut.GetExpiringSecretCandidatesAsync(tenant.Id);

        ExpiringSecretInfo? kubeconfig = candidates.SingleOrDefault(c => c.SecretType == VaultSecretType.Kubeconfig);
        kubeconfig.Should().NotBeNull();
        kubeconfig!.OwnerClusterId.Should().Be(cluster.Id);
        kubeconfig.ClusterName.Should().Be(cluster.Name);
        kubeconfig.TypeLabel.Should().Be("Kubeconfig");
        kubeconfig.ScopeLabel.Should().Be($"Cluster: {cluster.Name}");
        kubeconfig.DaysUntilExpiry.Should().BeInRange(9, 10);
    }
}
