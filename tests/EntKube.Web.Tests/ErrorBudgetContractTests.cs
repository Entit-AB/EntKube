using EntKube.Contracts.Delivery;
using EntKube.Contracts.Support;
using EntKube.Contracts.Telemetry;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The error-budget figures reach the public API's contract in the right fields.
///
/// <para><b>What was missing.</b> Nothing on the public API could answer "are we meeting the
/// SLAs" — a search for budget, availability, uptime, sla and maintenance across the whole
/// endpoint file returned nothing. The figure a support agreement is invoiced against was
/// readable in a browser and nowhere else, so no CI gate, status page or ticketing system could
/// ask for it.</para>
///
/// <para>⚠️ <b>Why a value test and not a shape test.</b> The arithmetic is already covered by
/// <c>ErrorBudgetWindowTests</c> and <c>ErrorBudgetRoundingTests</c>; what is new here is a
/// 14-field hand-written mapping from <see cref="ErrorBudgetStatus"/> to
/// <see cref="ErrorBudgetSummary"/>. A test comparing the two records' field NAMES would pass
/// happily while <c>BurnRateRecent</c> and <c>BudgetConsumedFraction</c> were swapped — and this
/// repo's defects are overwhelmingly wrong numbers in untested customer-facing figures, not wrong
/// structure. So the scenario below is built so that <b>every one of the six numbers is
/// distinct</b>: 99.0, 30, 99.5, 0.5, 2.0, 180.0. Any transposition changes a value.</para>
/// </summary>
public class ErrorBudgetContractTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid DeploymentId = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Guid ClusterId = Guid.NewGuid();

    /// <summary>
    /// A tenant with one managed deployment, a 99.0% objective over 30 days, 99.5% availability
    /// across the window and 98.0% over the last day.
    ///
    /// <para>Which makes the expected figures, from the objective's own arithmetic: an allowance
    /// of 1.0 percentage points, so 0.5 points missed is <b>half</b> the budget spent, and the
    /// last day's 2.0 points missed is a burn rate of <b>2.0</b> — twice sustainable. Half a
    /// budget left, burning at double, over 30 days, exhausts in
    /// <c>0.5 × 30 × 24 ÷ 2.0 = 180</c> hours.</para>
    /// </summary>
    private static ITelemetryApi Subject()
    {
        Mock<IDeliveryApi> delivery = new(MockBehavior.Strict);
        Mock<ISupportApi> support = new(MockBehavior.Strict);

        delivery.Setup(d => d.GetDeploymentsForTenantAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DeploymentSummary(
                DeploymentId, Tenant, AppId, "Billing", Guid.NewGuid(), "prod",
                ClusterId, "prod-cluster", "billing-api", DeploymentType.HelmChart, "billing",
                SyncStatus.Synced, HealthStatus.Healthy, null, null, IsManaged: true)]);

        delivery.Setup(d => d.GetAppsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AppSummary(AppId, Tenant, CustomerId, "Acme AB", "Billing", "billing")]);

        support.Setup(s => s.GetSlaTargetsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SlaTargetSummary(CustomerId, null, TargetPercent: 99.0, MeasurementWindowDays: 30)]);

        // ⚠️ The two health calls are told apart by their `from`, which is the only thing that
        // distinguishes them: the 30-day SLA window and the fixed one-day burn window are the
        // same method with the same ids. Matching on anything looser would let one answer both
        // and the burn rate would silently equal the consumed fraction — the exact collapse this
        // test exists to detect.
        delivery.Setup(d => d.GetHealthRatiosAsync(
                Tenant, It.IsAny<IReadOnlyCollection<Guid>>(),
                It.Is<DateTime>(from => from < DateTime.UtcNow.AddDays(-2)),
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DeploymentHealthRatio(DeploymentId, HealthySamples: 1990, TotalSamples: 2000)]);

        delivery.Setup(d => d.GetHealthRatiosAsync(
                Tenant, It.IsAny<IReadOnlyCollection<Guid>>(),
                It.Is<DateTime>(from => from >= DateTime.UtcNow.AddDays(-2)),
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DeploymentHealthRatio(DeploymentId, HealthySamples: 490, TotalSamples: 500)]);

        return new TelemetryApi(
            dbFactory: null!,   // GetErrorBudgetsAsync never touches Telemetry's own context
            errorBudgets: new ErrorBudgetService(delivery.Object, support.Object),
            syntheticChecks: null!);
    }

    [Fact]
    public async Task Every_figure_lands_in_the_field_that_names_it()
    {
        IReadOnlyList<ErrorBudgetSummary> budgets =
            await Subject().GetErrorBudgetsAsync(Tenant, CancellationToken.None);

        ErrorBudgetSummary b = budgets.Should().ContainSingle().Subject;

        b.DeploymentId.Should().Be(DeploymentId);
        b.DeploymentName.Should().Be("billing-api");
        b.AppName.Should().Be("Billing");
        b.ClusterId.Should().Be(ClusterId);

        // The customer comes from the APP, not the deployment — DeploymentSummary carries none.
        b.CustomerId.Should().Be(CustomerId);
        b.CustomerName.Should().Be("Acme AB");

        b.TargetPercent.Should().Be(99.0);
        b.WindowDays.Should().Be(30);
        b.AchievedPercent.Should().Be(99.5);
        b.BudgetConsumedFraction.Should().BeApproximately(0.5, 1e-9);
        b.BurnRateRecent.Should().BeApproximately(2.0, 1e-9);
        b.HoursToExhaustion.Should().BeApproximately(180.0, 1e-6);
    }

    /// <summary>
    /// <c>HasData</c> and <c>IsBreached</c> are COMPUTED on the source record, so they are the two
    /// fields a client cannot re-derive from the numbers it is given — a rounded percentage cannot
    /// reproduce the <c>&gt;</c> versus <c>&gt;=</c> distinction that once reported a met objective
    /// as a breach. If the mapping ever stops carrying them, every caller silently reads false.
    /// </summary>
    [Fact]
    public async Task The_two_computed_verdicts_are_carried_and_not_left_to_the_client()
    {
        ErrorBudgetSummary b = (await Subject().GetErrorBudgetsAsync(Tenant, CancellationToken.None))
            .Should().ContainSingle().Subject;

        b.HasData.Should().BeTrue("there are samples in the window");
        b.IsBreached.Should().BeFalse(
            "half the budget is spent; breached means strictly more than all of it");
    }

    /// <summary>
    /// ⚠️ <b>No samples is UNKNOWN availability, not zero.</b> A client that renders a missing
    /// number as 0 reports a total outage for a deployment registered yesterday — and would page
    /// someone for it. The contract leaves it null and says so; this proves the null survives the
    /// mapping rather than being defaulted on the way through.
    /// </summary>
    [Fact]
    public async Task A_deployment_with_no_samples_reports_unknown_rather_than_zero()
    {
        Mock<IDeliveryApi> delivery = new(MockBehavior.Strict);
        Mock<ISupportApi> support = new(MockBehavior.Strict);

        delivery.Setup(d => d.GetDeploymentsForTenantAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DeploymentSummary(
                DeploymentId, Tenant, AppId, "Billing", Guid.NewGuid(), "prod",
                ClusterId, "prod-cluster", "billing-api", DeploymentType.HelmChart, "billing",
                SyncStatus.Synced, HealthStatus.Unknown, null, null, IsManaged: true)]);

        delivery.Setup(d => d.GetAppsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AppSummary(AppId, Tenant, CustomerId, "Acme AB", "Billing", "billing")]);

        support.Setup(s => s.GetSlaTargetsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SlaTargetSummary(CustomerId, null, 99.0, 30)]);

        // Nothing observed in either window.
        delivery.Setup(d => d.GetHealthRatiosAsync(
                Tenant, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<DateTime>(),
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        ITelemetryApi subject = new TelemetryApi(
            dbFactory: null!, errorBudgets: new ErrorBudgetService(delivery.Object, support.Object), syntheticChecks: null!);

        ErrorBudgetSummary b = (await subject.GetErrorBudgetsAsync(Tenant, CancellationToken.None))
            .Should().ContainSingle().Subject;

        b.AchievedPercent.Should().BeNull();
        b.BudgetConsumedFraction.Should().BeNull();
        b.BurnRateRecent.Should().BeNull();
        b.HasData.Should().BeFalse();
        b.IsBreached.Should().BeFalse(
            "a deployment nobody has measured has not breached anything — reporting it as a "
            + "breach would put an unmeasured deployment on a customer's SLA report");
    }

    /// <summary>
    /// An unmanaged deployment has no objective to miss, so it is omitted rather than reported
    /// with a defaulted target — which would invent an SLA nobody agreed to.
    /// </summary>
    [Fact]
    public async Task An_unmanaged_deployment_is_left_out_entirely()
    {
        Mock<IDeliveryApi> delivery = new(MockBehavior.Strict);
        Mock<ISupportApi> support = new(MockBehavior.Strict);

        delivery.Setup(d => d.GetDeploymentsForTenantAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DeploymentSummary(
                DeploymentId, Tenant, AppId, "Billing", Guid.NewGuid(), "prod",
                ClusterId, "prod-cluster", "adopted-thing", DeploymentType.HelmChart, "billing",
                SyncStatus.Synced, HealthStatus.Healthy, null, null, IsManaged: false)]);

        ITelemetryApi subject = new TelemetryApi(
            dbFactory: null!, errorBudgets: new ErrorBudgetService(delivery.Object, support.Object), syntheticChecks: null!);

        (await subject.GetErrorBudgetsAsync(Tenant, CancellationToken.None)).Should().BeEmpty();

        // ⚠️ The measure for this test: strict mocks prove the short-circuit really happened.
        // GetTenantErrorBudgetsAsync returns before asking Support for objectives, so a
        // GetSlaTargetsAsync set up here would go unused — and if the filter were removed, the
        // strict mock would throw rather than quietly return an empty list.
        support.VerifyNoOtherCalls();
    }
}
