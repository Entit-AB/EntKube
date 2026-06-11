using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the ComponentCatalog — the static registry of well-known
/// infrastructure components. Verifies that catalog entries are valid,
/// the lookup methods work, and that catalog-based registration flows
/// correctly through the lifecycle service.
/// </summary>
public class ComponentCatalogTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly ComponentLifecycleService lifecycleService;
    private readonly Guid clusterId = Guid.NewGuid();

    public ComponentCatalogTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        // Seed a tenant, environment, and cluster for integration tests.

        Guid tenantId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Tenant tenant = new() { Id = tenantId, Name = "CatalogTenant", Slug = "catalog" };
        Data.Environment env = new() { Id = envId, TenantId = tenantId, Name = "production" };
        KubernetesCluster cluster = new()
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = envId,
            Name = "catalog-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = "apiVersion: v1\nkind: Config"
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        db.SaveChanges();

        byte[] testRootKey = Convert.FromBase64String("dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
        VaultEncryptionService encryption = new(testRootKey);
        VaultService vaultService = new(dbFactory, encryption);
        lifecycleService = new ComponentLifecycleService(dbFactory, vaultService);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ──────── Catalog integrity ────────

    [Fact]
    public void Catalog_HasEntries()
    {
        // The catalog should never be empty — we ship with known components.

        ComponentCatalog.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public void Catalog_AllEntriesHaveRequiredFields()
    {
        // Every catalog entry must have all the fields needed for a valid
        // Helm install. Missing fields would cause install failures.

        foreach (CatalogEntry entry in ComponentCatalog.Entries)
        {
            entry.Key.Should().NotBeNullOrWhiteSpace($"entry must have a key");
            entry.DisplayName.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a display name");
            entry.Description.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a description");
            entry.Icon.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have an icon");
            entry.Category.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a category");
            entry.HelmRepoUrl.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a Helm repo URL");
            entry.HelmChartName.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a Helm chart name");
            entry.DefaultNamespace.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a default namespace");
        }
    }

    [Fact]
    public void Catalog_KeysAreUnique()
    {
        // Duplicate keys would cause lookup ambiguity.

        List<string> keys = ComponentCatalog.Entries.Select(e => e.Key).ToList();
        keys.Should().OnlyHaveUniqueItems();
    }

    // ──────── Lookup ────────

    [Fact]
    public void GetByKey_ExistingEntry_ReturnsEntry()
    {
        CatalogEntry? entry = ComponentCatalog.GetByKey("kube-prometheus-stack");

        entry.Should().NotBeNull();
        entry!.DisplayName.Should().Be("Kube Prometheus Stack");
        entry.Category.Should().Be("Monitoring");
    }

    [Fact]
    public void GetByKey_CaseInsensitive_ReturnsEntry()
    {
        // Operators shouldn't have to remember exact casing.

        CatalogEntry? entry = ComponentCatalog.GetByKey("MINIO");

        entry.Should().NotBeNull();
        entry!.Key.Should().Be("minio");
    }

    [Fact]
    public void GetByKey_Unknown_ReturnsNull()
    {
        CatalogEntry? entry = ComponentCatalog.GetByKey("nonexistent-component");

        entry.Should().BeNull();
    }

    [Fact]
    public void GetByCategory_GroupsCorrectly()
    {
        IReadOnlyList<IGrouping<string, CatalogEntry>> groups = ComponentCatalog.GetByCategory();

        groups.Should().NotBeEmpty();
        groups.SelectMany(g => g).Should().HaveCount(ComponentCatalog.Entries.Count);
    }

    // ──────── Registration from catalog ────────

    [Fact]
    public void ToRegistration_FillsAllHelmDetails()
    {
        // When we create a registration from a catalog entry, it should
        // carry over all the Helm details so the operator doesn't need
        // to enter them manually.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);

        registration.Name.Should().Be("kube-prometheus-stack");
        registration.ComponentType.Should().Be("HelmChart");
        registration.Namespace.Should().Be("monitoring");
        registration.HelmRepoUrl.Should().Be("https://prometheus-community.github.io/helm-charts");
        registration.HelmChartName.Should().Be("kube-prometheus-stack");
        registration.ReleaseName.Should().Be("kube-prometheus-stack");
        registration.HelmValues.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RegisterFromCatalog_CreatesComponentWithDefaults()
    {
        // The full flow: pick from catalog → register → component is ready
        // with all Helm details pre-filled.

        CatalogEntry entry = ComponentCatalog.GetByKey("minio")!;
        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);

        ClusterComponent component = await lifecycleService.RegisterComponentAsync(clusterId, registration);

        component.Name.Should().Be("minio");
        component.HelmRepoUrl.Should().Be("https://charts.min.io/");
        component.HelmChartName.Should().Be("minio");
        component.Namespace.Should().Be("minio");
        component.Status.Should().Be(ComponentStatus.NotInstalled);
        component.HelmValues.Should().Contain("rootUser");
    }

    [Fact]
    public async Task RegisterFromCatalog_CanOverrideValues()
    {
        // Operators should be able to modify the default values before
        // registering — for example, changing resource limits or passwords.

        CatalogEntry entry = ComponentCatalog.GetByKey("cert-manager")!;
        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);

        // Override the values with custom config.

        registration.HelmValues = """
            crds:
              enabled: true
            resources:
              requests:
                memory: 256Mi
                cpu: 100m
            """;

        ClusterComponent component = await lifecycleService.RegisterComponentAsync(clusterId, registration);

        component.HelmValues.Should().Contain("256Mi");
        component.HelmValues.Should().NotContain("128Mi");
    }

    [Fact]
    public async Task RegisterFromCatalog_DuplicateRejected()
    {
        // Can't install the same catalog component twice on one cluster.

        CatalogEntry entry = ComponentCatalog.GetByKey("traefik")!;
        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);
        await lifecycleService.RegisterComponentAsync(clusterId, registration);

        // Second attempt should be rejected.

        Func<Task> secondAttempt = () => lifecycleService.RegisterComponentAsync(
            clusterId, ComponentCatalog.ToRegistration(entry));

        await secondAttempt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    // ──────── Dependency resolution ────────

    [Fact]
    public void CheckDependencies_NoDependencies_Satisfied()
    {
        // Components with no dependencies should always pass the check.

        CatalogEntry entry = ComponentCatalog.GetByKey("minio")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeTrue();
        result.MissingDependencies.Should().BeEmpty();
        result.MissingOneOfRequirements.Should().BeEmpty();
    }

    [Fact]
    public void CheckDependencies_DirectDependencyMissing_NotSatisfied()
    {
        // kube-prometheus-stack requires cert-manager and letsencrypt-issuer.
        // If they're not installed, the check should report them missing.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeFalse();
        result.MissingDependencies.Should().Contain("cert-manager");
        result.MissingDependencies.Should().Contain("letsencrypt-issuer");
    }

    [Fact]
    public void CheckDependencies_OneOfMissing_NotSatisfied()
    {
        // kube-prometheus-stack needs either traefik or istio.
        // If neither is present, the one-of requirement should be missing.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["cert-manager", "letsencrypt-issuer"]);

        result.IsSatisfied.Should().BeFalse();
        result.MissingOneOfRequirements.Should().HaveCount(1);
        result.MissingOneOfRequirements[0].Label.Should().Be("Ingress Controller");
    }

    [Fact]
    public void CheckDependencies_TraefikSatisfiesIngress()
    {
        // Installing traefik should satisfy the "one of ingress" requirement.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["cert-manager", "letsencrypt-issuer", "traefik"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CheckDependencies_IstioSatisfiesIngress()
    {
        // Installing istio should also satisfy the "one of ingress" requirement.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["cert-manager", "letsencrypt-issuer", "istio"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CheckDependencies_IstioRequiresBase()
    {
        // The istio gateway depends on istio-base (istiod).

        CatalogEntry entry = ComponentCatalog.GetByKey("istio")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeFalse();
        result.MissingDependencies.Should().Contain("istio-base");
    }

    [Fact]
    public void CheckDependencies_IstioWithBase_Satisfied()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("istio")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, ["istio-base"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CheckDependencies_LetsEncryptRequiresCertManager()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("letsencrypt-issuer")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeFalse();
        result.MissingDependencies.Should().Contain("cert-manager");
    }

    [Fact]
    public void CheckDependencies_CaseInsensitive()
    {
        // Installed component names should match case-insensitively.

        CatalogEntry entry = ComponentCatalog.GetByKey("letsencrypt-issuer")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, ["Cert-Manager"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void Catalog_IstioBaseExists()
    {
        // Ensure the istio-base entry exists since istio depends on it.

        CatalogEntry? entry = ComponentCatalog.GetByKey("istio-base");
        entry.Should().NotBeNull();
        entry!.HelmChartName.Should().Be("istiod");
    }

    [Fact]
    public void Catalog_LetsEncryptIssuerExists()
    {
        CatalogEntry? entry = ComponentCatalog.GetByKey("letsencrypt-issuer");
        entry.Should().NotBeNull();
        entry!.Dependencies.Should().Contain("cert-manager");
    }
}
