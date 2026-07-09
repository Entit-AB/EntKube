using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the StorageService which manages external storage links (AWS S3,
/// Azure Storage, Cleura S3) and MinIO discovery. Uses SQLite in-memory for
/// fast, isolated database tests.
///
/// Note: MinIO discovery tests are not included here because they require
/// a live Kubernetes cluster. These tests focus on the CRUD operations
/// for external storage links and vault credential management.
/// </summary>
public class StorageServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly VaultService vaultService;
    private readonly StorageService sut;

    public StorageServiceTests()
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
        vaultService = new VaultService(dbFactory, encryption);
        Mock<IHttpClientFactory> httpFactory = new();
        OpenStackS3Service openStackS3 = new(vaultService, httpFactory.Object, new OpenStackKeystoneClient(httpFactory.Object));
        StorageLinkClientFactory storageClientFactory = new(vaultService, dbFactory);
        sut = new StorageService(dbFactory, vaultService, openStackS3, new Mock<IKubernetesClientFactory>().Object, storageClientFactory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private (Tenant tenant, Data.Environment env) CreateTenantWithEnvironment()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Production"
        };
        db.Set<Data.Environment>().Add(env);
        db.SaveChanges();

        return (tenant, env);
    }

    // ──────── IsMinioAvailableAsync ────────

    [Fact]
    public async Task IsMinioAvailableAsync_NoComponents_ReturnsFalse()
    {
        // Arrange — tenant with no clusters/components.

        (Tenant tenant, _) = CreateTenantWithEnvironment();

        // Act

        bool result = await sut.IsMinioAvailableAsync(tenant.Id);

        // Assert

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsMinioAvailableAsync_MinioInstalled_ReturnsTrue()
    {
        // Arrange — tenant with a cluster that has minio installed.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com"
        };
        db.KubernetesClusters.Add(cluster);

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "minio",
            ComponentType = "helm",
            Status = ComponentStatus.Installed
        };
        db.ClusterComponents.Add(component);
        await db.SaveChangesAsync();

        // Act

        bool result = await sut.IsMinioAvailableAsync(tenant.Id);

        // Assert

        result.Should().BeTrue();
    }

    // ──────── GetStorageLinksAsync ────────

    [Fact]
    public async Task GetStorageLinksAsync_NoLinks_ReturnsEmpty()
    {
        // Arrange

        (Tenant tenant, _) = CreateTenantWithEnvironment();

        // Act

        List<StorageLink> result = await sut.GetStorageLinksAsync(tenant.Id);

        // Assert

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStorageLinksAsync_WithLinks_ReturnsAll()
    {
        // Arrange — add two links for the tenant.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        db.StorageLinks.AddRange(
            new StorageLink
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EnvironmentId = env.Id,
                Provider = StorageProvider.AwsS3,
                Name = "Backups"
            },
            new StorageLink
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EnvironmentId = env.Id,
                Provider = StorageProvider.CleuraS3,
                Name = "Media"
            });
        await db.SaveChangesAsync();

        // Act

        List<StorageLink> result = await sut.GetStorageLinksAsync(tenant.Id);

        // Assert

        result.Should().HaveCount(2);
        result.Select(s => s.Name).Should().Contain("Backups").And.Contain("Media");
    }

    [Fact]
    public async Task GetStorageLinksAsync_FilterByEnvironment_ReturnsFiltered()
    {
        // Arrange — two environments, one link each.

        (Tenant tenant, Data.Environment env1) = CreateTenantWithEnvironment();

        Data.Environment env2 = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Staging"
        };
        db.Set<Data.Environment>().Add(env2);

        db.StorageLinks.AddRange(
            new StorageLink
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EnvironmentId = env1.Id,
                Provider = StorageProvider.AwsS3,
                Name = "Prod Backups"
            },
            new StorageLink
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EnvironmentId = env2.Id,
                Provider = StorageProvider.AwsS3,
                Name = "Staging Backups"
            });
        await db.SaveChangesAsync();

        // Act

        List<StorageLink> result = await sut.GetStorageLinksAsync(tenant.Id, env1.Id);

        // Assert

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Prod Backups");
    }

    // ──────── CreateStorageLinkAsync ────────

    [Fact]
    public async Task CreateStorageLinkAsync_CreatesLinkAndStoresCredentials()
    {
        // Arrange

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        // Act — create a link with credentials.

        StorageLink link = await sut.CreateStorageLinkAsync(
            tenant.Id,
            env.Id,
            StorageProvider.AwsS3,
            "My Bucket",
            "https://s3.eu-west-1.amazonaws.com",
            "my-app-backups",
            "eu-west-1",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            "Production backup bucket");

        // Assert — the link is persisted.

        StorageLink? saved = await db.StorageLinks.FindAsync(link.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("My Bucket");
        saved.Provider.Should().Be(StorageProvider.AwsS3);
        saved.BucketName.Should().Be("my-app-backups");
        saved.Region.Should().Be("eu-west-1");
        saved.Endpoint.Should().Be("https://s3.eu-west-1.amazonaws.com");
        saved.Notes.Should().Be("Production backup bucket");

        // Assert — credentials are stored in the vault.

        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenant.Id, link.Id);
        secrets.Should().HaveCount(2);
        secrets.Select(s => s.Name).Should().Contain("ACCESS_KEY").And.Contain("SECRET_KEY");
    }

    [Fact]
    public async Task CreateStorageLinkAsync_WithoutCredentials_CreatesLinkOnly()
    {
        // Arrange

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        // Act — create a link without credentials (just a reference).

        StorageLink link = await sut.CreateStorageLinkAsync(
            tenant.Id,
            env.Id,
            StorageProvider.AzureStorage,
            "Azure Media",
            "https://myaccount.blob.core.windows.net",
            "media-container",
            "swedencentral",
            null,
            null,
            null);

        // Assert — link exists, no vault secrets.

        StorageLink? saved = await db.StorageLinks.FindAsync(link.Id);
        saved.Should().NotBeNull();
        saved!.Provider.Should().Be(StorageProvider.AzureStorage);

        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenant.Id, link.Id);
        secrets.Should().BeEmpty();
    }

    // ──────── UpdateStorageLinkAsync ────────

    [Fact]
    public async Task UpdateStorageLinkAsync_UpdatesMetadata()
    {
        // Arrange

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Old Name",
            BucketName = "old-bucket"
        };
        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        // Act

        await sut.UpdateStorageLinkAsync(
            link.Id, "New Name", "https://new-endpoint.com", "new-bucket", "eu-north-1", "Updated notes");

        // Assert — clear tracking so we get fresh data from DB

        db.ChangeTracker.Clear();
        StorageLink? updated = await db.StorageLinks.FindAsync(link.Id);
        updated!.Name.Should().Be("New Name");
        updated.Endpoint.Should().Be("https://new-endpoint.com");
        updated.BucketName.Should().Be("new-bucket");
        updated.Region.Should().Be("eu-north-1");
        updated.Notes.Should().Be("Updated notes");
    }

    // ──────── DeleteStorageLinkAsync ────────

    [Fact]
    public async Task DeleteStorageLinkAsync_RemovesLinkAndVaultSecrets()
    {
        // Arrange — create a link with credentials.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        StorageLink link = await sut.CreateStorageLinkAsync(
            tenant.Id, env.Id, StorageProvider.AwsS3, "To Delete",
            "https://s3.amazonaws.com", "bucket", "us-east-1",
            "key123", "secret456", null);

        // Verify credentials exist before deletion.

        List<VaultSecret> secretsBefore = await vaultService.GetStorageLinkSecretsAsync(tenant.Id, link.Id);
        secretsBefore.Should().HaveCount(2);

        // Act

        await sut.DeleteStorageLinkAsync(tenant.Id, link.Id);

        // Assert — link and secrets are gone.

        StorageLink? deleted = await db.StorageLinks.FindAsync(link.Id);
        deleted.Should().BeNull();

        List<VaultSecret> secretsAfter = await vaultService.GetStorageLinkSecretsAsync(tenant.Id, link.Id);
        secretsAfter.Should().BeEmpty();
    }

    // ──────── GetEnvironmentsAsync ────────

    [Fact]
    public async Task GetEnvironmentsAsync_ReturnsTenantEnvironments()
    {
        // Arrange

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        // Act

        List<Data.Environment> result = await sut.GetEnvironmentsAsync(tenant.Id);

        // Assert

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Production");
    }

    // ──────── OpenStack Connections ────────

    [Fact]
    public async Task GetOpenStackConnectionsAsync_NoConnections_ReturnsEmpty()
    {
        // Arrange

        (Tenant tenant, _) = CreateTenantWithEnvironment();

        // Act

        List<OpenStackConnection> result = await sut.GetOpenStackConnectionsAsync(tenant.Id);

        // Assert

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateOpenStackConnectionAsync_CreatesConnectionAndStoresPassword()
    {
        // Arrange

        (Tenant tenant, _) = CreateTenantWithEnvironment();

        // Act

        OpenStackConnection connection = await sut.CreateOpenStackConnectionAsync(
            tenant.Id,
            "Cleura Prod",
            "https://identity.c2.citycloud.com:5000/v3",
            "Kna1",
            "my-project",
            "abc123",
            "Default",
            "Default",
            "admin@example.com",
            "supersecret");

        // Assert — connection is persisted with metadata.

        OpenStackConnection? saved = await db.OpenStackConnections.FindAsync(connection.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Cleura Prod");
        saved.AuthUrl.Should().Be("https://identity.c2.citycloud.com:5000/v3");
        saved.Region.Should().Be("Kna1");
        saved.ProjectName.Should().Be("my-project");
        saved.ProjectId.Should().Be("abc123");
        saved.UserDomainName.Should().Be("Default");
        saved.ProjectDomainName.Should().Be("Default");
        saved.Username.Should().Be("admin@example.com");

        // Assert — password is in the vault.

        List<VaultSecret> secrets = await vaultService.GetOpenStackSecretsAsync(tenant.Id, connection.Id);
        secrets.Should().HaveCount(1);
        secrets[0].Name.Should().Be("OS_PASSWORD");
    }

    [Fact]
    public async Task DeleteOpenStackConnectionAsync_RemovesConnectionAndVaultSecrets()
    {
        // Arrange

        (Tenant tenant, _) = CreateTenantWithEnvironment();

        OpenStackConnection connection = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "ToDelete", "https://auth.example.com/v3",
            "Sto2", null, null, null, null, "user", "pass123");

        // Act

        bool deleted = await sut.DeleteOpenStackConnectionAsync(tenant.Id, connection.Id);

        // Assert

        deleted.Should().BeTrue();

        OpenStackConnection? found = await db.OpenStackConnections.FindAsync(connection.Id);
        found.Should().BeNull();

        List<VaultSecret> secrets = await vaultService.GetOpenStackSecretsAsync(tenant.Id, connection.Id);
        secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteOpenStackConnectionAsync_WithLinkedStorage_ReturnsFalse()
    {
        // Arrange — create a connection with a storage link referencing it.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        OpenStackConnection connection = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "InUse", "https://auth.example.com/v3",
            "Kna1", null, null, null, null, null, null);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Cleura Bucket",
            OpenStackConnectionId = connection.Id
        };
        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        // Act — should not delete because a link depends on it.

        bool deleted = await sut.DeleteOpenStackConnectionAsync(tenant.Id, connection.Id);

        // Assert

        deleted.Should().BeFalse();

        OpenStackConnection? stillExists = await db.OpenStackConnections.FindAsync(connection.Id);
        stillExists.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateStorageLinkAsync_CleuraWithOpenStack_SetsConnectionId()
    {
        // Arrange

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        OpenStackConnection connection = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Cleura", "https://auth.example.com/v3",
            "Kna1", null, null, null, null, null, null);

        // Act

        StorageLink link = await sut.CreateStorageLinkAsync(
            tenant.Id, env.Id, StorageProvider.CleuraS3, "Cleura Bucket",
            "https://s3-kna1.cloudferro.com", "my-bucket", "Kna1",
            null, null, null, connection.Id);

        // Assert

        StorageLink? saved = await db.StorageLinks.FindAsync(link.Id);
        saved.Should().NotBeNull();
        saved!.OpenStackConnectionId.Should().Be(connection.Id);
        saved.Provider.Should().Be(StorageProvider.CleuraS3);
    }

    // ══════════════════════════════════════════════════════════════
    //  Storage Bindings
    // ══════════════════════════════════════════════════════════════

    private (KubernetesCluster cluster, AppDeployment deployment, StorageLink link) CreateDeploymentWithStorageLink(
        Tenant tenant, Data.Environment env)
    {
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Acme" };
        db.Customers.Add(customer);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing-api" };
        db.Apps.Add(app);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com"
        };
        db.KubernetesClusters.Add(cluster);

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Name = "billing-api-prod",
            Type = DeploymentType.HelmChart,
            EnvironmentId = env.Id,
            ClusterId = cluster.Id,
            Namespace = "billing"
        };
        db.AppDeployments.Add(deployment);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.AwsS3,
            Name = "Invoice PDFs",
            Endpoint = "https://s3.eu-west-1.amazonaws.com",
            BucketName = "acme-invoices",
            Region = "eu-west-1"
        };
        db.StorageLinks.Add(link);
        db.SaveChanges();

        return (cluster, deployment, link);
    }

    [Fact]
    public async Task BindStorageToDeploymentAsync_CreatesBinding()
    {
        // Arrange — a deployment and a storage link ready to be connected.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();
        (_, AppDeployment deployment, StorageLink link) = CreateDeploymentWithStorageLink(tenant, env);

        // Act — bind the storage to the deployment so credentials will be synced
        // to a K8s Secret named "invoice-storage" in the deployment's namespace.

        StorageBinding binding = await sut.BindStorageToDeploymentAsync(
            link.Id, deployment.Id, "invoice-storage");

        // Assert — the binding exists and references both sides correctly.

        binding.Should().NotBeNull();
        binding.StorageLinkId.Should().Be(link.Id);
        binding.AppDeploymentId.Should().Be(deployment.Id);
        binding.ComponentId.Should().BeNull();
        binding.KubernetesSecretName.Should().Be("invoice-storage");
        binding.SyncEnabled.Should().BeTrue();

        StorageBinding? persisted = await db.Set<StorageBinding>().FindAsync(binding.Id);
        persisted.Should().NotBeNull();
    }

    [Fact]
    public async Task BindStorageToComponentAsync_CreatesBinding()
    {
        // Arrange — a cluster component that needs access to storage.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "infra-cluster",
            ApiServerUrl = "https://k8s.infra.example.com"
        };
        db.KubernetesClusters.Add(cluster);

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "loki",
            ComponentType = "helm",
            Namespace = "monitoring"
        };
        db.ClusterComponents.Add(component);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Loki Chunks",
            Endpoint = "https://s3-kna1.citycloud.com",
            BucketName = "loki-chunks",
            Region = "Kna1"
        };
        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        // Act — bind storage to the component.

        StorageBinding binding = await sut.BindStorageToComponentAsync(
            link.Id, component.Id, "loki-s3-credentials");

        // Assert

        binding.Should().NotBeNull();
        binding.StorageLinkId.Should().Be(link.Id);
        binding.AppDeploymentId.Should().BeNull();
        binding.ComponentId.Should().Be(component.Id);
        binding.KubernetesSecretName.Should().Be("loki-s3-credentials");
    }

    [Fact]
    public async Task GetBindingsForDeploymentAsync_ReturnsOnlyDeploymentBindings()
    {
        // Arrange — one deployment with two storage bindings.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();
        (_, AppDeployment deployment, StorageLink link) = CreateDeploymentWithStorageLink(tenant, env);

        StorageLink link2 = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.AzureStorage,
            Name = "Logs",
            Endpoint = "https://acmelogs.blob.core.windows.net",
            BucketName = "logs"
        };
        db.StorageLinks.Add(link2);
        await db.SaveChangesAsync();

        await sut.BindStorageToDeploymentAsync(link.Id, deployment.Id, "invoices");
        await sut.BindStorageToDeploymentAsync(link2.Id, deployment.Id, "logs");

        // Act

        List<StorageBinding> bindings = await sut.GetBindingsForDeploymentAsync(deployment.Id);

        // Assert

        bindings.Should().HaveCount(2);
        bindings.Select(b => b.KubernetesSecretName)
            .Should().Contain("invoices").And.Contain("logs");
        bindings.Should().AllSatisfy(b => b.StorageLink.Should().NotBeNull());
    }

    [Fact]
    public async Task UnbindStorageAsync_RemovesBinding()
    {
        // Arrange

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();
        (_, AppDeployment deployment, StorageLink link) = CreateDeploymentWithStorageLink(tenant, env);

        StorageBinding binding = await sut.BindStorageToDeploymentAsync(
            link.Id, deployment.Id, "temp-storage");

        // Act — unbind the storage.

        bool removed = await sut.UnbindStorageAsync(binding.Id);

        // Assert

        removed.Should().BeTrue();
        StorageBinding? found = await db.Set<StorageBinding>().FindAsync(binding.Id);
        found.Should().BeNull();
    }

    [Fact]
    public async Task UnbindStorageAsync_NonExistentBinding_ReturnsFalse()
    {
        // Act

        bool removed = await sut.UnbindStorageAsync(Guid.NewGuid());

        // Assert

        removed.Should().BeFalse();
    }

    [Fact]
    public async Task BindStorageToDeploymentAsync_ConfiguresVaultSecretsForSync()
    {
        // Arrange — a storage link with credentials already in the vault.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();
        (_, AppDeployment deployment, StorageLink link) = CreateDeploymentWithStorageLink(tenant, env);

        // Store S3 credentials in the vault for this storage link.

        await vaultService.InitializeVaultAsync(tenant.Id);
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, link.Id, "ACCESS_KEY", "AKIAEXAMPLE");
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, link.Id, "SECRET_KEY", "s3cr3t");

        // Act — bind the storage, which should mark vault secrets for K8s sync.

        StorageBinding binding = await sut.BindStorageToDeploymentAsync(
            link.Id, deployment.Id, "invoice-storage");

        // Assert — the storage link's secrets should now have K8s sync configured
        // targeting the deployment's namespace and the binding's secret name.

        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenant.Id, link.Id);
        secrets.Should().HaveCount(2);
        secrets.Should().AllSatisfy(s =>
        {
            s.SyncToKubernetes.Should().BeTrue();
            s.KubernetesSecretName.Should().Be("invoice-storage");
            s.KubernetesNamespace.Should().Be("billing");
        });
    }

    [Fact]
    public async Task UnbindStorageAsync_DisablesVaultSecretSync()
    {
        // Arrange — bind a storage with credentials, then unbind.

        (Tenant tenant, Data.Environment env) = CreateTenantWithEnvironment();
        (_, AppDeployment deployment, StorageLink link) = CreateDeploymentWithStorageLink(tenant, env);

        await vaultService.InitializeVaultAsync(tenant.Id);
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, link.Id, "ACCESS_KEY", "AKIAEXAMPLE");
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, link.Id, "SECRET_KEY", "s3cr3t");

        StorageBinding binding = await sut.BindStorageToDeploymentAsync(
            link.Id, deployment.Id, "invoice-storage");

        // Act — unbind.

        await sut.UnbindStorageAsync(binding.Id);

        // Assert — secrets should no longer be marked for K8s sync.

        List<VaultSecret> secrets = await vaultService.GetStorageLinkSecretsAsync(tenant.Id, link.Id);
        secrets.Should().AllSatisfy(s =>
        {
            s.SyncToKubernetes.Should().BeFalse();
            s.KubernetesSecretName.Should().BeNull();
            s.KubernetesNamespace.Should().BeNull();
        });
    }
}
