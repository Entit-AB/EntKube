using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What reaches the cluster when EntKube runs helm.
///
/// <para><b>Why helm needed a seam operation at all.</b> It was the last thing keeping
/// <c>KubernetesOperationsService</c> and <c>ComponentLifecycleService</c> on the raw credential
/// (docs/decomposition.md §4.0.1) and the last thing keeping their releases outside the
/// acknowledgment gate — a service that spawns its own process bypasses the factory, and the
/// factory is where the gate is raised.</para>
///
/// <para><b>And why it is an invocation rather than a modelled Helm API.</b> The first sketch was
/// <c>HelmUpgradeAsync(release, chart, version, values…)</c>, which fit neither caller: both
/// resolve the chart reference themselves, in three different ways, and both interleave the helm
/// run with kubectl work around it. <see cref="HelmInvocation"/> carries the arguments plus just
/// enough identity for the gate to describe the change.</para>
/// </summary>
public class HelmSeamTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    /// <summary>Every helm invocation handed to the cluster, with the kubeconfig it was given.</summary>
    private readonly List<(HelmInvocation Invocation, string Kubeconfig)> helm = [];

    private Guid tenantId, clusterId, deploymentId;

    public HelmSeamTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.RunHelmAsync(It.IsAny<HelmInvocation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((HelmInvocation i, string kc, CancellationToken _) => helm.Add((i, kc)))
            .ReturnsAsync(new HelmExecutionResult { Success = true, Output = "release deployed" });
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private KubernetesOperationsService Sut()
    {
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);

        return new KubernetesOperationsService(
            testDb.Factory,
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            new KyvernoPolicyService(testDb.Factory, k8s.Object,
                new ClusterClientFactory(testDb.Factory, k8s.Object), gate,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), NullLogger<KyvernoPolicyService>.Instance),
            gate,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            NullLogger<KubernetesOperationsService>.Instance);
    }

    private async Task SeedAsync(
        DeploymentType type = DeploymentType.HelmChart,
        string? chartVersion = "1.2.3",
        string? values = null,
        string? repoUrl = null,
        bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        deploymentId = Guid.NewGuid();
        Guid envId = Guid.NewGuid(), customerId = Guid.NewGuid(), appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "Billing API", Type = type,
            EnvironmentId = envId, ClusterId = clusterId, Namespace = "billing-ns",
            HelmChartName = "billing", HelmChartVersion = chartVersion,
            HelmValues = values, HelmRepoUrl = repoUrl, IsManaged = true,
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Install / upgrade
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task An_install_names_the_release_the_chart_and_the_namespace()
    {
        await SeedAsync();

        KubernetesOperationResult<string> result = await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeTrue(result.Error);

        HelmInvocation i = helm.Should().ContainSingle().Subject.Invocation;
        i.Verb.Should().Be("upgrade --install");
        i.ReleaseName.Should().Be("billing-api", "the release name is derived from the deployment name");
        i.Namespace.Should().Be("billing-ns");

        string argv = string.Join(' ', i.Arguments);
        argv.Should().Contain("billing-api").And.Contain("billing")
            .And.Contain("--namespace billing-ns")
            .And.Contain("--create-namespace")
            .And.Contain("--version 1.2.3")
            .And.Contain("--wait");
    }

    [Fact]
    public async Task The_credential_is_supplied_by_the_seam_and_never_by_the_caller()
    {
        await SeedAsync();

        await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        (HelmInvocation invocation, string kubeconfig) = helm.Should().ContainSingle().Subject;

        // The point of the whole exercise: the caller named a cluster, and the credential arrived
        // from inside the seam.
        kubeconfig.Should().Be(TestKubeconfig.Valid);
        string.Join(' ', invocation.Arguments).Should().NotContain("--kubeconfig",
            "the seam appends it; a caller that passes one is still a custodian");
    }

    [Fact]
    public async Task A_chart_with_no_version_pins_nothing_rather_than_passing_an_empty_flag()
    {
        await SeedAsync(chartVersion: null);

        await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        string argv = string.Join(' ', helm.Single().Invocation.Arguments);
        argv.Should().NotContain("--version");
    }

    [Fact]
    public async Task Values_cross_the_seam_as_a_file_path_never_as_content()
    {
        await SeedAsync(values: "grafana:\n  adminPassword: hunter2\n");

        await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        string argv = string.Join(' ', helm.Single().Invocation.Arguments);

        // A values file routinely holds a secret, so it is written to a 0600 file and passed by
        // path. The password must not travel through the invocation, which the gate also sees.
        argv.Should().Contain("--values");
        argv.Should().NotContain("hunter2");
        helm.Single().Invocation.Summary.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task The_summary_the_operator_sees_names_the_chart_and_version()
    {
        await SeedAsync();

        await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        // Carried on the invocation rather than left to the gate's fallback, which for a helm
        // change is the bare words "Helm manifest".
        helm.Single().Invocation.Summary.Should()
            .Contain("billing-api").And.Contain("billing@1.2.3").And.Contain("billing-ns");
    }

    [Fact]
    public async Task Without_a_stored_kubeconfig_no_helm_runs()
    {
        await SeedAsync(withKubeconfig: false);

        KubernetesOperationResult<string> result = await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
        helm.Should().BeEmpty();
    }

    [Fact]
    public async Task An_observed_only_deployment_is_refused_before_helm_is_asked()
    {
        await SeedAsync();
        AppDeployment deployment = db.AppDeployments.Single(d => d.Id == deploymentId);
        deployment.IsManaged = false;
        await db.SaveChangesAsync();

        KubernetesOperationResult<string> result = await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeFalse();
        helm.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════
    //  Uninstall
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Deleting_a_helm_deployment_uninstalls_the_release()
    {
        await SeedAsync();

        KubernetesOperationResult<string> result = await Sut().DeleteDeploymentFromClusterAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeTrue(result.Error);

        HelmInvocation i = helm.Should().ContainSingle().Subject.Invocation;
        i.Verb.Should().Be("uninstall");
        i.ReleaseName.Should().Be("billing-api");
        i.Namespace.Should().Be("billing-ns");
        i.Summary.Should().Contain("removes all release resources");

        // The namespace is on the command line as well as on the record: helm needs the flag, and
        // the gate needs the field. Asserting only the field let a mutant change the flag.
        string.Join(' ', i.Arguments).Should().Be("billing-api --namespace billing-ns");
    }

    // ════════════════════════════════════════════════════════════════
    //  The seam's own two jobs
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The three tests below exist because mutation testing caught their absence. The ones above
    /// drive the callers through a <em>mocked</em> factory, so the factory's own new code — the
    /// credential it appends and the acknowledgment it raises — never ran. Mutants that stopped
    /// doing both passed every test. Testing a seam's callers is not testing the seam.
    /// </summary>
    [Fact]
    public void The_command_line_ends_with_the_credential_the_seam_supplies()
    {
        string argv = KubernetesClientFactory.BuildHelmArguments(
            new HelmInvocation("upgrade --install", "billing-api", "billing-ns",
                ["billing-api", "repo/billing", "--namespace", "billing-ns"]),
            "/tmp/kc.yaml");

        argv.Should().Be(
            "upgrade --install billing-api repo/billing --namespace billing-ns --kubeconfig /tmp/kc.yaml");
    }

    [Fact]
    public async Task The_factory_asks_before_it_runs_helm()
    {
        await SeedAsync();

        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new(ClusterChangeDecision.Cancelled);
        using IDisposable registration = gate.RegisterSink(sink);

        KubernetesClientFactory factory = new(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance));

        // Cancelled, so if the acknowledgment is raised at all this throws — and it throws before
        // helm is reached, which is the ordering that matters.
        Func<Task> act = () => factory.RunHelmAsync(
            new HelmInvocation("uninstall", "billing-api", "billing-ns", ["billing-api"],
                Summary: "helm uninstall billing-api (removes all release resources)"),
            TestKubeconfig.Valid);

        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        sink.Asked[0].Verb.Should().Be(ChangeVerb.Helm);
        sink.Asked[0].Name.Should().Be("billing-api");
        sink.Asked[0].Namespace.Should().Be("billing-ns");
        sink.Asked[0].Describe().Should().Contain("removes all release resources");
    }

    [Fact]
    public async Task A_helm_change_with_no_summary_still_describes_itself()
    {
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new(ClusterChangeDecision.Cancelled);
        using IDisposable registration = gate.RegisterSink(sink);

        KubernetesClientFactory factory = new(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance));

        Func<Task> act = () => factory.RunHelmAsync(
            new HelmInvocation("uninstall", "orphan", "some-ns", ["orphan"]), TestKubeconfig.Valid);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The gate's own fallback for a Helm verb is the bare words "Helm manifest", which names
        // neither the release nor where it is, so the seam supplies one.
        sink.Asked[0].Describe().Should().Contain("orphan").And.Contain("some-ns");
    }

    private sealed class RecordingSink(ClusterChangeDecision decision) : IClusterChangeAckSink
    {
        public List<PlannedClusterChange> Asked { get; } = [];

        public Task<ClusterChangeDecision> RequestAsync(
            PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct)
        {
            Asked.Add(change);
            return Task.FromResult(decision);
        }
    }

    [Fact]
    public async Task Deleting_a_manifest_deployment_does_not_run_helm()
    {
        await SeedAsync(type: DeploymentType.Manual);
        db.DeploymentManifests.Add(new DeploymentManifest
        {
            Id = Guid.NewGuid(), DeploymentId = deploymentId, SortOrder = 0,
            Name = "svc.yaml", Kind = "Service", YamlContent = "kind: Service\n",
        });
        await db.SaveChangesAsync();

        await Sut().DeleteDeploymentFromClusterAsync(tenantId, deploymentId);

        // A manifest deployment is removed with `kubectl delete -f`, which the seam has no
        // operation for — so this path still spawns its own kubectl and is not helm's business.
        helm.Should().BeEmpty();
    }

    /// <summary>
    /// The apply paths took a deployment id and read the tenant off that deployment's cluster, so
    /// the check compared a row against itself. Asserted on <c>helm</c> — the list of invocations
    /// that reached a cluster — because a refusal that still ran helm would be no refusal.
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_install_upgrade_or_delete_this_deployment()
    {
        await SeedAsync();
        Guid stranger = Guid.NewGuid();

        KubernetesOperationResult<string> upgrade =
            await Sut().HelmInstallOrUpgradeAsync(stranger, deploymentId);
        KubernetesOperationResult<string> delete =
            await Sut().DeleteDeploymentFromClusterAsync(stranger, deploymentId);

        upgrade.IsSuccess.Should().BeFalse();
        upgrade.Error.Should().Contain("not this tenant's");
        delete.IsSuccess.Should().BeFalse();

        helm.Should().BeEmpty("no helm invocation may reach a cluster this tenant does not own");
    }

    // ──────── Tenancy at the row, not only at the seam ────────

    /// <summary>
    /// Every entry point in <see cref="KubernetesOperationsService"/> already took a tenant, and
    /// for the cluster-reaching ones the tenant was load-bearing <em>at the seam</em>:
    /// <c>ForAsync(tenantId, clusterId)</c> refuses a cluster that is not this tenant's, so the
    /// unscoped row reads above it were redundant rather than exploitable.
    ///
    /// <para>⚠️ <c>SetRouteManagementAsync</c> is the exception, and the reason this unit exists.
    /// It writes <c>AppRoute.IsManaged</c> before any cluster call, so the seam never gets the
    /// chance to refuse. Unmanaging another tenant's route abandons the HTTPRoute EntKube
    /// re-applies for them; managing one makes EntKube start overwriting a route it does not own.
    /// The assertion is on the row, because that is where the damage was.</para>
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_change_who_manages_a_route()
    {
        await SeedAsync();

        Guid routeId = Guid.NewGuid();
        db.Set<AppRoute>().Add(new AppRoute
        {
            Id = routeId,
            AppId = db.Apps.Single().Id,
            Hostname = "billing.example.com",
            IsManaged = true,
        });
        await db.SaveChangesAsync();

        KubernetesOperationsService sut = Sut();

        KubernetesOperationResult<string> refused =
            await sut.SetRouteManagementAsync(Guid.NewGuid(), routeId, managed: false);

        refused.IsSuccess.Should().BeFalse();

        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<AppRoute>().Single(r => r.Id == routeId).IsManaged.Should().BeTrue(
            "unmanaging a route abandons the HTTPRoute EntKube re-applies for its owner");

        // The owner's own call still lands, so the refusal is about the crossing.
        KubernetesOperationResult<string> ours =
            await sut.SetRouteManagementAsync(tenantId, routeId, managed: false);
        ours.IsSuccess.Should().BeTrue(ours.Error ?? "");

        using ApplicationDbContext owned = testDb.CreateContext();
        owned.Set<AppRoute>().Single(r => r.Id == routeId).IsManaged.Should().BeFalse();
    }

    /// <summary>
    /// The deployment resolver that the apply, restart, scale and delete paths all share. The
    /// seam already refused a foreign cluster; the row is now scoped too, so nothing of another
    /// tenant's is loaded to decide what to say about it.
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_reach_a_deployment_through_the_shared_resolver()
    {
        await SeedAsync();

        KubernetesOperationsService sut = Sut();

        KubernetesOperationResult refused =
            await sut.RestartDeploymentAsync(Guid.NewGuid(), deploymentId, "billing-api");

        refused.IsSuccess.Should().BeFalse();
        refused.Error.Should().Contain("not this tenant's",
            "the row predicate refuses before the seam does now, so the message has to stay true "
            + "for an absent row AND for somebody else's — telling the two apart would leak "
            + "whether a stranger's deployment exists");

        // ⚠️ The owner's half has to assert a DIFFERENT failure, not a success: this harness
        // mocks helm and not kubectl, so the owner's restart also fails — on the network. A bare
        // IsSuccess==false for the stranger would therefore pass against no guard at all.
        KubernetesOperationResult ours =
            await sut.RestartDeploymentAsync(tenantId, deploymentId, "billing-api");
        ours.Error.Should().NotContain("not this tenant's",
            "the owner resolves the deployment and gets to the cluster call, so whatever "
            + "fails after that is the harness and not authorization");
    }

    // ──────── A failure is a result, not an exception ────────

    /// <summary>
    /// ⚠️ Regression test for a dead page. <c>HelmInstallOrUpgradeAsync</c> was the only cluster
    /// operation in its file with a <c>try/finally</c> and no <c>catch</c> — thirty-one siblings
    /// have one, including <c>ApplyYamlDeploymentAsync</c>, which is the same button for YAML
    /// deployments. Everything inside that block can throw: writing the values file, spawning
    /// helm, and the post-deploy route refresh.
    ///
    /// <para>So a failed install did not render an error. It escaped the method, escaped the page
    /// handler — which also had <c>try/finally</c> and no <c>catch</c> — and terminated the
    /// Blazor circuit, which the browser reports as "unhandled exception on the current circuit"
    /// with nothing about the cause. The YAML button next to it showed a message, because its
    /// service caught; the Helm button killed the page.</para>
    ///
    /// <para>The signature promises <c>KubernetesOperationResult</c>. This pins that promise
    /// against the one thing on the path that is guaranteed to throw in a test: the seam itself.</para>
    /// </summary>
    [Fact]
    public async Task A_helm_run_that_throws_is_returned_as_a_failure()
    {
        await SeedAsync();

        // The cluster client throws rather than returning an unsuccessful result — what a helm
        // binary that will not spawn, or a values file that cannot be written, looks like here.
        k8s.Setup(x => x.RunHelmAsync(
                It.IsAny<HelmInvocation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("helm: executable file not found in $PATH"));

        KubernetesOperationResult<string> result =
            await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("executable file not found",
            "the operator has to be told what actually failed — this is the whole point of "
            + "returning a result rather than letting it reach the circuit");
    }

    /// <summary>
    /// ⚠️ The likeliest way the circuit actually died, and the reason this is a separate branch:
    /// <see cref="EntKube.Web.Services.ClusterChanges.ClusterChangeGate"/> implements
    /// declare-or-refuse <b>by throwing</b>. An operator who answers the acknowledgment dialog
    /// with anything other than Acknowledged gets <c>OperationCanceledException</c> — so
    /// dismissing that dialog on a Helm install used to terminate the page.
    ///
    /// <para>Cancelling is a deliberate operator action, not a failure, so it must not be
    /// reported as one. The assertion is on the wording for that reason.</para>
    /// </summary>
    [Fact]
    public async Task A_cancelled_acknowledgment_reads_as_cancelled_not_as_a_failure()
    {
        await SeedAsync();

        k8s.Setup(x => x.RunHelmAsync(
                It.IsAny<HelmInvocation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(
                "Cluster change cancelled by operator: helm upgrade --install billing-api"));

        KubernetesOperationResult<string> result =
            await Sut().HelmInstallOrUpgradeAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeFalse("nothing was applied");
        result.Error.Should().Contain("Cancelled");
        result.Error.Should().NotContain("failed",
            "the operator cancelled on purpose — calling that a failure teaches them to ignore "
            + "the message");
    }
}
