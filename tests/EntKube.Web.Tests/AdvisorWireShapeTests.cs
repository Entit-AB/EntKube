using System.Text.Json;
using EntKube.Contracts.Telemetry;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;
using AdvisorCategory = EntKube.Contracts.Advisor.FindingCategory;
using AdvisorHorizon = EntKube.Contracts.Advisor.FindingHorizon;
using AdvisorSeverity = EntKube.Contracts.Advisor.FindingSeverity;
using AdvisorState = EntKube.Contracts.Advisor.FindingStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /advisor/findings</c>, <c>GET /incidents</c> and <c>GET /error-budgets</c> serialise
/// exactly as the anonymous objects they replaced.
///
/// <para>⚠️ <b>The oracles are the OLD code, cut out of <c>origin/main</c> rather than
/// retyped.</b> A record's mapping and its expected values written from one reading of the handler
/// would agree on a consistent misreading of it.</para>
///
/// <para>⚠️ <b>The four Advisor enums are ALIASED, not imported.</b>
/// <c>EntKube.Contracts.Advisor.FindingSeverity</c> and
/// <c>EntKube.Web.Services.FindingSeverity</c> are two different enums that publish different
/// words at the same ordinal — <c>Critical</c> at 0 against <c>Error</c> at 0 — so a cast between
/// them is silent mislabelling. The compiler catches the ambiguity only in a file that imports
/// BOTH; importing one and meaning the other compiles quietly. Aliasing makes the choice explicit,
/// and the hard-coded expectations below would fail anyway, which is the belt to that
/// braces.</para>
///
/// <para>⚠️ <b>Every enum expectation is literal JSON text.</b> <c>AdvisorContractEnumTests</c>
/// and <c>ModuleApiTests</c> hold these enums to their entity declarations by comparing the two
/// <b>to each other</b>, so a rename applied to both sides stays green while every client's wire
/// changes. Writing <c>severity.ToString()</c> here would inherit exactly that blindness: it
/// would be the production expression compared against itself.</para>
/// </summary>
public class AdvisorWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    private static readonly Guid ClusterId = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid CustomerId = Guid.Parse("c1000000-0000-0000-0000-00000000000d");
    private static readonly Guid DeploymentId = Guid.Parse("d0000000-0000-0000-0000-00000000000e");

    /// <summary>
    /// ⚠️ Every nullable member is SET and every string differs, because the point is a byte
    /// comparison: a null on both sides proves nothing about whether the right member was read.
    /// The four enum members are four DIFFERENT ones, and none is its type's zero — a fixture on
    /// the defaults would pass even if the factory read the wrong enum.
    /// </summary>
    private static EntKube.Contracts.Advisor.FindingSummary Finding() => new(
        Id: "cert:11111111-1111-1111-1111-111111111111",
        Category: AdvisorCategory.Capacity,
        Severity: AdvisorSeverity.Warning,
        Horizon: AdvisorHorizon.ThisWeek,
        Title: "A certificate expires in six days",
        Detail: "wildcard.example.com",
        ScopeLabel: "prod-north / istio-system",
        DueAt: new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc),
        Remediation: "Renew it, or let cert-manager renew it",
        Source: "certificates",
        State: AdvisorState.Acknowledged,
        ClusterId: ClusterId,
        CustomerName: "Acme AB");

    /// <summary>
    /// ⚠️ <c>Severity</c> is free text here on purpose — <c>"page"</c>, as an alert rule would
    /// write it — while <c>Status</c> is an enum. The two are adjacent strings on the record, and
    /// a fixture whose severity looked like an enum member would make a transposition read as
    /// plausible data.
    /// </summary>
    private static IncidentSummary Incident() => new(
        Id: Guid.Parse("a0000000-0000-0000-0000-00000000000a"),
        ClusterId: ClusterId,
        ClusterName: "prod-north",
        AlertName: "KubePodCrashLooping",
        Summary: "billing-api has restarted 14 times in 10 minutes",
        Severity: "page",
        Status: IncidentStatus.Acknowledged,
        StartsAt: new DateTime(2026, 11, 12, 6, 30, 0, DateTimeKind.Utc),
        ResolvedAt: new DateTime(2026, 11, 12, 7, 15, 0, DateTimeKind.Utc),
        AcknowledgedBy: "nils",
        RunbookUrl: "https://runbooks.example.com/crashloop");

    /// <summary>
    /// ⚠️ All fourteen fields hold distinct values, and the four <c>double?</c>s are on four
    /// different scales so a transposition among them is visible: 99.82 is a percentage, 1.8 a
    /// fraction, 2.4 a ratio, 36.5 a count of hours.
    /// </summary>
    private static ErrorBudgetSummary Budget() => new(
        DeploymentId: DeploymentId,
        DeploymentName: "billing-api-prod",
        AppName: "billing",
        CustomerId: CustomerId,
        CustomerName: "Acme AB",
        ClusterId: ClusterId,
        TargetPercent: 99.9d,
        WindowDays: 30,
        AchievedPercent: 99.82d,
        BudgetConsumedFraction: 1.8d,
        BurnRateRecent: 2.4d,
        HoursToExhaustion: 36.5d,
        HasData: true,
        IsBreached: false);

    [Fact]
    public void A_finding_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        EntKube.Contracts.Advisor.FindingSummary f = Finding();

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            id = f.Id,
            category = f.Category.ToString(),
            severity = f.Severity.ToString(),
            horizon = f.Horizon.ToString(),
            title = f.Title,
            detail = f.Detail,
            scope = f.ScopeLabel,
            dueAt = f.DueAt,
            remediation = f.Remediation,
            source = f.Source,
            state = f.State.ToString(),
            clusterId = f.ClusterId,
            customer = f.CustomerName,
        };

        JsonSerializer.Serialize(AdvisorFindingResponse.From(f), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void An_incident_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        IncidentSummary i = Incident();

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            id = i.Id,
            alertName = i.AlertName,
            summary = i.Summary,
            severity = i.Severity,
            status = i.Status.ToString(),
            cluster = i.ClusterName,
            startsAt = i.StartsAt,
            resolvedAt = i.ResolvedAt,
            acknowledgedBy = i.AcknowledgedBy,
            runbookUrl = i.RunbookUrl,
        };

        JsonSerializer.Serialize(IncidentResponse.From(i), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void The_error_budget_report_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        List<ErrorBudgetSummary> budgets = [Budget()];

        // Copied verbatim from origin/main's handler, envelope and item together.
        object asItWas = new
        {
            deployments = budgets.Count,
            withData = budgets.Count(b => b.HasData),
            breached = budgets.Count(b => b.IsBreached),
            atRisk = budgets.Count(b => !b.IsBreached && b.BurnRateRecent > 1.0),
            budgets = budgets.Select(b => new
            {
                deploymentId = b.DeploymentId,
                deployment = b.DeploymentName,
                app = b.AppName,
                customerId = b.CustomerId,
                customer = b.CustomerName,
                clusterId = b.ClusterId,
                targetPercent = b.TargetPercent,
                windowDays = b.WindowDays,
                achievedPercent = b.AchievedPercent,
                budgetConsumedFraction = b.BudgetConsumedFraction,
                burnRateRecent = b.BurnRateRecent,
                hoursToExhaustion = b.HoursToExhaustion,
                hasData = b.HasData,
                isBreached = b.IsBreached,
            }),
        };

        JsonSerializer.Serialize(ErrorBudgetReportResponse.From(budgets), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named as well as compared, so a failure says WHICH key moved — and in key order, which is
    /// what a client reading the JSON sees.
    /// </summary>
    [Fact]
    public void The_keys_are_the_ones_clients_already_read()
    {
        Keys(AdvisorFindingResponse.From(Finding())).Should().Equal(
            "id", "category", "severity", "horizon", "title", "detail", "scope", "dueAt",
            "remediation", "source", "state", "clusterId", "customer");

        Keys(IncidentResponse.From(Incident())).Should().Equal(
            "id", "alertName", "summary", "severity", "status", "cluster", "startsAt",
            "resolvedAt", "acknowledgedBy", "runbookUrl");

        Keys(ErrorBudgetReportResponse.From([Budget()])).Should().Equal(
            "deployments", "withData", "breached", "atRisk", "budgets");

        System.Text.Json.Nodes.JsonObject envelope =
            JsonSerializer.SerializeToNode(
                ErrorBudgetReportResponse.From([Budget()]), AsTheApiSerialises)!.AsObject();

        envelope["budgets"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "deploymentId", "deployment", "app", "customerId", "customer", "clusterId",
            "targetPercent", "windowDays", "achievedPercent", "budgetConsumedFraction",
            "burnRateRecent", "hoursToExhaustion", "hasData", "isBreached");
    }

    private static IEnumerable<string> Keys<T>(T value) =>
        JsonSerializer.SerializeToNode(value, AsTheApiSerialises)!.AsObject().Select(p => p.Key);

    /// <summary>
    /// ⚠️ <b><c>clusterId</c> is on the finding and NOT on the incident</b>, although
    /// <c>IncidentSummary</c> carries one. The anonymous object never emitted it, so adding it
    /// would change the document rather than describe it — asserted against the fixture's
    /// recognisable Guid so the failure names what leaked.
    /// </summary>
    [Fact]
    public void The_incident_does_not_publish_its_cluster_id()
    {
        string incident = JsonSerializer.Serialize(
            IncidentResponse.From(Incident()), AsTheApiSerialises);

        incident.Should().Contain("prod-north", "the cluster is published by NAME");
        incident.Should().NotContain(
            ClusterId.ToString(),
            "and not by id — IncidentSummary has one, but this route has never sent it");

        JsonSerializer.Serialize(AdvisorFindingResponse.From(Finding()), AsTheApiSerialises)
            .Should().Contain(
                ClusterId.ToString(),
                "whereas the finding DOES publish clusterId, which is why this pair is asserted "
                + "together rather than one of them alone");
    }

    /// <summary>
    /// ⚠️ The renames, asserted as VALUES. The key lists would be satisfied by a mapping that put
    /// the right names on the wrong members, and on the finding eleven of thirteen parameters are
    /// strings.
    /// </summary>
    [Fact]
    public void The_renamed_fields_carry_the_value_their_source_member_holds()
    {
        AdvisorFindingResponse finding = AdvisorFindingResponse.From(Finding());

        finding.Scope.Should().Be("prod-north / istio-system", "`scope` is ScopeLabel");
        finding.Title.Should().Be("A certificate expires in six days", "and not the scope");
        finding.Customer.Should().Be("Acme AB", "`customer` is CustomerName");
        finding.Source.Should().Be("certificates", "`source` is the rule that raised it");
        finding.Remediation.Should().Be(
            "Renew it, or let cert-manager renew it", "and not the source, which is adjacent");

        IncidentResponse incident = IncidentResponse.From(Incident());

        incident.Cluster.Should().Be("prod-north", "`cluster` is ClusterName");
        incident.AlertName.Should().Be("KubePodCrashLooping");
        incident.Summary.Should().Be(
            "billing-api has restarted 14 times in 10 minutes", "and not the alert name");

        ErrorBudgetResponse budget = ErrorBudgetReportResponse.From([Budget()]).Budgets.Single();

        budget.Deployment.Should().Be("billing-api-prod", "`deployment` is DeploymentName");
        budget.App.Should().Be("billing", "`app` is AppName, not the deployment");
        budget.Customer.Should().Be("Acme AB");

        // ⚠️ The four doubles, which are the transposition that would ship a wrong SLA number.
        budget.TargetPercent.Should().Be(99.9d);
        budget.AchievedPercent.Should().Be(99.82d, "a percentage");
        budget.BudgetConsumedFraction.Should().Be(1.8d, "a fraction of the budget, not a percent");
        budget.BurnRateRecent.Should().Be(2.4d, "a ratio");
        budget.HoursToExhaustion.Should().Be(36.5d, "hours — a different scale again");
    }

    /// <summary>
    /// ⚠️ <b>Hard-coded expectations.</b> See the class summary: both enum-parity tests compare
    /// two declarations to each other, so only literal text catches a coordinated rename. A number
    /// here would break the CLI's SEVERITY, WHEN and CATEGORY columns.
    /// </summary>
    [Theory]
    [InlineData(AdvisorSeverity.Critical, "Critical")]
    [InlineData(AdvisorSeverity.Warning, "Warning")]
    [InlineData(AdvisorSeverity.Info, "Info")]
    public void A_finding_severity_is_published_by_name_in_its_own_case(
        AdvisorSeverity severity, string expected)
    {
        AdvisorFindingResponse response =
            AdvisorFindingResponse.From(Finding() with { Severity = severity });

        response.Severity.Should().Be(expected);

        JsonSerializer.Serialize(response, AsTheApiSerialises)
            .Should().Contain($"\"severity\":\"{expected}\"");
    }

    /// <summary>
    /// ⚠️ <c>Critical</c> is this enum's ZERO member, which is also where the connectivity
    /// analyzer's unrelated <c>FindingSeverity</c> has <c>Error</c>. If the factory were ever
    /// handed the wrong enum, the ordinals would line up and nothing would fail — except this.
    /// </summary>
    [Fact]
    public void The_zero_severity_publishes_Critical_and_not_Error()
    {
        AdvisorFindingResponse.From(Finding() with { Severity = (AdvisorSeverity)0 })
            .Severity.Should().Be("Critical");
    }

    [Theory]
    [InlineData(AdvisorCategory.Security, "Security")]
    [InlineData(AdvisorCategory.Reliability, "Reliability")]
    [InlineData(AdvisorCategory.DataProtection, "DataProtection")]
    [InlineData(AdvisorCategory.Capacity, "Capacity")]
    [InlineData(AdvisorCategory.Maintenance, "Maintenance")]
    [InlineData(AdvisorCategory.SupplyChain, "SupplyChain")]
    public void A_finding_category_is_published_by_name_in_its_own_case(
        AdvisorCategory category, string expected)
    {
        JsonSerializer.Serialize(
            AdvisorFindingResponse.From(Finding() with { Category = category }), AsTheApiSerialises)
            .Should().Contain($"\"category\":\"{expected}\"");
    }

    [Theory]
    [InlineData(AdvisorHorizon.Overdue, "Overdue")]
    [InlineData(AdvisorHorizon.Today, "Today")]
    [InlineData(AdvisorHorizon.ThisWeek, "ThisWeek")]
    [InlineData(AdvisorHorizon.Later, "Later")]
    public void A_finding_horizon_is_published_by_name_in_its_own_case(
        AdvisorHorizon horizon, string expected)
    {
        JsonSerializer.Serialize(
            AdvisorFindingResponse.From(Finding() with { Horizon = horizon }), AsTheApiSerialises)
            .Should().Contain($"\"horizon\":\"{expected}\"");
    }

    [Theory]
    [InlineData(AdvisorState.Active, "Active")]
    [InlineData(AdvisorState.Acknowledged, "Acknowledged")]
    [InlineData(AdvisorState.Snoozed, "Snoozed")]
    [InlineData(AdvisorState.Dismissed, "Dismissed")]
    public void A_finding_state_is_published_by_name_in_its_own_case(
        AdvisorState state, string expected)
    {
        JsonSerializer.Serialize(
            AdvisorFindingResponse.From(Finding() with { State = state }), AsTheApiSerialises)
            .Should().Contain($"\"state\":\"{expected}\"");
    }

    [Theory]
    [InlineData(IncidentStatus.Active, "Active")]
    [InlineData(IncidentStatus.Acknowledged, "Acknowledged")]
    [InlineData(IncidentStatus.Resolved, "Resolved")]
    public void An_incident_status_is_published_by_name_in_its_own_case(
        IncidentStatus status, string expected)
    {
        IncidentResponse response = IncidentResponse.From(Incident() with { Status = status });

        response.Status.Should().Be(expected);

        JsonSerializer.Serialize(response, AsTheApiSerialises)
            .Should().Contain($"\"status\":\"{expected}\"");
    }

    /// <summary>
    /// ⚠️ <c>severity</c> on an INCIDENT is free text and must survive verbatim — including a
    /// spelling no enum would produce. Flattening it to a tidy member name would silently drop
    /// whatever the alert rule actually wrote.
    /// </summary>
    [Theory]
    [InlineData("page")]
    [InlineData("warning")]
    [InlineData("P1 — wake someone")]
    public void An_incident_severity_is_carried_through_verbatim(string severity)
    {
        IncidentResponse.From(Incident() with { Severity = severity })
            .Severity.Should().Be(severity);
    }

    /// <summary>
    /// ⚠️ <b>The four counters, each landing on a DIFFERENT number.</b> They are computed over the
    /// list and cannot be set, so the fixture has to make each one distinct — otherwise a factory
    /// that returned the same count four times would pass.
    ///
    /// <para>⚠️ And the one that matters: <c>atRisk</c>'s predicate is
    /// <c>!IsBreached &amp;&amp; BurnRateRecent &gt; 1.0</c> over a <c>double?</c>. That is a
    /// LIFTED comparison and is <b>false</b> when the burn rate is null, so a deployment with no
    /// samples is in neither <c>breached</c> nor <c>atRisk</c>. <c>deployments</c> is therefore
    /// NOT their sum, and a client computing "healthy" by subtraction counts the unknowns as
    /// healthy.</para>
    /// </summary>
    [Fact]
    public void The_four_counters_are_computed_and_a_null_burn_rate_is_in_neither_set()
    {
        List<ErrorBudgetSummary> budgets =
        [
            // breached, so excluded from atRisk however fast it is burning
            Budget() with { IsBreached = true, BurnRateRecent = 9d, HasData = true },
            // in credit and burning too fast: atRisk
            Budget() with { IsBreached = false, BurnRateRecent = 2.4d, HasData = true },
            Budget() with { IsBreached = false, BurnRateRecent = 1.5d, HasData = true },
            // in credit and sustainable: neither
            Budget() with { IsBreached = false, BurnRateRecent = 0.2d, HasData = true },
            // ⚠️ no samples: a NULL burn rate, so the lifted `> 1.0` is false and this row is in
            // neither counter — and HasData is false, which is the only field that says so.
            Budget() with { IsBreached = false, BurnRateRecent = null, HasData = false },
        ];

        ErrorBudgetReportResponse report = ErrorBudgetReportResponse.From(budgets);

        report.Deployments.Should().Be(5, "every row, measurable or not");
        report.WithData.Should().Be(4, "the rows with samples in the window");
        report.Breached.Should().Be(1);
        report.AtRisk.Should().Be(2, "in credit AND burning above 1.0");

        (report.Breached + report.AtRisk).Should().NotBe(
            report.Deployments,
            "the two sets are disjoint and do not cover the list — this is the arithmetic a "
            + "caller gets wrong by computing healthy = deployments - breached - atRisk");
    }

    /// <summary>
    /// ⚠️ A budget exactly spent MEETS its objective. <c>IsBreached</c> comes off the source,
    /// whose own doc records that this was once <c>&gt;=</c> and reported a breach for
    /// availability exactly equal to target — so a burn rate of exactly 1.0 is not at risk
    /// either, because the predicate is strictly greater.
    /// </summary>
    [Fact]
    public void A_burn_rate_of_exactly_one_is_not_at_risk()
    {
        ErrorBudgetReportResponse report = ErrorBudgetReportResponse.From(
            [Budget() with { IsBreached = false, BurnRateRecent = 1.0d, HasData = true }]);

        report.AtRisk.Should().Be(
            0, "strictly greater than 1.0 — sustainable exactly is still sustainable");
    }

    /// <summary>
    /// ⚠️ A nullable member publishes <b>null</b>; it is not omitted and it is not 0. Four of the
    /// budget's fields are <c>double?</c> precisely so that "no measurement" is distinguishable
    /// from "zero availability", and a client rendering the latter reports a total outage for a
    /// deployment registered yesterday.
    /// </summary>
    [Fact]
    public void A_budget_with_no_samples_publishes_nulls_and_not_zeroes()
    {
        string json = JsonSerializer.Serialize(
            ErrorBudgetResponse.From(Budget() with
            {
                AchievedPercent = null,
                BudgetConsumedFraction = null,
                BurnRateRecent = null,
                HoursToExhaustion = null,
                HasData = false,
            }),
            AsTheApiSerialises);

        json.Should().Contain("\"achievedPercent\":null");
        json.Should().Contain("\"budgetConsumedFraction\":null");
        json.Should().Contain("\"burnRateRecent\":null");
        json.Should().Contain("\"hoursToExhaustion\":null");
        json.Should().NotContain(":0,", "a zero here would read as a total outage");
    }

    /// <summary>
    /// ⚠️ A finding on the radar has no deadline, and an installation-wide one belongs to no
    /// cluster and no customer. All four keys must still be WRITTEN.
    /// </summary>
    [Fact]
    public void An_installation_wide_finding_publishes_nulls()
    {
        string json = JsonSerializer.Serialize(
            AdvisorFindingResponse.From(Finding() with
            {
                Detail = null,
                DueAt = null,
                Remediation = null,
                ClusterId = null,
                CustomerName = null,
            }),
            AsTheApiSerialises);

        json.Should().Contain("\"detail\":null");
        json.Should().Contain("\"dueAt\":null");
        json.Should().Contain("\"remediation\":null");
        json.Should().Contain("\"clusterId\":null");
        json.Should().Contain("\"customer\":null");
    }
}
