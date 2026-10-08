using System.Net.Http;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using Microsoft.Extensions.Configuration;
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
        TestDbContextFactory dbFactory, VaultService vaultService)
    {
        IKubernetesClientFactory k8sFactory = new Mock<IKubernetesClientFactory>().Object;
        IHttpClientFactory httpFactory = new Mock<IHttpClientFactory>().Object;
        CnpgService cnpgService = new(dbFactory, vaultService,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(dbFactory, k8sFactory));
        return new KeycloakService(dbFactory, vaultService, httpFactory, cnpgService,
            new EntKube.Web.Modules.Api.CatalogApi(dbFactory), k8sFactory);
    }

    /// <summary>A 32-byte base64 key — enough for the vault encryption service and to derive ingest tokens.</summary>
    public const string TestRootKeyBase64 = "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=";

    /// <summary>
    /// In-memory configuration carrying the keys the services under test read. Pass
    /// <paramref name="publicIngestUrl"/> to make the telemetry collector's ingest URL derivable.
    /// </summary>
    public static IConfiguration TestConfiguration(string? publicIngestUrl = null)
    {
        Dictionary<string, string?> values = new() { ["Vault:RootKey"] = TestRootKeyBase64 };

        if (publicIngestUrl is not null)
        {
            values["Telemetry:PublicIngestUrl"] = publicIngestUrl;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// A ComponentLifecycleService over an <see cref="InterceptingTestDb"/>, for tests that need
    /// <c>cluster.Kubeconfig</c> to resolve the way production resolves it — through the vault
    /// materialisation interceptor rather than from a column that no longer exists.
    /// </summary>
    public static ComponentLifecycleService BuildLifecycle(
        InterceptingTestDb testDb, IKubernetesClientFactory k8sFactory, string? publicIngestUrl = null)
    {
        IConfiguration config = TestConfiguration(publicIngestUrl);
        IngestTokenService tokens = new(config);
        IHttpClientFactory httpFactory = new Mock<IHttpClientFactory>().Object;
        VaultService vaultService = testDb.CreateVaultService();

        CnpgService cnpg = new(testDb.Factory, vaultService,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8sFactory));

        return new ComponentLifecycleService(
            testDb.Factory, vaultService,
            new KeycloakService(testDb.Factory, vaultService, httpFactory, cnpg,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), k8sFactory),
            tokens,
            new EntKubeTelemetryService(testDb.Factory, vaultService, tokens,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), config),
            config,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8sFactory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComponentLifecycleService>.Instance);
    }

    /// <summary>
    /// Builds a ComponentLifecycleService with its telemetry-ingest dependencies satisfied from an
    /// in-memory configuration, for tests that do not drive collector behaviour.
    /// </summary>
    public static ComponentLifecycleService BuildLifecycle(
        TestDbContextFactory dbFactory, VaultService vaultService,
        string? publicIngestUrl = null)
    {
        IConfiguration config = TestConfiguration(publicIngestUrl);
        IngestTokenService tokens = new(config);
        return new ComponentLifecycleService(
            dbFactory, vaultService, BuildKeycloak(dbFactory, vaultService),
            tokens, new EntKubeTelemetryService(dbFactory, vaultService, tokens,
                new EntKube.Web.Modules.Api.CatalogApi(dbFactory), config), config,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                dbFactory, new Mock<IKubernetesClientFactory>().Object),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComponentLifecycleService>.Instance);
    }
}

/// <summary>
/// A test-only IDbContextFactory that produces ApplicationDbContext instances
/// sharing the same in-memory SQLite connection. This ensures all contexts
/// created by the factory see the same seeded test data.
/// </summary>
public sealed class TestDbContextFactory(SqliteConnection connection)
    : IDbContextFactory<ApplicationDbContext>,
      IDbContextFactory<IdentityDbContext>,
      IDbContextFactory<FleetDbContext>,
      IDbContextFactory<CatalogDbContext>,
      IDbContextFactory<DataServicesDbContext>,
      IDbContextFactory<MailDbContext>,
      IDbContextFactory<DeliveryDbContext>,
      IDbContextFactory<ConnectivityDbContext>,
      IDbContextFactory<SecretsDbContext>,
      IDbContextFactory<TelemetryDbContext>,
      IDbContextFactory<CostDbContext>,
      IDbContextFactory<SupportDbContext>,
      IDbContextFactory<AdvisorDbContext>
{
    public ApplicationDbContext CreateDbContext() => Create<ApplicationDbContext>();

    /// <summary>
    /// One factory object satisfies every context, so a test does not have to care which one
    /// the service it is building happens to take. As services move off ApplicationDbContext
    /// onto their module's context — see docs/decomposition.md — these call sites do not
    /// change, which is the point: the migration should not churn the tests.
    /// </summary>
    private TContext Create<TContext>() where TContext : DbContext
        => (TContext)Activator.CreateInstance(
            typeof(TContext),
            new DbContextOptionsBuilder<TContext>().UseSqlite(connection).Options)!;

    IdentityDbContext IDbContextFactory<IdentityDbContext>.CreateDbContext() => Create<IdentityDbContext>();
    FleetDbContext IDbContextFactory<FleetDbContext>.CreateDbContext() => Create<FleetDbContext>();
    CatalogDbContext IDbContextFactory<CatalogDbContext>.CreateDbContext() => Create<CatalogDbContext>();
    DataServicesDbContext IDbContextFactory<DataServicesDbContext>.CreateDbContext() => Create<DataServicesDbContext>();
    MailDbContext IDbContextFactory<MailDbContext>.CreateDbContext() => Create<MailDbContext>();
    DeliveryDbContext IDbContextFactory<DeliveryDbContext>.CreateDbContext() => Create<DeliveryDbContext>();
    ConnectivityDbContext IDbContextFactory<ConnectivityDbContext>.CreateDbContext() => Create<ConnectivityDbContext>();
    SecretsDbContext IDbContextFactory<SecretsDbContext>.CreateDbContext() => Create<SecretsDbContext>();
    TelemetryDbContext IDbContextFactory<TelemetryDbContext>.CreateDbContext() => Create<TelemetryDbContext>();
    CostDbContext IDbContextFactory<CostDbContext>.CreateDbContext() => Create<CostDbContext>();
    SupportDbContext IDbContextFactory<SupportDbContext>.CreateDbContext() => Create<SupportDbContext>();
    AdvisorDbContext IDbContextFactory<AdvisorDbContext>.CreateDbContext() => Create<AdvisorDbContext>();
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
    public InterceptingFactory Factory { get; }

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

    /// <summary>
    /// Satisfies every context, like <see cref="TestDbContextFactory"/> and for the same
    /// reason — and every one of them carries the kubeconfig interceptor, because a module
    /// context without it hands back a cluster whose Kubeconfig is silently null.
    /// </summary>
    public sealed class InterceptingFactory(string connectionString, KubeconfigMaterializationInterceptor interceptor)
        : IDbContextFactory<ApplicationDbContext>,
          IDbContextFactory<IdentityDbContext>,
          IDbContextFactory<FleetDbContext>,
          IDbContextFactory<CatalogDbContext>,
          IDbContextFactory<DataServicesDbContext>,
          IDbContextFactory<MailDbContext>,
          IDbContextFactory<DeliveryDbContext>,
          IDbContextFactory<ConnectivityDbContext>,
          IDbContextFactory<SecretsDbContext>,
          IDbContextFactory<TelemetryDbContext>,
          IDbContextFactory<CostDbContext>,
          IDbContextFactory<SupportDbContext>,
          IDbContextFactory<AdvisorDbContext>
    {
        public ApplicationDbContext CreateDbContext() => Create<ApplicationDbContext>();

        private TContext Create<TContext>() where TContext : DbContext
            => (TContext)Activator.CreateInstance(
                typeof(TContext),
                new DbContextOptionsBuilder<TContext>()
                    .UseSqlite(connectionString)
                    .AddInterceptors(interceptor)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                    .Options)!;

        IdentityDbContext IDbContextFactory<IdentityDbContext>.CreateDbContext() => Create<IdentityDbContext>();
        FleetDbContext IDbContextFactory<FleetDbContext>.CreateDbContext() => Create<FleetDbContext>();
        CatalogDbContext IDbContextFactory<CatalogDbContext>.CreateDbContext() => Create<CatalogDbContext>();
        DataServicesDbContext IDbContextFactory<DataServicesDbContext>.CreateDbContext() => Create<DataServicesDbContext>();
        MailDbContext IDbContextFactory<MailDbContext>.CreateDbContext() => Create<MailDbContext>();
        DeliveryDbContext IDbContextFactory<DeliveryDbContext>.CreateDbContext() => Create<DeliveryDbContext>();
        ConnectivityDbContext IDbContextFactory<ConnectivityDbContext>.CreateDbContext() => Create<ConnectivityDbContext>();
        SecretsDbContext IDbContextFactory<SecretsDbContext>.CreateDbContext() => Create<SecretsDbContext>();
        TelemetryDbContext IDbContextFactory<TelemetryDbContext>.CreateDbContext() => Create<TelemetryDbContext>();
        CostDbContext IDbContextFactory<CostDbContext>.CreateDbContext() => Create<CostDbContext>();
        SupportDbContext IDbContextFactory<SupportDbContext>.CreateDbContext() => Create<SupportDbContext>();
        AdvisorDbContext IDbContextFactory<AdvisorDbContext>.CreateDbContext() => Create<AdvisorDbContext>();
    }
}

/// <summary>
/// A kubeconfig usable in tests.
///
/// <para>It satisfies <see cref="KubeconfigHelper.Validate"/> and — since
/// <c>current-context</c> was added — can also be built into a real <c>Kubernetes</c> SDK client.
/// Without that line the SDK refuses with "cannot infer server host url", which is a confusing
/// way to learn that a kubeconfig can be valid enough for EntKube's own check and not valid
/// enough for the client library.</para>
/// </summary>
public static class TestKubeconfig
{
    public const string Valid = """
        apiVersion: v1
        kind: Config
        current-context: test
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
