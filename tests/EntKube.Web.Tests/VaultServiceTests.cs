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

    // --- Certificate import (kubernetes.io/tls → readable Certificate secret) ---

    [Fact]
    public async Task ImportObservedAppCertificate_StoresParseableCertificate_ClusterOwned()
    {
        // A tls.crt + tls.key Secret imported from a cluster becomes one Certificate-type
        // vault secret, tracked read-only (sync off, target recorded).

        (Tenant tenant, App app) = CreateTenantWithApp();
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
        Guid clusterId = cluster.Id;
        await sut.InitializeVaultAsync(tenant.Id);

        (string certPem, string keyPem) = MakeSelfSignedCert("import-test.example.com");
        (bool ok, CertificateBundle bundle) = TryBuild(certPem, keyPem);
        ok.Should().BeTrue();

        (bool stored, string? error) = await sut.ImportObservedAppCertificateAsync(
            tenant.Id, app.Id, "my-app-tls", bundle,
            clusterId, secretName: "my-app-tls", ns: "prod");

        stored.Should().BeTrue(error);

        List<VaultSecret> secrets = await sut.GetAppSecretsAsync(tenant.Id, app.Id);
        secrets.Should().HaveCount(1);
        VaultSecret cert = secrets[0];
        cert.Name.Should().Be("my-app-tls");
        cert.SecretType.Should().Be(VaultSecretType.Certificate);
        cert.SyncToKubernetes.Should().BeFalse();
        cert.KubernetesClusterId.Should().Be(clusterId);
        cert.KubernetesSecretName.Should().Be("my-app-tls");
        cert.KubernetesNamespace.Should().Be("prod");

        // It is genuinely readable as a certificate.
        CertificateInfo? info = await sut.GetCertificateInfoByIdAsync(cert.Id);
        info.Should().NotBeNull();
        info!.Subject.Should().Contain("import-test.example.com");

        // And it lands in the observed set the reverse-refresher polls.
        List<Guid> observed = await sut.GetObservedAppSecretIdsAsync();
        observed.Should().Contain(cert.Id);
    }

    [Fact]
    public async Task ConvertImportedTlsSecrets_MergesOpaqueKeyPair_IntoOneCertificate()
    {
        // A pre-existing import that landed as two opaque secrets (tls.crt + tls.key sharing
        // one K8s target) is merged into a single readable Certificate secret.

        (Tenant tenant, App app) = CreateTenantWithApp();
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
        await sut.InitializeVaultAsync(tenant.Id);

        (string certPem, string keyPem) = MakeSelfSignedCert("legacy-import.example.com");

        // Simulate the old opaque import: one vault secret per key, same K8s target, sync off.
        foreach ((string key, string val) in new[] { ("tls.crt", certPem), ("tls.key", keyPem) })
        {
            VaultSecret s = await sut.SetAppSecretAsync(tenant.Id, app.Id, key, val);
            await sut.ConfigureKubernetesSyncAsync(
                s.Id, syncEnabled: false, secretName: "legacy-tls", ns: "prod", clusterId: cluster.Id);
        }

        int converted = await sut.ConvertImportedTlsSecretsToCertificatesAsync();

        converted.Should().Be(1);

        List<VaultSecret> secrets = await sut.GetAppSecretsAsync(tenant.Id, app.Id);
        secrets.Should().HaveCount(1);
        VaultSecret cert = secrets[0];
        cert.Name.Should().Be("legacy-tls");
        cert.SecretType.Should().Be(VaultSecretType.Certificate);
        cert.KubernetesSecretName.Should().Be("legacy-tls");
        cert.SyncToKubernetes.Should().BeFalse();

        CertificateInfo? info = await sut.GetCertificateInfoByIdAsync(cert.Id);
        info!.Subject.Should().Contain("legacy-import.example.com");

        // Idempotent: a second pass finds nothing to do.
        (await sut.ConvertImportedTlsSecretsToCertificatesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ConvertImportedTlsSecrets_LeavesNonCertificateOpaqueKeysAlone()
    {
        // A "tls.crt" opaque secret whose value isn't actually a certificate is not merged.

        (Tenant tenant, App app) = CreateTenantWithApp();
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
        await sut.InitializeVaultAsync(tenant.Id);

        VaultSecret s = await sut.SetAppSecretAsync(tenant.Id, app.Id, "tls.crt", "not-a-certificate");
        await sut.ConfigureKubernetesSyncAsync(
            s.Id, syncEnabled: false, secretName: "weird", ns: "prod", clusterId: cluster.Id);

        (await sut.ConvertImportedTlsSecretsToCertificatesAsync()).Should().Be(0);

        List<VaultSecret> secrets = await sut.GetAppSecretsAsync(tenant.Id, app.Id);
        secrets.Should().ContainSingle(x => x.Name == "tls.crt" && x.SecretType == VaultSecretType.Opaque);
    }

    [Theory]
    [InlineData(new[] { "tls.crt", "tls.key" }, true)]
    [InlineData(new[] { "tls.crt", "tls.key", "ca.crt" }, true)]
    [InlineData(new[] { "tls.crt" }, true)]
    [InlineData(new[] { "tls.crt", "extra" }, false)] // extra key → opaque, don't drop it
    [InlineData(new[] { "username", "password" }, false)]
    public void DetectedSecret_IsCertificate_ClassifiesByTlsKeys(string[] keys, bool expected)
    {
        DetectedSecret detected = new() { SecretName = "s", Namespace = "ns" };
        foreach (string k in keys)
        {
            detected.Values[k] = k == "tls.crt" ? "-----BEGIN CERTIFICATE-----\nx\n-----END CERTIFICATE-----" : "v";
        }

        detected.IsCertificate.Should().Be(expected);
    }

    [Fact]
    public void DetectedSecret_IsCertificate_False_WhenTlsCrtEmpty()
    {
        DetectedSecret detected = new() { SecretName = "s", Namespace = "ns" };
        detected.Values["tls.crt"] = "";
        detected.Values["tls.key"] = "key";

        detected.IsCertificate.Should().BeFalse();
    }

    // --- Tenant "library" certificates (app-less) ---

    [Fact]
    public async Task SetTenantCertificate_CreatesListsAndDecrypts()
    {
        Tenant tenant = CreateTenant();
        (string certPem, string keyPem) = MakeSelfSignedCert("wildcard.example.com");
        (bool built, CertificateBundle bundle) = TryBuild(certPem, keyPem);
        built.Should().BeTrue();

        (bool ok, string? error, Guid? id) = await sut.SetTenantCertificateAsync(tenant.Id, "wildcard", bundle);
        ok.Should().BeTrue(error);
        id.Should().NotBeNull();

        List<TenantCertificateInfo> list = await sut.GetTenantCertificatesAsync(tenant.Id);
        list.Should().ContainSingle();
        list[0].Name.Should().Be("wildcard");
        list[0].HasPrivateKey.Should().BeTrue();
        list[0].Info.Should().NotBeNull();

        // Decrypts back to a usable bundle.
        CertificateBundle? loaded = await sut.GetCertificateBundleByIdAsync(id!.Value);
        loaded!.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public async Task SetTenantCertificate_UpsertsByName()
    {
        Tenant tenant = CreateTenant();
        (string c1, string k1) = MakeSelfSignedCert("a.example.com");
        (string c2, string k2) = MakeSelfSignedCert("b.example.com");

        (_, _, Guid? first) = await sut.SetTenantCertificateAsync(tenant.Id, "svc", TryBuild(c1, k1).Bundle);
        (_, _, Guid? second) = await sut.SetTenantCertificateAsync(tenant.Id, "svc", TryBuild(c2, k2).Bundle);

        second.Should().Be(first!.Value); // same row updated in place
        (await sut.GetTenantCertificatesAsync(tenant.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteTenantCertificate_Removes()
    {
        Tenant tenant = CreateTenant();
        (string certPem, string keyPem) = MakeSelfSignedCert("x.example.com");
        (_, _, Guid? id) = await sut.SetTenantCertificateAsync(tenant.Id, "x", TryBuild(certPem, keyPem).Bundle);

        await sut.DeleteTenantCertificateAsync(id!.Value);

        (await sut.GetTenantCertificatesAsync(tenant.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetTenantCertificates_ExcludesAppScopedCerts()
    {
        (Tenant tenant, App app) = CreateTenantWithApp();
        await sut.InitializeVaultAsync(tenant.Id);
        (string certPem, string keyPem) = MakeSelfSignedCert("app.example.com");
        CertificateBundle bundle = TryBuild(certPem, keyPem).Bundle;

        await sut.SetAppCertificateAsync(tenant.Id, app.Id, "app-cert", bundle);
        await sut.SetTenantCertificateAsync(tenant.Id, "tenant-cert", bundle);

        List<TenantCertificateInfo> library = await sut.GetTenantCertificatesAsync(tenant.Id);
        library.Should().ContainSingle(c => c.Name == "tenant-cert");
    }

    private static (bool Ok, CertificateBundle Bundle) TryBuild(string certPem, string keyPem)
    {
        string combined = keyPem.Trim() + "\n" + certPem.Trim();
        (bool ok, _, CertificateBundle? built) = CertificateImporter.Import(
            System.Text.Encoding.UTF8.GetBytes(combined), "import.pem", null);
        return (ok && built is not null, built ?? new CertificateBundle());
    }

    private static (string CertPem, string KeyPem) MakeSelfSignedCert(string cn)
    {
        using System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest req = new(
            $"CN={cn}", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using System.Security.Cryptography.X509Certificates.X509Certificate2 cert =
            req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }
}
