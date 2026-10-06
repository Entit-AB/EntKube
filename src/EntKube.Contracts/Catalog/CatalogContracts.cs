namespace EntKube.Contracts.Catalog;

/// <summary>Where an installed component is in its lifecycle.</summary>
public enum ComponentStatus
{
    NotInstalled,
    Installing,
    Installed,
    Failed,
    Uninstalling,
}

/// <summary>
/// A component installed on a cluster, as everyone outside Catalog sees it.
///
/// <para><b>Why the cluster is carried inline.</b> Of the cross-module reads of this table,
/// 56 were <c>.Include(c =&gt; c.Cluster)</c> — nobody wants a component without knowing which
/// cluster it is on. Returning the ids and the cluster's name here means a caller does not
/// have to make a second call to be able to do anything, which is what would otherwise turn
/// one query into two network round trips.</para>
/// </summary>
/// <param name="Id">The component's id.</param>
/// <param name="TenantId">Owning tenant, by way of its cluster.</param>
/// <param name="ClusterId">The cluster it is installed on.</param>
/// <param name="ClusterName">That cluster's name, for display without a second lookup.</param>
/// <param name="EnvironmentId">The cluster's environment.</param>
/// <param name="Name">The catalog key — what kind of component this is.</param>
/// <param name="ComponentType">How it is installed (Helm, manifest, and so on).</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="Namespace">Kubernetes namespace, when it has one.</param>
/// <param name="ReleaseName">Helm release name, when it is a chart.</param>
/// <param name="HelmChartName">Chart name.</param>
/// <param name="HelmChartVersion">Chart version.</param>
/// <param name="HelmValues">The values it was installed with.</param>
/// <param name="LastError">Why the last attempt failed, if it did.</param>
/// <param name="InstalledAt">When it was installed.</param>
public sealed record InstalledComponent(
    Guid Id,
    Guid TenantId,
    Guid ClusterId,
    string ClusterName,
    Guid EnvironmentId,
    string Name,
    string ComponentType,
    ComponentStatus Status,
    string? Namespace,
    string? ReleaseName,
    string? HelmChartName,
    string? HelmChartVersion,
    string? HelmValues,
    string? LastError,
    DateTime? InstalledAt);

/// <summary>
/// What Catalog offers the rest of EntKube.
///
/// <para><b>Read-only, and that is a finding rather than a choice.</b> Thirty-six services in
/// ten other modules read this table and <em>not one of them writes it</em>. Installing,
/// upgrading and removing components is Catalog's own work and stays behind
/// <c>ComponentLifecycleService</c>; everyone else only ever needs to know what is there.</para>
///
/// <para>Every method is tenant-scoped, because the dominant query already was —
/// <c>c.Id == componentId &amp;&amp; c.Cluster.TenantId == tenantId</c> appeared 21 times. Making
/// the tenant a parameter rather than a filter the caller remembers to apply is the whole
/// point of having a contract.</para>
/// </summary>
public interface ICatalogApi
{
    /// <summary>One component, or null when it is not this tenant's.</summary>
    Task<InstalledComponent?> GetComponentAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default);

    /// <summary>Everything installed on one cluster.</summary>
    Task<IReadOnlyList<InstalledComponent>> GetComponentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <summary>Everything installed anywhere in the tenant.</summary>
    Task<IReadOnlyList<InstalledComponent>> GetComponentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>One named component on one cluster — the "is Harbor installed here" question.</summary>
    Task<InstalledComponent?> FindComponentAsync(
        Guid tenantId, Guid clusterId, string catalogKey, CancellationToken ct = default);

    /// <summary>Every installation of one catalog key across the tenant's clusters.</summary>
    Task<IReadOnlyList<InstalledComponent>> FindComponentsAsync(
        Guid tenantId, string catalogKey, CancellationToken ct = default);
}
