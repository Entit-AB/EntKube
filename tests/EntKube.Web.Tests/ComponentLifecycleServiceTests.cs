using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for ComponentLifecycleService — the service that manages the full
/// lifecycle of cluster components (install, configure, upgrade, uninstall).
/// Tests cover the data-layer operations and validation; actual Helm CLI
/// execution requires a live cluster + helm binary.
/// </summary>
public class ComponentLifecycleServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly ComponentLifecycleService sut;
    private readonly VaultService vaultService;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public ComponentLifecycleServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        // Seed a tenant, environment, and cluster for component lifecycle tests.

        Tenant tenant = new() { Id = tenantId, Name = "LifecycleTenant", Slug = "lifecycle" };
        Data.Environment env = new() { Id = envId, TenantId = tenantId, Name = "production" };
        KubernetesCluster cluster = new()
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = envId,
            Name = "lifecycle-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = "apiVersion: v1\nkind: Config\nclusters:\n- cluster:\n    server: https://k8s.example.com\n  name: test\ncontexts:\n- context:\n    cluster: test\n    user: test\n  name: test\ncurrent-context: test\nusers:\n- name: test\n  user:\n    token: fake-token"
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        db.SaveChanges();

        byte[] testRootKey = Convert.FromBase64String("dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
        VaultEncryptionService encryption = new(testRootKey);
        vaultService = new VaultService(dbFactory, encryption);
        sut = new ComponentLifecycleService(dbFactory, vaultService, TestServices.BuildKeycloak(dbFactory, vaultService));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ──────── RegisterComponentAsync ────────

    [Fact]
    public async Task RegisterComponentAsync_ValidInput_CreatesWithNotInstalledStatus()
    {
        // Arrange — a new Helm component registration for kube-prometheus-stack.

        ComponentRegistration registration = new()
        {
            Name = "kube-prometheus-stack",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0"
        };

        // Act

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, registration);

        // Assert — component exists in DB with NotInstalled status.

        component.Should().NotBeNull();
        component.Status.Should().Be(ComponentStatus.NotInstalled);
        component.Namespace.Should().Be("monitoring");
        component.HelmRepoUrl.Should().Be("https://prometheus-community.github.io/helm-charts");
        component.HelmChartName.Should().Be("kube-prometheus-stack");
        component.HelmChartVersion.Should().Be("65.1.0");
        component.ReleaseName.Should().Be("kube-prometheus-stack");
    }

    [Fact]
    public async Task RegisterComponentAsync_DuplicateName_Throws()
    {
        // Arrange — register a component, then try to register another with the same name.

        ComponentRegistration registration = new()
        {
            Name = "duplicate-comp",
            ComponentType = "HelmChart",
            Namespace = "default"
        };

        await sut.RegisterComponentAsync(clusterId, registration);

        // Act & Assert

        Func<Task> act = () => sut.RegisterComponentAsync(clusterId, registration);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    // ──────── UpdateConfigurationAsync ────────

    [Fact]
    public async Task UpdateConfigurationAsync_ExistingComponent_UpdatesHelmValues()
    {
        // Arrange — register a component, then update its values.

        ComponentRegistration reg = new()
        {
            Name = "config-test",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmChartName = "kube-prometheus-stack"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        string newValues = "alertmanager:\n  enabled: true\ngrafana:\n  enabled: true";

        // Act

        ClusterComponent updated = await sut.UpdateConfigurationAsync(
            component.Id, newValues, chartVersion: "65.2.0");

        // Assert

        updated.HelmValues.Should().Be(newValues);
        updated.HelmChartVersion.Should().Be("65.2.0");
    }

    [Fact]
    public async Task UpdateConfigurationAsync_NonExistentComponent_Throws()
    {
        // Act & Assert

        Func<Task> act = () => sut.UpdateConfigurationAsync(Guid.NewGuid(), "values: true");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ──────── PrepareInstallAsync — validates before install ────────

    [Fact]
    public async Task PrepareInstallAsync_ValidComponent_SetsInstallingStatus()
    {
        // Arrange — a properly configured component ready to install.

        ComponentRegistration reg = new()
        {
            Name = "ready-to-install",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        // Act

        ClusterComponent prepared = await sut.PrepareInstallAsync(component.Id);

        // Assert — status should transition to Installing.

        prepared.Status.Should().Be(ComponentStatus.Installing);
    }

    [Fact]
    public async Task PrepareInstallAsync_MissingHelmChart_ReturnsFailure()
    {
        // Arrange — component without chart info can't be installed.

        ComponentRegistration reg = new()
        {
            Name = "no-chart",
            ComponentType = "HelmChart",
            Namespace = "default"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        // Act & Assert

        Func<Task> act = () => sut.PrepareInstallAsync(component.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*chart name*");
    }

    [Fact]
    public async Task PrepareInstallAsync_AlreadyInstalled_Throws()
    {
        // Arrange — component that's already in Installed state.

        ComponentRegistration reg = new()
        {
            Name = "already-installed",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        // Manually set status to Installed to simulate.
        // Re-fetch from test db since the returned entity is detached.
        ClusterComponent tracked = (await db.Set<ClusterComponent>().FindAsync(component.Id))!;
        tracked.Status = ComponentStatus.Installed;
        await db.SaveChangesAsync();

        // Act & Assert — can't install something that's already installed (use upgrade instead).

        Func<Task> act = () => sut.PrepareInstallAsync(component.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already installed*");
    }

    // ──────── MarkInstallResultAsync ────────

    [Fact]
    public async Task MarkInstallResultAsync_Success_SetsInstalledStatus()
    {
        // Arrange

        ComponentRegistration reg = new()
        {
            Name = "mark-success",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);
        await sut.PrepareInstallAsync(component.Id);

        // Act

        ClusterComponent result = await sut.MarkInstallResultAsync(component.Id, success: true);

        // Assert

        result.Status.Should().Be(ComponentStatus.Installed);
        result.InstalledAt.Should().NotBeNull();
        result.LastError.Should().BeNull();
    }

    [Fact]
    public async Task MarkInstallResultAsync_Failure_SetsFailedStatusWithError()
    {
        // Arrange

        ComponentRegistration reg = new()
        {
            Name = "mark-failure",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);
        await sut.PrepareInstallAsync(component.Id);

        // Act

        ClusterComponent result = await sut.MarkInstallResultAsync(
            component.Id, success: false, error: "timeout waiting for resources");

        // Assert

        result.Status.Should().Be(ComponentStatus.Failed);
        result.LastError.Should().Contain("timeout");
        result.InstalledAt.Should().BeNull();
    }

    // ──────── PrepareUninstallAsync ────────

    [Fact]
    public async Task PrepareUninstallAsync_InstalledComponent_SetsUninstallingStatus()
    {
        // Arrange — an installed component ready to be removed.

        ComponentRegistration reg = new()
        {
            Name = "to-uninstall",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        // Re-fetch from test db since the returned entity is detached.
        ClusterComponent tracked = (await db.Set<ClusterComponent>().FindAsync(component.Id))!;
        tracked.Status = ComponentStatus.Installed;
        tracked.InstalledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Act

        ClusterComponent prepared = await sut.PrepareUninstallAsync(component.Id);

        // Assert

        prepared.Status.Should().Be(ComponentStatus.Uninstalling);
    }

    [Fact]
    public async Task PrepareUninstallAsync_NotInstalled_Throws()
    {
        // Arrange — can't uninstall something that's not installed.

        ComponentRegistration reg = new()
        {
            Name = "not-installed-yet",
            ComponentType = "HelmChart",
            Namespace = "default",
            HelmChartName = "some-chart"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        // Act & Assert

        Func<Task> act = () => sut.PrepareUninstallAsync(component.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not installed*");
    }

    // ──────── GetInstallCommandAsync — builds helm CLI command ────────

    [Fact]
    public async Task GetInstallCommandAsync_WithAllFields_ReturnsCorrectCommand()
    {
        // Arrange — a fully configured component.

        ComponentRegistration reg = new()
        {
            Name = "full-cmd",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0",
            ReleaseName = "prom-stack"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);
        await sut.UpdateConfigurationAsync(component.Id, "grafana:\n  enabled: true");

        // Act

        HelmCommand command = await sut.GetInstallCommandAsync(component.Id);

        // Assert — verify the command structure is correct.

        command.Operation.Should().Be("upgrade --install");
        command.ReleaseName.Should().Be("prom-stack");
        command.ChartReference.Should().Contain("kube-prometheus-stack");
        command.Namespace.Should().Be("monitoring");
        command.RepoUrl.Should().Be("https://prometheus-community.github.io/helm-charts");
        command.Version.Should().Be("65.1.0");
        command.HasValues.Should().BeTrue();
    }

    [Fact]
    public async Task GetUninstallCommandAsync_InstalledComponent_ReturnsCorrectCommand()
    {
        // Arrange

        ComponentRegistration reg = new()
        {
            Name = "uninstall-cmd",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            HelmChartName = "kube-prometheus-stack",
            ReleaseName = "prom-stack"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, reg);

        // Re-fetch from test db since the returned entity is detached.
        ClusterComponent tracked = (await db.Set<ClusterComponent>().FindAsync(component.Id))!;
        tracked.Status = ComponentStatus.Installed;
        await db.SaveChangesAsync();

        // Act

        HelmCommand command = await sut.GetUninstallCommandAsync(component.Id);

        // Assert

        command.Operation.Should().Be("uninstall");
        command.ReleaseName.Should().Be("prom-stack");
        command.Namespace.Should().Be("monitoring");
    }

    // ──────── Secret Injection ────────

    [Fact]
    public async Task GetInstallCommandAsync_WithVaultSecret_InjectsSecretIntoValues()
    {
        // Arrange — register kube-prometheus-stack and store a Grafana admin
        // password in the vault. The password should NOT be in the plain YAML
        // but SHOULD appear in the final install command values.

        ComponentRegistration registration = new()
        {
            Name = "kube-prometheus-stack",
            ComponentType = "Helm",
            Namespace = "monitoring",
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0",
            ReleaseName = "kube-prometheus-stack",
            HelmValues = "grafana:\n  enabled: true\n"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, registration);

        // Store the admin password in the vault under the secret name
        // matching the catalog's SecretName for grafana-password.

        await vaultService.InitializeVaultAsync(tenantId);
        await vaultService.SetComponentSecretAsync(tenantId, component.Id, "GRAFANA_ADMIN_PASSWORD", "SuperSecret123!");

        await sut.PrepareInstallAsync(component.Id);

        // Act

        HelmCommand command = await sut.GetInstallCommandAsync(component.Id);

        // Assert — the values YAML should contain the injected secret.

        command.ValuesYaml.Should().Contain("SuperSecret123!");
        command.ValuesYaml.Should().Contain("adminPassword");
        command.HasValues.Should().BeTrue();
    }

    [Fact]
    public async Task GetInstallCommandAsync_WithoutVaultSecret_DoesNotInjectAnything()
    {
        // Arrange — register kube-prometheus-stack but DON'T store any secret.
        // The values YAML should remain unchanged.

        ComponentRegistration registration = new()
        {
            Name = "kube-prometheus-stack",
            ComponentType = "Helm",
            Namespace = "monitoring",
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0",
            ReleaseName = "prom-no-secret",
            HelmValues = "grafana:\n  enabled: true\n"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(clusterId, registration);
        await sut.PrepareInstallAsync(component.Id);

        // Act

        HelmCommand command = await sut.GetInstallCommandAsync(component.Id);

        // Assert — no secret injection means original YAML is preserved.

        command.ValuesYaml.Should().Be("grafana:\n  enabled: true\n");
        command.HasValues.Should().BeTrue();
    }
}
