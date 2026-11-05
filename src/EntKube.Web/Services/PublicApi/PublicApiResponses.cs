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

/// <summary>
/// What the cost ledger recorded over a window — the endpoint a billing system reads.
///
/// <para>⚠️ <b>The factory is <c>FromReport</c>, not <c>From</c>, because this record HAS a
/// property called <c>From</c>.</b> Renaming the property was not an option: <c>from</c> is the
/// wire name a client already reads.</para>
///
/// <para>Unlike <see cref="ClusterRateResponse"/>, this is NOT an identity projection — the
/// handler renamed as it went (<c>complete</c> from <c>IsComplete</c>, <c>unattributed</c> from
/// <c>IsUnattributed</c>) and flattened the grouping enum to a lowercase string. Those renames are
/// the wire contract, so they are reproduced exactly and proved by
/// <c>CostWireShapeTests</c>.</para>
/// </summary>
public sealed record CostHistoryResponse(
    DateTime From,
    DateTime To,
    string Currency,
    string GroupBy,
    decimal TotalCost,
    decimal PreviousTotalCost,
    decimal Change,
    double? ChangeFraction,
    decimal UnattributedCost,
    decimal MultiAppCost,
    decimal PlatformCost,
    decimal SharedChargedOutCost,
    decimal DailyAverage,
    CostCoverageResponse Coverage,
    IReadOnlyList<string> Warnings,
    IEnumerable<CostHistoryDayResponse> Daily,
    IEnumerable<CostHistoryGroupResponse> Groups)
{
    public static CostHistoryResponse FromReport(EntKube.Web.Services.Cost.CostHistoryReport report) =>
        new(
            From: report.From,
            To: report.To,
            Currency: report.Currency,
            // ⚠️ The enum is flattened to a lowercase string, not serialised as an enum. A client
            // reads "customer", not 0 and not "Customer".
            GroupBy: report.GroupBy.ToString().ToLowerInvariant(),
            TotalCost: report.TotalCost,
            PreviousTotalCost: report.PreviousTotalCost,
            Change: report.Change,
            ChangeFraction: report.ChangeFraction,
            UnattributedCost: report.UnattributedCost,
            MultiAppCost: report.MultiAppCost,
            PlatformCost: report.PlatformCost,
            SharedChargedOutCost: report.SharedChargedOutCost,
            DailyAverage: report.DailyAverage,
            Coverage: CostCoverageResponse.From(report),
            Warnings: report.Warnings,
            Daily: report.Daily.Select(CostHistoryDayResponse.From),
            Groups: report.Groups.Select(CostHistoryGroupResponse.From));
}

/// <summary>
/// How much of the window the ledger actually accounts for.
///
/// <para>Carried beside every total deliberately: an under-measured window under-states cost, and
/// a caller that cannot see the shortfall reads it as a saving.</para>
/// </summary>
public sealed record CostCoverageResponse(
    decimal CoveredHours,
    decimal ExpectedHours,
    decimal GapHours,
    double Completeness,
    bool Complete)
{
    public static CostCoverageResponse From(EntKube.Web.Services.Cost.CostHistoryReport report) =>
        new(
            CoveredHours: report.CoveredHours,
            ExpectedHours: report.ExpectedHours,
            GapHours: report.GapHours,
            Completeness: report.Completeness,
            Complete: report.IsComplete);
}

/// <summary>One day of the window.</summary>
public sealed record CostHistoryDayResponse(
    DateTime Day,
    decimal Cost,
    decimal CoveredHours,
    decimal ExpectedHours,
    bool Complete)
{
    public static CostHistoryDayResponse From(EntKube.Web.Services.Cost.CostHistoryPoint point) =>
        new(
            Day: point.Day,
            Cost: point.Cost,
            CoveredHours: point.CoveredHours,
            ExpectedHours: point.ExpectedHours,
            Complete: point.IsComplete);
}

/// <summary>
/// One line of the breakdown, with the same line in the preceding window beside it.
///
/// <para><c>ChangeFraction</c> stays nullable: a line that did not exist last window has no
/// percentage, and reporting +100% would invent a trend out of an appearance.</para>
/// </summary>
public sealed record CostHistoryGroupResponse(
    Guid? Id,
    string Label,
    decimal Cost,
    decimal PreviousCost,
    decimal Change,
    double? ChangeFraction,
    bool IsNew,
    bool IsGone,
    double CpuCoreHours,
    double MemoryGiBHours,
    double StorageGiBHours,
    bool Unattributed)
{
    public static CostHistoryGroupResponse From(EntKube.Web.Services.Cost.CostHistoryGroup group) =>
        new(
            Id: group.Id,
            Label: group.Label,
            Cost: group.Cost,
            PreviousCost: group.PreviousCost,
            Change: group.Change,
            ChangeFraction: group.ChangeFraction,
            IsNew: group.IsNew,
            IsGone: group.IsGone,
            CpuCoreHours: group.CpuCoreHours,
            MemoryGiBHours: group.MemoryGiBHours,
            StorageGiBHours: group.StorageGiBHours,
            Unattributed: group.IsUnattributed);
}

/// <summary>One calendar month of recorded cost.</summary>
public sealed record CostMonthResponse(
    int Year,
    int Month,
    string Label,
    decimal Cost,
    string Currency,
    double Completeness,
    bool Complete)
{
    public static CostMonthResponse From(EntKube.Web.Services.Cost.CostMonth month) =>
        new(
            Year: month.Year,
            Month: month.Month,
            Label: month.Label,
            Cost: month.Cost,
            Currency: month.Currency,
            Completeness: month.Completeness,
            Complete: month.IsComplete);
}

/// <summary>
/// The tenant's current run rate — what today's fleet would cost over a month, as opposed to
/// <see cref="CostHistoryResponse"/>, which is what was actually incurred.
///
/// <para>⚠️ <b>Every figure on this envelope is COMPUTED from the namespace list.</b>
/// <c>TotalMonthlyCost</c> sums the billable namespaces, <c>IdleMonthlyCost</c> the idle ones,
/// <c>SharedPoolMonthlyCost</c> every namespace's share of the pool, and the three breakdowns are
/// groupings of the same list. None of them can be set independently, which is why the wire-shape
/// fixture builds a namespace list that makes each one land on a distinct non-zero value rather
/// than assigning the totals directly: a fixture that set them by hand would prove the mapping
/// reads SOMETHING, not that it reads the right thing.</para>
///
/// <para>⚠️ <b>Six nested shapes, and the renames are the contract.</b> <c>ns</c> from
/// <c>Namespace</c>, <c>cluster</c> from <c>ClusterName</c>, <c>app</c> from <c>AppName</c>,
/// <c>monthlyCost</c> from <c>TotalMonthlyCost</c>, and <c>unattributed</c> /
/// <c>redistributed</c> / <c>idle</c> from the <c>Is…</c> members. Those names are what the
/// existing clients read, so reproducing them exactly is the whole job —
/// <c>CostRunRateWireShapeTests</c> compares against the anonymous objects taken out of the
/// handler verbatim.</para>
/// </summary>
public sealed record CostRunRateResponse(
    DateTime GeneratedAt,
    string Currency,
    decimal TotalMonthlyCost,
    decimal TotalHourlyCost,
    decimal UnattributedMonthlyCost,
    decimal SharedPoolMonthlyCost,
    decimal IdleMonthlyCost,
    decimal MultiAppMonthlyCost,
    IReadOnlyList<string> Warnings,
    IEnumerable<CostCapacityResponse> Capacity,
    IEnumerable<CostCustomerResponse> ByCustomer,
    IEnumerable<CostAppResponse> ByApp,
    IEnumerable<CostEnvironmentResponse> ByEnvironment,
    IEnumerable<CostNamespaceResponse> Namespaces)
{
    public static CostRunRateResponse From(EntKube.Web.Services.Cost.CostReport report) =>
        new(
            GeneratedAt: report.GeneratedAt,
            Currency: report.Currency,
            TotalMonthlyCost: report.TotalMonthlyCost,
            TotalHourlyCost: report.TotalHourlyCost,
            UnattributedMonthlyCost: report.UnattributedMonthlyCost,
            SharedPoolMonthlyCost: report.SharedPoolMonthlyCost,
            // Node capacity nothing holds, priced. Inside the shared pool and the total;
            // reported apart because it is what makes the total approach the invoice rather
            // than the sum of what was requested.
            IdleMonthlyCost: report.IdleMonthlyCost,
            MultiAppMonthlyCost: report.MultiAppMonthlyCost,
            Warnings: report.Warnings,
            Capacity: report.Capacity.Select(CostCapacityResponse.From),
            ByCustomer: report.ByCustomer.Select(CostCustomerResponse.From),
            ByApp: report.ByApp.Select(CostAppResponse.From),
            ByEnvironment: report.ByEnvironment.Select(CostEnvironmentResponse.From),
            Namespaces: report.Namespaces.Select(CostNamespaceResponse.From));
}

/// <summary>
/// One cluster's nodes against what is scheduled on them.
///
/// <para><c>NodeMonthlyCost</c> is what the nodes cost at the compute rates whatever runs on
/// them — the figure to hold up against the provider's invoice. <c>MonthlyCost</c> is what the
/// report attributes to the cluster. The distance between them is the idle capacity.</para>
///
/// <para>⚠️ <b>Named arguments.</b> Four adjacent <c>double</c>s (<c>CpuCapacity</c>,
/// <c>CpuAllocated</c>, <c>MemoryCapacityGiB</c>, <c>MemoryAllocatedGiB</c>) and two adjacent
/// <c>decimal</c>s, where a positional swap would compile and would report a cluster as fully
/// utilised or as entirely idle.</para>
/// </summary>
public sealed record CostCapacityResponse(
    Guid ClusterId,
    string Cluster,
    bool HasCapacity,
    double Nodes,
    double CpuCapacity,
    double CpuAllocated,
    double MemoryCapacityGiB,
    double MemoryAllocatedGiB,
    decimal NodeMonthlyCost,
    decimal IdleMonthlyCost,
    bool IdleCharged,
    decimal MonthlyCost)
{
    public static CostCapacityResponse From(EntKube.Web.Services.Cost.ClusterCapacityCost cost) =>
        new(
            ClusterId: cost.ClusterId,
            Cluster: cost.ClusterName,
            HasCapacity: cost.HasCapacity,
            Nodes: cost.Nodes,
            CpuCapacity: cost.CpuCapacity,
            CpuAllocated: cost.CpuAllocated,
            MemoryCapacityGiB: cost.MemoryCapacityGiB,
            MemoryAllocatedGiB: cost.MemoryAllocatedGiB,
            NodeMonthlyCost: cost.NodeMonthlyCost,
            IdleMonthlyCost: cost.IdleMonthlyCost,
            IdleCharged: cost.IdleCharged,
            MonthlyCost: cost.TotalMonthlyCost);
}

/// <summary>
/// One customer's share of the run rate — the chargeback line.
///
/// <para>The source is a tuple, so the mapping has no member names to lean on and a transposition
/// between <c>CustomerId</c> and the name would not even be a type error if both were strings.
/// They are not, but the tuple's <c>Item3</c> money field is why this is a named record.</para>
/// </summary>
public sealed record CostCustomerResponse(Guid CustomerId, string Customer, decimal MonthlyCost)
{
    public static CostCustomerResponse From(
        (Guid CustomerId, string CustomerName, decimal MonthlyCost) line) =>
        new(
            CustomerId: line.CustomerId,
            Customer: line.CustomerName,
            MonthlyCost: line.MonthlyCost);
}

/// <summary>
/// What one app costs: its own consumption plus its share of the platform it runs on.
///
/// <para><c>CustomerId</c> and <c>Customer</c> stay nullable — an app whose namespaces are not
/// attributed to a customer has none, and inventing one would put cost on a customer who does not
/// owe it.</para>
/// </summary>
public sealed record CostAppResponse(
    Guid AppId,
    string App,
    Guid? CustomerId,
    string? Customer,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> Namespaces,
    double CpuCores,
    double MemoryGiB,
    double StorageGiB,
    decimal DirectMonthlyCost,
    decimal SharedMonthlyCost,
    decimal MonthlyCost,
    double ShareOfBillable)
{
    public static CostAppResponse From(EntKube.Web.Services.Cost.AppCost cost) =>
        new(
            AppId: cost.AppId,
            App: cost.AppName,
            CustomerId: cost.CustomerId,
            Customer: cost.CustomerName,
            Environments: cost.Environments,
            Namespaces: cost.Namespaces,
            CpuCores: cost.CpuCores,
            MemoryGiB: cost.MemoryGiB,
            StorageGiB: cost.StorageGiB,
            DirectMonthlyCost: cost.DirectMonthlyCost,
            SharedMonthlyCost: cost.SharedMonthlyCost,
            MonthlyCost: cost.TotalMonthlyCost,
            ShareOfBillable: cost.ShareOfBillable);
}

/// <summary>One environment's share of the run rate.</summary>
public sealed record CostEnvironmentResponse(string Environment, decimal MonthlyCost)
{
    public static CostEnvironmentResponse From((string Environment, decimal MonthlyCost) line) =>
        new(Environment: line.Environment, MonthlyCost: line.MonthlyCost);
}

/// <summary>
/// What one namespace costs — the measurement every figure above is derived from.
///
/// <para>⚠️ <b>Three booleans a caller must not confuse.</b> <c>Unattributed</c> means no
/// customer could be found for it, so its cost reached nobody; <c>Redistributed</c> means its cost
/// was pooled onto the billable namespaces and must therefore be left OUT of any total a caller
/// computes, or the money is counted twice; <c>Idle</c> means the row is not a namespace at all
/// but the cluster's unallocated node capacity, carried as a line so it can be pooled and audited
/// like one. All three are reported precisely so the pool can be audited.</para>
/// </summary>
public sealed record CostNamespaceResponse(
    string Ns,
    string Cluster,
    string? Customer,
    string? App,
    Guid? AppId,
    string? Environment,
    double CpuCores,
    double MemoryGiB,
    double StorageGiB,
    int LoadBalancers,
    int PublicIps,
    decimal DirectMonthlyCost,
    decimal SharedMonthlyCost,
    double ShareOfCluster,
    decimal MonthlyCost,
    bool Unattributed,
    bool Redistributed,
    bool Idle)
{
    public static CostNamespaceResponse From(EntKube.Web.Services.Cost.NamespaceCost cost) =>
        new(
            Ns: cost.Namespace,
            Cluster: cost.ClusterName,
            Customer: cost.CustomerName,
            App: cost.AppName,
            AppId: cost.AppId,
            Environment: cost.EnvironmentName,
            CpuCores: cost.CpuCores,
            MemoryGiB: cost.MemoryGiB,
            StorageGiB: cost.StorageGiB,
            LoadBalancers: cost.LoadBalancers,
            PublicIps: cost.PublicIps,
            DirectMonthlyCost: cost.DirectMonthlyCost,
            SharedMonthlyCost: cost.SharedMonthlyCost,
            ShareOfCluster: cost.ShareOfCluster,
            MonthlyCost: cost.TotalMonthlyCost,
            Unattributed: cost.IsUnattributed,
            Redistributed: cost.IsRedistributed,
            Idle: cost.IsIdle);
}
