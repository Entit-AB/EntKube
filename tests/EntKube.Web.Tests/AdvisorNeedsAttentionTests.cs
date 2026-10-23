using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What the monitoring overview's "Needs attention" tile counts.
///
/// <para><b>The defect.</b> The count excluded every finding whose state was
/// <see cref="AdvisorFindingStatus.Snoozed"/>, with no comparison against
/// <c>SnoozedUntil</c> — and the status outlives the date. <c>AdvisorStateService</c>
/// rewrites it only on an explicit acknowledge, dismiss or reactivate; its reconcile pass
/// prunes only rows whose finding has gone <i>away</i>; and
/// <c>OperationsAdvisorService.MergeStateAsync</c> copies the status onto the finding
/// untouched. Snoozes are offered in 1, 3 and 7 days, so expired-but-still-Snoozed is the
/// ordinary steady state.</para>
///
/// <para>So an overdue finding snoozed until last week was listed by the Advisor tab that
/// the tile clicks through to — which renders a "Snoozed → &lt;past date&gt;" badge on it —
/// while the tile itself showed 0 in green. ⭐ The tile and the tab behind it disagreed
/// about the same finding, and the tile was the one claiming nothing was wrong.</para>
///
/// <para>The two sites that render these findings as a list both pair the status with the
/// date: <c>OperationsAdvisorTab.razor</c>'s default filter, and <c>FleetAdvisor.razor</c>'s
/// <c>Visible</c>, whose <c>Priorities</c> list then filters the identical
/// <c>Overdue or Today</c> horizons off that pairing — the same count as this tile, across
/// the fleet instead of one tenant.</para>
///
/// <para>⚠️ Asserted against the predicate rather than the rendered component. The tile's
/// number is produced inside <c>Refresh()</c>, which reaches a WAN round-trip to every
/// cluster through <c>OperationsAdvisorService</c> and its eleven dependencies; the number
/// on screen is <c>advisorActionable</c> and nothing else, so the predicate is the whole of
/// what was wrong.</para>
/// </summary>
public class AdvisorNeedsAttentionTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static OperationsFinding Finding(
        AdvisorHorizon horizon = AdvisorHorizon.Overdue,
        AdvisorFindingStatus state = AdvisorFindingStatus.Active,
        DateTime? snoozedUntil = null) => new()
        {
            Id = "cert:" + Guid.NewGuid(),
            Category = AdvisorCategory.Security,
            Severity = AdvisorSeverity.Critical,
            Horizon = horizon,
            Title = "Certifikatet har gått ut",
            State = state,
            SnoozedUntil = snoozedUntil,
        };

    /// <summary>
    /// ⭐ The defect, stated as what the operator did: snoozed an overdue finding a week ago
    /// for three days, and the tile has been reporting zero ever since.
    /// </summary>
    [Fact]
    public void A_snooze_that_has_already_run_out_needs_attention_again()
    {
        OperationsFinding expired = Finding(
            state: AdvisorFindingStatus.Snoozed,
            snoozedUntil: Now.AddDays(-4));

        TenantMonitoringOverview.NeedsAttention(expired, Now).Should().BeTrue(
            "the status is documented as \"hidden until SnoozedUntil passes\", it has "
            + "passed, and the Advisor tab this tile links to lists the finding as open");
    }

    /// <summary>
    /// The positive that makes the one above mean something: a snooze still in force does
    /// suppress the count, so the fix did not simply stop honouring snoozes.
    /// </summary>
    [Fact]
    public void A_snooze_still_in_force_is_still_honoured()
    {
        OperationsFinding snoozed = Finding(
            state: AdvisorFindingStatus.Snoozed,
            snoozedUntil: Now.AddDays(2));

        TenantMonitoringOverview.NeedsAttention(snoozed, Now).Should().BeFalse();
    }

    /// <summary>
    /// A snooze with no end date is not a snooze. Both control sites behave the same way
    /// (<c>null &gt; now</c> is false), and treating it as indefinite would reintroduce the
    /// defect for any row written without a date.
    /// </summary>
    [Fact]
    public void A_snooze_with_no_end_date_needs_attention()
    {
        OperationsFinding open = Finding(
            state: AdvisorFindingStatus.Snoozed, snoozedUntil: null);

        TenantMonitoringOverview.NeedsAttention(open, Now).Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ Dismissed is <b>not</b> time-bounded — it is "accepted/ignored", cleared only by an
    /// explicit reactivate — so it stays suppressed whatever date is on the row. Pinned here
    /// because the obvious way to write the fix is one combined expiry test, which would
    /// quietly un-suppress every dismissal.
    /// </summary>
    [Fact]
    public void A_dismissal_does_not_expire()
    {
        TenantMonitoringOverview.NeedsAttention(
            Finding(state: AdvisorFindingStatus.Dismissed), Now).Should().BeFalse();

        TenantMonitoringOverview.NeedsAttention(
            Finding(state: AdvisorFindingStatus.Dismissed, snoozedUntil: Now.AddDays(-9)), Now)
            .Should().BeFalse();
    }

    /// <summary>
    /// The horizon half of the predicate, unchanged: the tile counts what is due now or
    /// already late, not everything on the radar.
    /// </summary>
    [Theory]
    [InlineData(AdvisorHorizon.Overdue, true)]
    [InlineData(AdvisorHorizon.Today, true)]
    [InlineData(AdvisorHorizon.ThisWeek, false)]
    [InlineData(AdvisorHorizon.Later, false)]
    public void Only_overdue_and_today_count(AdvisorHorizon horizon, bool expected) =>
        TenantMonitoringOverview.NeedsAttention(Finding(horizon), Now).Should().Be(expected);

    /// <summary>An acknowledged finding still counts — somebody owning it is not it being done.</summary>
    [Fact]
    public void An_acknowledged_finding_still_counts() =>
        TenantMonitoringOverview.NeedsAttention(
            Finding(state: AdvisorFindingStatus.Acknowledged), Now).Should().BeTrue();
}
