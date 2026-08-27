using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the route refresh that runs after a successful Helm install/upgrade.
///
/// The story these tests protect: when a chart upgrade replaces every workload and Service in a
/// namespace at once, the gateway has been seen to come out the other side with no virtual host
/// for the app's hostname — TLS still terminates, but Envoy answers a bare 404 for the whole
/// domain until an operator re-applies the route by hand. EntKube now re-applies the routes it
/// owns itself, right after the release lands.
///
/// The real apply talks to a cluster, so here we substitute it: a test double records which
/// deployment routes were asked for, which is exactly what the interesting behaviour is about
/// (how many applies, and for which routes).
/// </summary>
public class PostDeployRouteRefreshTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly CapturingLogger<KubernetesOperationsService> logger = new();

    public PostDeployRouteRefreshTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ── Helpers ──

    /// <summary>
    /// A stand-in for the real service that never touches a cluster. It records every route apply
    /// it is asked to perform, and can be told to fail or to throw so we can watch what the
    /// refresh does when the cluster is unhappy. The refresh step itself is inherited unchanged —
    /// that is the code under test.
    /// </summary>
    private sealed class RecordingOpsService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<KubernetesOperationsService> logger,
        ClusterChangeGate gate,
        Func<Guid, KubernetesOperationResult<string>> applyBehaviour)
        : KubernetesOperationsService(
            dbFactory,
            new AuditService(dbFactory),
            new KyvernoPolicyService(dbFactory, new Mock<IKubernetesClientFactory>().Object, gate,
                NullLogger<KyvernoPolicyService>.Instance),
            gate,
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            logger)
    {
        /// <summary>Every deployment route id the refresh asked us to apply, in order.</summary>
        public List<Guid> AppliedRouteIds { get; } = [];

        public override Task<KubernetesOperationResult<string>> ApplyDeploymentRouteAsync(
            Guid deploymentRouteId, CancellationToken ct = default)
        {
            AppliedRouteIds.Add(deploymentRouteId);
            return Task.FromResult(applyBehaviour(deploymentRouteId));
        }

        /// <summary>Exposes the post-deploy step, which is protected on the real service.</summary>
        public Task<string?> RefreshForTestAsync(AppDeployment deployment, CancellationToken ct = default)
            => RefreshDeploymentRoutesAsync(deployment, ct);
    }

    /// <summary>Builds the service under test with a chosen apply behaviour. Default: every apply succeeds.</summary>
    private RecordingOpsService BuildService(Func<Guid, KubernetesOperationResult<string>>? applyBehaviour = null)
    {
        // A gate with no interactive sink registered passes straight through (background/test scope).
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);

        return new RecordingOpsService(dbFactory, logger, gate,
            applyBehaviour ?? (_ => KubernetesOperationResult<string>.Success("applied")));
    }

    /// <summary>Seeds a tenant, environment, cluster, customer, app and one deployment to hang routes off.</summary>
    private (AppDeployment deployment, App app) SeedDeployment()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "production" };
        db.Environments.Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = TestKubeconfig.Valid
        };
        db.KubernetesClusters.Add(cluster);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Contoso" };
        db.Customers.Add(customer);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "flow" };
        db.Apps.Add(app);

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Name = "flow",
            Type = DeploymentType.HelmChart,
            EnvironmentId = env.Id,
            ClusterId = cluster.Id,
            Namespace = "flow"
        };
        db.AppDeployments.Add(deployment);

        db.SaveChanges();
        return (deployment, app);
    }

    /// <summary>Attaches a route for a hostname to a deployment, at a given path prefix.</summary>
    private AppDeploymentRoute SeedRoute(
        App app, AppDeployment deployment, string hostname, string pathPrefix,
        bool isManaged = true, bool isEnabled = true, AppRoute? existingRoute = null)
    {
        AppRoute route = existingRoute ?? new AppRoute
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Hostname = hostname,
            IsManaged = isManaged
        };

        if (existingRoute is null) db.AppRoutes.Add(route);

        AppDeploymentRoute deploymentRoute = new()
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = deployment.Id,
            PathPrefix = pathPrefix,
            ServiceName = "flow-ui",
            ServicePort = 80,
            IsEnabled = isEnabled
        };
        db.AppDeploymentRoutes.Add(deploymentRoute);

        db.SaveChanges();
        return deploymentRoute;
    }

    // ════════════════════════════════════════════════════════════════
    //  Dedupe by hostname
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshDeploymentRoutes_TwoRoutesSameHostname_AppliesOnce()
    {
        // An app commonly attaches the same hostname twice — "/" for the UI and "/api" for the
        // backend. Both live in one HTTPRoute resource, and one apply rebuilds every rule on it,
        // so refreshing per route would redo identical work and churn the Gateway twice.
        (AppDeployment deployment, App app) = SeedDeployment();
        AppDeploymentRoute uiRoute = SeedRoute(app, deployment, "flow.sto2.entit.eu", "/");
        SeedRoute(app, deployment, "flow.sto2.entit.eu", "/api",
            existingRoute: db.AppRoutes.First(r => r.Id == uiRoute.AppRouteId));

        RecordingOpsService sut = BuildService();

        string? note = await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().HaveCount(1);
        note.Should().Contain("flow.sto2.entit.eu");
    }

    [Fact]
    public async Task RefreshDeploymentRoutes_DistinctHostnames_AppliesEachOnce()
    {
        // Two genuinely different hostnames are two different HTTPRoute resources, so both need
        // their own apply — the dedupe must not collapse them together.
        (AppDeployment deployment, App app) = SeedDeployment();
        SeedRoute(app, deployment, "flow.sto2.entit.eu", "/");
        SeedRoute(app, deployment, "flow-test.sto2.entit.eu", "/");

        RecordingOpsService sut = BuildService();

        await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().HaveCount(2);
    }

    // ════════════════════════════════════════════════════════════════
    //  Ownership and enablement filters
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshDeploymentRoutes_UnmanagedRoute_IsNotApplied()
    {
        // An observed-only route belongs to ArgoCD or Flux. EntKube shows it but must never
        // reconcile it — quietly taking it over on every deploy would be the exact ownership
        // violation this feature is meant to reinforce against.
        (AppDeployment deployment, App app) = SeedDeployment();
        SeedRoute(app, deployment, "argo-owned.sto2.entit.eu", "/", isManaged: false);

        RecordingOpsService sut = BuildService();

        string? note = await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().BeEmpty();
        note.Should().BeNull();
    }

    [Fact]
    public async Task RefreshDeploymentRoutes_DisabledRoute_IsNotApplied()
    {
        // A route switched off in the portal is deliberately not published. A deploy is no reason
        // to bring it back.
        (AppDeployment deployment, App app) = SeedDeployment();
        SeedRoute(app, deployment, "retired.sto2.entit.eu", "/", isEnabled: false);

        RecordingOpsService sut = BuildService();

        await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshDeploymentRoutes_OtherDeploymentsRoutes_AreNotApplied()
    {
        // Refreshing after a release is scoped to the deployment that was just released. A
        // sibling deployment's routes are not ours to touch on this run.
        (AppDeployment deployment, App app) = SeedDeployment();

        AppDeployment sibling = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Name = "flow-test",
            Type = DeploymentType.HelmChart,
            EnvironmentId = deployment.EnvironmentId,
            ClusterId = deployment.ClusterId,
            Namespace = "flow-test"
        };
        db.AppDeployments.Add(sibling);
        db.SaveChanges();

        SeedRoute(app, deployment, "flow.sto2.entit.eu", "/");
        SeedRoute(app, sibling, "flow-test.sto2.entit.eu", "/");

        RecordingOpsService sut = BuildService();

        await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().HaveCount(1);
    }

    // ════════════════════════════════════════════════════════════════
    //  Never disturb a successful deploy
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshDeploymentRoutes_NoRoutes_IsQuietAndUneventful()
    {
        // Plenty of deployments are internal-only and have no external routes at all. That is not
        // a problem worth a warning in the deploy log.
        (AppDeployment deployment, _) = SeedDeployment();

        RecordingOpsService sut = BuildService();

        string? note = await sut.RefreshForTestAsync(deployment);

        note.Should().BeNull();
        sut.AppliedRouteIds.Should().BeEmpty();
        logger.Entries.Should().NotContain(e => e.Level >= MsLogLevel.Warning);
    }

    [Fact]
    public async Task RefreshDeploymentRoutes_ApplyFails_LogsWarningAndReturnsNormally()
    {
        // The Helm release has already succeeded by the time we run. A cluster that refuses the
        // route apply is worth telling the operator about, but it must not read as a failed deploy.
        (AppDeployment deployment, App app) = SeedDeployment();
        SeedRoute(app, deployment, "flow.sto2.entit.eu", "/");

        RecordingOpsService sut = BuildService(
            _ => KubernetesOperationResult<string>.Failure("connection refused"));

        string? note = await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().HaveCount(1);
        note.Should().Contain("failed");
        logger.Entries.Should().Contain(e => e.Level == MsLogLevel.Warning);
    }

    [Fact]
    public async Task RefreshDeploymentRoutes_ApplyThrows_IsSwallowed()
    {
        // Same contract for the harsher failure — an exception out of the apply (an unreachable
        // cluster, a cancelled acknowledgment) is caught here rather than escaping into
        // HelmInstallOrUpgradeAsync, where it would flip a good deploy to Failure.
        (AppDeployment deployment, App app) = SeedDeployment();
        SeedRoute(app, deployment, "flow.sto2.entit.eu", "/");

        RecordingOpsService sut = BuildService(
            _ => throw new InvalidOperationException("cluster unreachable"));

        string? note = await sut.RefreshForTestAsync(deployment);

        note.Should().BeNull();
        logger.Entries.Should().Contain(e => e.Level == MsLogLevel.Warning);
    }

    [Fact]
    public async Task RefreshDeploymentRoutes_MixedOutcomes_StillTriesEveryHostname()
    {
        // One broken hostname should not cost the others their refresh — they are independent
        // HTTPRoute resources and each deserves its own attempt.
        (AppDeployment deployment, App app) = SeedDeployment();
        AppDeploymentRoute good = SeedRoute(app, deployment, "flow.sto2.entit.eu", "/");
        SeedRoute(app, deployment, "flow-test.sto2.entit.eu", "/");

        RecordingOpsService sut = BuildService(id => id == good.Id
            ? KubernetesOperationResult<string>.Success("applied")
            : KubernetesOperationResult<string>.Failure("connection refused"));

        string? note = await sut.RefreshForTestAsync(deployment);

        sut.AppliedRouteIds.Should().HaveCount(2);
        note.Should().Contain("flow.sto2.entit.eu").And.Contain("failed");
    }
}

/// <summary>
/// A logger that keeps what it was told, so a test can assert on what an operator would have seen
/// in the log — in particular that a quiet, uneventful path stayed quiet.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public record Entry(MsLogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(MsLogLevel logLevel) => true;

    public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
}
