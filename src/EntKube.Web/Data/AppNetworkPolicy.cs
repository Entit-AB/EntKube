namespace EntKube.Web.Data;

public enum AppNetworkPolicyType
{
    /// <summary>Deny all ingress and egress traffic (security baseline).</summary>
    DenyAll,

    /// <summary>Allow ingress from the ingress controller namespace.</summary>
    AllowFromIngress,

    /// <summary>Allow ingress from pods in the same namespace.</summary>
    AllowFromSameNamespace,

    /// <summary>Allow ingress from a specific namespace (see AllowFromNamespace).</summary>
    AllowFromNamespace,

    /// <summary>Custom YAML policy — see CustomYaml.</summary>
    Custom
}

/// <summary>
/// A NetworkPolicy applied to the app's namespace. Multiple policies can
/// coexist (e.g. DenyAll + AllowFromIngress is a common baseline).
/// Applied via kubectl apply; managed by tenant admins and read-only in the portal.
/// </summary>
public class AppNetworkPolicy
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }

    /// <summary>Which environment this policy applies to. Policies are per-environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>Human-readable name and also the K8s NetworkPolicy resource name.</summary>
    public required string Name { get; set; }

    public AppNetworkPolicyType PolicyType { get; set; }

    /// <summary>For AllowFromNamespace: the source namespace to allow.</summary>
    public string? AllowFromNamespace { get; set; }

    /// <summary>For Custom: the complete NetworkPolicy YAML document.</summary>
    public string? CustomYaml { get; set; }

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
