namespace EntKube.Web.Data;

/// <summary>
/// A vanilla PostgreSQL instance running inside a Kubernetes cluster that is
/// not managed by the CloudNativePG operator. Registered so EntKube can manage
/// its databases and credentials without owning the server lifecycle.
///
/// SQL is executed via kubectl exec into the postgres pod, using the admin
/// credentials stored in the vault.
/// </summary>
public class RegisteredPostgresInstance
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The Kubernetes cluster where this Postgres instance runs.
    /// </summary>
    public Guid KubernetesClusterId { get; set; }

    /// <summary>
    /// Human-readable display name (e.g. "Keycloak DB – Old cluster").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Kubernetes namespace where the Postgres pod and service live.
    /// </summary>
    public required string Namespace { get; set; }

    /// <summary>
    /// The Kubernetes Service name used as the HOST in connection credentials
    /// (e.g. "postgres" → resolves as postgres.namespace.svc.cluster.local).
    /// </summary>
    public required string ServiceName { get; set; }

    /// <summary>
    /// TCP port the Postgres service listens on. Defaults to 5432.
    /// </summary>
    public int Port { get; set; } = 5432;

    /// <summary>
    /// The name of the pod to kubectl exec into for admin SQL.
    /// Must be the primary/standalone pod (not a replica).
    /// </summary>
    public required string AdminPodName { get; set; }

    /// <summary>
    /// The PostgreSQL superuser username for running CREATE/DROP DATABASE commands.
    /// Typically "postgres". The password is stored in the vault.
    /// </summary>
    public string AdminUsername { get; set; } = "postgres";

    /// <summary>
    /// Optional free-text notes (e.g. "Keycloak DB provisioned by Helm in 2023").
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public ICollection<RegisteredPostgresDatabase> Databases { get; set; } = [];
}
