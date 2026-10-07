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
/// <param name="Configuration">
/// The installer module's own JSON blob for this component — a <c>TempoConfig</c>, a
/// <c>VeleroConfig</c> and so on. Carried because nine foreign call sites deserialize it, which
/// the first version of this record left out: a contract missing the one field its callers read
/// cannot be adopted by them, which is a good way for an interface to stay unused.
/// </param>
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
    string? Configuration,
    string? LastError,
    DateTime? InstalledAt);

/// <summary>
/// What Catalog offers the rest of EntKube.
///
/// <para><b>Almost read-only — and the exception was missed the first time.</b> This interface
/// used to say that thirty-six services in ten other modules read this table and "not one of them
/// writes it". That was wrong, and wrong in a way worth recording: the measurement looked for
/// <c>.ClusterComponents.Add/Remove/Update</c> and found none, which misses every write EF performs
/// through change tracking. <b>Nine foreign services mutate a component in 22 places</b>, all of
/// them <c>component.HelmValues = …</c> (18) or <c>component.Configuration = …</c> (4) followed by
/// <c>SaveChangesAsync</c>. A write with no <c>Update()</c> call is still a write.</para>
///
/// <para>Those 22 are not 22 different operations. Fourteen are
/// <c>MergeFormValues(component.HelmValues, dict)</c>, four replace the values outright, and all
/// four <c>Configuration</c> writes sit in the same method as one of those — so the whole set is
/// two operations, which is what <see cref="MergeHelmValuesAsync"/> and
/// <see cref="SetHelmValuesAsync"/> are. Installing, upgrading and removing components remains
/// Catalog's own work behind <c>ComponentLifecycleService</c>; what the contract had to admit is
/// that <em>configuring</em> a component is something its installer module asks for.</para>
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

    /// <summary>
    /// Merges form values into a component's stored Helm values and returns it as it now stands.
    /// Null when the component is not this tenant's — the same answer as
    /// <see cref="GetComponentAsync"/>, so a caller needs one call rather than a read, a check and
    /// a write.
    ///
    /// <para>How the merge is performed is deliberately Catalog's business, not the caller's.
    /// Fourteen call sites reached for the same <c>YamlFormMerger.MergeFormValues</c> on values they
    /// had just loaded; passing the dictionary instead means there is one place that decides what
    /// merging a component's values means.</para>
    /// </summary>
    /// <param name="configuration">
    /// The component's configuration blob, or <c>null</c> to leave the stored one untouched. There
    /// is no way to clear it through this contract, because nothing does — say so here rather than
    /// let a null argument mean two things.
    /// </param>
    Task<InstalledComponent?> MergeHelmValuesAsync(
        Guid tenantId, Guid componentId, IReadOnlyDictionary<string, string> formValues,
        string? configuration = null, CancellationToken ct = default);

    /// <summary>
    /// Replaces a component's Helm values outright, for the callers that build the whole document
    /// themselves (a rendered manifest, or values assembled from a config object) rather than
    /// patching fields into what is already there.
    /// </summary>
    /// <param name="configuration">As <see cref="MergeHelmValuesAsync"/>: null leaves it alone.</param>
    Task<InstalledComponent?> SetHelmValuesAsync(
        Guid tenantId, Guid componentId, string helmValues,
        string? configuration = null, CancellationToken ct = default);
}
