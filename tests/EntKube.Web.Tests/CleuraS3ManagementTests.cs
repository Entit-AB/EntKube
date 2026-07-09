using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for Cleura S3 bucket management operations exposed via StorageService.
/// These verify that the StorageService correctly validates inputs, loads
/// credentials from the vault, and delegates to OpenStackS3Service.
///
/// Since actual S3/Keystone calls require live infrastructure, we mock
/// OpenStackS3Service and focus on the orchestration logic.
/// </summary>
public class CleuraS3ManagementTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly VaultService vaultService;
    private readonly Mock<IHttpClientFactory> httpFactory;
    private readonly StorageService sut;

    // We'll use a real OpenStackS3Service since the methods we're testing
    // on StorageService validate state before calling it. For actual S3 calls
    // that would fail, we test error paths.

    public CleuraS3ManagementTests()
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
        httpFactory = new Mock<IHttpClientFactory>();
        OpenStackS3Service openStackS3 = new(vaultService, httpFactory.Object, new OpenStackKeystoneClient(httpFactory.Object));
        StorageLinkClientFactory storageClientFactory = new(vaultService, dbFactory);
        sut = new StorageService(dbFactory, vaultService, openStackS3, new Mock<IKubernetesClientFactory>().Object, storageClientFactory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ──────── Helpers ────────

    private async Task<(Tenant tenant, Data.Environment env, StorageLink link)> CreateCleuraLinkWithCredentialsAsync()
    {
        // Create tenant, environment, OpenStack connection, and a Cleura S3 storage link
        // with access/secret keys stored in the vault.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Production"
        };

        db.Set<Data.Environment>().Add(env);

        OpenStackConnection osConn = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Cleura Kna1",
            AuthUrl = "https://identity.c2.citycloud.com:5000/v3",
            Region = "Kna1",
            Username = "testuser",
            ProjectId = "project-123"
        };

        db.OpenStackConnections.Add(osConn);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Test Bucket",
            Endpoint = "https://s3-kna1.citycloud.com:8080",
            BucketName = "my-test-bucket",
            Region = "Kna1",
            OpenStackConnectionId = osConn.Id
        };

        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        // Store credentials in vault.

        await vaultService.InitializeVaultAsync(tenant.Id);
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, link.Id, "ACCESS_KEY", "AKTEST123", default);
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, link.Id, "SECRET_KEY", "secrettest456", default);

        return (tenant, env, link);
    }

    // ──────── ListCleuraS3BucketsAsync ────────

    [Fact]
    public async Task ListCleuraS3BucketsAsync_LinkNotFound_Throws()
    {
        // Arrange — no storage link exists.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.ListCleuraS3BucketsAsync(tenant.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ListCleuraS3BucketsAsync_MissingCredentials_Throws()
    {
        // Arrange — link exists but no vault secrets.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dev" };
        db.Set<Data.Environment>().Add(env);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "No Creds",
            Endpoint = "https://s3-kna1.citycloud.com:8080",
            BucketName = "bucket",
            Region = "Kna1"
        };

        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        await vaultService.InitializeVaultAsync(tenant.Id);

        // Act & Assert — should throw because credentials don't exist in vault.

        Func<Task> act = () => sut.ListCleuraS3BucketsAsync(tenant.Id, link.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*credentials*");
    }

    [Fact]
    public async Task ListCleuraS3BucketsAsync_WrongProvider_Throws()
    {
        // Arrange — link exists but is AWS S3, not Cleura.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dev" };
        db.Set<Data.Environment>().Add(env);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.AwsS3,
            Name = "AWS Bucket",
            Endpoint = "https://s3.eu-west-1.amazonaws.com",
            BucketName = "bucket",
            Region = "eu-west-1"
        };

        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        // Act & Assert — wrong provider should not be found.

        Func<Task> act = () => sut.ListCleuraS3BucketsAsync(tenant.Id, link.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ──────── DeleteCleuraS3BucketAsync ────────

    [Fact]
    public async Task DeleteCleuraS3BucketAsync_LinkNotFound_Throws()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.DeleteCleuraS3BucketAsync(tenant.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ──────── RotateCleuraS3CredentialsAsync ────────

    [Fact]
    public async Task RotateCleuraS3CredentialsAsync_LinkNotFound_Throws()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.RotateCleuraS3CredentialsAsync(tenant.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task RotateCleuraS3CredentialsAsync_MissingOpenStackConnection_Throws()
    {
        // Arrange — link exists but its OpenStack connection has been deleted.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dev" };
        db.Set<Data.Environment>().Add(env);

        // Create a real OpenStack connection so the FK is valid.

        OpenStackConnection osConn = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "ToBeDeleted",
            AuthUrl = "https://identity.example.com:5000/v3",
            Region = "Kna1",
            Username = "user"
        };

        db.OpenStackConnections.Add(osConn);

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Orphaned",
            Endpoint = "https://s3-kna1.citycloud.com:8080",
            BucketName = "bucket",
            Region = "Kna1",
            OpenStackConnectionId = osConn.Id
        };

        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        // Now delete the OpenStack connection to simulate orphaned state.

        db.OpenStackConnections.Remove(osConn);
        link.OpenStackConnectionId = null;
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.RotateCleuraS3CredentialsAsync(tenant.Id, link.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OpenStack connection not found*");
    }

    // ──────── GetStorageLinkSecretValueAsync (VaultService) ────────

    [Fact]
    public async Task GetStorageLinkSecretValueAsync_ReturnsDecryptedValue()
    {
        // Arrange — store a secret and retrieve it.

        (Tenant tenant, _, StorageLink link) = await CreateCleuraLinkWithCredentialsAsync();

        // Act

        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenant.Id, link.Id, "ACCESS_KEY");

        // Assert

        accessKey.Should().Be("AKTEST123");
    }

    [Fact]
    public async Task GetStorageLinkSecretValueAsync_NotFound_ReturnsNull()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await vaultService.InitializeVaultAsync(tenant.Id);

        // Act

        string? result = await vaultService.GetStorageLinkSecretValueAsync(tenant.Id, Guid.NewGuid(), "MISSING");

        // Assert

        result.Should().BeNull();
    }

    // ──────── GetBucketCorsAsync / SetBucketCorsAsync ────────

    [Fact]
    public async Task GetBucketCorsAsync_LinkNotFound_Throws()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.GetBucketCorsAsync(tenant.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task SetBucketCorsAsync_LinkNotFound_Throws()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        List<S3CorsRule> rules = [new() { AllowedOrigins = ["*"], AllowedMethods = ["GET"] }];

        // Act & Assert

        Func<Task> act = () => sut.SetBucketCorsAsync(tenant.Id, Guid.NewGuid(), rules);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ──────── GetBucketPolicyAsync / SetBucketPolicyAsync ────────

    [Fact]
    public async Task GetBucketPolicyAsync_LinkNotFound_Throws()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.GetBucketPolicyAsync(tenant.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task SetBucketPolicyAsync_LinkNotFound_Throws()
    {
        // Arrange

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.SetBucketPolicyAsync(tenant.Id, Guid.NewGuid(), "{}");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }
}
