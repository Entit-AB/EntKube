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
            new EntKube.Web.Services.Clusters.ClusterClientFactory(dbFactory, k8sFactory),
            new EntKube.Web.Modules.Api.CatalogApi(dbFactory),
            new EntKube.Web.Modules.Api.DeliveryApi(dbFactory), TestServices.NewGate());
        return new KeycloakService(dbFactory, vaultService, httpFactory, cnpgService,
            new EntKube.Web.Modules.Api.CatalogApi(dbFactory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(dbFactory, k8sFactory));
    }

    /// <summary>
    /// A real change gate, as production registers one: empty configuration, so acknowledgment is
    /// required, and no sink, so nothing silently approves. Tests that drive a mocked
    /// <see cref="IKubernetesClientFactory"/> never reach it — the gate lives inside the real
    /// factory — but a service that opens an operation needs one to open.
    /// </summary>
    public static EntKube.Web.Services.ClusterChanges.IClusterChangeGate NewGate() =>
        new EntKube.Web.Services.ClusterChanges.ClusterChangeGate(
            new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance);

    /// <summary>A 32-byte base64 key — enough for the vault encryption service and to derive ingest tokens.</summary>
    /// <summary>
    /// An <see cref="EntKube.Web.Services.Clusters.IClusterClientFactory"/> that resolves nothing.
    ///
    /// <para>For the many tests that construct a <see cref="VaultService"/> to exercise encryption
    /// and storage and never push a secret to a cluster. Returning null is what the real factory
    /// returns for a cluster it has no credential for, so these tests take the same branch they
    /// always did — and a test that unexpectedly starts syncing gets "no kubeconfig" rather than a
    /// null reference.</para>
    /// </summary>
    public static EntKube.Web.Services.Clusters.IClusterClientFactory NoClusterAccess { get; } =
        new NoClusterAccessFactory();

    private sealed class NoClusterAccessFactory : EntKube.Web.Services.Clusters.IClusterClientFactory
    {
        public Task<EntKube.Web.Services.Clusters.IClusterClient?> ForAsync(
            Guid tenantId, Guid clusterId, CancellationToken ct = default)
            => Task.FromResult<EntKube.Web.Services.Clusters.IClusterClient?>(null);
    }

    /// <summary>
    /// Registers <paramref name="factory"/> under EVERY context interface, as production's
    /// <c>AddModuleContexts</c> does — not just <c>ApplicationDbContext</c>.
    ///
    /// <para><b>Why this exists.</b> A render harness that registers only the god context compiles
    /// and then fails at RESOLUTION the moment the component under test takes its own module's
    /// context. Six harnesses did, and converting one Razor page off the god context turned four
    /// portal render tests red for a reason that had nothing to do with the page. A test harness
    /// that offers fewer registrations than production makes a correct change look broken, which
    /// is the wrong way round for a test to be wrong.</para>
    /// </summary>
    public static IServiceCollection AddEveryContext(
        this IServiceCollection services, TestDbContextFactory factory)
        => services
            .AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory)
            .AddSingleton<IDbContextFactory<IdentityDbContext>>(factory)
            .AddSingleton<IDbContextFactory<FleetDbContext>>(factory)
            .AddSingleton<IDbContextFactory<CatalogDbContext>>(factory)
            .AddSingleton<IDbContextFactory<DataServicesDbContext>>(factory)
            .AddSingleton<IDbContextFactory<MailDbContext>>(factory)
            .AddSingleton<IDbContextFactory<DeliveryDbContext>>(factory)
            .AddSingleton<IDbContextFactory<ConnectivityDbContext>>(factory)
            .AddSingleton<IDbContextFactory<SecretsDbContext>>(factory)
            .AddSingleton<IDbContextFactory<TelemetryDbContext>>(factory)
            .AddSingleton<IDbContextFactory<CostDbContext>>(factory)
            .AddSingleton<IDbContextFactory<SupportDbContext>>(factory)
            .AddSingleton<IDbContextFactory<AdvisorDbContext>>(factory);

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
    /// <param name="gate">
    /// Supplied only by the tests that count acknowledgments. Null everywhere else, which is the
    /// production-safe direction: without a gate <c>BeginOperation</c> is a no-op, so an action
    /// asks more times, never fewer.
    /// </param>
    public static ComponentLifecycleService BuildLifecycle(
        InterceptingTestDb testDb, IKubernetesClientFactory k8sFactory, string? publicIngestUrl = null,
        EntKube.Web.Services.ClusterChanges.IClusterChangeGate? gate = null)
    {
        IConfiguration config = TestConfiguration(publicIngestUrl);
        IngestTokenService tokens = new(config);
        IHttpClientFactory httpFactory = new Mock<IHttpClientFactory>().Object;
        VaultService vaultService = testDb.CreateVaultService();

        CnpgService cnpg = new(testDb.Factory, vaultService,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8sFactory),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory), TestServices.NewGate());

        return new ComponentLifecycleService(
            testDb.Factory, vaultService,
            new KeycloakService(testDb.Factory, vaultService, httpFactory, cnpg,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
                new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8sFactory)),
            tokens,
            new EntKubeTelemetryService(new EntKube.Web.Modules.Api.DataServicesApi(testDb.Factory), vaultService, tokens,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), config),
            config,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8sFactory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComponentLifecycleService>.Instance,
            gate);
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
            tokens, new EntKubeTelemetryService(new EntKube.Web.Modules.Api.DataServicesApi(dbFactory), vaultService, tokens,
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
/// <summary>
/// ⚠️ <b>ONE INTERCEPTOR INSTANCE FOR THE WHOLE SUITE, and that is the entire point of this type.</b>
///
/// <para><c>IMaterializationInterceptor</c> is an <c>ISingletonInterceptor</c>, and EF Core hashes
/// its singleton interceptors <b>by reference</b> into the key for its internal service provider
/// (<c>CoreOptionsExtension.GetServiceProviderHashCode</c> /
/// <c>ShouldUseSameServiceProvider</c>). <c>ServiceProviderCache.Instance</c> is a <b>static</b>
/// dictionary with no <c>Remove</c> and no <c>Clear</c>. So a new interceptor instance per test
/// meant a new EF internal provider per test, cached for the life of the process, each holding its
/// own <c>ModelSource</c> cache — a full 165-entity model. About 740 of them, at roughly 11 MB
/// each: the suite peaked at <b>8.3 GB</b> and the CI runner has 7.8 GB, which is why the Tests
/// job had never once finished.</para>
///
/// <para>⚠️ EF SAYS SO ITSELF, and the fixture used to silence it:
/// <c>ManyServiceProvidersCreatedWarning</c> — "commonly caused by injection of a new singleton
/// service instance into every DbContext instance" — was suppressed here with the comment "expected
/// in tests, not a leak". It fires past the twentieth provider, so it had been logging hundreds of
/// times. The suppression is now <c>Throw</c> rather than <c>Ignore</c>: if anything reintroduces a
/// per-test interceptor, the suite fails instead of quietly costing gigabytes.</para>
///
/// <para><b>The product interceptor still does the work</b>, which matters: the reason every
/// context here carries one is that a module context without it hands back a cluster whose
/// <c>Kubeconfig</c> is silently null, and a test-only replacement would stop proving that. This
/// routes by connection string — unique per <see cref="InterceptingTestDb"/> — so there is no
/// ambient state and no dependence on which thread a materialization happens on.</para>
/// </summary>
internal sealed class RoutingKubeconfigInterceptor : IMaterializationInterceptor
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KubeconfigMaterializationInterceptor> ByConnection = new(StringComparer.Ordinal);

    public static readonly RoutingKubeconfigInterceptor Instance = new();

    private RoutingKubeconfigInterceptor() { }

    public static void Register(string connectionString, KubeconfigMaterializationInterceptor real)
        => ByConnection[connectionString] = real;

    /// <summary>
    /// Called from <see cref="InterceptingTestDb.Dispose"/>. Without it the routing table would
    /// become the very retention it exists to remove — smaller per entry, but still one live
    /// resolver and service provider per test for the life of the run.
    /// </summary>
    public static void Unregister(string connectionString)
        => ByConnection.TryRemove(connectionString, out _);

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        string? connectionString = materializationData.Context.Database.GetConnectionString();

        return connectionString is not null && ByConnection.TryGetValue(connectionString, out KubeconfigMaterializationInterceptor? real)
            ? real.InitializedInstance(materializationData, entity)
            : entity;
    }
}

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

        // Every module factory, not just ApplicationDbContext's. Production registers them all
        // (ModuleContextRegistration), and a harness that offers only one makes a service moving
        // onto its module's context fail here while working in the product — which is the wrong
        // way round for a test to be wrong.
        ServiceProvider sp = new ServiceCollection()
            .AddSingleton<IDbContextFactory<ApplicationDbContext>>(plain)
            .AddSingleton<IDbContextFactory<SecretsDbContext>>(plain)
            .BuildServiceProvider();

        Resolver = new KubeconfigResolver(sp, Encryption);

        // The product interceptor, registered against this database and reached through the one
        // shared routing instance — so EF sees a single singleton interceptor for the whole suite.
        RoutingKubeconfigInterceptor.Register(
            ConnectionString, new KubeconfigMaterializationInterceptor(Resolver));

        Factory = new InterceptingFactory(ConnectionString);

        using ApplicationDbContext ctx = plain.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    /// <summary>A context for seeding/reading test data. Also intercepting, like production reads.</summary>
    public ApplicationDbContext CreateContext() => Factory.CreateDbContext();

    /// <summary>
    /// Builds a VaultService bound to this database and resolver.
    ///
    /// <para><paramref name="clusterClients"/> is for the tests that push a secret to a cluster;
    /// everything else gets <see cref="TestServices.NoClusterAccess"/>, which refuses in the same
    /// way the real factory refuses a cluster it holds no credential for.</para>
    /// </summary>
    public VaultService CreateVaultService(
        EntKube.Web.Services.Clusters.IClusterClientFactory? clusterClients = null)
        => new(Factory, Encryption, clusterClients ?? TestServices.NoClusterAccess, Resolver);

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

    public void Dispose()
    {
        RoutingKubeconfigInterceptor.Unregister(ConnectionString);
        keepAlive.Dispose();
    }

    /// <summary>
    /// A factory with NO kubeconfig interceptor — this is the one the resolver itself holds, and
    /// giving it the interceptor would make resolving a kubeconfig re-enter the resolver.
    ///
    /// <para>It satisfies <see cref="SecretsDbContext"/> as well as
    /// <see cref="ApplicationDbContext"/> because that is the context the resolver reads the vault
    /// through, and production registers every module factory.</para>
    /// </summary>
    private sealed class PlainFactory(string connectionString)
        : IDbContextFactory<ApplicationDbContext>, IDbContextFactory<SecretsDbContext>
    {
        public ApplicationDbContext CreateDbContext() => Build<ApplicationDbContext>();

        SecretsDbContext IDbContextFactory<SecretsDbContext>.CreateDbContext()
            => Build<SecretsDbContext>();

        private TContext Build<TContext>()
            where TContext : DbContext
            => (TContext)Activator.CreateInstance(
                typeof(TContext),
                new DbContextOptionsBuilder<TContext>()
                    .UseSqlite(connectionString)
                    // ⚠️ The comment here used to read "expected in tests, not a leak". It was a
                    // leak, and the biggest one in the suite — see RoutingKubeconfigInterceptor.
                    // This path never carried an interceptor, so it always collapsed into one
                    // provider; Throw keeps it that way.
                    .ConfigureWarnings(w => w.Throw(CoreEventId.ManyServiceProvidersCreatedWarning))
                    .Options)!;
    }

    /// <summary>
    /// Satisfies every context, like <see cref="TestDbContextFactory"/> and for the same
    /// reason — and every one of them carries the kubeconfig interceptor, because a module
    /// context without it hands back a cluster whose Kubeconfig is silently null.
    /// </summary>
    public sealed class InterceptingFactory(string connectionString)
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
                    .AddInterceptors(RoutingKubeconfigInterceptor.Instance)
                    // ⚠️ THROW, not Ignore. One shared interceptor means EF builds one internal
                    // provider, so this cannot fire — and if a change reintroduces a per-instance
                    // interceptor it fails here instead of costing gigabytes silently.
                    .ConfigureWarnings(w => w.Throw(CoreEventId.ManyServiceProvidersCreatedWarning))
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
