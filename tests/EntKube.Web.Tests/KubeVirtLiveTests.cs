using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Services › Virtual Machines against a real cluster running KubeVirt — real kubectl, real API server,
/// real subresources, no mocks below <see cref="VirtualMachineService"/>.
///
/// <para>These no-op unless <c>ENTKUBE_KUBEVIRT_TEST_KUBECONFIG</c> names a kubeconfig file, since they
/// need a cluster with the KubeVirt components installed (and software emulation turned on where the
/// nodes have no <c>/dev/kvm</c>). They live in the suite rather than in a script because they are the
/// only check of what every other test here mocks: that KubeVirt accepts the manifest EntKube writes, that
/// <c>{}</c> is an acceptable pause body, that the console websocket really carries bytes under
/// <c>plain.kubevirt.io</c>, and that the JSON the parser reads is the JSON KubeVirt writes.</para>
///
/// <para>Optional: <c>ENTKUBE_KUBEVIRT_TEST_NAMESPACE</c> (default <c>entkube-kubevirt-test</c>) and
/// <c>ENTKUBE_KUBEVIRT_TEST_IMAGE</c> (default KubeVirt's cirros demo disk, which is multi-arch and boots
/// in seconds under emulation). The VM is deleted at the end; the namespace is left.</para>
/// </summary>
public sealed class KubeVirtLiveTests : IDisposable
{
    private static string? KubeconfigPath => System.Environment.GetEnvironmentVariable("ENTKUBE_KUBEVIRT_TEST_KUBECONFIG");
    private static bool Enabled => !string.IsNullOrWhiteSpace(KubeconfigPath);

    private static string Namespace =>
        System.Environment.GetEnvironmentVariable("ENTKUBE_KUBEVIRT_TEST_NAMESPACE") ?? "entkube-kubevirt-test";

    private static string Image =>
        System.Environment.GetEnvironmentVariable("ENTKUBE_KUBEVIRT_TEST_IMAGE")
        ?? "quay.io/kubevirt/cirros-container-disk-demo:v1.9.1";

    /// <summary>Generous: a VM booting under software emulation on a laptop is slow.</summary>
    private static readonly TimeSpan StateTimeout = TimeSpan.FromMinutes(10);

    private readonly InterceptingTestDb testDb = new(Convert.FromBase64String(TestServices.TestRootKeyBase64));
    private readonly string vmName = $"live-{Guid.NewGuid():N}"[..20];

    public void Dispose()
    {
        unattended?.Dispose();
        testDb.Dispose();
    }

    /// <summary>
    /// Everything that does not need a guest to boot, so it runs on any cluster with KubeVirt and CDI —
    /// including arm64 nodes without /dev/kvm, where no VM can start at all (see docs/kubevirt.md).
    /// Halted VMs are enough to prove the manifests are accepted and the real JSON reads back.
    /// </summary>
    [Fact]
    public async Task The_manifests_entkube_writes_are_accepted_and_read_back()
    {
        if (!Enabled) return;

        string kubeconfig = await File.ReadAllTextAsync(KubeconfigPath!);
        (VirtualMachineService vms, KubernetesClientFactory k8s, Guid tenantId, Guid clusterId) = await BuildAsync(kubeconfig);
        string imported = vmName[..^1] + "i";
        const string url = "https://download.cirros-cloud.net/0.6.2/cirros-0.6.2-x86_64-disk.img";

        try
        {
            await vms.CreateAsync(tenantId, clusterId, new VirtualMachineRequest
            {
                Name = vmName, Namespace = Namespace, Cores = 1, Memory = "256Mi", Image = Image,
                SshPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGxpdmUtdGVzdA live: true # test", StartImmediately = false,
            }, "live-test");
            await vms.CreateAsync(tenantId, clusterId, new VirtualMachineRequest
            {
                Name = imported, Namespace = Namespace, Cores = 2, Memory = "512Mi",
                DiskSource = VirtualMachineDiskSource.ImportUrl, Image = url, DiskSize = "1Gi", StartImmediately = false,
            }, "live-test");

            VirtualMachineSnapshot snapshot = await vms.LoadAsync(tenantId, clusterId);
            snapshot.Error.Should().BeNull();

            VirtualMachineView disk = snapshot.Machines.Single(v => v.Name == vmName);
            disk.Status.Should().Be("Stopped");
            disk.RunStrategy.Should().Be("Halted");
            (disk.Cores, disk.Memory, disk.DiskSource).Should().Be((1, "256Mi", Image));
            disk.LiveMigratable.Should().BeNull("a halted VM has no instance to judge");

            VirtualMachineView import = snapshot.Machines.Single(v => v.Name == imported);
            (import.Cores, import.Memory, import.DiskSource).Should().Be((2, "512Mi", url),
                "the import URL is read back from the VM's own DataVolume template");

            // CDI accepted the DataVolume the VM owns.
            (await k8s.GetJsonAsync($"datavolume.cdi.kubevirt.io/{imported}-root", Namespace, kubeconfig))
                .Should().Contain(url);

            // The Service a published port puts in front of the VM.
            await k8s.ApplyManifestAsync(
                VmRouteService.BuildServiceYaml(Namespace, vmName, VmRouteKind.Tcp, [22, 3389]), kubeconfig);
            using JsonDocument service = JsonDocument.Parse(await k8s.GetJsonAsync(
                $"service/{VmRouteService.ServiceName(vmName, VmRouteKind.Tcp)}", Namespace, kubeconfig));
            service.RootElement.GetProperty("spec").GetProperty("selector").GetProperty(VmRouteService.VmLabel)
                .GetString().Should().Be(vmName);
            service.RootElement.GetProperty("spec").GetProperty("ports").EnumerateArray()
                .Select(port => port.GetProperty("name").GetString()).Should().Equal("tcp-22", "tcp-3389");
        }
        finally
        {
            await k8s.DeleteManifestAsync("service", VmRouteService.ServiceName(vmName, VmRouteKind.Tcp), Namespace, kubeconfig);
            await vms.DeleteAsync(tenantId, clusterId, Namespace, vmName, "live-test");
            await vms.DeleteAsync(tenantId, clusterId, Namespace, imported, "live-test");
        }
    }

    /// <summary>
    /// A running guest end to end. Needs a node that can boot one: KubeVirt advertising
    /// devices.kubevirt.io/kvm, or an x86 node with software emulation. arm64 nodes without KVM cannot run
    /// a VM in KubeVirt 1.9, so on those this returns after saying nothing — as it does with no cluster.
    /// </summary>
    [Fact]
    public async Task A_vm_lives_its_whole_life_through_the_service()
    {
        if (!Enabled) return;

        string kubeconfig = await File.ReadAllTextAsync(KubeconfigPath!);
        (VirtualMachineService vms, KubernetesClientFactory k8s, Guid tenantId, Guid clusterId) = await BuildAsync(kubeconfig);
        if (!await SomeNodeCanBootAVmAsync(k8s, kubeconfig)) return;

        try
        {
            // ── Create, and the manifest KubeVirt accepts ──
            await vms.CreateAsync(tenantId, clusterId, new VirtualMachineRequest
            {
                Name = vmName, Namespace = Namespace, Cores = 1, Memory = "256Mi",
                Image = Image, SshPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGxpdmUtdGVzdA live-test",
            }, "live-test");

            VirtualMachineView running = await WaitForAsync(vms, tenantId, clusterId, v => v.Status == "Running");
            running.Node.Should().NotBeNullOrEmpty("the parser must find nodeName in a real VMI");
            running.IpAddress.Should().NotBeNullOrEmpty("and the pod-network address KubeVirt reports");
            running.Cores.Should().Be(1);
            running.LiveMigratable.Should().NotBeNull("a running instance carries a LiveMigratable condition");

            // ── The serial console carries bytes both ways ──
            using (System.Net.WebSockets.WebSocket serial = await vms.OpenConsoleAsync(
                       tenantId, clusterId, Namespace, vmName, VirtualMachineConsoleKind.Serial, "live-test"))
            {
                await serial.SendAsync(Encoding.ASCII.GetBytes("\r"), WebSocketMessageType.Binary, true, CancellationToken.None);
                string received = await ReceiveAsync(serial, TimeSpan.FromSeconds(60));
                received.Should().NotBeEmpty("a newline on a booted guest's serial port brings back a prompt");
            }

            // ── VNC speaks RFB from its first frame ──
            using (System.Net.WebSockets.WebSocket vnc = await vms.OpenConsoleAsync(
                       tenantId, clusterId, Namespace, vmName, VirtualMachineConsoleKind.Vnc, "live-test"))
            {
                (await ReceiveAsync(vnc, TimeSpan.FromSeconds(30))).Should().StartWith("RFB ",
                    "a VNC server's first message is its protocol version, which is what noVNC waits for");
            }

            // ── Pause and unpause through the subresource verb ──
            await vms.PauseAsync(tenantId, clusterId, Namespace, vmName, "live-test");
            await WaitForAsync(vms, tenantId, clusterId, v => v.Status == "Paused");
            await vms.UnpauseAsync(tenantId, clusterId, Namespace, vmName, "live-test");
            await WaitForAsync(vms, tenantId, clusterId, v => v.Status == "Running");

            // ── Restart is a new instance, not the same one ──
            string before = await InstanceUidAsync(k8s, kubeconfig);
            await vms.RestartAsync(tenantId, clusterId, Namespace, vmName, "live-test");
            await WaitForAsync(vms, tenantId, clusterId, v => v.Status == "Running",
                then: async () => await InstanceUidAsync(k8s, kubeconfig) is string after && after != before);

            // ── Stop through runStrategy ──
            await vms.StopAsync(tenantId, clusterId, Namespace, vmName, "live-test");
            VirtualMachineView stopped = await WaitForAsync(vms, tenantId, clusterId, v => v.Status == "Stopped");
            stopped.RunStrategy.Should().Be("Halted");
            stopped.Node.Should().BeNull("a stopped VM has no instance");
        }
        finally
        {
            await vms.DeleteAsync(tenantId, clusterId, Namespace, vmName, "live-test");
        }
    }

    // ──────── Harness ────────

    /// <summary>
    /// The production objects, with only the database in memory. The gate is real and the change is
    /// declared unattended for the test's lifetime — there is no person to ask — which is exactly how a
    /// background sweep or an API token reaches the cluster.
    /// </summary>
    private async Task<(VirtualMachineService, KubernetesClientFactory, Guid, Guid)> BuildAsync(string kubeconfig)
    {
        Guid tenantId = Guid.NewGuid(), clusterId = Guid.NewGuid(), envId = Guid.NewGuid();
        using (ApplicationDbContext db = testDb.CreateContext())
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "live", Slug = $"live-{tenantId:N}" });
            db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "live" });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = "live", ApiServerUrl = "https://live",
            });
            await db.SaveChangesAsync();
        }

        VaultService vault = testDb.CreateVaultService();
        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, kubeconfig);

        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        unattended = gate.DeclareUnattended("kubevirt-live-test");

        KubernetesClientFactory k8s = new(gate, new KubernetesProxyClientPool(NullLogger<KubernetesProxyClientPool>.Instance));
        VirtualMachineService vms = new(
            testDb.Factory, new ClusterClientFactory(testDb.Factory, k8s),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), gate,
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            NullLogger<VirtualMachineService>.Instance);

        return (vms, k8s, tenantId, clusterId);
    }

    private IDisposable? unattended;

    /// <summary>
    /// Polls until the VM satisfies <paramref name="until"/> (and <paramref name="then"/>, when given).
    /// Measured with a Stopwatch: a laptop that sleeps mid-run moves the wall clock, not this.
    /// </summary>
    private async Task<VirtualMachineView> WaitForAsync(
        VirtualMachineService vms, Guid tenantId, Guid clusterId, Func<VirtualMachineView, bool> until,
        Func<Task<bool>>? then = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        VirtualMachineView? last = null;

        while (elapsed.Elapsed < StateTimeout)
        {
            VirtualMachineSnapshot snapshot = await vms.LoadAsync(tenantId, clusterId);
            snapshot.Error.Should().BeNull();

            last = snapshot.Machines.FirstOrDefault(v => v.Namespace == Namespace && v.Name == vmName);
            if (last is not null && until(last) && (then is null || await then()))
                return last;

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        throw new TimeoutException(
            $"VM {Namespace}/{vmName} did not reach the expected state in {StateTimeout}; last seen "
            + $"{last?.Status ?? "absent"}{(last?.Message is string m ? $" — {m}" : "")}.");
    }

    private static async Task<bool> SomeNodeCanBootAVmAsync(KubernetesClientFactory k8s, string kubeconfig)
    {
        using JsonDocument nodes = JsonDocument.Parse(await k8s.GetJsonAsync("nodes", "", kubeconfig));

        return nodes.RootElement.GetProperty("items").EnumerateArray().Any(n =>
            (n.GetProperty("status").TryGetProperty("allocatable", out JsonElement alloc)
                && alloc.TryGetProperty("devices.kubevirt.io/kvm", out JsonElement kvm)
                && kvm.GetString() is string count && count != "0")
            || n.GetProperty("status").GetProperty("nodeInfo").GetProperty("architecture").GetString() == "amd64");
    }

    private async Task<string?> InstanceUidAsync(KubernetesClientFactory k8s, string kubeconfig)
    {
        try
        {
            string json = await k8s.GetJsonAsync($"virtualmachineinstance.kubevirt.io/{vmName}", Namespace, kubeconfig);
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("metadata").GetProperty("uid").GetString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // between instances
        }
    }

    /// <summary>Everything that arrives until a quiet second, or the timeout.</summary>
    private static async Task<string> ReceiveAsync(System.Net.WebSockets.WebSocket socket, TimeSpan timeout)
    {
        StringBuilder text = new();
        byte[] buffer = new byte[8192];
        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            using CancellationTokenSource quiet = new(text.Length == 0 ? timeout - elapsed.Elapsed : TimeSpan.FromSeconds(1));
            try
            {
                WebSocketReceiveResult r = await socket.ReceiveAsync(buffer, quiet.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, r.Count));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return text.ToString();
    }
}
