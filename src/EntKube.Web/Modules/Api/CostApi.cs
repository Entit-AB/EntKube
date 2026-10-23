using EntKube.Contracts.Cost;
using EntKube.Web.Data;
using EntKube.Web.Services.Cost;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Cost's side of the boundary: price sheets, for everyone outside the module.
///
/// <para>The four routes behind this are the public API's only full CRUD surface, and
/// deliberately so — a Terraform provider has to create a sheet, read it back for drift
/// detection, update it in place and destroy it, and a read-only surface cannot be managed
/// declaratively. That is also why the mapping below is total rather than convenient: a field the
/// response omits cannot be reconciled, and <c>ChargeIdleCapacity</c> having been omitted is how
/// an API write came to silently re-enable a billing option worth about half the cloud bill.</para>
///
/// <para>⚠️ The mapping is the dangerous part of a contract, which is why
/// <c>CostContractMappingTests</c> asserts it field by field with every value distinct. A dropped
/// field still compiles, still serialises and still looks like a price sheet.</para>
/// </summary>
public class CostApi(CostRateService rates) : ICostApi
{
    public async Task<IReadOnlyList<ClusterRate>> ListRatesAsync(
        Guid tenantId, CancellationToken ct = default)
        => [.. (await rates.ListAsync(tenantId, ct)).Select(Map)];

    public async Task<ClusterRate?> GetRateAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
        => await rates.GetAsync(tenantId, clusterId, ct) is ClusterCostRate rate ? Map(rate) : null;

    public async Task<ClusterRate?> PatchRateAsync(
        Guid tenantId, Guid clusterId, ClusterRatePatch patch, string? updatedBy,
        CancellationToken ct = default)
    {
        // The contract's patch and the service's are separate declarations on purpose — rule 2:
        // a contract carries its own types. They agree member for member, and
        // CostContractMappingTests pins that they do rather than leaving it to a reader.
        CostRatePatch internalPatch = new(
            patch.CpuCoreHourCost,
            patch.MemoryGiBHourCost,
            patch.StorageGiBMonthCost,
            patch.ClusterMonthlyOverhead,
            patch.Currency,
            patch.ChargeOnRequests,
            patch.ChargeIdleCapacity,
            patch.LoadBalancerMonthlyCost,
            patch.PublicIpMonthlyCost);

        return await rates.PatchAsync(tenantId, clusterId, internalPatch, updatedBy, ct)
            is ClusterCostRate saved ? Map(saved) : null;
    }

    public Task<bool> DeleteRateAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
        => rates.DeleteAsync(tenantId, clusterId, ct);

    private static ClusterRate Map(ClusterCostRate r) => new(
        r.ClusterId,
        r.CpuCoreHourCost,
        r.MemoryGiBHourCost,
        r.StorageGiBMonthCost,
        r.LoadBalancerMonthlyCost,
        r.PublicIpMonthlyCost,
        r.ClusterMonthlyOverhead,
        r.Currency,
        r.ChargeOnRequests,
        r.ChargeIdleCapacity,
        r.UpdatedAt,
        r.UpdatedBy);
}
