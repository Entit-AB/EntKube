using System.Net.Http;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Test helpers for constructing services whose only purpose in a given test is to satisfy a
/// constructor dependency (their Kubernetes/HTTP interactions are not exercised).
/// </summary>
public static class TestServices
{
    /// <summary>
    /// Builds a KeycloakService backed by the test DB and vault, with mocked Kubernetes/HTTP
    /// factories. Used to satisfy ComponentLifecycleService's dependency in tests that do not
    /// drive any Keycloak behaviour.
    /// </summary>
    public static KeycloakService BuildKeycloak(
        IDbContextFactory<ApplicationDbContext> dbFactory, VaultService vaultService)
    {
        IKubernetesClientFactory k8sFactory = new Mock<IKubernetesClientFactory>().Object;
        IHttpClientFactory httpFactory = new Mock<IHttpClientFactory>().Object;
        CnpgService cnpgService = new(dbFactory, vaultService, k8sFactory);
        return new KeycloakService(dbFactory, vaultService, httpFactory, cnpgService, k8sFactory);
    }
}

/// <summary>
/// A test-only IDbContextFactory that produces ApplicationDbContext instances
/// sharing the same in-memory SQLite connection. This ensures all contexts
/// created by the factory see the same seeded test data.
/// </summary>
public sealed class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly SqliteConnection connection;

    public TestDbContextFactory(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public ApplicationDbContext CreateDbContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        return new ApplicationDbContext(options);
    }
}

/// <summary>
/// A test database that mirrors production for cluster kubeconfigs: contexts created by
/// <see cref="Factory"/> attach the <see cref="KubeconfigMaterializationInterceptor"/>, so a
/// cluster's <c>Kubeconfig</c> is transparently resolved from the vault on load (it is no longer a
/// plaintext column). Backed by a uniquely-named shared-cache in-memory SQLite database, so the
/// resolver can open its own connection — separate from the one materializing a cluster — without a
/// reader conflict, exactly as it does against a real database.
///
/// Seed a cluster's kubeconfig with <see cref="SeedKubeconfigAsync"/>. Dispose to tear it down.
/// </summary>
public sealed class InterceptingTestDb : IDisposable
{
    public string ConnectionString { get; } = $"DataSource=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection keepAlive;

    public VaultEncryptionService Encryption { get; }
    public KubeconfigResolver Resolver { get; }

    /// <summary>Creates contexts WITH the kubeconfig interceptor (use for the service under test).</summary>
    public IDbContextFactory<ApplicationDbContext> Factory { get; }

    public InterceptingTestDb(byte[] rootKey)
    {
        keepAlive = new SqliteConnection(ConnectionString);
        keepAlive.Open();

        Encryption = new VaultEncryptionService(rootKey);

        PlainFactory plain = new(ConnectionString);
        ServiceProvider sp = new ServiceCollection()
            .AddSingleton<IDbContextFactory<ApplicationDbContext>>(plain)
            .BuildServiceProvider();

        Resolver = new KubeconfigResolver(sp, Encryption);
        Factory = new InterceptingFactory(ConnectionString, new KubeconfigMaterializationInterceptor(Resolver));

        using ApplicationDbContext ctx = plain.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    /// <summary>A context for seeding/reading test data. Also intercepting, like production reads.</summary>
    public ApplicationDbContext CreateContext() => Factory.CreateDbContext();

    /// <summary>Builds a VaultService bound to this database and resolver.</summary>
    public VaultService CreateVaultService() => new(Factory, Encryption, Resolver);

    /// <summary>
    /// Stores a kubeconfig for a cluster in the vault (as production would), so subsequent loads
    /// via <see cref="Factory"/> resolve <c>cluster.Kubeconfig</c> to <paramref name="yaml"/>.
    /// </summary>
    public async Task SeedKubeconfigAsync(VaultService vault, Guid tenantId, Guid clusterId, string yaml)
    {
        (bool ok, string? error, _) = await vault.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle { ConfigYaml = yaml }, "test");
        if (!ok)
        {
            throw new InvalidOperationException($"Failed to seed kubeconfig: {error}");
        }
    }

    public void Dispose() => keepAlive.Dispose();

    private sealed class PlainFactory(string connectionString) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                // Each test class builds its own interceptor instance, so the full suite creates many
                // EF internal service providers — expected in tests, not a leak.
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options);
    }

    private sealed class InterceptingFactory(string connectionString, KubeconfigMaterializationInterceptor interceptor)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options);
    }
}

/// <summary>A kubeconfig that satisfies <see cref="KubeconfigHelper.Validate"/> for use in tests.</summary>
public static class TestKubeconfig
{
    public const string Valid = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: test
          cluster:
            server: https://k8s.example.com
        contexts:
        - name: test
          context:
            cluster: test
            user: test
        users:
        - name: test
          user:
            token: test-token
        """;
}
