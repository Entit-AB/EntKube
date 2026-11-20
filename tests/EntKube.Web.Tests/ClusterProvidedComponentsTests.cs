using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using k8s.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// metrics-server is usually already running on managed Kubernetes, in whatever namespace the provider
/// chose and seldom as a Helm release. An install has to find it there and track it, never install a
/// second server over the same API, and never act on a server EntKube did not install.
/// </summary>
public class ClusterProvidedComponentsTests : IDisposable
{
    private const string MetricsApi = "v1beta1.metrics.k8s.io";

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory dbFactory;
    private readonly ClusterProvidedComponents service;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public ClusterProvidedComponentsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        Guid envId = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Provided", Slug = $"provided-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.SaveChanges();
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = "aks-prod",
            ApiServerUrl = "https://k8s.example.com", Kubeconfig = "apiVersion: v1\nkind: Config"
        });
        db.SaveChanges();

        service = new ClusterProvidedComponents(
            dbFactory, TestServices.NoClusterAccess, NullLogger<ClusterProvidedComponents>.Instance);
    }

    public void Dispose() => connection.Dispose();

    // ── Reading the APIService ──────────────────────────────────────────────────

    [Fact]
    public void Describe_AProviderAddOn_IsLocatedInItsOwnNamespace_AndIsNoHelmRelease()
    {
        // GKE runs it as kube-system/metrics-server-v1.x, managed by its addon manager.
        ExistingProvider p = ClusterProvidedComponents.Describe(
            ApiService("kube-system", "metrics-server-v1-31"),
            Service("kube-system", "metrics-server-v1-31", annotations: null));

        p.Where.Should().Be("kube-system/metrics-server-v1-31");
        p.ServiceExists.Should().BeTrue();
        p.HelmRelease.Should().BeNull();
        p.Available.Should().BeTrue();
    }

    [Fact]
    public void Describe_AHelmReleaseInAnyNamespace_NamesTheRelease()
    {
        ExistingProvider p = ClusterProvidedComponents.Describe(
            ApiService("monitoring", "ms-metrics-server"),
            Service("monitoring", "ms-metrics-server", HelmOwned("ms", "monitoring")));

        p.Namespace.Should().Be("monitoring");
        p.HelmRelease.Should().Be("ms");
        p.HelmReleaseNamespace.Should().Be("monitoring");
    }

    [Fact]
    public void Describe_ARegistrationWhoseServiceIsGone_IsStale()
    {
        ExistingProvider p = ClusterProvidedComponents.Describe(
            ApiService("kube-system", "metrics-server", available: false, message: "service/metrics-server not found"),
            service: null);

        p.ServiceExists.Should().BeFalse();
        p.Available.Should().BeFalse();
        p.UnavailableReason.Should().Be("service/metrics-server not found");
    }

    // ── Deciding ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_NothingServesTheApi_Installs()
    {
        ClusterProvidedComponents.Decide(CatalogComponent(), found: null)
            .Should().BeOfType<ProvidedDecision.Install>();
    }

    [Fact]
    public void Decide_AProviderAddOn_IsTracked()
    {
        ExistingProvider addOn = Provider("kube-system", "metrics-server", helmRelease: null);

        ClusterProvidedComponents.Decide(CatalogComponent(), addOn)
            .Should().BeOfType<ProvidedDecision.Track>().Which.Provider.Should().Be(addOn);
    }

    [Fact]
    public void Decide_SomeoneElsesHelmRelease_IsTracked_EvenWithTheSameReleaseNameInAnotherNamespace()
    {
        // Helm identifies a release by namespace AND name. Same name elsewhere is a different release.
        ExistingProvider theirs = Provider("monitoring", "metrics-server", helmRelease: "metrics-server");

        ClusterProvidedComponents.Decide(CatalogComponent(), theirs)
            .Should().BeOfType<ProvidedDecision.Track>();
    }

    [Fact]
    public void Decide_EntKubesOwnRelease_Installs_SoHelmUpgradeInstallCanRepairIt()
    {
        // A failed earlier attempt that left the release behind: helm upgrade --install is the fix,
        // and tracking it as "the cluster's" would hand EntKube's own release away.
        ExistingProvider ours = Provider("kube-system", "metrics-server", helmRelease: "metrics-server");

        ClusterProvidedComponents.Decide(CatalogComponent(), ours)
            .Should().BeOfType<ProvidedDecision.Install>();
    }

    [Fact]
    public void Decide_AStaleRegistration_IsRefused_WithTheCommandThatClearsIt()
    {
        ExistingProvider stale = Provider("kube-system", "metrics-server", helmRelease: null) with { ServiceExists = false };

        ClusterProvidedComponents.Decide(CatalogComponent(), stale)
            .Should().BeOfType<ProvidedDecision.Refuse>()
            .Which.Reason.Should().Contain("kube-system/metrics-server").And.Contain($"kubectl delete apiservice {MetricsApi}");
    }

    // ── The install path, end to end against the database ───────────────────────

    [Fact]
    public async Task Install_OnAClusterThatAlreadyServesTheApi_TracksItWhereItRuns_AndInstallsNothing()
    {
        Guid id = await AddCatalogComponentAsync();

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(Provider("kube-system", "metrics-server-v1-31", null)), default);

        result.Should().NotBeNull("an install that found a server must not fall through to helm");
        result!.Success.Should().BeTrue();
        result.Output.Should().Contain("kube-system/metrics-server-v1-31").And.Contain("Nothing was installed");

        ClusterComponent stored = await LoadAsync(id);
        stored.ComponentType.Should().Be(ClusterProvidedComponents.ComponentType);
        stored.Status.Should().Be(ComponentStatus.Installed);
        stored.Namespace.Should().Be("kube-system");
        stored.ReleaseName.Should().BeNull("there is no release of EntKube's to act on");
        stored.HelmChartVersion.Should().BeNull("the provider's version is not the catalog's");
    }

    [Fact]
    public async Task Install_FindsTheServerInANonDefaultNamespace()
    {
        Guid id = await AddCatalogComponentAsync();

        await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(Provider("monitoring", "ms-metrics-server", "ms")), default);

        ClusterComponent stored = await LoadAsync(id);
        stored.Namespace.Should().Be("monitoring");
        stored.ReleaseName.Should().Be("ms", "so the Helm scan sees that release as tracked and does not offer it twice");
    }

    [Fact]
    public async Task Install_OnAClusterWithoutOne_CarriesOnWithTheInstall()
    {
        Guid id = await AddCatalogComponentAsync();

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(null), default);

        result.Should().BeNull();
        (await LoadAsync(id)).ComponentType.Should().Be("HelmChart");
    }

    [Fact]
    public async Task Install_WhenTheClusterCannotBeRead_CarriesOn_RatherThanGuessingItIsThere()
    {
        Guid id = await AddCatalogComponentAsync();

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, (_, _, _) => throw new HttpRequestException("forbidden"), default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Upgrade_OfEntKubesOwnRelease_NeverAsksTheCluster()
    {
        Guid id = await AddCatalogComponentAsync(status: ComponentStatus.Installed);
        bool asked = false;

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(tenantId, id, isUpgrade: true,
            (_, _, _) => { asked = true; return Task.FromResult<ExistingProvider?>(null); }, default);

        result.Should().BeNull();
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task Install_OfATrackedComponentWhoseProviderIsGone_BecomesTheCatalogsOwnInstall()
    {
        // The provider removed its add-on; HPAs still need the API. Install the catalog's, at the
        // catalog's identity, rather than at the namespace the provider happened to use.
        Guid id = await AddCatalogComponentAsync();
        await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(Provider("custom-ns", "metrics-server", null)), default);

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(null), default);

        result.Should().BeNull("the install carries on");
        ClusterComponent stored = await LoadAsync(id);
        stored.ComponentType.Should().Be("HelmChart");
        stored.Status.Should().Be(ComponentStatus.NotInstalled);
        stored.Namespace.Should().Be("kube-system");
        stored.ReleaseName.Should().Be("metrics-server");
        stored.HelmChartVersion.Should().Be(ComponentCatalog.GetByKey("metrics-server")!.HelmChartVersion);
    }

    [Fact]
    public async Task AComponentWithoutAProvidedApi_IsNeverExamined()
    {
        Guid id = await AddCatalogComponentAsync(name: "reloader", chart: "reloader");
        bool asked = false;

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(tenantId, id, isUpgrade: false,
            (_, _, _) => { asked = true; return Task.FromResult<ExistingProvider?>(null); }, default);

        result.Should().BeNull();
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task AnotherTenantsComponent_IsNotTouched()
    {
        Guid id = await AddCatalogComponentAsync();

        HelmExecutionResult? result = await service.TrackIfProvidedAsync(
            Guid.NewGuid(), id, isUpgrade: false, Locates(Provider("kube-system", "metrics-server", null)), default);

        result.Should().BeNull();
        (await LoadAsync(id)).ComponentType.Should().Be("HelmChart");
    }

    // ── Lifecycle never runs helm against a tracked component ───────────────────

    [Fact]
    public async Task Uninstalling_ATrackedComponent_IsANoop()
    {
        Guid id = await AddCatalogComponentAsync();
        await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(Provider("kube-system", "metrics-server", null)), default);

        HelmCommand command = await Lifecycle().GetUninstallCommandAsync(tenantId, id);

        command.Operation.Should().Be("noop", "helm uninstall would remove the provider's server, or someone's release");
    }

    [Fact]
    public async Task PreparingAnInstallOfATrackedComponent_IsRefused()
    {
        Guid id = await AddCatalogComponentAsync();
        await service.TrackIfProvidedAsync(
            tenantId, id, isUpgrade: false, Locates(Provider("kube-system", "metrics-server", null)), default);

        Func<Task> upgrade = () => Lifecycle().PrepareUpgradeAsync(tenantId, id);

        await upgrade.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provided by the cluster*");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private ComponentLifecycleService Lifecycle()
    {
        VaultEncryptionService encryption = new(Convert.FromBase64String(TestServices.TestRootKeyBase64));
        return TestServices.BuildLifecycle(dbFactory, new VaultService(dbFactory, encryption, TestServices.NoClusterAccess));
    }

    private async Task<Guid> AddCatalogComponentAsync(
        string name = "metrics-server", string chart = "metrics-server", ComponentStatus status = ComponentStatus.NotInstalled)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(name)!;
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = name, ComponentType = "HelmChart",
            Namespace = entry.DefaultNamespace, ReleaseName = entry.DefaultReleaseName,
            HelmChartName = chart, HelmRepoUrl = entry.HelmRepoUrl, HelmChartVersion = entry.HelmChartVersion,
            Status = status
        };

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.ClusterComponents.Add(component);
        await db.SaveChangesAsync();
        return component.Id;
    }

    private async Task<ClusterComponent> LoadAsync(Guid id)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static ClusterComponent CatalogComponent() => new()
    {
        Name = "metrics-server", ComponentType = "HelmChart",
        Namespace = "kube-system", ReleaseName = "metrics-server", HelmChartName = "metrics-server"
    };

    private static ExistingProvider Provider(string ns, string service, string? helmRelease) =>
        new(MetricsApi, ns, service, ServiceExists: true, helmRelease, helmRelease is null ? null : ns,
            Available: true, UnavailableReason: null);

    private static Func<Guid, string, CancellationToken, Task<ExistingProvider?>> Locates(ExistingProvider? provider) =>
        (_, api, _) =>
        {
            api.Should().Be(MetricsApi);
            return Task.FromResult(provider);
        };

    private static V1APIService ApiService(string ns, string service, bool available = true, string? message = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = MetricsApi },
        Spec = new V1APIServiceSpec
        {
            Group = "metrics.k8s.io", Version = "v1beta1", GroupPriorityMinimum = 100, VersionPriority = 100,
            Service = new Apiregistrationv1ServiceReference { NamespaceProperty = ns, Name = service }
        },
        Status = new V1APIServiceStatus
        {
            Conditions =
            [
                new V1APIServiceCondition
                {
                    Type = "Available", Status = available ? "True" : "False",
                    Reason = available ? "Passed" : "ServiceNotFound", Message = message
                }
            ]
        }
    };

    private static V1Service Service(string ns, string name, IDictionary<string, string>? annotations) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Annotations = annotations }
    };

    private static Dictionary<string, string> HelmOwned(string release, string ns) => new()
    {
        ["meta.helm.sh/release-name"] = release,
        ["meta.helm.sh/release-namespace"] = ns
    };
}
