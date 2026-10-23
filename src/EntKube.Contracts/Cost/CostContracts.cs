namespace EntKube.Contracts.Cost;

/// <summary>
/// A cluster's price sheet, as everyone outside Cost sees one.
///
/// <para>⚠️ <c>ChargeIdleCapacity</c> is here deliberately. It was absent from the public API's
/// request type, absent from its response, and absent from the handler's object initializer — so
/// every API write silently re-enabled a billing option worth about half the cloud bill for a
/// tenant who had turned it off. A field that cannot be read back cannot be reconciled, which is
/// the whole purpose of these four routes being full CRUD: a provider manages a price sheet
/// declaratively, so it must be able to see what it set.</para>
/// </summary>
public sealed record ClusterRate(
    Guid ClusterId,
    decimal CpuCoreHourCost,
    decimal MemoryGiBHourCost,
    decimal StorageGiBMonthCost,
    decimal LoadBalancerMonthlyCost,
    decimal PublicIpMonthlyCost,
    decimal ClusterMonthlyOverhead,
    string Currency,
    bool ChargeOnRequests,
    bool ChargeIdleCapacity,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// A price sheet as a write describes one: four required prices, and the rest optional so that
/// <b>omitting a field keeps what is stored</b>.
///
/// <para>⚠️ Every optional member is nullable, and that is the type's whole reason to exist. A
/// <c>bool</c> cannot express "not supplied" — <c>false</c> and absent are the same value — and a
/// whole entity cannot either, because an unset property is indistinguishable from one
/// deliberately set to its default. That is exactly how an API write came to overwrite a field it
/// never mentioned.</para>
/// </summary>
public sealed record ClusterRatePatch(
    decimal CpuCoreHourCost,
    decimal MemoryGiBHourCost,
    decimal StorageGiBMonthCost,
    decimal ClusterMonthlyOverhead,
    string? Currency = null,
    bool? ChargeOnRequests = null,
    bool? ChargeIdleCapacity = null,
    decimal? LoadBalancerMonthlyCost = null,
    decimal? PublicIpMonthlyCost = null);

/// <summary>
/// What Cost offers everyone else.
///
/// <para>This first slice is the price sheets — the four routes a Terraform provider needs to
/// create, read back for drift detection, update in place and destroy. The cost <em>history</em>
/// and the monthly roll-up are a second slice: their wire shape is some twenty fields plus two
/// nested collections, and mapping that is where fields vanish silently, so it is worth its own
/// change with its own field-by-field test rather than being bundled here.</para>
/// </summary>
public interface ICostApi
{
    /// <summary>Every priced cluster of this tenant's.</summary>
    Task<IReadOnlyList<ClusterRate>> ListRatesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// One cluster's sheet, or null when it has none.
    ///
    /// <para>⚠️ A method rather than a filter the caller applies: the route behind this used to
    /// fetch <em>every</em> rate of the tenant and pick one out in memory. Null is the honest
    /// answer for "no sheet", and lets the route return 404 — a provider reading back a resource
    /// that no longer exists has to be able to tell, so it can plan a re-create rather than
    /// reporting a zero-priced cluster as configured.</para>
    /// </summary>
    Task<ClusterRate?> GetRateAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a sheet from a partial set of values, returning what is now stored, or
    /// null when the cluster is not this tenant's.
    ///
    /// <para>⚠️ The merge belongs to the implementation, in the context that loads the row — not
    /// to the caller. A caller that read the sheet and wrote it back would leave a window in which
    /// two concurrent writes, each omitting a different field, each write back the other's stale
    /// value.</para>
    /// </summary>
    Task<ClusterRate?> PatchRateAsync(
        Guid tenantId, Guid clusterId, ClusterRatePatch patch, string? updatedBy,
        CancellationToken ct = default);

    /// <summary>Removes a sheet; false when there was none, or it is not this tenant's.</summary>
    Task<bool> DeleteRateAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);
}
