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

        Tenant tenant = new() { Id = tenantId, Name = "LifecycleTenant", Slug = $"lifecycle-{tenantId:N}" };
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
        vaultService = new VaultService(dbFactory, encryption, TestServices.NoClusterAccess);
        sut = TestServices.BuildLifecycle(dbFactory, vaultService);
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, registration);

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

        await sut.RegisterComponentAsync(tenantId, clusterId, registration);

        // Act & Assert

        Func<Task> act = () => sut.RegisterComponentAsync(tenantId, clusterId, registration);
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        string newValues = "alertmanager:\n  enabled: true\ngrafana:\n  enabled: true";

        // Act

        ClusterComponent updated = await sut.UpdateConfigurationAsync(tenantId, 
            component.Id, newValues, chartVersion: "65.2.0");

        // Assert

        updated.HelmValues.Should().Be(newValues);
        updated.HelmChartVersion.Should().Be("65.2.0");
    }

    [Fact]
    public async Task UpdateConfigurationAsync_NonExistentComponent_Throws()
    {
        // Act & Assert

        Func<Task> act = () => sut.UpdateConfigurationAsync(tenantId, Guid.NewGuid(), "values: true");
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        // Act

        ClusterComponent prepared = await sut.PrepareInstallAsync(tenantId, component.Id);

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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        // Act & Assert

        Func<Task> act = () => sut.PrepareInstallAsync(tenantId, component.Id);
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        // Manually set status to Installed to simulate.
        // Re-fetch from test db since the returned entity is detached.
        ClusterComponent tracked = (await db.Set<ClusterComponent>().FindAsync(component.Id))!;
        tracked.Status = ComponentStatus.Installed;
        await db.SaveChangesAsync();

        // Act & Assert — can't install something that's already installed (use upgrade instead).

        Func<Task> act = () => sut.PrepareInstallAsync(tenantId, component.Id);
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);
        await sut.PrepareInstallAsync(tenantId, component.Id);

        // Act

        ClusterComponent result = await sut.MarkInstallResultAsync(tenantId, component.Id, success: true);

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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);
        await sut.PrepareInstallAsync(tenantId, component.Id);

        // Act

        ClusterComponent result = await sut.MarkInstallResultAsync(tenantId, 
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        // Re-fetch from test db since the returned entity is detached.
        ClusterComponent tracked = (await db.Set<ClusterComponent>().FindAsync(component.Id))!;
        tracked.Status = ComponentStatus.Installed;
        tracked.InstalledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Act

        ClusterComponent prepared = await sut.PrepareUninstallAsync(tenantId, component.Id);

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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        // Act & Assert

        Func<Task> act = () => sut.PrepareUninstallAsync(tenantId, component.Id);
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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);
        await sut.UpdateConfigurationAsync(tenantId, component.Id, "grafana:\n  enabled: true");

        // Act

        HelmCommand command = await sut.GetInstallCommandAsync(tenantId, component.Id);

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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, reg);

        // Re-fetch from test db since the returned entity is detached.
        ClusterComponent tracked = (await db.Set<ClusterComponent>().FindAsync(component.Id))!;
        tracked.Status = ComponentStatus.Installed;
        await db.SaveChangesAsync();

        // Act

        HelmCommand command = await sut.GetUninstallCommandAsync(tenantId, component.Id);

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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, registration);

        // Store the admin password in the vault under the secret name
        // matching the catalog's SecretName for grafana-password.

        await vaultService.InitializeVaultAsync(tenantId);
        await vaultService.SetComponentSecretAsync(tenantId, component.Id, "GRAFANA_ADMIN_PASSWORD", "SuperSecret123!");

        await sut.PrepareInstallAsync(tenantId, component.Id);

        // Act

        HelmCommand command = await sut.GetInstallCommandAsync(tenantId, component.Id);

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

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, registration);
        await sut.PrepareInstallAsync(tenantId, component.Id);

        // Act

        HelmCommand command = await sut.GetInstallCommandAsync(tenantId, component.Id);

        // Assert — no secret injection. The operator's own values survive untouched; the document
        // is no longer byte-identical because catalog defaults this component never had are filled
        // in on the way to Helm (see ComponentLifecycleService.FillMissingCatalogDefaults), but
        // nothing the operator set is rewritten and no credential appears.

        command.ValuesYaml.Should().Contain("grafana:\n  enabled: true");
        command.ValuesYaml.Should().NotContain("adminPassword");
        command.HasValues.Should().BeTrue();
    }

    // ──────── ManifestUrl components track the catalog's URL ────────

    [Fact]
    public async Task GetInstallCommandAsync_ManifestUrl_AppliesTheCatalogUrlNotTheRegisteredOne()
    {
        // For a ManifestUrl component the URL *is* the version — these are release assets pinned
        // to a tag. Applying the stored one means the component reinstalls whatever version it was
        // registered at, for ever, while reporting success: "upgrade the CRDs" quietly reapplies
        // the old CRDs. There is no version field to bump instead; Helm components have
        // HelmChartVersion, this path has nothing.
        ComponentRegistration registration = new()
        {
            Name = "gateway-api-crds",
            ComponentType = "ManifestUrl",
            Namespace = "gateway-system",
            // The URL this component would have been registered with a few releases ago.
            HelmRepoUrl = "https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.2.1/experimental-install.yaml",
            HelmChartName = "",
            ReleaseName = "gateway-api-crds"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, registration);

        HelmCommand command = await sut.GetInstallCommandAsync(tenantId, component.Id);

        string catalogUrl = ComponentCatalog.Entries.Single(e => e.Key == "gateway-api-crds").HelmRepoUrl;

        command.ManifestUrl.Should().Be(catalogUrl);
        command.ManifestUrl.Should().NotContain("v1.2.1");
    }

    [Fact]
    public async Task GetInstallCommandAsync_ManifestUrl_CarriesTheCatalogsPatchesForThatUrl()
    {
        // Without the patch, virt-operator is applied requiring a control-plane node, and on a managed
        // cluster it never schedules.
        ComponentRegistration registration = new()
        {
            Name = "kubevirt-operator",
            ComponentType = "ManifestUrl",
            Namespace = "kubevirt",
            HelmRepoUrl = ComponentCatalog.GetByKey("kubevirt-operator")!.HelmRepoUrl,
            HelmChartName = "",
            ReleaseName = "kubevirt-operator"
        };

        ClusterComponent component = await sut.RegisterComponentAsync(tenantId, clusterId, registration);

        HelmCommand command = await sut.GetInstallCommandAsync(tenantId, component.Id);

        command.ManifestPatches.Should().ContainSingle()
            .Which.Name.Should().Be("virt-operator");
    }

    // ──────── UpdateConfigurationAsync — Helm identity fields ────────

    private async Task<ClusterComponent> RegisterAsync(string name = "prom", string ns = "monitoring") =>
        await sut.RegisterComponentAsync(tenantId, clusterId, new ComponentRegistration
        {
            Name = name,
            ComponentType = "HelmChart",
            Namespace = ns,
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            HelmChartVersion = "65.1.0",
            ReleaseName = name,
        });

    [Fact]
    public async Task UpdateConfigurationAsync_PersistsChartNameNamespaceAndReleaseName()
    {
        // These were editable in the UI but had no parameter here, so the edits were dropped
        // on the floor and reappeared as the old values on the next load.
        ClusterComponent component = await RegisterAsync();

        await sut.UpdateConfigurationAsync(tenantId, 
            component.Id, helmValues: null,
            componentNamespace: "observability", releaseName: "prom-stack", chartName: "prometheus");

        ClusterComponent reloaded = await db.ClusterComponents.AsNoTracking()
            .FirstAsync(c => c.Id == component.Id);
        reloaded.Namespace.Should().Be("observability");
        reloaded.ReleaseName.Should().Be("prom-stack");
        reloaded.HelmChartName.Should().Be("prometheus");
    }

    [Fact]
    public async Task UpdateConfigurationAsync_RefusesToMoveAnInstalledRelease()
    {
        // Helm keys a release on (namespace, name). Repointing them would strand the running
        // release rather than move it, so the change is refused while the component is installed.
        ClusterComponent component = await RegisterAsync();
        await sut.PrepareInstallAsync(tenantId, component.Id);
        await sut.MarkInstallResultAsync(tenantId, component.Id, true);

        Func<Task> moveNamespace = () => sut.UpdateConfigurationAsync(tenantId, 
            component.Id, helmValues: null, componentNamespace: "elsewhere");
        Func<Task> renameRelease = () => sut.UpdateConfigurationAsync(tenantId, 
            component.Id, helmValues: null, releaseName: "renamed");

        await moveNamespace.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Uninstall it first*");
        await renameRelease.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Uninstall it first*");

        ClusterComponent reloaded = await db.ClusterComponents.AsNoTracking()
            .FirstAsync(c => c.Id == component.Id);
        reloaded.Namespace.Should().Be("monitoring");
        reloaded.ReleaseName.Should().Be("prom");
    }

    [Fact]
    public async Task UpdateConfigurationAsync_AllowsEditingAnInstalledComponentThatKeepsItsIdentity()
    {
        // The config form posts every field on every save, so resending the unchanged namespace
        // and release name must not trip the guard — only an actual move does.
        ClusterComponent component = await RegisterAsync();
        await sut.PrepareInstallAsync(tenantId, component.Id);
        await sut.MarkInstallResultAsync(tenantId, component.Id, true);

        await sut.UpdateConfigurationAsync(tenantId, 
            component.Id, helmValues: "grafana:\n  enabled: false\n",
            chartVersion: "66.0.0",
            componentNamespace: "monitoring", releaseName: "prom", chartName: "kube-prometheus-stack");

        ClusterComponent reloaded = await db.ClusterComponents.AsNoTracking()
            .FirstAsync(c => c.Id == component.Id);
        reloaded.HelmChartVersion.Should().Be("66.0.0");
        reloaded.HelmValues.Should().Contain("enabled: false");
    }

    [Fact]
    public async Task UpdateConfigurationAsync_TreatsOmittedFieldsAsNoChange()
    {
        ClusterComponent component = await RegisterAsync();

        await sut.UpdateConfigurationAsync(tenantId, component.Id, helmValues: "replicas: 2\n");

        ClusterComponent reloaded = await db.ClusterComponents.AsNoTracking()
            .FirstAsync(c => c.Id == component.Id);
        reloaded.HelmValues.Should().Be("replicas: 2\n");
        reloaded.Namespace.Should().Be("monitoring");
        reloaded.ReleaseName.Should().Be("prom");
        reloaded.HelmChartName.Should().Be("kube-prometheus-stack");
    }

    // ──────── Tenancy ────────

    /// <summary>A second tenant with a cluster and one registered component.</summary>
    private (Guid Tenant, Guid Cluster, Guid Component, Guid WgEasy) SeedStranger()
    {
        Guid tenant = Guid.NewGuid(), environment = Guid.NewGuid(), cluster = Guid.NewGuid();

        db.Set<Tenant>().Add(new Tenant
        {
            Id = tenant, Name = "Stranger", Slug = $"stranger-{tenant:N}",
        });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environment, TenantId = tenant, Name = "their-prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster, TenantId = tenant, EnvironmentId = environment,
            Name = "their-cluster", ApiServerUrl = "https://their-k8s.example.com",
        });

        Guid component = Guid.NewGuid();
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = component, ClusterId = cluster, Name = "their-minio",
            ComponentType = "Helm", Namespace = "minio",
            HelmRepoUrl = "https://charts.min.io/", HelmChartName = "minio",
            HelmChartVersion = "5.0.0", ReleaseName = "their-minio",
            HelmValues = "replicas: 1", Status = ComponentStatus.Installed,
        });
        // ⚠️ ResolveWireGuardGatewayIdAsync returns null both for "not found" and for "not named
        // wg-easy", so a stranger's minio could never prove its tenant predicate. It needs a real
        // wg-easy and a gateway for the owner's call to return something.
        Guid wgEasy = Guid.NewGuid();
        db.ClusterComponents.AddRange(
            new ClusterComponent
            {
                Id = wgEasy, ClusterId = cluster, Name = "wg-easy",
                ComponentType = "Helm", Namespace = "wg-easy",
                ReleaseName = "wg-easy", Status = ComponentStatus.Installed,
            },
            new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = cluster, Name = "istio",
                ComponentType = "Helm", Namespace = "istio-system",
                ReleaseName = "istio", Status = ComponentStatus.Installed,
            });

        db.SaveChanges();

        return (tenant, cluster, component, wgEasy);
    }

    /// <summary>
    /// ⚠️ Six of these methods <em>already took</em> a <c>tenantId</c> and never put it in the
    /// predicate — <c>GetInstallCommandAsync</c>, <c>InstallSubchartsAsync</c>,
    /// <c>UninstallSubchartsAsync</c>, <c>SyncComponentSecretsAsync</c>, <c>ExecuteHelmAsync</c>
    /// and <c>ApplyExternalRoutesAsync</c> each looked the component up by id alone. A parameter
    /// that never reaches the query authorizes nothing, which is the shape
    /// <c>VacuousTenantCheckTests</c> exists to catch; it only watches cluster rows, so these
    /// went unnoticed.
    ///
    /// <para><c>ClusterComponent</c> has no <c>TenantId</c>, so all fourteen lookups in the file
    /// now run <c>c.Cluster.TenantId</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_component_cannot_be_installed_or_reconfigured_across_a_tenant()
    {
        var them = SeedStranger();

        // Registering against their cluster.
        Func<Task> register = () => sut.RegisterComponentAsync(
            tenantId, them.Cluster, new ComponentRegistration
            {
                Name = "planted", ComponentType = "Helm", Namespace = "kube-system",
                HelmRepoUrl = "https://evil.example.com/", HelmChartName = "miner",
            });
        await register.Should().ThrowAsync<InvalidOperationException>();

        db.ClusterComponents.Count(c => c.ClusterId == them.Cluster).Should().Be(3,
            "a component row is what the installer then acts on — theirs are untouched");

        // Helm values are arbitrary chart input — image, replicas, mounted secrets.
        Func<Task> reconfigure = () => sut.UpdateConfigurationAsync(
            tenantId, them.Component, helmValues: "image:\n  repository: evil/backdoor");
        Func<Task> install = () => sut.PrepareInstallAsync(tenantId, them.Component);
        Func<Task> upgrade = () => sut.PrepareUpgradeAsync(tenantId, them.Component);
        Func<Task> uninstall = () => sut.PrepareUninstallAsync(tenantId, them.Component);
        Func<Task> forgeResult = () => sut.MarkInstallResultAsync(tenantId, them.Component, true);
        Func<Task> command = () => sut.GetUninstallCommandAsync(tenantId, them.Component);
        // ⚠️ The one that already had the parameter.
        Func<Task> installCommand = () => sut.GetInstallCommandAsync(tenantId, them.Component);

        await reconfigure.Should().ThrowAsync<InvalidOperationException>();
        await install.Should().ThrowAsync<InvalidOperationException>();
        await upgrade.Should().ThrowAsync<InvalidOperationException>();
        await uninstall.Should().ThrowAsync<InvalidOperationException>();
        await forgeResult.Should().ThrowAsync<InvalidOperationException>();
        await command.Should().ThrowAsync<InvalidOperationException>();
        await installCommand.Should().ThrowAsync<InvalidOperationException>();

        // ⚠️ ClearErrorAsync throws as well. Every one of these looks its row up with
        // FirstOrDefaultAsync and a `?? throw`, so the refusal shape IS uniform here — unlike
        // ClusterBlueprintService, where two deletes return silently. Worth checking per
        // service rather than assuming: an assertion written for the wrong shape fails for the
        // right reason only by luck.
        Func<Task> clearError = () => sut.ClearErrorAsync(tenantId, them.Component);
        await clearError.Should().ThrowAsync<InvalidOperationException>();

        // The WireGuard resolver returns Guid? rather than throwing, so null is its refusal.
        (await sut.ResolveWireGuardGatewayIdAsync(tenantId, them.WgEasy)).Should().BeNull(
            "their wg-easy is installed and their istio gateway is there to be found");
        (await sut.ResolveWireGuardGatewayIdAsync(them.Tenant, them.WgEasy)).Should().NotBeNull(
            "the owner's own resolve finds it, so the null above is a refusal and not an absence");

        using ApplicationDbContext after = dbFactory.CreateDbContext();
        ClusterComponent intact = after.ClusterComponents.Single(c => c.Id == them.Component);
        intact.HelmValues.Should().Be("replicas: 1",
            "their chart input decides what runs on their cluster");
        intact.Status.Should().Be(ComponentStatus.Installed,
            "neither a prepare nor a forged result may move their status");

        // And the owner's own component still goes all the way through.
        ClusterComponent ours = await sut.RegisterComponentAsync(
            tenantId, clusterId, new ComponentRegistration
            {
                Name = "our-minio", ComponentType = "Helm", Namespace = "minio",
                HelmRepoUrl = "https://charts.min.io/", HelmChartName = "minio",
                HelmChartVersion = "5.0.0",
            });
        await sut.UpdateConfigurationAsync(tenantId, ours.Id, helmValues: "replicas: 4");
        await sut.PrepareInstallAsync(tenantId, ours.Id);
        (await sut.MarkInstallResultAsync(tenantId, ours.Id, success: true))
            .Status.Should().Be(ComponentStatus.Installed);
    }
}
