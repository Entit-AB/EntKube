using System.Text.Json;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;

namespace EntKube.Web.Services;

/// <summary>Where a new VM's root disk comes from.</summary>
public enum VirtualMachineDiskSource
{
    /// <summary>
    /// A container image carrying a disk (quay.io/containerdisks/…). Needs no storage and no CDI, but
    /// the disk is ephemeral: every restart boots the pristine image again.
    /// </summary>
    ContainerDisk,

    /// <summary>
    /// A cloud image downloaded by CDI into a PersistentVolumeClaim the VM owns. Survives restarts;
    /// deleted with the VM.
    /// </summary>
    ImportUrl
}

/// <summary>What the operator asked for when creating a VM.</summary>
public record VirtualMachineRequest
{
    public string Name { get; init; } = "";
    public string Namespace { get; init; } = "virtual-machines";
    public int Cores { get; init; } = 2;

    /// <summary>Guest memory as a Kubernetes quantity, Mi or Gi — "2Gi".</summary>
    public string Memory { get; init; } = "2Gi";

    public VirtualMachineDiskSource DiskSource { get; init; } = VirtualMachineDiskSource.ContainerDisk;

    /// <summary>The container image (ContainerDisk) or the http(s) URL of the image (ImportUrl).</summary>
    public string Image { get; init; } = "";

    /// <summary>Size of the imported disk, ImportUrl only — "20Gi".</summary>
    public string DiskSize { get; init; } = "20Gi";

    /// <summary>StorageClass for the imported disk; empty uses the cluster default.</summary>
    public string? StorageClass { get; init; }

    /// <summary>
    /// Installed by cloud-init for the image's own default user (ubuntu, fedora, debian, cloud-user…).
    /// Naming a user instead would replace that user, which every distro names differently.
    /// </summary>
    public string? SshPublicKey { get; init; }

    public bool StartImmediately { get; init; } = true;
}

/// <summary>One VirtualMachine, joined with its running instance and its disk import when there are any.</summary>
public record VirtualMachineView
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>KubeVirt's own word for the state — "Running", "Stopped", "Provisioning", "ErrorUnschedulable"…</summary>
    public string Status { get; init; } = "Unknown";

    public HealthStatus Health { get; init; } = HealthStatus.Unknown;

    /// <summary>Always, Halted, Manual, RerunOnFailure — or the equivalent of the deprecated spec.running.</summary>
    public string RunStrategy { get; init; } = "Halted";

    public int? Cores { get; init; }
    public string? Memory { get; init; }

    /// <summary>Where the root disk came from: the container image, the import URL, or the PVC.</summary>
    public string? DiskSource { get; init; }

    public string? Node { get; init; }
    public string? IpAddress { get; init; }

    /// <summary>Reported by the guest agent, when the image runs one.</summary>
    public string? GuestOs { get; init; }

    /// <summary>Import progress of the VM's disk while CDI fills it — "45.20%".</summary>
    public string? ImportProgress { get; init; }

    /// <summary>The condition message explaining a failure or a wait.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// KubeVirt's own verdict on the running instance (its <c>LiveMigratable</c> condition): null without
    /// an instance. False most often means a ReadWriteOnce disk, which only one node can mount.
    /// </summary>
    public bool? LiveMigratable { get; init; }

    /// <summary>Why the instance cannot be live-migrated, in KubeVirt's words.</summary>
    public string? MigrationBlocker { get; init; }

    public DateTime? CreatedAt { get; init; }

    public bool IsRunning => Status == "Running";

    /// <summary>
    /// Restart deletes the running instance and relies on KubeVirt to bring it back. Only these
    /// strategies do; under Manual or Halted the same delete would simply stop the VM.
    /// </summary>
    public bool CanRestart => IsRunning && RunStrategy is "Always" or "RerunOnFailure";

    public bool IsPaused => Status == "Paused";

    /// <summary>A console needs a running instance to attach to; a paused one still has its screen.</summary>
    public bool HasInstance => IsRunning || IsPaused;

    public string Key => $"{Namespace}/{Name}";
}

/// <summary>One read of a cluster's VMs. Same partial-failure shape as <see cref="WorkloadSnapshot"/>.</summary>
public record VirtualMachineSnapshot
{
    public IReadOnlyList<VirtualMachineView> Machines { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    public bool IsSuccess => Error is null;
    public static VirtualMachineSnapshot Failure(string error) => new() { Error = error };
}

/// <summary>Which of the two Virtualization components a cluster has installed.</summary>
public record VirtualizationAvailability(bool KubeVirt, bool Cdi);

/// <summary>
/// KubeVirt virtual machines on a tenant's clusters — Services › Virtual Machines.
///
/// <para><b>No table.</b> A VirtualMachine is already a declarative object KubeVirt reconciles, so the
/// cluster is the record: this service reads it live and changes it through the gated seam verbs. A copy
/// in the database would be a second source of truth for the one property an operator cares about —
/// whether the VM is running — and would drift the first time somebody used virtctl.</para>
///
/// <para><b>Power actions without subresources.</b> KubeVirt's start/stop/restart endpoints are
/// subresource PUTs the seam has no verb for. The same effects are reachable declaratively: start and
/// stop set <c>spec.runStrategy</c> (a merge patch, shown in the acknowledgment), and restart deletes the
/// VirtualMachineInstance, which KubeVirt recreates under Always/RerunOnFailure.</para>
/// </summary>
public class VirtualMachineService(
    IDbContextFactory<FleetDbContext> dbFactory,
    Clusters.IClusterClientFactory clusterAccess,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    IClusterChangeGate gate,
    AuditService auditService,
    ILogger<VirtualMachineService> logger)
{
    public const string KubeVirtKey = "kubevirt";
    public const string CdiKey = "kubevirt-cdi";

    private const string VmResource = "virtualmachines.kubevirt.io";
    private const string VmiResource = "virtualmachineinstances.kubevirt.io";
    private const string DataVolumeResource = "datavolumes.cdi.kubevirt.io";

    /// <summary>KubeVirt's aggregated API for actions on VMs and their instances.</summary>
    public const string SubresourcesGroupVersion = "subresources.kubevirt.io/v1";

    /// <summary>DNS-1123 label. The DataVolume is named "{name}-root", so the VM name leaves room for it.</summary>
    private static readonly Regex VmName = new("^[a-z0-9]([-a-z0-9]{0,56}[a-z0-9])?$", RegexOptions.Compiled);
    private static readonly Regex NamespaceName = new("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.Compiled);
    private static readonly Regex MemoryQuantity = new(@"^[1-9]\d{0,5}(Mi|Gi)$", RegexOptions.Compiled);
    private static readonly Regex DiskQuantity = new(@"^[1-9]\d{0,4}Gi$", RegexOptions.Compiled);

    /// <summary>Statuses that mean something is wrong, as opposed to on its way or deliberately off.</summary>
    private static readonly HashSet<string> FailedStatuses = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ErrorUnschedulable", "ErrImagePull", "ImagePullBackOff",
        "ErrorPvcNotFound", "ErrorDataVolumeNotFound", "DataVolumeError", "Unknown"
    };

    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithIndentedSequences()
        // An SSH key or a URL is a string however it looks; a value that YAML would read as a number or
        // a boolean must arrive quoted.
        .WithQuotingNecessaryStrings()
        .Build();

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>The tenant's clusters, narrowed to one environment when the section is opened under one.</summary>
    public async Task<List<KubernetesCluster>> GetClustersAsync(
        Guid tenantId, Guid? environmentId, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId
                && (environmentId == null || c.EnvironmentId == environmentId))
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>Per cluster: is KubeVirt deployed, and is CDI? Asked of Catalog, not of its table.</summary>
    public async Task<Dictionary<Guid, VirtualizationAvailability>> GetAvailabilityAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> components =
            await catalog.GetComponentsForTenantAsync(tenantId, ct);

        static bool Is(EntKube.Contracts.Catalog.InstalledComponent c, string key) =>
            c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed
            && (c.Name == key || c.ReleaseName == key);

        return components
            .GroupBy(c => c.ClusterId)
            .ToDictionary(
                g => g.Key,
                g => new VirtualizationAvailability(g.Any(c => Is(c, KubeVirtKey)), g.Any(c => Is(c, CdiKey))));
    }

    /// <summary>Reads every VM on one cluster, with its instance and disk import joined in.</summary>
    public async Task<VirtualMachineSnapshot> LoadAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        Clusters.IClusterClient? client = await clusterAccess.ForAsync(tenantId, clusterId, ct);
        if (client is null)
            return VirtualMachineSnapshot.Failure(
                "This cluster is not this tenant's, or has no kubeconfig configured.");

        Task<string> vms = client.GetJsonAllNamespacesAsync(VmResource, ct: ct);
        Task<string> vmis = client.GetJsonAllNamespacesAsync(VmiResource, ct: ct);
        Task<string> dvs = client.GetJsonAllNamespacesAsync(DataVolumeResource, ct: ct);

        // Let all three finish before looking at any, so none is left running — or failing unobserved —
        // behind an early return. Each one's own outcome is examined below.
        try { await Task.WhenAll(vms, vmis, dvs); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        try
        {
            await vms;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the VirtualMachine list there is nothing to show. The usual cause is that the CRD
            // is absent, and kubectl says so in words worth passing on.
            logger.LogInformation(ex, "Could not list virtual machines on cluster {ClusterId}", clusterId);
            return VirtualMachineSnapshot.Failure($"Could not list virtual machines: {ex.Message}");
        }

        List<string> warnings = [];
        string? vmiJson = null;
        string? dvJson = null;

        try { vmiJson = await vmis; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Could not read running instances, so node and IP are missing: {ex.Message}");
        }

        // Silent on failure: without CDI there is no DataVolume type to list, and a cluster running only
        // container-disk VMs should not carry a permanent warning about a component it does not need.
        try { dvJson = await dvs; }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        return new VirtualMachineSnapshot
        {
            Machines = Parse(vms.Result, vmiJson, dvJson),
            Warnings = warnings
        };
    }

    // ── Changes ───────────────────────────────────────────────────────────────

    /// <summary>Creates the VM, and its namespace when that does not exist yet.</summary>
    public async Task CreateAsync(
        Guid tenantId, Guid clusterId, VirtualMachineRequest request, string performedBy,
        CancellationToken ct = default)
    {
        Validate(request);
        string manifest = BuildManifest(request);
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);

        // Two changes, one decision: the operator chose "create this VM", and the namespace is part of it.
        using (gate.BeginOperation($"create virtual machine {request.Namespace}/{request.Name}"))
        {
            await client.EnsureNamespaceAsync(request.Namespace, ct);
            await client.ApplyManifestInNamespaceAsync(
                manifest, request.Namespace, $"Create virtual machine {request.Namespace}/{request.Name}", ct);
        }

        await AuditAsync("vm.create", request.Namespace, request.Name,
            $"{request.Cores} cores, {request.Memory}, {request.DiskSource}: {request.Image}", performedBy, ct);
    }

    public Task StartAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default) =>
        SetRunStrategyAsync(tenantId, clusterId, ns, name, "Always", "vm.start", performedBy, ct);

    public Task StopAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default) =>
        SetRunStrategyAsync(tenantId, clusterId, ns, name, "Halted", "vm.stop", performedBy, ct);

    /// <summary>
    /// Deletes the running instance so KubeVirt boots a fresh one. Refused unless the VM's run strategy
    /// brings it back — under Manual the delete would be a stop that says it was a restart.
    /// </summary>
    public async Task RestartAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default)
    {
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);

        string vmJson = await client.GetJsonAsync($"virtualmachine.kubevirt.io/{name}", ns, ct: ct);
        using JsonDocument doc = JsonDocument.Parse(vmJson);
        string strategy = RunStrategyOf(doc.RootElement);

        if (strategy is not ("Always" or "RerunOnFailure"))
            throw new InvalidOperationException(
                $"'{name}' runs with runStrategy {strategy}, so KubeVirt would not start it again after a "
                + "restart. Stop it and start it instead.");

        await client.DeleteManifestAsync("virtualmachineinstance.kubevirt.io", name, ns, ct);
        await AuditAsync("vm.restart", ns, name, null, performedBy, ct);
    }

    /// <summary>
    /// Freezes the running guest in place: its memory and CPU state are kept, nothing runs. Unlike stop,
    /// nothing is shut down, and unpausing carries on exactly where it was.
    /// </summary>
    public Task PauseAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default) =>
        InvokeOnInstanceAsync(tenantId, clusterId, ns, name, "pause", "vm.pause", performedBy, ct);

    public Task UnpauseAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default) =>
        InvokeOnInstanceAsync(tenantId, clusterId, ns, name, "unpause", "vm.unpause", performedBy, ct);

    /// <summary>Pause and unpause exist only as subresources of the running instance, not of the VM.</summary>
    private async Task InvokeOnInstanceAsync(
        Guid tenantId, Guid clusterId, string ns, string name, string subresource, string action,
        string performedBy, CancellationToken ct)
    {
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);
        await client.InvokeSubresourceAsync(
            SubresourcesGroupVersion, "virtualmachineinstances", name, ns, subresource,
            $"{char.ToUpperInvariant(subresource[0])}{subresource[1..]} virtual machine {ns}/{name}", ct);
        await AuditAsync(action, ns, name, null, performedBy, ct);
    }

    /// <summary>
    /// Connects to one of the running instance's consoles and records who did. The caller bridges the
    /// returned socket to the browser and disposes it. Not acknowledged — a console changes nothing in
    /// the cluster — but always audited, because it is interactive access to a machine.
    /// </summary>
    public async Task<System.Net.WebSockets.WebSocket> OpenConsoleAsync(
        Guid tenantId, Guid clusterId, string ns, string name, VirtualMachineConsoleKind kind,
        string performedBy, CancellationToken ct = default)
    {
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);

        // Both consoles speak raw bytes under the subprotocol KubeVirt's own virtctl uses.
        System.Net.WebSockets.WebSocket socket = await client.ConnectSubresourceAsync(
            SubresourcesGroupVersion, "virtualmachineinstances", name, ns,
            kind == VirtualMachineConsoleKind.Vnc ? "vnc" : "console", "plain.kubevirt.io", ct);

        try
        {
            await AuditAsync("vm.console", ns, name,
                kind == VirtualMachineConsoleKind.Vnc ? "VNC console opened" : "serial console opened", performedBy, ct);
        }
        catch
        {
            // A session nobody can account for is not one to hand over.
            socket.Dispose();
            throw;
        }

        return socket;
    }

    /// <summary>
    /// Moves the running guest to another node without stopping it, by creating a
    /// <c>VirtualMachineInstanceMigration</c>. KubeVirt picks the target node and refuses on its own if the
    /// instance cannot move; the page offers this only when KubeVirt says it can.
    /// </summary>
    public async Task MigrateAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default)
    {
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);
        await client.ApplyManifestInNamespaceAsync(
            BuildMigrationManifest(ns, name, DateTime.UtcNow), ns,
            $"Live-migrate virtual machine {ns}/{name} to another node", ct);
        await AuditAsync("vm.migrate", ns, name, null, performedBy, ct);
    }

    /// <summary>
    /// One migration request. Named per request, because apply would otherwise update a finished
    /// migration in place and nothing would move. The VM name is cut so the whole name stays a DNS label.
    /// </summary>
    public static string BuildMigrationManifest(string ns, string name, DateTime now) =>
        Yaml.Serialize(new Dictionary<string, object>
        {
            ["apiVersion"] = "kubevirt.io/v1",
            ["kind"] = "VirtualMachineInstanceMigration",
            ["metadata"] = new Dictionary<string, object>
            {
                ["name"] = $"{name[..Math.Min(name.Length, 40)].TrimEnd('-')}-mig-{now:yyyyMMddHHmmss}",
                ["namespace"] = ns,
                ["labels"] = new Dictionary<string, object> { ["app.kubernetes.io/managed-by"] = "entkube" }
            },
            ["spec"] = new Dictionary<string, object> { ["vmiName"] = name }
        });

    /// <summary>Deletes the VM. Disks it imported (its DataVolume templates) are owned by it and go too.</summary>
    public async Task DeleteAsync(Guid tenantId, Guid clusterId, string ns, string name, string performedBy,
        CancellationToken ct = default)
    {
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);
        await client.DeleteManifestAsync("virtualmachine.kubevirt.io", name, ns, ct);
        await AuditAsync("vm.delete", ns, name, "including the disks it imported", performedBy, ct);
    }

    private async Task SetRunStrategyAsync(
        Guid tenantId, Guid clusterId, string ns, string name, string strategy, string action,
        string performedBy, CancellationToken ct)
    {
        Clusters.IClusterClient client = await ClusterFor(tenantId, clusterId, ct);
        await client.PatchJsonAsync("virtualmachine.kubevirt.io", name, ns, RunStrategyPatch(strategy), ct);
        await AuditAsync(action, ns, name, null, performedBy, ct);
    }

    /// <summary>
    /// The merge patch that sets a run strategy. It also removes <c>spec.running</c>: KubeVirt rejects a
    /// VM that sets both, and a VM created by older tooling still carries the deprecated field.
    /// </summary>
    public static string RunStrategyPatch(string strategy) =>
        JsonSerializer.Serialize(new { spec = new Dictionary<string, object?> { ["running"] = null, ["runStrategy"] = strategy } });

    private async Task<Clusters.IClusterClient> ClusterFor(Guid tenantId, Guid clusterId, CancellationToken ct) =>
        await clusterAccess.ForAsync(tenantId, clusterId, ct)
        ?? throw new InvalidOperationException("This cluster is not this tenant's, or has no kubeconfig configured.");

    private Task AuditAsync(string action, string ns, string name, string? details, string performedBy,
        CancellationToken ct) =>
        auditService.RecordAsync(
            deploymentId: null, action: action, resourceKind: "VirtualMachine",
            resourceName: $"{ns}/{name}", details: details, performedBy: performedBy, ct: ct);

    // ── Manifest ──────────────────────────────────────────────────────────────

    /// <summary>Rejects a request KubeVirt or Kubernetes would reject, with a sentence instead of an API error.</summary>
    public static void Validate(VirtualMachineRequest r)
    {
        if (!VmName.IsMatch(r.Name))
            throw new ArgumentException(
                "Name must be lowercase letters, digits and '-', start and end alphanumeric, at most 58 characters.");
        if (!NamespaceName.IsMatch(r.Namespace))
            throw new ArgumentException("Namespace must be lowercase letters, digits and '-', at most 63 characters.");
        if (r.Cores is < 1 or > 64)
            throw new ArgumentException("Cores must be between 1 and 64.");
        if (!MemoryQuantity.IsMatch(r.Memory))
            throw new ArgumentException("Memory must be a whole number of Mi or Gi, e.g. 2Gi or 512Mi.");
        if (string.IsNullOrWhiteSpace(r.Image))
            throw new ArgumentException(r.DiskSource == VirtualMachineDiskSource.ImportUrl
                ? "An image URL is required." : "A container disk image is required.");

        if (r.DiskSource == VirtualMachineDiskSource.ImportUrl)
        {
            if (!Uri.TryCreate(r.Image.Trim(), UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https"))
                throw new ArgumentException("The image URL must be an http or https URL.");
            if (!DiskQuantity.IsMatch(r.DiskSize))
                throw new ArgumentException("Disk size must be a whole number of Gi, e.g. 20Gi.");
        }
        else if (r.Image.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A container image reference cannot contain spaces.");
        }

        if (!string.IsNullOrWhiteSpace(r.SshPublicKey) && !LooksLikeOnePublicKey(r.SshPublicKey.Trim()))
            throw new ArgumentException("The SSH key must be a single public key line, e.g. 'ssh-ed25519 AAAA… you@host'.");
    }

    /// <summary>
    /// One line, in an OpenSSH public key format. Catches the two real mistakes — pasting the private
    /// key, and pasting several keys — without trying to be a key parser.
    /// </summary>
    private static bool LooksLikeOnePublicKey(string key) =>
        !key.Contains('\n') && !key.Contains('\r')
        && (key.StartsWith("ssh-", StringComparison.Ordinal)
            || key.StartsWith("ecdsa-", StringComparison.Ordinal)
            || key.StartsWith("sk-", StringComparison.Ordinal));

    /// <summary>
    /// The VirtualMachine manifest. Built as data and serialized, never by string interpolation: the image,
    /// URL and SSH key are typed by a person, and one with a newline or a colon in it must stay a value.
    /// </summary>
    public static string BuildManifest(VirtualMachineRequest r)
    {
        string rootDisk = $"{r.Name}-root";

        Dictionary<string, object> cloudConfig = new() { ["hostname"] = r.Name };
        if (!string.IsNullOrWhiteSpace(r.SshPublicKey))
            cloudConfig["ssh_authorized_keys"] = new List<object> { r.SshPublicKey.Trim() };
        string userData = "#cloud-config\n" + Yaml.Serialize(cloudConfig);

        Dictionary<string, object> rootVolume = r.DiskSource == VirtualMachineDiskSource.ImportUrl
            ? new() { ["name"] = "root", ["dataVolume"] = new Dictionary<string, object> { ["name"] = rootDisk } }
            : new() { ["name"] = "root", ["containerDisk"] = new Dictionary<string, object> { ["image"] = r.Image.Trim() } };

        Dictionary<string, object> spec = new()
        {
            ["runStrategy"] = r.StartImmediately ? "Always" : "Halted",
            ["template"] = new Dictionary<string, object>
            {
                ["metadata"] = new Dictionary<string, object>
                {
                    ["labels"] = new Dictionary<string, object> { ["kubevirt.io/vm"] = r.Name }
                },
                ["spec"] = new Dictionary<string, object>
                {
                    ["domain"] = new Dictionary<string, object>
                    {
                        ["cpu"] = new Dictionary<string, object> { ["cores"] = r.Cores },
                        ["resources"] = new Dictionary<string, object>
                        {
                            ["requests"] = new Dictionary<string, object> { ["memory"] = r.Memory }
                        },
                        ["devices"] = new Dictionary<string, object>
                        {
                            ["disks"] = new List<object>
                            {
                                Disk("root"),
                                Disk("cloudinit")
                            },
                            ["interfaces"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["name"] = "default",
                                    ["masquerade"] = new Dictionary<string, object>()
                                }
                            }
                        }
                    },
                    ["networks"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "default",
                            ["pod"] = new Dictionary<string, object>()
                        }
                    },
                    ["volumes"] = new List<object>
                    {
                        rootVolume,
                        new Dictionary<string, object>
                        {
                            ["name"] = "cloudinit",
                            ["cloudInitNoCloud"] = new Dictionary<string, object> { ["userData"] = userData }
                        }
                    }
                }
            }
        };

        if (r.DiskSource == VirtualMachineDiskSource.ImportUrl)
        {
            Dictionary<string, object> storage = new()
            {
                ["resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new Dictionary<string, object> { ["storage"] = r.DiskSize }
                }
            };
            if (!string.IsNullOrWhiteSpace(r.StorageClass))
                storage["storageClassName"] = r.StorageClass.Trim();

            spec["dataVolumeTemplates"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["metadata"] = new Dictionary<string, object> { ["name"] = rootDisk },
                    ["spec"] = new Dictionary<string, object>
                    {
                        ["source"] = new Dictionary<string, object>
                        {
                            ["http"] = new Dictionary<string, object> { ["url"] = r.Image.Trim() }
                        },
                        ["storage"] = storage
                    }
                }
            };
        }

        Dictionary<string, object> vm = new()
        {
            ["apiVersion"] = "kubevirt.io/v1",
            ["kind"] = "VirtualMachine",
            ["metadata"] = new Dictionary<string, object>
            {
                ["name"] = r.Name,
                ["namespace"] = r.Namespace,
                ["labels"] = new Dictionary<string, object> { ["app.kubernetes.io/managed-by"] = "entkube" }
            },
            ["spec"] = spec
        };

        return Yaml.Serialize(vm);

        static Dictionary<string, object> Disk(string name) => new()
        {
            ["name"] = name,
            ["disk"] = new Dictionary<string, object> { ["bus"] = "virtio" }
        };
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Joins the three lists by namespace/name. A VMI shares its VM's name; a DataVolume template's name
    /// is listed on the VM, so the import progress is found through the VM's own volumes.
    /// </summary>
    public static List<VirtualMachineView> Parse(string vmJson, string? vmiJson, string? dvJson)
    {
        Dictionary<string, JsonElement> instances = Index(vmiJson);
        Dictionary<string, JsonElement> dataVolumes = Index(dvJson);

        using JsonDocument vms = JsonDocument.Parse(vmJson);
        List<VirtualMachineView> result = [];

        if (!vms.RootElement.TryGetProperty("items", out JsonElement items))
            return result;

        foreach (JsonElement vm in items.EnumerateArray())
        {
            JsonElement meta = vm.GetProperty("metadata");
            string name = Str(meta, "name") ?? "";
            string ns = Str(meta, "namespace") ?? "";
            string key = $"{ns}/{name}";

            JsonElement? template = Path(vm, "spec", "template", "spec");
            JsonElement? domain = template is { } t ? Path(t, "domain") : null;
            string status = Path(vm, "status") is { } st ? Str(st, "printableStatus") ?? "Unknown" : "Unknown";

            instances.TryGetValue(key, out JsonElement vmi);
            bool hasVmi = vmi.ValueKind == JsonValueKind.Object;

            (string? diskSource, string? dvName) = RootDisk(vm, template);
            string? progress = dvName is not null && dataVolumes.TryGetValue($"{ns}/{dvName}", out JsonElement dv)
                && Path(dv, "status") is { } dvStatus && Str(dvStatus, "phase") is not "Succeeded"
                ? Str(dvStatus, "progress")
                : null;

            result.Add(new VirtualMachineView
            {
                Name = name,
                Namespace = ns,
                Status = status,
                Health = HealthOf(status),
                RunStrategy = RunStrategyOf(vm),
                Cores = domain is { } d && Path(d, "cpu") is { } cpu && cpu.TryGetProperty("cores", out JsonElement c)
                        && c.TryGetInt32(out int cores) ? cores : null,
                Memory = domain is { } d2
                    ? (Path(d2, "memory") is { } mem ? Str(mem, "guest") : null)
                      ?? (Path(d2, "resources", "requests") is { } req ? Str(req, "memory") : null)
                    : null,
                DiskSource = diskSource,
                Node = hasVmi && Path(vmi, "status") is { } vs ? Str(vs, "nodeName") : null,
                IpAddress = hasVmi ? FirstIp(vmi) : null,
                GuestOs = hasVmi && Path(vmi, "status", "guestOSInfo") is { } os ? Str(os, "prettyName") : null,
                ImportProgress = progress,
                Message = ConditionMessage(vm),
                LiveMigratable = hasVmi ? Condition(vmi, "LiveMigratable") is { } lm ? lm.Status == "True" : null : null,
                MigrationBlocker = hasVmi && Condition(vmi, "LiveMigratable") is { Status: "False" } blocked
                    ? blocked.Message ?? blocked.Reason : null,
                CreatedAt = Str(meta, "creationTimestamp") is { } ts
                    && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime created)
                    ? created : null
            });
        }

        return [.. result.OrderBy(v => v.Namespace, StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.Ordinal)];
    }

    public static HealthStatus HealthOf(string status) => status switch
    {
        "Running" => HealthStatus.Healthy,
        "Stopped" or "Paused" => HealthStatus.Suspended,
        _ when FailedStatuses.Contains(status) => HealthStatus.Degraded,
        _ => HealthStatus.Progressing
    };

    /// <summary>spec.runStrategy, or what the deprecated spec.running boolean means in its terms.</summary>
    private static string RunStrategyOf(JsonElement vm)
    {
        if (Path(vm, "spec") is not { } spec) return "Halted";
        if (Str(spec, "runStrategy") is { } strategy) return strategy;
        return spec.TryGetProperty("running", out JsonElement running) && running.ValueKind == JsonValueKind.True
            ? "Always" : "Halted";
    }

    private static (string? Source, string? DataVolume) RootDisk(JsonElement vm, JsonElement? template)
    {
        if (template is not { } t || !t.TryGetProperty("volumes", out JsonElement volumes)
            || volumes.ValueKind != JsonValueKind.Array)
            return (null, null);

        // The first volume that is a disk rather than cloud-init config.
        foreach (JsonElement v in volumes.EnumerateArray())
        {
            if (Path(v, "containerDisk") is { } cd) return (Str(cd, "image"), null);
            if (Path(v, "persistentVolumeClaim") is { } pvc) return ($"PVC {Str(pvc, "claimName")}", null);
            if (Path(v, "dataVolume") is { } dvRef)
            {
                string? dvName = Str(dvRef, "name");
                return (DataVolumeSource(vm, dvName) ?? $"DataVolume {dvName}", dvName);
            }
        }

        return (null, null);
    }

    /// <summary>The import URL or registry of a DataVolume template the VM declares.</summary>
    private static string? DataVolumeSource(JsonElement vm, string? dvName)
    {
        if (Path(vm, "spec") is not { } spec || !spec.TryGetProperty("dataVolumeTemplates", out JsonElement templates)
            || templates.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement dvt in templates.EnumerateArray())
        {
            if (Path(dvt, "metadata") is not { } m || Str(m, "name") != dvName) continue;
            if (Path(dvt, "spec", "source", "http") is { } http) return Str(http, "url");
            if (Path(dvt, "spec", "source", "registry") is { } registry) return Str(registry, "url");
        }

        return null;
    }

    private static string? FirstIp(JsonElement vmi)
    {
        if (Path(vmi, "status") is not { } st || !st.TryGetProperty("interfaces", out JsonElement ifaces)
            || ifaces.ValueKind != JsonValueKind.Array)
            return null;

        return ifaces.EnumerateArray().Select(i => Str(i, "ipAddress")).FirstOrDefault(ip => !string.IsNullOrEmpty(ip));
    }

    /// <summary>
    /// Why the status is what it is, most specific first. Found against a real cluster: a VM that cannot
    /// start carries <c>Ready=False</c> with the generic "Guest VM is not reported as running", while the
    /// reason — libvirt refusing the domain, the pod network failing to wire — is on
    /// <c>Synchronized=False</c>, and a VMI the admission webhook refused is on <c>Failure=True</c>.
    /// Reading Ready first showed the generic line and hid both.
    /// </summary>
    private static string? ConditionMessage(JsonElement vm)
    {
        foreach ((string type, string status) in new[] { ("Failure", "True"), ("Synchronized", "False"), ("Ready", "False") })
        {
            if (Condition(vm, type) is { } c && c.Status == status && !string.IsNullOrWhiteSpace(c.Message))
                return c.Message;
        }

        return null;
    }

    private sealed record ConditionView(string? Status, string? Reason, string? Message);

    private static ConditionView? Condition(JsonElement obj, string type)
    {
        if (Path(obj, "status") is not { } st || !st.TryGetProperty("conditions", out JsonElement conditions)
            || conditions.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement c in conditions.EnumerateArray())
        {
            if (Str(c, "type") == type)
                return new ConditionView(Str(c, "status"), Str(c, "reason"), Str(c, "message"));
        }

        return null;
    }

    private static Dictionary<string, JsonElement> Index(string? json)
    {
        Dictionary<string, JsonElement> index = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return index;

        // Cloned out of the document so the elements outlive it.
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return index;

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (Path(item, "metadata") is not { } m) continue;
            index[$"{Str(m, "namespace")}/{Str(m, "name")}"] = item.Clone();
        }

        return index;
    }

    private static JsonElement? Path(JsonElement e, params string[] path)
    {
        JsonElement current = e;
        foreach (string p in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(p, out JsonElement next))
                return null;
            current = next;
        }
        return current;
    }

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out JsonElement v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
