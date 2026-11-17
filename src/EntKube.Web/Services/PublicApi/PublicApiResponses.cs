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
    decimal PreviousDailyAverage,
    CostCoverageResponse Coverage,
    CostCoverageResponse PreviousCoverage,
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
            // ⚠️ The figure `changeFraction` is a ratio OF — see its remarks on
            // CostHistoryReport. It is on the wire because a client that cannot recompute a
            // percentage cannot check it, and this one is read by billing systems.
            PreviousDailyAverage: report.PreviousDailyAverage,
            Coverage: CostCoverageResponse.From(report),
            PreviousCoverage: CostCoverageResponse.FromPrevious(report),
            Warnings: report.Warnings,
            Daily: report.Daily.Select(CostHistoryDayResponse.From),
            Groups: report.Groups.Select(CostHistoryGroupResponse.From));
}

/// <summary>
/// How much of the window the ledger actually accounts for.
///
/// <para>Carried beside every total deliberately: an under-measured window under-states cost, and
/// a caller that cannot see the shortfall reads it as a saving.</para>
///
/// <para>⚠️ <b>That sentence was true and <see cref="CostHistoryResponse.PreviousTotalCost"/>
/// was the exception to it.</b> Only one of these shapes used to be emitted, for the requested
/// window; the earlier window's coverage was not even fetched from the database, so the one total
/// on this response that nothing could check was the one the <c>changeFraction</c> comparison
/// divided by — and a caller did read the shortfall as a saving, on every render. There are two
/// instances now, and <see cref="MeasuredDays"/> is here so a caller can recompute the daily
/// rates that comparison is between.</para>
/// </summary>
public sealed record CostCoverageResponse(
    decimal CoveredHours,
    decimal ExpectedHours,
    decimal GapHours,
    decimal MeasuredDays,
    int UnmeasuredDays,
    double Completeness,
    bool Complete)
{
    public static CostCoverageResponse From(EntKube.Web.Services.Cost.CostHistoryReport report) =>
        new(
            CoveredHours: report.CoveredHours,
            ExpectedHours: report.ExpectedHours,
            GapHours: report.GapHours,
            MeasuredDays: report.MeasuredDays,
            UnmeasuredDays: report.UnmeasuredDays,
            Completeness: report.Completeness,
            Complete: report.IsComplete);

    /// <summary>
    /// The same shape for the window immediately before the requested one — the window
    /// <c>previousTotalCost</c> and <c>changeFraction</c> are about.
    /// </summary>
    public static CostCoverageResponse FromPrevious(
        EntKube.Web.Services.Cost.CostHistoryReport report) =>
        new(
            CoveredHours: report.PreviousCoveredHours,
            ExpectedHours: report.PreviousExpectedHours,
            GapHours: report.PreviousGapHours,
            MeasuredDays: report.PreviousMeasuredDays,
            UnmeasuredDays: report.PreviousUnmeasuredDays,
            Completeness: report.PreviousCompleteness,
            Complete: report.PreviousIsComplete);
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

/// <summary>
/// One registered cluster, as <c>GET /clusters</c> lists it.
///
/// <para>⚠️ <b><c>ProvisioningStatus</c> is a published string and it is NOT the enum's name.</b>
/// Fleet's entity calls the initial state <c>None</c>; the contract calls the same state
/// <c>Registered</c>. This API publishes the entity's spelling, and both declarations are live —
/// the translation is not legacy. It lives in
/// <c>PublicApiEndpoints.PublishedProvisioningStatus</c> and the handler passes the result in, so
/// this parameter must be fed that helper's output and never <c>status.ToString()</c>, which would
/// silently publish <c>"Registered"</c> to the CLI (<c>clusters list</c> prints this column) and to
/// the MCP server. Changing the published value is a deliberate API decision, not a side effect of
/// typing a handler.</para>
///
/// <para>⚠️ <b><c>ComponentCount</c> is not Fleet's to know, so the factory cannot compute it.</b>
/// It is Catalog's count, obtained by one query for the whole tenant and grouped by cluster, then
/// looked up per row with <c>GetValueOrDefault</c> — a cluster with nothing installed reports
/// <c>0</c> rather than being absent. Handing it to the factory as a parameter keeps that second
/// query where it can be bounded instead of becoming one query per cluster.</para>
///
/// <para><c>Environment</c> is a rename of <c>EnvironmentName</c>, and is the wire name clients
/// already read. Two source fields are deliberately not here, for two different reasons:
/// <c>TenantId</c> is off the wire because the token fixes the tenant and no route accepts a tenant
/// id from the caller; <c>EnvironmentId</c> is simply not something this list has published —
/// clients read the environment's name instead — and it is <b>not</b> withheld for tenancy reasons
/// (<c>POST /apps/{appId}/secrets/sync</c> does take an environment id from the caller).</para>
/// </summary>
public sealed record ClusterResponse(
    Guid Id,
    string Name,
    string ApiServerUrl,
    string Environment,
    string ProvisioningStatus,
    int ComponentCount)
{
    /// <summary>
    /// The one place a cluster summary becomes a response.
    ///
    /// <para>⚠️ <b>Named arguments, not positional.</b> Four of the six parameters are adjacent
    /// <c>string</c>s — <c>Name</c>, <c>ApiServerUrl</c>, <c>Environment</c> and
    /// <c>ProvisioningStatus</c> — so a positional call that transposed two would compile and
    /// would publish a cluster's environment as its name, or its name as its provisioning
    /// status, to every client reading those fields.</para>
    ///
    /// <para>⚠️ <b><c>provisioningStatus</c> and <c>componentCount</c> are the handler's, not the
    /// summary's.</b> Neither can be read off <paramref name="cluster"/>: the first is the
    /// published spelling of an enum member, the second comes from a second query against Catalog.
    /// Recomputing either here would duplicate the rename or put a per-cluster query inside a
    /// projection.</para>
    /// </summary>
    public static ClusterResponse From(
        EntKube.Contracts.Fleet.ClusterSummary cluster,
        string provisioningStatus,
        int componentCount) =>
        new(
            Id: cluster.Id,
            Name: cluster.Name,
            ApiServerUrl: cluster.ApiServerUrl,
            Environment: cluster.EnvironmentName,
            ProvisioningStatus: provisioningStatus,
            ComponentCount: componentCount);
}

/// <summary>
/// One component installed on a cluster, as <c>GET /clusters/{clusterId}/components</c> lists it.
///
/// <para>⚠️ <b><c>Type</c> and <c>Status</c> sit side by side and are NOT the same kind of
/// thing.</b> <c>ComponentType</c> is a <c>string</c> on the contract, stored as a 50-character
/// column, and it is <b>not a closed set</b>: the entity's own doc says it "will be refined later
/// with a proper enum/value set" and lists <c>"Deployment"</c>, <c>"StatefulSet"</c> and
/// <c>"Operator"</c> among its examples, while the live write paths only ever store
/// <c>"HelmChart"</c>, <c>"Manifest"</c> or <c>"ManifestUrl"</c> today. So it stays a
/// <c>string</c> here for the reason that survives either way: the published value is whatever the
/// row holds, and typing it as an enum would turn an unrecognised stored value into an
/// unserialisable response rather than a passed-through string.
/// <c>Status</c>, beside it, is the <c>ComponentStatus</c> enum published BY NAME in the
/// contract's own case — <c>"NotInstalled"</c>, <c>"Installing"</c>, <c>"Installed"</c>,
/// <c>"Failed"</c>, <c>"Uninstalling"</c>. It is <b>not</b> lowercased, unlike
/// <see cref="CostHistoryResponse.GroupBy"/> further up this same file, which IS lowercased;
/// pattern-matching on that neighbour would break this wire format. <c>ApiComponentStatusTests</c> holds the contract's enum and the
/// entity's together precisely because this route turns the name into JSON.</para>
///
/// <para>⚠️ <b>Four nullable fields, and null is PUBLISHED, not omitted.</b> <c>Ns</c>,
/// <c>Chart</c>, <c>Version</c> and <c>InstalledAt</c> are all optional on the contract, and the
/// reason is <em>not</em> that a component which has not been installed yet lacks them:
/// <c>ComponentLifecycleService.RegisterComponentAsync</c> copies the namespace, chart name and
/// version off the catalog entry at creation, while setting <c>Status</c> to
/// <c>NotInstalled</c> — so a never-installed component normally has all three, and only
/// <c>InstalledAt</c> is null. They are nullable because a <c>Manifest</c> or <c>ManifestUrl</c>
/// component has no Helm chart at all, and because <c>VaultService.CreateComponentAsync</c> creates
/// a row carrying nothing but a name and a type. The anonymous object emitted <c>"ns": null</c> for
/// those, so they stay nullable rather than being defaulted to an empty string, which a client
/// would read as a namespace called "".</para>
///
/// <para>The renames are the contract: <c>ns</c> from <c>Namespace</c> (the CLI prints it as its
/// NAMESPACE column), <c>chart</c> from <c>HelmChartName</c>, <c>version</c> from
/// <c>HelmChartVersion</c>. <c>TenantId</c>, <c>ClusterId</c>, <c>ClusterName</c>,
/// <c>EnvironmentId</c>, <c>ReleaseName</c>, <c>HelmValues</c>, <c>Configuration</c> and
/// <c>LastError</c> are on the source record and not on this one. <c>TenantId</c> is off the wire
/// because the token fixes the tenant; <c>ClusterId</c>, <c>ClusterName</c> and
/// <c>EnvironmentId</c> because the cluster is already in the path; <c>HelmValues</c>,
/// <c>Configuration</c> and <c>LastError</c> because a values blob and an error string are not
/// this list's to publish. <c>ReleaseName</c> has no stated reason beyond never having been
/// published — it is an implementation detail of the Helm install, not a property of the
/// component.</para>
/// </summary>
public sealed record ComponentResponse(
    Guid Id,
    string Name,
    string Type,
    string Status,
    string? Ns,
    string? Chart,
    string? Version,
    DateTime? InstalledAt)
{
    /// <summary>
    /// The one place an installed component becomes a response.
    ///
    /// <para>⚠️ <b>Named arguments, not positional.</b> Six of the eight parameters are
    /// <c>string</c> or <c>string?</c>, and <c>Type</c>/<c>Status</c> and <c>Chart</c>/<c>Version</c>
    /// are adjacent same-typed pairs: a positional call transposing either would compile and would
    /// report every component's chart as its version.</para>
    /// </summary>
    public static ComponentResponse From(EntKube.Contracts.Catalog.InstalledComponent component) =>
        new(
            Id: component.Id,
            Name: component.Name,
            // ⚠️ Already a string on the contract. No ToString(), no enum.
            Type: component.ComponentType,
            // ⚠️ The enum published by NAME and in its own case — "Installed", not 2 and not
            // "installed". Editing either copy of ComponentStatus changes this wire format.
            Status: component.Status.ToString(),
            Ns: component.Namespace,
            Chart: component.HelmChartName,
            Version: component.HelmChartVersion,
            InstalledAt: component.InstalledAt);
}

/// <summary>
/// One customer application, as <c>GET /apps</c> lists it.
///
/// <para>⚠️ <b><c>deploymentCount</c> is not a member of
/// <see cref="EntKube.Contracts.Delivery.AppSummary"/>.</b> The handler fetches the tenant's
/// deployments once, groups them by app id and looks each app up in that dictionary, so the count
/// belongs to the HANDLER and the factory takes it as an argument. The record must not try to
/// derive it: an app with no deployments is absent from the grouping and reads back as 0 through
/// <c>GetValueOrDefault</c>, which is the right answer and is only reachable from the caller's
/// side.</para>
///
/// <para>⚠️ <b><c>customer</c> is renamed from <c>CustomerName</c></b>, and it is NOT nullable: an
/// app reaches its tenant by way of a customer, so there is always one.</para>
///
/// <para><c>AppSummary</c> also carries <c>TenantId</c>, <c>CustomerId</c> and <c>Namespace</c>.
/// This route has never emitted any of the three, so they are absent here too — adding one would
/// be a new wire field rather than a transcription, and
/// <c>AppsAndDeploymentsWireShapeTests</c> pins the key list at four to keep it that way.</para>
/// </summary>
public sealed record AppResponse(
    Guid Id,
    string Name,
    string Customer,
    int DeploymentCount)
{
    /// <summary>
    /// ⚠️ <b>Named arguments, not positional.</b> <c>Name</c> and <c>Customer</c> are adjacent
    /// <c>string</c>s, so a positional call that swapped them would compile and would print the
    /// customer in the CLI's NAME column and the app in its CUSTOMER column.
    ///
    /// <para>⚠️ <paramref name="deploymentCount"/> comes from the handler's grouping, so nothing
    /// on <paramref name="app"/> can contradict a wrong value — the mapping test has to supply a
    /// count distinct from every other number in the fixture for that argument to be proved at
    /// all.</para>
    /// </summary>
    public static AppResponse From(
        EntKube.Contracts.Delivery.AppSummary app, int deploymentCount) =>
        new(
            Id: app.Id,
            Name: app.Name,
            Customer: app.CustomerName,
            DeploymentCount: deploymentCount);
}

/// <summary>
/// One deployment of one app into one environment on one cluster, as <c>GET /deployments</c>
/// lists it.
///
/// <para>⚠️ <b>Three enums arrive as STRINGS, by member name.</b> <c>type</c>, <c>syncStatus</c>
/// and <c>healthStatus</c> are <c>.ToString()</c> of <c>DeploymentType</c>, <c>SyncStatus</c> and
/// <c>HealthStatus</c>, so a client reads <c>"GitHelm"</c>, <c>"OutOfSync"</c>, <c>"Degraded"</c>
/// — not 4, 2 and 3, and not a camelCased form. They are declared <c>string</c> here rather than
/// as the enum types deliberately: typing them as the enums would serialise the NUMERIC value
/// under <c>JsonSerializerDefaults.Web</c>, which adds no string-enum converter, and would
/// silently change the wire format for every existing client. The consequence is that renaming a
/// member of any of the three contract enums is a wire change — and ⚠️ <c>ModuleApiTests</c> does
/// NOT catch that: it compares the two <c>Enum.GetNames</c> lists, so a rename applied to both
/// declarations stays green while the wire changes under it. The literal JSON assertions in
/// <c>AppsAndDeploymentsWireShapeTests</c> are what stand in that gap.</para>
///
/// <para>⚠️ <b><c>ns</c> is nullable because the CONTRACT is, not because the data is.</b>
/// <c>DeploymentSummary.Namespace</c> is <c>string?</c>, so this must accept a null — but the only
/// thing that fills it today cannot produce one: <c>DeliveryApi</c> projects through a private
/// <c>Row</c> whose <c>Namespace</c> is a non-nullable <c>string</c>, off an
/// <c>AppDeployment.Namespace</c> that is <c>required</c> and a column declared
/// <c>nullable: false</c>. The contract widens it; the route has never emitted it. So ⚠️ the
/// <c>"ns": null</c> case in <c>AppsAndDeploymentsWireShapeTests</c> defends a contract allowance
/// against a future producer, <b>not</b> an observed response — and that is worth knowing before
/// anyone "simplifies" this to <c>string</c>, because doing so would make the contract's own
/// nullable member unassignable. What must not happen either way is a <c>""</c> or a
/// <c>"default"</c> invented here, which a client would read as a real namespace.</para>
///
/// <para>⚠️ <b><c>isManaged</c> keeps its <c>Is</c> prefix, unlike the cost shapes.</b> The
/// anonymous object emitted <c>isManaged</c>, so the property is <c>IsManaged</c>. Dropping the
/// prefix the way <see cref="CostNamespaceResponse"/> does for <c>Unattributed</c> would rename a
/// live field — the renames are per-route history, not a house rule.</para>
///
/// <para><c>DeploymentSummary</c> also carries <c>TenantId</c>, <c>AppId</c>,
/// <c>EnvironmentId</c>, <c>ClusterId</c> and <c>StatusMessage</c>; none of the five has ever been
/// on this route's wire. <c>appId</c> is a query PARAMETER of this route, not a field of it.</para>
/// </summary>
public sealed record DeploymentResponse(
    Guid Id,
    string Name,
    string App,
    string Type,
    string Environment,
    string Cluster,
    string? Ns,
    string SyncStatus,
    string HealthStatus,
    DateTime? LastSyncedAt,
    bool IsManaged)
{
    /// <summary>
    /// ⚠️ <b>Named arguments, and here they are close to forced.</b> Eight of the eleven
    /// parameters are <c>string</c> or <c>string?</c> and all eight are ADJACENT, so a positional
    /// call that transposed <c>environment</c> and <c>cluster</c> — or <c>name</c> and <c>app</c>,
    /// or the two statuses — would compile and would tell an operator that a degraded deployment
    /// is out of sync, or that it runs on a cluster it does not run on.
    /// </summary>
    public static DeploymentResponse From(
        EntKube.Contracts.Delivery.DeploymentSummary deployment) =>
        new(
            Id: deployment.Id,
            Name: deployment.Name,
            App: deployment.AppName,
            // ⚠️ By NAME, not by value: a client reads "GitHelm", never 4.
            Type: deployment.Type.ToString(),
            Environment: deployment.EnvironmentName,
            Cluster: deployment.ClusterName,
            Ns: deployment.Namespace,
            SyncStatus: deployment.SyncStatus.ToString(),
            HealthStatus: deployment.HealthStatus.ToString(),
            LastSyncedAt: deployment.LastSyncedAt,
            IsManaged: deployment.IsManaged);
}

/// <summary>One submission from the in-app feedback widget.</summary>
/// <param name="Id">The item's id.</param>
/// <param name="Kind">Bug, Idea, Question or Praise.</param>
/// <param name="Status">New, Triaged, Resolved or Dismissed.</param>
/// <param name="Rating">1 to 5, or null when the reporter did not rate. Never 0.</param>
/// <param name="Message">What the reporter wrote. Untrusted text from an anonymous browser.</param>
/// <param name="ContactEmail">An address the reporter typed. Unverified.</param>
/// <param name="Site">The RUM site's name when this arrived.</param>
/// <param name="PagePath">The page's path, without query string or fragment.</param>
/// <param name="HasScreenshot">Whether a screenshot is still held; they age out before the item.</param>
/// <param name="TicketId">The ticket this was promoted to, if any.</param>
/// <param name="CreatedAt">When it arrived, UTC.</param>
public sealed record FeedbackResponse(
    Guid Id,
    string Kind,
    string Status,
    int? Rating,
    string Message,
    string? ContactEmail,
    string Site,
    string PagePath,
    bool HasScreenshot,
    Guid? TicketId,
    DateTime CreatedAt);
/// <summary>The table a log query produced.</summary>
/// <param name="Columns">The output columns, in order.</param>
/// <param name="Rows">Rows aligned with <paramref name="Columns"/>. Every cell is text; null means no value.</param>
/// <param name="LinesScanned">How many log lines were read to produce this.</param>
/// <param name="Partial">
/// True when reading stopped at its limit, so this describes only the newest lines in the window
/// and any count is a lower bound.
/// </param>
public sealed record LogQueryResponse(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int LinesScanned,
    bool Partial);
/// <summary>A synthetic check and how it has done.</summary>
/// <param name="Id">The check's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Url">The URL it requests.</param>
/// <param name="IsEnabled">Whether it runs.</param>
/// <param name="State">Unknown, Passing or Failing.</param>
/// <param name="ConsecutiveFailures">Failures in a row.</param>
/// <param name="UptimePercent">Runs passed over the last 24 hours. Null means no runs, NOT zero.</param>
/// <param name="LastStatusCode">The last status received. Null when no response came back.</param>
/// <param name="LastDurationMs">How long the last run took.</param>
/// <param name="LastFailure">Why the last run failed, if it did.</param>
/// <param name="LastRunAt">When it last ran, UTC.</param>
public sealed record SyntheticCheckResponse(
    Guid Id,
    string Name,
    string Url,
    bool IsEnabled,
    string State,
    int ConsecutiveFailures,
    double? UptimePercent,
    int? LastStatusCode,
    int? LastDurationMs,
    string? LastFailure,
    DateTime? LastRunAt);

/// <summary>One fault, however many times it happened.</summary>
/// <param name="Id">The issue's id.</param>
/// <param name="Source">Log or Browser.</param>
/// <param name="Scope">The cluster or RUM site's name.</param>
/// <param name="Namespace">The namespace, for a log issue.</param>
/// <param name="Workload">The workload, for a log issue.</param>
/// <param name="Title">The message with whatever varies replaced. Untrusted log text.</param>
/// <param name="SampleMessage">The latest real occurrence. Untrusted log text.</param>
/// <param name="Occurrences">How many times it happened.</param>
/// <param name="CountIsPartial">True when <paramref name="Occurrences"/> is a lower bound.</param>
/// <param name="Status">Open, Resolved or Ignored.</param>
/// <param name="Regressions">How many times it came back after being resolved.</param>
/// <param name="FirstSeenAt">When it was first seen, UTC.</param>
/// <param name="LastSeenAt">When it was last seen, UTC.</param>
public sealed record ErrorIssueResponse(
    Guid Id,
    string Source,
    string Scope,
    string? Namespace,
    string? Workload,
    string Title,
    string SampleMessage,
    long Occurrences,
    bool CountIsPartial,
    string Status,
    int Regressions,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);

/// <summary>A service level objective and where it stands.</summary>
/// <param name="Id">The objective's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">SyntheticUptime, ServiceAvailability or ServiceLatency.</param>
/// <param name="TargetPercent">The share of good events promised.</param>
/// <param name="WindowDays">The rolling window, in days.</param>
/// <param name="Good">Good events in the window.</param>
/// <param name="Total">All events in the window.</param>
/// <param name="AttainedPercent">Good over total. Null with no events: unknown, NOT zero.</param>
/// <param name="BudgetSpentFraction">Bad over the bad the target allows. 1 is exactly spent and still met; over 1 is breached.</param>
/// <param name="BurnRate">The latest day's spending pace. Above 1 spends faster than the window allows.</param>
/// <param name="IsBreached">True only when the budget is overspent, and never without events.</param>
public sealed record SloResponse(
    Guid Id,
    string Name,
    string Kind,
    double TargetPercent,
    int WindowDays,
    long Good,
    long Total,
    double? AttainedPercent,
    double? BudgetSpentFraction,
    double? BurnRate,
    bool IsBreached);

/// <summary>
/// One KubeVirt virtual machine, as <c>GET /clusters/{clusterId}/virtual-machines</c> lists it — read
/// live from the cluster, so it is as current as the request.
///
/// <para><c>ns</c> rather than <c>namespace</c>, as <see cref="ComponentResponse"/> spells it, and
/// <c>ip</c> for the first address the guest reported.</para>
/// </summary>
public sealed record VirtualMachineResponse(
    string Ns,
    string Name,
    string Status,
    string RunStrategy,
    int? Cores,
    string? Memory,
    string? Node,
    string? Ip,
    bool? LiveMigratable,
    DateTime? CreatedAt)
{
    /// <summary>Named arguments: six of the ten are strings, several adjacent.</summary>
    public static VirtualMachineResponse From(EntKube.Contracts.Fleet.VirtualMachineSummary v) =>
        new(
            Ns: v.Namespace,
            Name: v.Name,
            Status: v.Status,
            RunStrategy: v.RunStrategy,
            Cores: v.Cores,
            Memory: v.Memory,
            Node: v.Node,
            Ip: v.IpAddress,
            LiveMigratable: v.LiveMigratable,
            CreatedAt: v.CreatedAt);
}

/// <summary>
/// The VMs on one cluster, with any part of the read that failed. A failed instance read leaves
/// <c>node</c> and <c>ip</c> empty and says so here rather than leaving a reader to wonder why.
/// </summary>
public sealed record VirtualMachineListResponse(
    IEnumerable<VirtualMachineResponse> Machines,
    IEnumerable<string> Warnings);

/// <summary>An action that reached the cluster: which one, on which VM.</summary>
public sealed record VirtualMachineActionResponse(string Action, string Ns, string Name);

/// <summary>
/// The whole body of an operation whose success has nothing to report: <c>{"ok":true}</c>.
///
/// <para>⚠️ <b>One record, two operations, on purpose.</b>
/// <c>POST /deployments/{deploymentId}/restart</c> and
/// <c>POST /advisor/findings/{findingId}/acknowledge</c> emitted byte-identical
/// <c>new { ok = true }</c> literals. Two records would be two names for one wire shape, and two
/// places for a later field to be added to only one of them.</para>
///
/// <para>⚠️ <b><see cref="DeploymentSyncResponse"/> deliberately does NOT share this record.</b>
/// <c>POST /deployments/{deploymentId}/sync</c> emits <c>{ ok, output }</c>, a superset, so the
/// obvious economy is to give this record a nullable <c>Output</c> and let restart and
/// acknowledge leave it null. That is a WIRE CHANGE, not a refactor: minimal APIs serialise with
/// <c>JsonSerializerDefaults.Web</c>, which sets no <c>DefaultIgnoreCondition</c>, so a null
/// member is WRITTEN. Restart and acknowledge would begin answering
/// <c>{"ok":true,"output":null}</c>, and a consumer validating the keys it has always received
/// breaks for the sake of one fewer type. One duplicated <c>bool</c> is the cheaper mistake.</para>
///
/// <para>There is no <c>From</c> factory because there is no source object: neither handler reads
/// <c>ok</c> off anything, it asserts it. <see cref="Succeeded"/> is that assertion named once, so
/// a handler has one obvious thing to return rather than a literal to retype.
/// ⚠️ It is a convenience and <b>not</b> an enforcement — the primary constructor is public, so
/// <c>new OkResponse(false)</c> compiles and would ship an <c>ok:false</c> under a 200, a body
/// contradicting its own status. Making that unrepresentable would mean a private constructor, and
/// a record with a private primary constructor cannot be deserialised by a generated client. The
/// guard is the <b>handler</b>: both routes return this only on their success branch, and a
/// failure on either is a 4xx carrying <c>ProblemDetails</c>, never this shape.</para>
/// </summary>
/// <param name="Ok">Always true on the wire; see remarks for why that is a handler property.</param>
public sealed record OkResponse(bool Ok)
{
    /// <summary>The only value this shape has ever had on the wire.</summary>
    public static OkResponse Succeeded { get; } = new(Ok: true);
}

/// <summary>
/// What applying a deployment's manifests printed — the body the CI pipeline that triggered the
/// sync reads back.
///
/// <para>⚠️ <b>Not <see cref="OkResponse"/> with one extra member.</b> See that record's remarks:
/// sharing one record would add an <c>"output":null</c> key to the restart and acknowledge
/// responses, because <c>JsonSerializerDefaults.Web</c> writes nulls rather than omitting them.
/// The duplicated <c>ok</c> here is deliberate.</para>
///
/// <para>⚠️ <c>Output</c> stays nullable, and null is a value a caller may already be branching
/// on. It is <c>KubernetesOperationResult&lt;string&gt;.Data</c>, declared <c>T?</c>, so a sync
/// that applied cleanly and printed nothing answers <c>"output":null</c>. Do not substitute
/// <c>""</c> to make the schema tidier — that is a different answer.</para>
///
/// <para><c>Ok</c> is mapped from <c>IsSuccess</c> rather than hard-coded <c>true</c> as the
/// anonymous object did. Identical on the wire, because the handler only reaches this on the
/// success branch — but it makes an <c>ok:true</c> built from a FAILED result unwriteable rather
/// than merely unwritten, which is the enforcement <see cref="OkResponse"/> cannot have.</para>
/// </summary>
/// <param name="Ok">True. From <c>IsSuccess</c>, so it cannot contradict the result it came from.</param>
/// <param name="Output">Whatever the apply printed, or null when it printed nothing.</param>
public sealed record DeploymentSyncResponse(bool Ok, string? Output)
{
    public static DeploymentSyncResponse From(
        EntKube.Web.Services.KubernetesOperationResult<string> result) =>
        new(
            Ok: result.IsSuccess,
            Output: result.Data);
}

/// <summary>
/// What a whole-app secret sync did, binding by binding.
///
/// <para>⚠️ <b>This one body is answered under TWO statuses, 200 and 207.</b> Partial success is
/// the normal case — one binding failing must not stop its siblings and one unlistable family must
/// not stop the others — so the handler answers <c>207 MultiStatus</c> with THIS SAME SHAPE
/// whenever <see cref="Failed"/> is true, and 200 otherwise. Nothing in the body changes between
/// the two; the status is the machine-readable half of the same answer, so that a pipeline
/// treating any 2xx as "fine" has to opt into that reading. ⚠️ <c>HttpResponseMessage</c>
/// <c>.IsSuccessStatusCode</c> is <b>true</b> for 207, which is why the CLI renders the bindings
/// table rather than erroring.</para>
///
/// <para>⚠️ <b><c>Failed</c> is COMPUTED and cannot be set independently.</b> It is
/// <c>AppSecretSyncResult.AnythingFailed</c>, which is true when there is a fatal error, OR any
/// family could not be listed, OR any binding carries an error. A fixture cannot hand it a value:
/// it falls out of the other three members, so the wire-shape test builds the disjuncts rather
/// than assigning the flag. It is read off the source here rather than recomputed, because a
/// second copy of "what counts as failed" is a second rule.</para>
///
/// <para>⚠️ <b>The two nested lists' <c>error</c> members have DIFFERENT nullability.</b>
/// <c>bindings[].error</c> is <c>string?</c> — null is how a binding says it synced — while
/// <c>unlistedFamilies[].error</c> is non-nullable, because a family only appears in that list
/// BECAUSE listing it failed. (<c>SecretSyncOutcome.Error</c> is <c>string?</c>;
/// <c>SecretSyncFamilyFailure.Error</c> is <c>string</c>.) That is why they are two records and
/// not one: a single nested shape with an optional error would invite a reader to read a null in
/// the families list as success.</para>
///
/// <para><c>Output</c> and <c>FatalError</c> are both nullable free text and are not the same
/// thing. <c>Output</c> is whatever the vault-side sync printed. <c>FatalError</c> is set only
/// when the sync stopped before reaching the bindings, in which case <c>bindings</c> is empty — so
/// an empty <c>bindings</c> array with a null <c>fatalError</c> means the app has no bindings, not
/// that the sync died.</para>
/// </summary>
/// <param name="Failed">Computed; see remarks. Also decides 200 versus 207.</param>
/// <param name="Output">What the sync printed, when it printed anything.</param>
/// <param name="Bindings">Every binding attempted, in the order attempted.</param>
/// <param name="UnlistedFamilies">Families that could not be listed, so none of their bindings were tried.</param>
/// <param name="FatalError">Set when the sync stopped before reaching the bindings.</param>
public sealed record AppSecretSyncResponse(
    bool Failed,
    string? Output,
    IReadOnlyList<SecretBindingSyncResponse> Bindings,
    IReadOnlyList<SecretFamilySyncFailureResponse> UnlistedFamilies,
    string? FatalError)
{
    public static AppSecretSyncResponse From(EntKube.Contracts.Secrets.AppSecretSyncResult result) =>
        new(
            Failed: result.AnythingFailed,
            Output: result.Output,
            Bindings: [.. result.Bindings.Select(SecretBindingSyncResponse.From)],
            UnlistedFamilies: [.. result.UnlistedFamilies.Select(SecretFamilySyncFailureResponse.From)],
            FatalError: result.FatalError);
}

/// <summary>
/// One binding's outcome — the rows the CLI's <c>secrets sync</c> table renders.
///
/// <para>⚠️ <b>Two of the three fields are renamed or reshaped.</b> <c>secret</c> comes from
/// <c>SecretName</c>, and <c>kind</c> is <c>ToString()</c> on the <c>SecretBindingKind</c> enum —
/// a client reads <c>"Database"</c>, not <c>0</c>. <c>CliCommands</c> reads <c>kind</c>,
/// <c>secret</c> and <c>error</c> by exactly these names out of the <c>bindings</c> array, so both
/// are the wire contract.</para>
///
/// <para>⚠️ <b>Named arguments, and the reason is here rather than inherited.</b> All three
/// members are strings, so a positional call that swapped <c>Kind</c> and <c>Secret</c> would
/// compile and would print the Kubernetes secret's name in the KIND column and the family name in
/// the SECRET column — a table that reads as data rather than as a bug.</para>
///
/// <para><c>Error</c> is null when the binding synced; that is the only thing distinguishing a
/// synced binding from a failed one, so it stays nullable and must keep being WRITTEN rather than
/// omitted. <c>SecretSyncOutcome.Succeeded</c> is deliberately not projected — the anonymous
/// object never sent it, and <c>error == null</c> already says it.</para>
/// </summary>
/// <param name="Kind">The family the binding belongs to, as the enum's name.</param>
/// <param name="Secret">The Kubernetes secret the binding writes — the NAME, never a value.</param>
/// <param name="Error">Why it failed, or null when it synced.</param>
public sealed record SecretBindingSyncResponse(string Kind, string Secret, string? Error)
{
    public static SecretBindingSyncResponse From(
        EntKube.Contracts.Secrets.SecretSyncOutcome outcome) =>
        new(
            // ⚠️ By NAME, not by value: a client reads "Database", never 0.
            Kind: outcome.Kind.ToString(),
            Secret: outcome.SecretName,
            Error: outcome.Error);
}

/// <summary>
/// A whole family that could not be listed, so none of its bindings were attempted.
///
/// <para>⚠️ <b><c>Error</c> is NOT nullable here, unlike
/// <see cref="SecretBindingSyncResponse.Error"/>.</b> A family is in this list precisely because
/// listing it failed, so there is always a reason — <c>SecretSyncFamilyFailure.Error</c> is a
/// non-nullable <c>string</c> on the contract. Widening it to <c>string?</c> to match its sibling
/// would let a reader take a null for success in a list where every entry is a failure.</para>
/// </summary>
/// <param name="Kind">Which family, as the enum's name.</param>
/// <param name="Error">Why it could not be listed.</param>
public sealed record SecretFamilySyncFailureResponse(string Kind, string Error)
{
    public static SecretFamilySyncFailureResponse From(
        EntKube.Contracts.Secrets.SecretSyncFamilyFailure failure) =>
        new(
            Kind: failure.Kind.ToString(),
            Error: failure.Error);
}

/// <summary>
/// One Operations Advisor finding, as <c>GET /advisor/findings</c> reports it.
///
/// <para>⚠️ <b><c>id</c> is a STRING, not a Guid.</b> Findings are computed on read and never
/// stored, so there is no row to fetch one by — the id is a stable synthetic key like
/// <c>cert:{secretId}</c>, which is also what
/// <c>POST /advisor/findings/{findingId}/acknowledge</c> takes. A client that parsed it as a Guid
/// would fail on every finding.</para>
///
/// <para>⚠️ <b>Four enums arrive as their NAMES, and this record holds all four as
/// <c>string</c>.</b> <c>category</c>, <c>severity</c>, <c>horizon</c> and <c>state</c> are
/// <c>.ToString()</c> of four different enum types, so a client reads <c>"Capacity"</c>,
/// <c>"Warning"</c>, <c>"ThisWeek"</c>, <c>"Active"</c> — never a number and never a lowercased
/// form, unlike <see cref="CostHistoryResponse.GroupBy"/>, which IS lowercased. On
/// <c>FindingSummary</c> a transposition among the four would be a type error; here they are four
/// <c>string</c>s and it would not be, which is why <see cref="From"/> uses named arguments and
/// the wire-shape fixture gives each a different value.</para>
///
/// <para>⚠️ <b>Two different <c>FindingSeverity</c> enums exist and they publish different words
/// at the same ordinal.</b> <c>EntKube.Web.Services.FindingSeverity</c> (the connectivity
/// analyzer's) has <c>Error</c> at 0; the Advisor contract's has <c>Critical</c> at 0. A cast
/// between them is silent mislabelling, which is why
/// <c>AdvisorContractEnumTests.The_connectivity_severity_is_a_different_enum_and_must_not_be_mapped_onto_this_one</c>
/// exists. This file fully qualifies every source type, so the collision cannot arise here — it
/// arises in a file that imports only one of the two namespaces, where the compiler stays
/// quiet.</para>
///
/// <para>⚠️ <b>What pins these four names is NOT enough on its own.</b>
/// <c>AdvisorContractEnumTests</c> compares the contract and entity declarations <b>to each
/// other</b> (<c>Enum.GetNames</c> equality, plus a cast round-trip by name), so a rename applied
/// to BOTH sides stays green while every client's wire changes under it. The literal JSON
/// assertions in <c>AdvisorWireShapeTests</c> are what stand in that gap.</para>
///
/// <para><c>scope</c> is renamed from <c>ScopeLabel</c> and <c>customer</c> from
/// <c>CustomerName</c>. Both are names existing clients already read — <c>scope</c> is the CLI's
/// <c>entkube advisor</c> SCOPE column — so neither is free to be "corrected".</para>
///
/// <para><c>detail</c>, <c>dueAt</c>, <c>remediation</c>, <c>clusterId</c> and <c>customer</c>
/// stay nullable: a finding on the radar has no deadline, and an installation-wide finding
/// belongs to no cluster and to no customer. <c>title</c>, <c>scope</c> and <c>source</c> are
/// always present.</para>
/// </summary>
public sealed record AdvisorFindingResponse(
    string Id,
    string Category,
    string Severity,
    string Horizon,
    string Title,
    string? Detail,
    string Scope,
    DateTime? DueAt,
    string? Remediation,
    string Source,
    string State,
    Guid? ClusterId,
    string? Customer)
{
    /// <summary>
    /// The one place a finding becomes a response.
    ///
    /// <para>⚠️ <b>Named arguments, not positional.</b> Eleven of the thirteen parameters are
    /// <c>string</c> or <c>string?</c>, broken only by <c>DueAt</c> and <c>ClusterId</c>, so a
    /// positional call that swapped <c>Title</c> with <c>Scope</c>, or <c>Remediation</c> with
    /// <c>Source</c>, would compile and would put the wrong text in front of the operator
    /// deciding what to act on. Swapping <c>Severity</c> with <c>Horizon</c> would reorder the
    /// whole queue.</para>
    /// </summary>
    public static AdvisorFindingResponse From(EntKube.Contracts.Advisor.FindingSummary finding) =>
        new(
            Id: finding.Id,
            // ⚠️ All four by NAME. A number here would break the CLI's columns and the MCP server.
            Category: finding.Category.ToString(),
            Severity: finding.Severity.ToString(),
            Horizon: finding.Horizon.ToString(),
            Title: finding.Title,
            Detail: finding.Detail,
            Scope: finding.ScopeLabel,
            DueAt: finding.DueAt,
            Remediation: finding.Remediation,
            Source: finding.Source,
            State: finding.State.ToString(),
            ClusterId: finding.ClusterId,
            Customer: finding.CustomerName);
}

/// <summary>
/// One alert incident, as <c>GET /incidents</c> reports it.
///
/// <para>⚠️ <b><c>severity</c> and <c>status</c> are both strings and only ONE of them is an
/// enum.</b> <c>severity</c> is FREE TEXT on the contract (<c>IncidentSummary.Severity</c> is
/// declared <c>string</c>), carried through exactly as the alert rule wrote it — there is no
/// closed set to validate against and no casing to rely on, so a client must not switch on it.
/// <c>status</c> is <c>IncidentStatus</c>'s NAME: <c>"Active"</c>, <c>"Acknowledged"</c> or
/// <c>"Resolved"</c>. Typing the first as an enum, or dropping <c>ToString()</c> from the second,
/// each breaks a client silently — and they sit next to each other in both the record and the
/// factory.</para>
///
/// <para><c>ModuleApiTests</c> holds <c>IncidentStatus</c>'s members in order across the two enum
/// declarations. ⚠️ As with the Advisor enums, that compares the two declarations to each other
/// and so cannot catch a rename applied to both.</para>
///
/// <para>⚠️ <b>The cluster's ID is deliberately NOT on this shape.</b> <c>IncidentSummary</c>
/// carries <c>ClusterId</c>, but the anonymous object never emitted it and this record is a
/// transcription of what shipped, not an improvement on it. Adding the field here would change
/// the document rather than describe it. <c>cluster</c> is renamed from <c>ClusterName</c> — the
/// CLI's <c>entkube incidents</c> CLUSTER column reads that name.</para>
///
/// <para><c>resolvedAt</c>, <c>acknowledgedBy</c> and <c>runbookUrl</c> stay nullable: an open
/// incident has not resolved, nobody may have acknowledged it, and the rule may not have named a
/// runbook. A client must not read a missing <c>resolvedAt</c> as anything other than "still
/// open".</para>
/// </summary>
public sealed record IncidentResponse(
    Guid Id,
    string AlertName,
    string Summary,
    string Severity,
    string Status,
    string Cluster,
    DateTime StartsAt,
    DateTime? ResolvedAt,
    string? AcknowledgedBy,
    string? RunbookUrl)
{
    /// <summary>
    /// The one place an incident becomes a response.
    ///
    /// <para>⚠️ <b>Named arguments, not positional.</b> <c>AlertName</c>, <c>Summary</c>,
    /// <c>Severity</c>, <c>Status</c> and <c>Cluster</c> are five adjacent <c>string</c>s, so a
    /// positional call that put the summary where the severity belongs would compile and would
    /// make every incident unsortable by urgency — and a swap of <c>Severity</c> and
    /// <c>Status</c> would be invisible to the type system, because the enum has already been
    /// flattened to its name by then.</para>
    /// </summary>
    public static IncidentResponse From(EntKube.Contracts.Telemetry.IncidentSummary incident) =>
        new(
            Id: incident.Id,
            AlertName: incident.AlertName,
            Summary: incident.Summary,
            // ⚠️ Already a string on the contract. No ToString(), no enum — unlike Status below.
            Severity: incident.Severity,
            Status: incident.Status.ToString(),
            Cluster: incident.ClusterName,
            StartsAt: incident.StartsAt,
            ResolvedAt: incident.ResolvedAt,
            AcknowledgedBy: incident.AcknowledgedBy,
            RunbookUrl: incident.RunbookUrl);
}

/// <summary>
/// Every managed deployment's standing against its SLA, with the four counters a caller polling
/// for trouble reads first — the envelope <c>GET /error-budgets</c> answers.
///
/// <para>⚠️ <b>All four counters are COMPUTED over the list and are read off nothing.</b>
/// <c>deployments</c> is the row count, <c>withData</c> the rows with health samples in the
/// window, <c>breached</c> the rows already past their objective, <c>atRisk</c> the rows still in
/// credit but burning faster than sustainable. None can be set independently, which is why
/// <see cref="From"/> takes the whole collection rather than four numbers: a factory accepting
/// them would let a caller pass a count that disagreed with the list beside it.</para>
///
/// <para>⚠️ <b><c>atRisk</c> excludes what <c>breached</c> counts, and a null burn rate is not at
/// risk.</b> The predicate is <c>!IsBreached &amp;&amp; BurnRateRecent &gt; 1.0</c> over a
/// <c>double?</c> — a LIFTED comparison, which is <c>false</c> when the burn rate is null. So a
/// deployment with no samples falls into NEITHER counter, the two sets are disjoint, and
/// <c>deployments</c> is not their sum. ⚠️ A caller computing "healthy" as
/// <c>deployments - breached - atRisk</c> is counting the unknowns as healthy;
/// <c>withData</c> is the field that says how many rows were measurable at all.</para>
///
/// <para>⚠️ <b><c>breached</c> here is an <c>int</c>; <c>isBreached</c> on each budget is a
/// <c>bool</c>.</b> Same word, different field, different type — and <c>deployments</c> (a count)
/// against each budget's <c>deployment</c> (a name) is the same trap one level down. Both pairs
/// are the wire names that shipped.</para>
/// </summary>
public sealed record ErrorBudgetReportResponse(
    int Deployments,
    int WithData,
    int Breached,
    int AtRisk,
    IEnumerable<ErrorBudgetResponse> Budgets)
{
    /// <summary>
    /// The one place the error-budget report becomes a response.
    ///
    /// <para>⚠️ <b>The parameter is <c>IReadOnlyList</c>, not <c>IEnumerable</c>, on purpose.</b>
    /// <c>Deployments</c> is <c>budgets.Count</c> — the list's own property, exactly as the
    /// handler had it. Against an <c>IEnumerable</c> the same text would silently become
    /// <c>Enumerable.Count()</c>, a fourth walk of a sequence the three <c>Count(predicate)</c>
    /// calls already walk three times, and a deferred source would then be enumerated four
    /// times.</para>
    ///
    /// <para>⚠️ <b>Named arguments, not positional.</b> Four adjacent <c>int</c>s, every one a
    /// count of the same list, where a positional swap would compile and would report the
    /// breached total as the fleet size — a figure a support agreement is invoiced against.</para>
    /// </summary>
    public static ErrorBudgetReportResponse From(
        IReadOnlyList<EntKube.Contracts.Telemetry.ErrorBudgetSummary> budgets) =>
        new(
            Deployments: budgets.Count,
            WithData: budgets.Count(b => b.HasData),
            Breached: budgets.Count(b => b.IsBreached),
            // Burning faster than sustainable while still in credit — the set that answers
            // "will we breach" rather than "have we". Reported separately because a caller
            // polling for trouble wants it before the breach, not after.
            AtRisk: budgets.Count(b => !b.IsBreached && b.BurnRateRecent > 1.0),
            Budgets: budgets.Select(ErrorBudgetResponse.From));
}

/// <summary>
/// One deployment's standing against its SLA over the objective's own window.
///
/// <para>⚠️ <b>Four members are deliberately nullable and 0 is NOT the fallback.</b>
/// <c>achievedPercent</c>, <c>budgetConsumedFraction</c>, <c>burnRateRecent</c> and
/// <c>hoursToExhaustion</c> are null when the deployment has no health samples in the window.
/// That is UNKNOWN availability, not 0% — a client rendering a missing number as zero reports a
/// total outage for a deployment that was merely registered yesterday, which is why the handler
/// left them null rather than defaulting them. <c>hasData</c> is the question to ask first.
/// <c>hoursToExhaustion</c> is additionally null for a budget not on track to exhaust, which
/// includes one already breached, so it is not an ordering key.</para>
///
/// <para><c>deployment</c> is renamed from <c>DeploymentName</c>, <c>app</c> from <c>AppName</c>
/// and <c>customer</c> from <c>CustomerName</c> — the names the CLI's
/// <c>entkube error-budgets</c> columns read. <c>customerId</c> and <c>customer</c> stay nullable:
/// a deployment not attributed to a customer has neither, and inventing one would put an SLA
/// figure on a customer who is not owed it.</para>
///
/// <para>⚠️ <b><c>isBreached</c> keeps its <c>Is</c> prefix on the wire.</b> Elsewhere the
/// <c>Is…</c> members were flattened as they were mapped (<see cref="CostNamespaceResponse"/>'s
/// <c>Unattributed</c> from <c>IsUnattributed</c>), but this handler never did, and
/// <see cref="ErrorBudgetReportResponse.Breached"/> already uses the flattened spelling for the
/// envelope's COUNT. Flattening this one to match the house habit would collide with that and
/// would break the CLI's BREACHED column. The source's own rule holds: breached is <b>strictly</b>
/// greater than 1.0, so a budget exactly spent MEETS the objective — it was once <c>&gt;=</c> and
/// reported a breach for availability exactly equal to its target.</para>
/// </summary>
public sealed record ErrorBudgetResponse(
    Guid DeploymentId,
    string Deployment,
    string App,
    Guid? CustomerId,
    string? Customer,
    Guid ClusterId,
    double TargetPercent,
    int WindowDays,
    double? AchievedPercent,
    double? BudgetConsumedFraction,
    double? BurnRateRecent,
    double? HoursToExhaustion,
    bool HasData,
    bool IsBreached)
{
    /// <summary>
    /// The one place a budget becomes a response.
    ///
    /// <para>⚠️ <b>Named arguments, not positional.</b> <c>AchievedPercent</c>,
    /// <c>BudgetConsumedFraction</c>, <c>BurnRateRecent</c> and <c>HoursToExhaustion</c> are four
    /// adjacent <c>double?</c>s on four entirely different scales — a percentage, a fraction, a
    /// ratio and a duration — with <c>TargetPercent</c> beside them making five. A positional
    /// swap would compile and would report 99.95 hours to exhaustion for a deployment at 99.95%
    /// availability. Wrong numbers in untested SLA figures are this repository's most frequent
    /// defect, so the wire-shape fixture gives all fourteen fields distinct values.</para>
    /// </summary>
    public static ErrorBudgetResponse From(
        EntKube.Contracts.Telemetry.ErrorBudgetSummary budget) =>
        new(
            DeploymentId: budget.DeploymentId,
            Deployment: budget.DeploymentName,
            App: budget.AppName,
            CustomerId: budget.CustomerId,
            Customer: budget.CustomerName,
            ClusterId: budget.ClusterId,
            TargetPercent: budget.TargetPercent,
            WindowDays: budget.WindowDays,
            // ⚠️ Left null rather than defaulted to 0. A deployment with no samples in the
            // window has unknown availability, and 0 would read as a total outage.
            AchievedPercent: budget.AchievedPercent,
            BudgetConsumedFraction: budget.BudgetConsumedFraction,
            BurnRateRecent: budget.BurnRateRecent,
            HoursToExhaustion: budget.HoursToExhaustion,
            HasData: budget.HasData,
            IsBreached: budget.IsBreached);
}

/// <summary>
/// Every installed component measured against its chart repository — the envelope
/// <c>GET /upgrades</c> answers.
///
/// <para>⚠️ <b><c>UnknownCount</c> is on the source and is NOT emitted.</b>
/// <c>UpgradeReportSummary</c> carries four counts; this route has only ever sent three. That is
/// reproduced rather than corrected, because adding a field changes the document rather than
/// describing it. A client that wants it can compute it: each component's <c>status</c> is on the
/// wire, so <c>components.filter(c =&gt; c.status === "Unknown").length</c> is the same number —
/// the omission costs a round of arithmetic, not information.</para>
///
/// <para>⚠️ The three counts are COMPUTED over the component list on the source
/// (<c>UpgradeCount</c> is <c>Status == UpgradeAvailable</c>, <c>MajorCount</c> is that AND
/// <c>Lag == Major</c>, <c>DeprecatedCount</c> is <c>Status == Deprecated</c>), so
/// <c>upgradeCount</c> and <c>deprecatedCount</c> are disjoint while <c>majorCount</c> is a
/// SUBSET of <c>upgradeCount</c>. They do not sum to the component total and were never meant
/// to.</para>
///
/// <para>⚠️ This route can also answer <b>500</b> by throwing: <c>GetUpgradeReportAsync</c>
/// raises <c>InvalidOperationException</c> when Catalog was constructed without
/// <c>ComponentUpgradeService</c>, which was chosen over returning an empty report. That is a DI
/// misconfiguration rather than a contract status, so it is not in the declared union — the
/// signature is not a complete list of what a caller can receive.</para>
/// </summary>
public sealed record UpgradeReportResponse(
    int UpgradeCount,
    int MajorCount,
    int DeprecatedCount,
    IEnumerable<ComponentUpgradeResponse> Components)
{
    /// <summary>
    /// ⚠️ <b>Named arguments.</b> Three adjacent <c>int</c>s, all counts of the same list, where a
    /// positional swap would compile and would report the deprecated total as the number of
    /// available upgrades — the figure an operator plans a maintenance window around.
    /// </summary>
    public static UpgradeReportResponse From(
        EntKube.Contracts.Catalog.UpgradeReportSummary report) =>
        new(
            UpgradeCount: report.UpgradeCount,
            MajorCount: report.MajorCount,
            DeprecatedCount: report.DeprecatedCount,
            Components: report.Components.Select(ComponentUpgradeResponse.From));
}

/// <summary>
/// One installed component's standing against its chart repository.
///
/// <para>⚠️ <b><c>component</c> is renamed from <c>DisplayName</c> and <c>chart</c> from
/// <c>ChartName</c>, and they are different things.</b> The display name is what an operator calls
/// the component; the chart name is what Helm calls it. The CLI's <c>upgrades</c> command renders
/// both, so a transposition would show each column the other's value and read as plausible.</para>
///
/// <para>⚠️ <b>Two enums arrive as their NAMES and both are published as <c>string</c>.</b>
/// <c>status</c> is <c>UpgradeAvailability</c> and <c>lag</c> is <c>UpgradeLag</c>. Typing them as
/// the enums would serialise the NUMERIC value under <c>JsonSerializerDefaults.Web</c>, which adds
/// no string converter — and <c>UpgradeAvailable</c> and <c>Major</c> are both ordinal 0, so the
/// wrong guess ships <c>0</c> for the two most common values and nothing fails.
/// <c>CatalogUpgradeContractTests</c> holds these to the entity enums by comparing the two
/// declarations <b>to each other</b>, so it cannot catch a rename applied to both; the literal
/// JSON assertions in <c>UpgradeScanWireShapeTests</c> are what stand in that gap.</para>
///
/// <para><c>chart</c>, <c>installed</c> and <c>latest</c> stay nullable: a component whose chart
/// could not be resolved has no chart name, and one never successfully queried has no latest
/// version. <c>versionsBehind</c> is an <c>int</c> and is 0 in those cases, which is why it must
/// not be read as "up to date" on its own — <c>status</c> is the field that says.</para>
/// </summary>
public sealed record ComponentUpgradeResponse(
    string Cluster,
    string Component,
    string? Chart,
    string? Installed,
    string? Latest,
    string Status,
    string Lag,
    int VersionsBehind)
{
    /// <summary>
    /// ⚠️ <b>Named arguments.</b> Five adjacent <c>string</c>/<c>string?</c> parameters followed by
    /// two more, where <c>Installed</c>/<c>Latest</c> and <c>Status</c>/<c>Lag</c> are both
    /// same-typed adjacent pairs: transposing either compiles, and showing the installed version
    /// as the latest one reads as "nothing to do".
    /// </summary>
    public static ComponentUpgradeResponse From(
        EntKube.Contracts.Catalog.ComponentUpgradeSummary component) =>
        new(
            Cluster: component.ClusterName,
            Component: component.DisplayName,
            Chart: component.ChartName,
            Installed: component.InstalledVersion,
            Latest: component.LatestVersion,
            // ⚠️ By NAME, not by value: UpgradeAvailable and Major are both ordinal 0.
            Status: component.Status.ToString(),
            Lag: component.Lag.ToString(),
            VersionsBehind: component.VersionsBehind);
}

/// <summary>
/// What the last drift sweep found — the envelope <c>GET /drift</c> answers.
///
/// <para>⚠️ <b>This route answers 503, not an empty 200, when no sweep has completed.</b> "No
/// sweep has run" and "nothing has drifted" are different answers and a caller polling this must
/// not confuse them. The 503 is in the declared union; it carries <c>ProblemDetails</c>.</para>
///
/// <para>⚠️ <b>The property is called <c>Results</c>, which shadows
/// <c>Microsoft.AspNetCore.Http.Results</c></b> inside this record's body — that static class is
/// in scope through the Web SDK's implicit usings. It compiles, and the wire key <c>results</c> is
/// not negotiable, so the name stays. But a static member added to this record cannot write
/// <c>Results.Ok(…)</c>; it would bind to this property. Same family of trap that forced
/// <see cref="CostHistoryResponse"/>'s factory to be called <c>FromReport</c>.</para>
///
/// <para>⚠️ The three counts are computed over the result list by state
/// (<c>Drifted</c>/<c>Unknown</c>/<c>InSync</c>), so unlike the upgrade envelope they ARE disjoint
/// and DO sum to the row count — every result has exactly one state.</para>
/// </summary>
public sealed record DriftReportResponse(
    DateTime GeneratedAt,
    int DriftedCount,
    int UnknownCount,
    int InSyncCount,
    IEnumerable<DriftResultResponse> Results)
{
    /// <summary>
    /// ⚠️ <b>Named arguments.</b> Three adjacent <c>int</c>s over one list; a positional swap
    /// would report the in-sync count as the drifted count, which is the number an operator acts
    /// on and the direction that reads as "all fine".
    /// </summary>
    public static DriftReportResponse FromReport(
        EntKube.Web.Services.Upgrades.DriftReport report) =>
        new(
            GeneratedAt: report.GeneratedAt,
            DriftedCount: report.DriftedCount,
            UnknownCount: report.UnknownCount,
            InSyncCount: report.InSyncCount,
            Results: report.Results.Select(DriftResultResponse.From));
}

/// <summary>
/// One deployment's drift standing.
///
/// <para>⚠️ <b><c>ns</c> is NON-nullable here, unlike the <c>ns</c> on
/// <see cref="DeploymentResponse"/>.</b> <c>DriftResult.Namespace</c> is a <c>required
/// string</c> — a sweep only examines deployments that resolved to a namespace — while
/// <c>DeploymentSummary.Namespace</c> is <c>string?</c>. Do not normalise the two: widening this
/// one would invite a client to handle a null that cannot occur, and narrowing the other would
/// make the contract's own member unassignable.</para>
///
/// <para>⚠️ <b><c>note</c> is published HERE and not on the supply-chain image</b>, although
/// <c>RunningImage.Note</c> exists and serves the same purpose — saying why a row is
/// <c>Unknown</c> or <c>Unmanaged</c>. The asymmetry is the shipped behaviour of two routes and is
/// reproduced on both; it is not a tidy-up to make in a transcription.</para>
///
/// <para><c>DriftResult</c> also carries <c>ClusterId</c>, <c>EnvironmentName</c>,
/// <c>CustomerId</c>, <c>CustomerName</c>, <c>DiffText</c>, <c>DeprecatedApis</c> and
/// <c>CheckedAt</c>; none has ever been on this route's wire. ⚠️ <c>DiffText</c> especially: the
/// diff of a deployment's manifests can contain anything the manifests do.</para>
/// </summary>
public sealed record DriftResultResponse(
    Guid DeploymentId,
    string App,
    string Deployment,
    string Cluster,
    string Ns,
    string State,
    int ChangedLines,
    string? Note)
{
    /// <summary>
    /// ⚠️ <b>Named arguments.</b> <c>App</c>, <c>Deployment</c>, <c>Cluster</c> and <c>Ns</c> are
    /// four adjacent non-nullable strings — a positional call that put the app where the
    /// deployment belongs would compile and would misattribute every drifted row.
    /// </summary>
    public static DriftResultResponse From(
        EntKube.Web.Services.Upgrades.DriftResult result) =>
        new(
            DeploymentId: result.DeploymentId,
            App: result.AppName,
            Deployment: result.DeploymentName,
            Cluster: result.ClusterName,
            Ns: result.Namespace,
            // ⚠️ By NAME: Drifted is ordinal 0, so a number here would read as a drifted row for
            // everything and as nothing at all for a reader expecting a word.
            State: result.State.ToString(),
            ChangedLines: result.ChangedLines,
            Note: result.Note);
}

/// <summary>
/// What the last supply-chain sweep found — the envelope <c>GET /supply-chain</c> answers.
///
/// <para>⚠️ <b>503, not an empty 200</b>, when no sweep has completed — same contract as
/// <see cref="DriftReportResponse"/> and for the same reason.</para>
///
/// <para>⚠️ <b><c>MutableTagCount</c> is on the source and is NOT emitted.</b> It is
/// <c>Images.Count(i =&gt; !i.IsDigestPinned &amp;&amp; i.State != Unmanaged)</c> — <b>two</b>
/// conditions, not one. A client CAN reconstruct it, because both fields are on each image:
/// <c>images.filter(i =&gt; !i.digestPinned &amp;&amp; i.state !== "Unmanaged").length</c>.
/// ⚠️ Reading it as "not digest-pinned" alone gives a different, larger number, because unmanaged
/// images are excluded — so a client that tries to recover this figure from <c>digestPinned</c>
/// by itself will disagree with the UI, which shows the source's value.</para>
///
/// <para>⚠️ <b>The four state counts are disjoint and the two severity totals are not counts at
/// all.</b> <c>vulnerableCount</c>, <c>unscannedCount</c>, <c>unmanagedCount</c> and
/// <c>cleanCount</c> partition the images by state; <c>totalCritical</c> and <c>totalHigh</c> are
/// SUMS of findings across images, so they can exceed the image count and say nothing about how
/// many images are affected.</para>
///
/// <para><c>warnings</c> is a list of strings explaining why a sweep was partial — it is published
/// and must stay published, because an empty warnings list is how a caller knows the counts are
/// complete.</para>
/// </summary>
public sealed record SupplyChainReportResponse(
    DateTime GeneratedAt,
    int VulnerableCount,
    int UnscannedCount,
    int UnmanagedCount,
    int CleanCount,
    int TotalCritical,
    int TotalHigh,
    IReadOnlyList<string> Warnings,
    IEnumerable<SupplyChainImageResponse> Images)
{
    /// <summary>
    /// ⚠️ <b>Named arguments, and here there are SIX adjacent <c>int</c>s.</b> Four partition the
    /// images by state and two sum severities across them, so a positional swap would compile and
    /// would report a severity total as a number of images — the figure a security review reads
    /// first.
    /// </summary>
    public static SupplyChainReportResponse FromReport(
        EntKube.Web.Services.SupplyChain.SupplyChainReport report) =>
        new(
            GeneratedAt: report.GeneratedAt,
            VulnerableCount: report.VulnerableCount,
            UnscannedCount: report.UnscannedCount,
            UnmanagedCount: report.UnmanagedCount,
            CleanCount: report.CleanCount,
            TotalCritical: report.TotalCritical,
            TotalHigh: report.TotalHigh,
            Warnings: report.Warnings,
            Images: report.Images.Select(SupplyChainImageResponse.From));
}

/// <summary>
/// One running image's scan standing.
///
/// <para>⚠️ <b>The three counts come from a NULLABLE scan and are 0 when there is none.</b>
/// <c>CriticalCount</c>, <c>HighCount</c> and <c>FixableCount</c> are <c>Scan?.X ?? 0</c> on the
/// source, so an image that was never scanned reports <c>critical: 0</c> — which is NOT "no
/// critical vulnerabilities". <c>state</c> is the field that distinguishes them:
/// <c>Unscanned</c> means unknown, <c>Clean</c> means measured and clear. A client that sorts by
/// <c>critical</c> puts every unscanned image beside the genuinely clean ones.</para>
///
/// <para>⚠️ <b><c>note</c> is NOT published here</b>, although <c>RunningImage.Note</c> exists and
/// explains why an image is <c>Unscanned</c> or <c>Unmanaged</c> — exactly what
/// <see cref="DriftResultResponse"/> publishes its own <c>note</c> for. Reproduced as shipped. The
/// consequence is that the one field which would explain a 0 is the one a caller cannot see.</para>
///
/// <para><c>RunningImage</c> also carries <c>ClusterId</c>, <c>Scan</c>, <c>Registry</c>,
/// <c>Project</c>, <c>Repository</c> and <c>Reference</c>; none has ever been on the wire.</para>
/// </summary>
public sealed record SupplyChainImageResponse(
    string Image,
    string Cluster,
    string State,
    int Critical,
    int High,
    int Fixable,
    bool DigestPinned,
    IReadOnlyList<string> Workloads)
{
    /// <summary>
    /// ⚠️ <b>Named arguments.</b> <c>Critical</c>, <c>High</c> and <c>Fixable</c> are three
    /// adjacent <c>int</c>s on three different meanings — two severities and a remediability — so
    /// a positional swap would compile and would report fixable findings as critical ones.
    ///
    /// <para>⚠️ <c>digestPinned</c> drops the <c>Is</c> prefix, which is the opposite of
    /// <see cref="ErrorBudgetResponse.IsBreached"/> keeping it. Both are the shipped spellings;
    /// the renames are per-route history, not a house rule.</para>
    /// </summary>
    public static SupplyChainImageResponse From(
        EntKube.Web.Services.SupplyChain.RunningImage image) =>
        new(
            Image: image.Image,
            Cluster: image.ClusterName,
            // ⚠️ By NAME: Vulnerable is ordinal 0.
            State: image.State.ToString(),
            Critical: image.CriticalCount,
            High: image.HighCount,
            Fixable: image.FixableCount,
            DigestPinned: image.IsDigestPinned,
            Workloads: image.Workloads);
}

/// <summary>
/// One watched release and the verdict its analysis window reached.
///
/// <para>⚠️ <b><c>status</c> is a string, not the enum.</b> <c>RolloutVerdictStatus.Watching</c> is
/// ordinal <b>0</b>, so typing this member as the enum would publish <c>"status": 0</c> for every
/// release still inside its window — the most common value on the route, and the one a client
/// polling for an outcome reads. The handler called <c>.ToString()</c>; this keeps that.
/// <c>ApiRolloutStatusTests</c> pins the seven strings themselves against a hard-coded list, which
/// matters because the two sibling tests beside it compare the contract enum to the <em>entity</em>
/// enum and so agree happily on a rename applied to both.</para>
///
/// <para>⚠️ <b>Seven adjacent strings.</b> <c>App</c>, <c>Deployment</c>, <c>Environment</c>,
/// <c>Cluster</c>, <c>Status</c>, <c>Verdict</c> and <c>TriggeredBy</c> all carry text, so any
/// positional swap compiles and publishes a cluster name as a verdict. Constructed by name below
/// for that reason.</para>
///
/// <para><c>finishedAt</c> is null while the window is still open and <c>verdict</c> is null until
/// the analyser reaches a sentence, so a row can legitimately carry <c>Watching</c> with both
/// absent. That is not a missing value to retry for.</para>
/// </summary>
public sealed record RolloutResponse(
    Guid Id,
    string App,
    string Deployment,
    string Environment,
    string Cluster,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? Verdict,
    string? TriggeredBy)
{
    public static RolloutResponse From(EntKube.Contracts.Delivery.RolloutSummary rollout) =>
        new(
            Id: rollout.Id,
            App: rollout.AppName,
            Deployment: rollout.DeploymentName,
            Environment: rollout.EnvironmentName,
            Cluster: rollout.ClusterName,
            // ⚠️ By NAME: Watching is ordinal 0.
            Status: rollout.Status.ToString(),
            StartedAt: rollout.StartedAt,
            FinishedAt: rollout.FinishedAt,
            Verdict: rollout.Verdict,
            TriggeredBy: rollout.TriggeredBy);
}

/// <summary>
/// One cluster's disaster-recovery posture: whether a restore is possible, when it was last
/// possible, and what stands between the cluster and that being true.
///
/// <para>⚠️ <b><c>restorable</c> is NOT "a backup finished".</b> It is
/// <c>LastUsableBackup is not null</c>, and <c>VeleroBackup.IsUsable</c> excludes
/// <c>PartiallyFailed</c> deliberately — Velero stamps those with a completion time although they
/// skipped resources, so anything that merely asks "did it finish" reports an unrestorable backup
/// as protection. The distinction is the whole point of the field.</para>
///
/// <para>⚠️ <b><c>restorable: true</c> with <c>lastUsableBackupAt: null</c> is reachable.</b>
/// <c>IsUsable</c> requires a clean <c>Completed</c> phase and no errors; it does not require a
/// <c>CompletedAt</c>. A client that infers protection from the timestamp rather than from the
/// flag therefore reads a protected cluster as unprotected, and the CLI's <c>LAST BACKUP</c>
/// column renders a dash beside a <c>RESTORABLE</c> of yes. Reproduced as shipped.</para>
///
/// <para>⚠️ <b><c>gaps</c> is computed, not stored</b> — <c>DrReadiness.Evaluate</c> is run
/// against a clock, so two reads of the same cached scan differ as a backup ages past
/// <c>StaleAfter</c>. That is why <see cref="From"/> takes the instant as an argument rather than
/// reading <c>DateTime.UtcNow</c> itself: one timestamp must serve the whole page, or two clusters
/// in one response are judged against two different "now"s.</para>
///
/// <para><c>ClusterDrStatus</c> also carries <c>Backups</c>, <c>Restores</c>,
/// <c>StorageLocations</c> and the <c>Schedules</c> themselves; only the schedule <em>count</em>
/// has ever been on the wire. An operator asking which schedule is paused has to open the UI.</para>
/// </summary>
public sealed record DrStatusResponse(
    Guid ClusterId,
    string Cluster,
    bool VeleroInstalled,
    string? Error,
    bool Restorable,
    DateTime? LastUsableBackupAt,
    DateTime? LastRestoreAt,
    int ScheduleCount,
    IEnumerable<DrGapResponse> Gaps)
{
    /// <summary>
    /// ⚠️ <b>Named arguments, and here two <c>bool</c>s sit beside each other on opposite
    /// meanings.</b> <c>VeleroInstalled</c> says the machinery is present; <c>Restorable</c> says
    /// it has produced something usable. A cluster with Velero installed and no clean backup is
    /// the normal first state, so swapping the two reports an unprotected cluster as protected —
    /// and both orderings compile.
    /// </summary>
    /// <param name="now">
    /// The instant every gap on the response is judged against. Passed in so one request's
    /// clusters are compared to one clock.
    /// </param>
    public static DrStatusResponse From(
        EntKube.Web.Services.Dr.ClusterDrStatus status, DateTime now) =>
        new(
            ClusterId: status.ClusterId,
            Cluster: status.ClusterName,
            VeleroInstalled: status.IsVeleroInstalled,
            Error: status.Error,
            // "restorable" reflects a clean backup only — a partially-failed backup has
            // skipped resources and is not something to report as recoverable.
            Restorable: status.LastUsableBackup is not null,
            LastUsableBackupAt: status.LastUsableBackup?.CompletedAt,
            LastRestoreAt: status.LastSuccessfulRestore?.CompletedAt,
            ScheduleCount: status.Schedules.Count,
            Gaps: EntKube.Web.Services.Dr.DrReadiness.Evaluate(status, now)
                .Select(DrGapResponse.From));
}

/// <summary>
/// One thing wrong with a cluster's disaster-recovery posture.
///
/// <para>⚠️ <b><c>severity</c> is a string, not the enum, and <c>DrSeverity.Critical</c> is
/// ordinal <b>0</b></b> — "there is no usable backup; a cluster loss right now is
/// unrecoverable". Publishing the number would put <c>0</c> on the single most consequential
/// finding this API can report, where a reader scanning for trouble expects a word and a sorter
/// would place it first by accident rather than by meaning.</para>
///
/// <para>⚠️ <b>Five adjacent strings.</b> <c>Key</c>, <c>Severity</c>, <c>Title</c>,
/// <c>Detail</c> and <c>Remediation</c>, so every positional mistake compiles. <c>Key</c> is the
/// stable identity the advisor snoozes against, which makes a swap between it and <c>Title</c>
/// quietly un-snooze every finding.</para>
/// </summary>
public sealed record DrGapResponse(
    string Key,
    string Severity,
    string Title,
    string Detail,
    string? Remediation)
{
    public static DrGapResponse From(EntKube.Web.Services.Dr.DrGap gap) =>
        new(
            Key: gap.Key,
            // ⚠️ By NAME: Critical is ordinal 0.
            Severity: gap.Severity.ToString(),
            Title: gap.Title,
            Detail: gap.Detail,
            Remediation: gap.Remediation);
}

// ── Tickets (docs/helpdesk.md, phase 1b) ─────────────────────────────────────────────────
//
// Mapped by hand from the Support contract's records, for the reason ClusterRateResponse is:
// a published wire shape should not move because an internal record gained a field.

/// <summary>One of a ticket's agreement clocks.</summary>
/// <param name="Deadline">When it runs out; null when this priority sets no target.</param>
/// <param name="MetAt">When it was met, if it was.</param>
/// <param name="Breached">Missed — met late, or still open past the deadline.</param>
/// <param name="Paused">Stopped while another party is waited on (§14.4).</param>
public sealed record TicketClockResponse(DateTime? Deadline, DateTime? MetAt, bool Breached, bool Paused)
{
    public static TicketClockResponse From(EntKube.Contracts.Support.TicketClock c) =>
        new(c.Deadline, c.MetAt, c.Breached, c.Paused);
}

/// <summary>A ticket as the queue lists it.</summary>
public sealed record TicketSummaryResponse(
    Guid Id,
    int Number,
    string Title,
    string Status,
    string Priority,
    bool PriorityConfirmed,
    Guid CustomerId,
    string Customer,
    string? Assignee,
    IEnumerable<string> Tags,
    DateTime ReportedAt,
    DateTime? ReminderAt,
    TicketClockResponse Response,
    TicketClockResponse Resolution,
    DateTime? UpdateDue,
    bool PenaltyDeviation)
{
    public static TicketSummaryResponse From(EntKube.Contracts.Support.TicketSummary t) =>
        new(t.Id, t.Number, t.Title, t.Status, t.Priority, t.PriorityConfirmed, t.CustomerId, t.Customer,
            t.Assignee, [.. t.Tags], t.ReportedAt, t.ReminderAt,
            TicketClockResponse.From(t.Response), TicketClockResponse.From(t.Resolution),
            t.UpdateDue, t.PenaltyDeviation);
}

/// <summary>One entry on a ticket's history.</summary>
/// <param name="FromMergedNumber">The duplicate it came from, when it was merged into this ticket.</param>
public sealed record TicketEntryResponse(
    Guid Id, DateTime At, string Kind, string? Actor, string Detail, bool CustomerVisible, int? FromMergedNumber)
{
    public static TicketEntryResponse From(EntKube.Contracts.Support.TicketEntry e) =>
        new(e.Id, e.At, e.Kind, e.Actor, e.Detail, e.CustomerVisible, e.FromMergedNumber);
}

/// <summary>A link to another ticket, seen from this one.</summary>
/// <param name="Role">What the other ticket is to this one: related, parent or child.</param>
public sealed record TicketLinkResponse(Guid LinkId, string Role, int Number, string Title, string Status)
{
    public static TicketLinkResponse From(EntKube.Contracts.Support.TicketLinkSummary l) =>
        new(l.LinkId, l.Role, l.Number, l.Title, l.Status);
}

/// <summary>One ticket in full: the summary, its description, links and whole history.</summary>
/// <param name="MergedInto">For a merged duplicate, the number of the ticket it went into.</param>
public sealed record TicketResponse(
    TicketSummaryResponse Ticket,
    string Description,
    string? ReminderNote,
    DateTime? AutoCloseAt,
    int? MergedInto,
    IEnumerable<TicketLinkResponse> Links,
    IEnumerable<TicketEntryResponse> History)
{
    public static TicketResponse From(EntKube.Contracts.Support.TicketView v) =>
        new(TicketSummaryResponse.From(v.Summary), v.Description, v.ReminderNote, v.AutoCloseAt, v.MergedInto,
            [.. v.Links.Select(TicketLinkResponse.From)], [.. v.History.Select(TicketEntryResponse.From)]);
}

/// <summary>A ticket's tags after a change.</summary>
public sealed record TicketTagsResponse(int Number, IEnumerable<string> Tags);

/// <summary>What a merge did.</summary>
/// <param name="RecordedAsResponse">The duplicate had no response; the merge became its first.</param>
/// <param name="ResponseWasLate">That response, or an earlier one, missed its deadline. A merge never erases that.</param>
public sealed record TicketMergeResponse(int Duplicate, int Original, bool RecordedAsResponse, bool ResponseWasLate);
