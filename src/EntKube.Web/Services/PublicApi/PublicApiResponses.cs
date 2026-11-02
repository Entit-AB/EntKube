namespace EntKube.Web.Services.PublicApi;

/// <summary>
/// Named shapes for what the public API RETURNS.
///
/// <para><b>Why these exist.</b> Every route answered with
/// <c>Results.Ok(new { … })</c> — an anonymous object behind a bare <see cref="IResult"/>, which
/// carries no type. ASP.NET Core therefore had nothing to infer a schema from, and the generated
/// OpenAPI document described all 35 operations as a <c>200</c> with no body at all. A client
/// generated from that document gets a method per route returning <c>object</c>, which is why
/// generating the clients (<c>docs/decomposition.md</c> item 8) is blocked on this rather than on
/// the choice of generator.</para>
///
/// <para>⚠️ <b>Returned, not declared.</b> These are handed to <c>TypedResults.Ok</c> so the
/// handler's own return type becomes <c>Ok&lt;T&gt;</c> and the schema is derived from the object
/// that is actually serialised. Declaring the same shape a second time with
/// <c>.Produces&lt;T&gt;()</c> would leave two copies of one fact free to drift — the defect
/// <c>ApiScopeMetadata</c> was restructured to avoid, where the document claimed a scope the
/// filter did not enforce.</para>
///
/// <para>⚠️ <b>These are a WIRE CONTRACT, so the property names are not free.</b> The CLI reads
/// them by name, the MCP tools hand them to a model, and every existing API consumer is already
/// reading the names the anonymous objects produced. Minimal APIs serialise with
/// <c>JsonSerializerDefaults.Web</c>, so a PascalCase property arrives camelCased and matches —
/// but only where the policy agrees, which is not obvious for a name like
/// <c>MemoryGiBHourCost</c>. <c>PublicApiWireShapeTests</c> proves each of these serialises byte
/// for byte identically to the anonymous object it replaced, rather than reasoning about
/// <c>JsonNamingPolicy.CamelCase</c>.</para>
/// </summary>
public static class PublicApiResponses;

/// <summary>Who the presented token belongs to, and what it is allowed to do.</summary>
/// <param name="TenantId">The tenant every other route on this token is scoped to.</param>
/// <param name="Token">The token's name, not its value — the value is never echoed.</param>
/// <param name="Scopes">Sorted, so a caller diffing two tokens sees a stable order.</param>
public sealed record WhoAmIResponse(
    Guid TenantId,
    string Token,
    IEnumerable<string> Scopes);

/// <summary>
/// One cluster's price sheet, as read back by a provider checking for drift.
///
/// <para>⚠️ <b>One record, three operations.</b> <c>GET /cost-rates</c>,
/// <c>GET /cost-rates/{clusterId}</c> and <c>PUT /cost-rates/{clusterId}</c> all answered through
/// a single private helper, so typing the helper types all three from one transcription. Three
/// separate hand-written shapes would have been three chances to transpose a price, and a
/// transposed price is this repo's most frequent defect.</para>
///
/// <para><c>ChargeIdleCapacity</c> is here deliberately: its absence from the read-back shape was
/// once a defect in its own right, because a provider could set it and then had no way to see
/// what it had been set to.</para>
/// </summary>
public sealed record ClusterRateResponse(
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
    string? UpdatedBy)
{
    /// <summary>
    /// The one place a price sheet becomes a response.
    ///
    /// <para>⚠️ <b>Named arguments, not positional, and it lives here so it can be TESTED.</b>
    /// Six of the twelve parameters are adjacent <c>decimal</c>s — <c>LoadBalancerMonthlyCost</c>
    /// and <c>PublicIpMonthlyCost</c> among them — so a positional call that swapped two would
    /// compile and would bill a load balancer at the price of a public IP. Named arguments make
    /// that unwriteable by accident.</para>
    ///
    /// <para>This was a private helper on the endpoint class, which meant the mapping itself
    /// could not be asserted: a test could prove the RECORD serialised correctly while the
    /// mapping into it transposed two prices. Wrong numbers in untested billing figures are this
    /// repo's most frequent defect, so the mapping is public and
    /// <c>PublicApiWireShapeTests</c> gives all twelve fields distinct values.</para>
    /// </summary>
    public static ClusterRateResponse From(EntKube.Contracts.Cost.ClusterRate rate) =>
        new(
            ClusterId: rate.ClusterId,
            CpuCoreHourCost: rate.CpuCoreHourCost,
            MemoryGiBHourCost: rate.MemoryGiBHourCost,
            StorageGiBMonthCost: rate.StorageGiBMonthCost,
            LoadBalancerMonthlyCost: rate.LoadBalancerMonthlyCost,
            PublicIpMonthlyCost: rate.PublicIpMonthlyCost,
            ClusterMonthlyOverhead: rate.ClusterMonthlyOverhead,
            Currency: rate.Currency,
            ChargeOnRequests: rate.ChargeOnRequests,
            ChargeIdleCapacity: rate.ChargeIdleCapacity,
            UpdatedAt: rate.UpdatedAt,
            UpdatedBy: rate.UpdatedBy);
}
