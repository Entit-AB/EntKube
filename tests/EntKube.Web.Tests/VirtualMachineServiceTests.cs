using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// Services › Virtual Machines. kubectl is mocked through IKubernetesClientFactory under the real
/// ClusterClientFactory, so tenant resolution is the production path and these cover what reaches the
/// cluster: the manifest, the patch, which object a restart deletes, and when an action is refused.
/// </summary>
public class VirtualMachineServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly VirtualMachineService sut;

    public VirtualMachineServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();

        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        sut = new VirtualMachineService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.ClusterChanges.ClusterChangeGate(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance),
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            NullLogger<VirtualMachineService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<KubernetesCluster> SeedClusterAsync(string label = "acme")
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = label, Slug = $"{label}-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "production" };
        db.Set<Data.Environment>().Add(env);
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = env.Id,
            Name = $"{label}-cluster", ApiServerUrl = "https://k8s.example.com"
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        await vaultService.InitializeVaultAsync(tenant.Id);
        await testDb.SeedKubeconfigAsync(vaultService, tenant.Id, cluster.Id, TestKubeconfig.Valid);
        return cluster;
    }

    private void Lists(string resource, string json) =>
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                resource, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    private void VmIs(string ns, string name, string json) =>
        k8s.Setup(f => f.GetJsonAsync(
                $"virtualmachine.kubevirt.io/{name}", ns, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    // ──────── Fixtures ────────

    private const string RunningVm = """
        {
          "metadata": { "name": "web-01", "namespace": "vms", "creationTimestamp": "2026-11-01T10:00:00Z" },
          "spec": {
            "runStrategy": "Always",
            "template": { "spec": {
              "domain": { "cpu": { "cores": 4 }, "resources": { "requests": { "memory": "8Gi" } } },
              "volumes": [
                { "name": "root", "containerDisk": { "image": "quay.io/containerdisks/ubuntu:24.04" } },
                { "name": "cloudinit", "cloudInitNoCloud": { "userData": "#cloud-config" } }
              ]
            } }
          },
          "status": { "printableStatus": "Running" }
        }
        """;

    private const string RunningVmi = """
        {
          "metadata": { "name": "web-01", "namespace": "vms" },
          "status": {
            "nodeName": "worker-2",
            "interfaces": [{ "name": "default", "ipAddress": "10.42.1.17" }],
            "guestOSInfo": { "prettyName": "Ubuntu 24.04.1 LTS" }
          }
        }
        """;

    /// <summary>Created by older tooling: spec.running instead of runStrategy.</summary>
    private const string LegacyStoppedVm = """
        {
          "metadata": { "name": "old-db", "namespace": "legacy" },
          "spec": { "running": false, "template": { "spec": { "domain": {} } } },
          "status": { "printableStatus": "Stopped" }
        }
        """;

    private const string ImportingVm = """
        {
          "metadata": { "name": "build-01", "namespace": "vms" },
          "spec": {
            "runStrategy": "Always",
            "dataVolumeTemplates": [{
              "metadata": { "name": "build-01-root" },
              "spec": { "source": { "http": { "url": "https://cloud-images.ubuntu.com/noble.img" } } }
            }],
            "template": { "spec": { "domain": {}, "volumes": [{ "name": "root", "dataVolume": { "name": "build-01-root" } }] } }
          },
          "status": { "printableStatus": "Provisioning" }
        }
        """;

    private const string ImportingDv = """
        { "metadata": { "name": "build-01-root", "namespace": "vms" }, "status": { "phase": "ImportInProgress", "progress": "45.20%" } }
        """;

    private const string UnschedulableVm = """
        {
          "metadata": { "name": "huge", "namespace": "vms" },
          "spec": { "runStrategy": "Always", "template": { "spec": { "domain": {} } } },
          "status": {
            "printableStatus": "ErrorUnschedulable",
            "conditions": [
              { "type": "Ready", "status": "False", "message": "0/3 nodes are available: 3 Insufficient devices.kubevirt.io/kvm." }
            ]
          }
        }
        """;

    private static string Wrap(params string[] items) => $$"""{"items":[{{string.Join(",", items)}}]}""";

    // ──────── Reading ────────

    [Fact]
    public void A_running_vm_is_joined_with_its_instance()
    {
        VirtualMachineView vm = VirtualMachineService.Parse(Wrap(RunningVm), Wrap(RunningVmi), null).Single();

        vm.Status.Should().Be("Running");
        vm.Health.Should().Be(HealthStatus.Healthy);
        vm.Cores.Should().Be(4);
        vm.Memory.Should().Be("8Gi");
        vm.DiskSource.Should().Be("quay.io/containerdisks/ubuntu:24.04");
        vm.Node.Should().Be("worker-2");
        vm.IpAddress.Should().Be("10.42.1.17");
        vm.GuestOs.Should().Be("Ubuntu 24.04.1 LTS");
        vm.CanRestart.Should().BeTrue();
    }

    [Fact]
    public void The_deprecated_running_field_is_read_as_a_run_strategy()
    {
        VirtualMachineView vm = VirtualMachineService.Parse(Wrap(LegacyStoppedVm), null, null).Single();

        vm.RunStrategy.Should().Be("Halted");
        vm.Health.Should().Be(HealthStatus.Suspended, "stopped on purpose is not a fault");
        vm.CanRestart.Should().BeFalse();
    }

    [Fact]
    public void A_provisioning_vm_shows_its_disk_import_progress_and_source()
    {
        VirtualMachineView vm = VirtualMachineService.Parse(Wrap(ImportingVm), null, Wrap(ImportingDv)).Single();

        vm.Health.Should().Be(HealthStatus.Progressing);
        vm.ImportProgress.Should().Be("45.20%");
        vm.DiskSource.Should().Be("https://cloud-images.ubuntu.com/noble.img");
    }

    [Fact]
    public void A_failing_vm_carries_the_reason_from_its_conditions()
    {
        VirtualMachineView vm = VirtualMachineService.Parse(Wrap(UnschedulableVm), null, null).Single();

        vm.Health.Should().Be(HealthStatus.Degraded);
        vm.Message.Should().Contain("Insufficient devices.kubevirt.io/kvm",
            "this is the message that says the nodes have no /dev/kvm — the most likely first failure");
    }

    [Fact]
    public async Task Load_reads_all_three_lists_on_the_tenants_own_cluster()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        Lists("virtualmachines.kubevirt.io", Wrap(RunningVm, LegacyStoppedVm));
        Lists("virtualmachineinstances.kubevirt.io", Wrap(RunningVmi));

        VirtualMachineSnapshot snapshot = await sut.LoadAsync(cluster.TenantId, cluster.Id);

        snapshot.IsSuccess.Should().BeTrue();
        snapshot.Machines.Select(m => m.Key).Should().Equal("legacy/old-db", "vms/web-01");
        snapshot.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenant_cannot_read_the_vms()
    {
        KubernetesCluster cluster = await SeedClusterAsync("owner");
        KubernetesCluster stranger = await SeedClusterAsync("stranger");
        Lists("virtualmachines.kubevirt.io", Wrap(RunningVm));

        VirtualMachineSnapshot snapshot = await sut.LoadAsync(stranger.TenantId, cluster.Id);

        snapshot.IsSuccess.Should().BeFalse();
        k8s.Verify(f => f.GetJsonAllNamespacesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Without_kubevirt_the_page_says_why_instead_of_showing_an_empty_list()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                "virtualmachines.kubevirt.io", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the server doesn't have a resource type \"virtualmachines\""));

        VirtualMachineSnapshot snapshot = await sut.LoadAsync(cluster.TenantId, cluster.Id);

        snapshot.Error.Should().Contain("doesn't have a resource type");
    }

    [Fact]
    public async Task A_missing_instance_list_warns_but_a_missing_cdi_does_not()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        Lists("virtualmachines.kubevirt.io", Wrap(RunningVm));
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                "virtualmachineinstances.kubevirt.io", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("forbidden"));
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                "datavolumes.cdi.kubevirt.io", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the server doesn't have a resource type \"datavolumes\""));

        VirtualMachineSnapshot snapshot = await sut.LoadAsync(cluster.TenantId, cluster.Id);

        snapshot.Machines.Should().ContainSingle();
        snapshot.Warnings.Should().ContainSingle().Which.Should().Contain("forbidden");
    }

    [Fact]
    public async Task Availability_counts_only_installed_components_on_their_own_cluster()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        db.ClusterComponents.AddRange(
            new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = cluster.Id, Name = "kubevirt", ComponentType = "Manifest",
                ReleaseName = "kubevirt", Status = ComponentStatus.Installed, Namespace = "kubevirt"
            },
            new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = cluster.Id, Name = "kubevirt-cdi", ComponentType = "Manifest",
                ReleaseName = "kubevirt-cdi", Status = ComponentStatus.Installing, Namespace = "cdi"
            });
        await db.SaveChangesAsync();

        Dictionary<Guid, VirtualizationAvailability> availability = await sut.GetAvailabilityAsync(cluster.TenantId);

        availability[cluster.Id].Should().Be(new VirtualizationAvailability(KubeVirt: true, Cdi: false));
    }

    // ──────── The manifest ────────

    private static YamlMappingNode Load(string yaml)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static YamlNode At(YamlNode node, params object[] path)
    {
        foreach (object step in path)
            node = step is int i ? ((YamlSequenceNode)node).Children[i] : ((YamlMappingNode)node)[(string)step];
        return node;
    }

    private static string Scalar(YamlNode node, params object[] path) => ((YamlScalarNode)At(node, path)).Value!;

    [Fact]
    public void A_container_disk_vm_boots_the_image_with_no_persistent_disk()
    {
        YamlMappingNode vm = Load(VirtualMachineService.BuildManifest(new VirtualMachineRequest
        {
            Name = "web-01", Namespace = "vms", Cores = 2, Memory = "4Gi",
            Image = "quay.io/containerdisks/ubuntu:24.04", StartImmediately = false
        }));

        Scalar(vm, "kind").Should().Be("VirtualMachine");
        Scalar(vm, "metadata", "namespace").Should().Be("vms");
        Scalar(vm, "spec", "runStrategy").Should().Be("Halted");
        Scalar(vm, "spec", "template", "spec", "domain", "cpu", "cores").Should().Be("2");
        Scalar(vm, "spec", "template", "spec", "domain", "resources", "requests", "memory").Should().Be("4Gi");
        Scalar(vm, "spec", "template", "spec", "volumes", 0, "containerDisk", "image")
            .Should().Be("quay.io/containerdisks/ubuntu:24.04");
        ((YamlMappingNode)At(vm, "spec")).Children.Keys.Select(k => k.ToString()).Should().NotContain("dataVolumeTemplates");
    }

    [Fact]
    public void An_imported_disk_is_a_data_volume_template_the_vm_owns()
    {
        YamlMappingNode vm = Load(VirtualMachineService.BuildManifest(new VirtualMachineRequest
        {
            Name = "build-01", Namespace = "vms", DiskSource = VirtualMachineDiskSource.ImportUrl,
            Image = "https://cloud-images.ubuntu.com/noble.img", DiskSize = "30Gi", StorageClass = "fast"
        }));

        YamlNode template = At(vm, "spec", "dataVolumeTemplates", 0);
        Scalar(template, "metadata", "name").Should().Be("build-01-root");
        Scalar(template, "spec", "source", "http", "url").Should().Be("https://cloud-images.ubuntu.com/noble.img");
        Scalar(template, "spec", "storage", "resources", "requests", "storage").Should().Be("30Gi");
        Scalar(template, "spec", "storage", "storageClassName").Should().Be("fast");
        Scalar(vm, "spec", "template", "spec", "volumes", 0, "dataVolume", "name").Should().Be("build-01-root");
        Scalar(vm, "spec", "runStrategy").Should().Be("Always");
    }

    [Fact]
    public void Typed_values_stay_values_in_the_manifest_and_in_cloud_init()
    {
        // A comment marker and a "key: value" lookalike in the SSH key's comment. Interpolated into YAML
        // either would change the document's structure; serialized, both are just characters.
        const string key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAbc admin: true # laptop";

        YamlMappingNode vm = Load(VirtualMachineService.BuildManifest(new VirtualMachineRequest
        {
            Name = "web-01", Namespace = "vms", Image = "quay.io/containerdisks/ubuntu:24.04", SshPublicKey = key
        }));

        string userData = Scalar(vm, "spec", "template", "spec", "volumes", 1, "cloudInitNoCloud", "userData");
        userData.Should().StartWith("#cloud-config\n");

        YamlMappingNode cloudConfig = Load(userData);
        Scalar(cloudConfig, "hostname").Should().Be("web-01");
        Scalar(cloudConfig, "ssh_authorized_keys", 0).Should().Be(key);
    }

    [Theory]
    [InlineData("Web-01", "Name")]
    [InlineData("web_01", "Name")]
    [InlineData("a-name-that-is-far-too-long-for-a-vm-because-its-disk-gets-a-suffix", "Name")]
    [InlineData("ok", "Memory", "2GB")]
    [InlineData("ok", "http", "2Gi", "ftp://example.com/disk.img")]
    [InlineData("ok", "SSH key", "2Gi", "", "-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void A_request_kubernetes_would_reject_is_refused_with_a_sentence(
        string name, string mentions, string memory = "2Gi", string url = "", string ssh = "")
    {
        VirtualMachineRequest request = new()
        {
            Name = name, Namespace = "vms", Memory = memory,
            DiskSource = url.Length > 0 ? VirtualMachineDiskSource.ImportUrl : VirtualMachineDiskSource.ContainerDisk,
            Image = url.Length > 0 ? url : "quay.io/containerdisks/ubuntu:24.04",
            SshPublicKey = ssh
        };

        Action validate = () => VirtualMachineService.Validate(request);

        validate.Should().Throw<ArgumentException>().WithMessage($"*{mentions}*");
    }

    [Fact]
    public void A_good_request_passes_validation()
    {
        Action validate = () => VirtualMachineService.Validate(new VirtualMachineRequest
        {
            Name = "web-01", Namespace = "vms", Image = "quay.io/containerdisks/ubuntu:24.04",
            SshPublicKey = "sk-ssh-ed25519@openssh.com AAAAGnNrLXNzaC1lZDI1NTE5QG9wZW5zc2guY29t you@host"
        });

        validate.Should().NotThrow();
    }

    // ──────── Changes ────────

    [Fact]
    public async Task Create_makes_the_namespace_and_applies_the_vm_into_it()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        List<string> calls = [];
        k8s.Setup(f => f.EnsureNamespaceAsync("vms", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("namespace")).Returns(Task.CompletedTask);
        string? applied = null;
        k8s.Setup(f => f.ApplyManifestInNamespaceAsync(
                It.IsAny<string>(), "vms", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>((m, _, _, _, _) => { applied = m; calls.Add("apply"); })
            .ReturnsAsync("virtualmachine.kubevirt.io/web-01 created");

        await sut.CreateAsync(cluster.TenantId, cluster.Id, new VirtualMachineRequest
        {
            Name = "web-01", Namespace = "vms", Image = "quay.io/containerdisks/ubuntu:24.04"
        }, "alice");

        calls.Should().Equal("namespace", "apply");
        Scalar(Load(applied!), "metadata", "name").Should().Be("web-01");
    }

    [Fact]
    public async Task An_invalid_request_reaches_no_cluster()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        Func<Task> create = () => sut.CreateAsync(cluster.TenantId, cluster.Id,
            new VirtualMachineRequest { Name = "Bad Name", Image = "x" }, "alice");

        await create.Should().ThrowAsync<ArgumentException>();
        k8s.Verify(f => f.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "Always")]
    [InlineData(false, "Halted")]
    public async Task Start_and_stop_set_the_run_strategy(bool start, string strategy)
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        if (start) await sut.StartAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");
        else await sut.StopAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");

        k8s.Verify(f => f.PatchJsonAsync("virtualmachine.kubevirt.io", "web-01", "vms",
            VirtualMachineService.RunStrategyPatch(strategy), It.IsAny<string>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void The_run_strategy_patch_clears_the_deprecated_running_field()
    {
        // KubeVirt rejects a VM carrying both, and a merge-patch null is what removes a field.
        VirtualMachineService.RunStrategyPatch("Always")
            .Should().Be("""{"spec":{"running":null,"runStrategy":"Always"}}""");
    }

    [Fact]
    public async Task Restart_deletes_the_instance_not_the_vm()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        VmIs("vms", "web-01", RunningVm);

        await sut.RestartAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");

        k8s.Verify(f => f.DeleteManifestAsync("virtualmachineinstance.kubevirt.io", "web-01", "vms",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        k8s.Verify(f => f.DeleteManifestAsync("virtualmachine.kubevirt.io", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restart_is_refused_when_kubevirt_would_not_start_the_vm_again()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        VmIs("vms", "web-01", RunningVm.Replace("\"Always\"", "\"Manual\""));

        Func<Task> restart = () => sut.RestartAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");

        await restart.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Manual*");
        k8s.Verify(f => f.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "pause")]
    [InlineData(false, "unpause")]
    public async Task Pause_and_unpause_act_on_the_running_instance(bool pause, string subresource)
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        if (pause) await sut.PauseAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");
        else await sut.UnpauseAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");

        k8s.Verify(f => f.InvokeSubresourceAsync("subresources.kubevirt.io/v1", "virtualmachineinstances",
            "web-01", "vms", subresource, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("True", null, true, null)]
    [InlineData("False", "DisksNotLiveMigratable: PVC root-disk is not shared, live migration requires that all PVCs must be shared (using ReadWriteMany access mode)", false, "ReadWriteMany")]
    public void Kubevirts_live_migration_verdict_is_read_from_the_instance(
        string status, string? message, bool migratable, string? blockerMentions)
    {
        string vmi = RunningVmi.Replace("\"status\": {", $$"""
            "status": {
              "conditions": [{ "type": "LiveMigratable", "status": "{{status}}", "reason": "DisksNotLiveMigratable"{{(message is null ? "" : $", \"message\": \"{message}\"")}} }],
            """);

        VirtualMachineView vm = VirtualMachineService.Parse(Wrap(RunningVm), Wrap(vmi), null).Single();

        vm.LiveMigratable.Should().Be(migratable);
        if (blockerMentions is null) vm.MigrationBlocker.Should().BeNull();
        else vm.MigrationBlocker.Should().Contain(blockerMentions, "the page says WHY it cannot move, not just that it cannot");
    }

    [Fact]
    public void A_stopped_vm_has_no_migration_verdict_at_all()
    {
        VirtualMachineService.Parse(Wrap(LegacyStoppedVm), null, null).Single().LiveMigratable.Should().BeNull();
    }

    [Fact]
    public void Each_migration_request_is_its_own_object_named_within_a_dns_label()
    {
        string longName = new string('a', 58);
        YamlMappingNode first = Load(VirtualMachineService.BuildMigrationManifest("vms", longName, new DateTime(2026, 11, 10, 12, 0, 0)));
        YamlMappingNode second = Load(VirtualMachineService.BuildMigrationManifest("vms", longName, new DateTime(2026, 11, 10, 12, 0, 1)));

        Scalar(first, "kind").Should().Be("VirtualMachineInstanceMigration");
        Scalar(first, "spec", "vmiName").Should().Be(longName);
        Scalar(first, "metadata", "name").Length.Should().BeLessThanOrEqualTo(63);
        Scalar(first, "metadata", "name").Should().NotBe(Scalar(second, "metadata", "name"),
            "applying the same name again would update a finished migration in place, and nothing would move");
    }

    [Fact]
    public async Task Migrating_applies_the_request_into_the_vms_namespace()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        string? applied = null;
        k8s.Setup(f => f.ApplyManifestInNamespaceAsync(
                It.IsAny<string>(), "vms", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>((m, _, _, _, _) => applied = m)
            .ReturnsAsync("created");

        await sut.MigrateAsync(cluster.TenantId, cluster.Id, "vms", "web-01", "alice");

        Scalar(Load(applied!), "spec", "vmiName").Should().Be("web-01");
    }

    [Fact]
    public async Task The_fleet_contract_maps_each_action_to_its_own_cluster_call()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        EntKube.Web.Modules.Api.FleetApi fleet = new(testDb.Factory, sut);

        (await fleet.ActOnVirtualMachineAsync(cluster.TenantId, cluster.Id, "vms", "web-01",
            EntKube.Contracts.Fleet.VirtualMachineAction.Pause, "api-token:ci")).Succeeded.Should().BeTrue();
        (await fleet.ActOnVirtualMachineAsync(cluster.TenantId, cluster.Id, "vms", "web-01",
            EntKube.Contracts.Fleet.VirtualMachineAction.Stop, "api-token:ci")).Succeeded.Should().BeTrue();

        k8s.Verify(f => f.InvokeSubresourceAsync(It.IsAny<string>(), "virtualmachineinstances", "web-01", "vms",
            "pause", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        k8s.Verify(f => f.PatchJsonAsync("virtualmachine.kubevirt.io", "web-01", "vms",
            VirtualMachineService.RunStrategyPatch("Halted"), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_refusal_comes_back_through_the_contract_as_words_not_as_an_exception()
    {
        // A CLI exits non-zero with the message, and an MCP tool hands it to the model; neither can
        // do that with an exception that became a 500.
        KubernetesCluster cluster = await SeedClusterAsync();
        VmIs("vms", "web-01", RunningVm.Replace("\"Always\"", "\"Manual\""));
        EntKube.Web.Modules.Api.FleetApi fleet = new(testDb.Factory, sut);

        EntKube.Contracts.Fleet.VirtualMachineActionOutcome outcome = await fleet.ActOnVirtualMachineAsync(
            cluster.TenantId, cluster.Id, "vms", "web-01", EntKube.Contracts.Fleet.VirtualMachineAction.Restart, "api-token:ci");

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("Manual");
    }

    [Fact]
    public async Task Another_tenant_cannot_change_a_vm()
    {
        KubernetesCluster cluster = await SeedClusterAsync("owner");
        KubernetesCluster stranger = await SeedClusterAsync("stranger");

        Func<Task> delete = () => sut.DeleteAsync(stranger.TenantId, cluster.Id, "vms", "web-01", "mallory");

        await delete.Should().ThrowAsync<InvalidOperationException>();
        k8s.Verify(f => f.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clusters_are_the_tenants_own_narrowed_to_the_environment()
    {
        KubernetesCluster ours = await SeedClusterAsync("ours");
        await SeedClusterAsync("theirs");

        (await sut.GetClustersAsync(ours.TenantId, ours.EnvironmentId)).Select(c => c.Id).Should().Equal(ours.Id);
        (await sut.GetClustersAsync(ours.TenantId, Guid.NewGuid())).Should().BeEmpty();
    }
}
