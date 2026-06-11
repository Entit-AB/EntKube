namespace EntKube.Web.Data;

public enum ServerProvider
{
    BareMetal,
    CloudVm,
    OnPremVm,
    Other
}

/// <summary>
/// Inventory record for the physical or virtual machine that backs a Kubernetes node.
/// Stores hardware specs, location, and SSH access info for out-of-band management.
/// Optionally linked to a K8s node name for the live-node ↔ server mapping.
/// </summary>
public class ClusterServer
{
    public Guid Id { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>
    /// K8s node name this server maps to (e.g. "node-1.example.com").
    /// Null means the server is registered but not yet associated with a live node.
    /// </summary>
    public string? NodeName { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Primary IP reachable within the cluster network.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Out-of-band / IPMI / management IP for direct server access.</summary>
    public string? ManagementIpAddress { get; set; }

    public ServerProvider Provider { get; set; } = ServerProvider.BareMetal;

    /// <summary>OS description, e.g. "Ubuntu 22.04 LTS".</summary>
    public string? OsDistribution { get; set; }

    public int? CpuCores { get; set; }

    /// <summary>RAM in GB.</summary>
    public int? RamGb { get; set; }

    /// <summary>Primary disk size in GB.</summary>
    public int? DiskGb { get; set; }

    /// <summary>Physical or cloud location — datacenter, availability zone, region, etc.</summary>
    public string? Location { get; set; }

    // ── SSH access ────────────────────────────────────────────────────────────

    public string? SshUser { get; set; }

    public int SshPort { get; set; } = 22;

    /// <summary>Jump / bastion host to tunnel through, e.g. "bastion.example.com:22".</summary>
    public string? JumpHost { get; set; }

    /// <summary>Free-form notes — maintenance history, special config, caveats, etc.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KubernetesCluster Cluster { get; set; } = null!;
}
