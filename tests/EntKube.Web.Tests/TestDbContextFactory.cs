using System.Net.Http;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
