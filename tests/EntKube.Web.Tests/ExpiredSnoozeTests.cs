using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether a snoozed advisor finding comes back when its snooze runs out.
///
/// <para><b>The defect.</b> Two of the four sites that filter these findings wrote
/// <c>State is not Dismissed and not Snoozed</c>. That reads as "not snoozed" and means "was
/// snoozed at some point": <see cref="AdvisorFindingStatus.Snoozed"/> is documented as "Hidden
/// until <c>AdvisorFindingState.SnoozedUntil</c> passes", and nothing ever rewrites the status
/// when it does. <c>AdvisorStateService</c> changes it only on an explicit acknowledge, dismiss
/// or reactivate, and its reconcile prunes only rows whose finding has gone <em>absent</em> — so
/// a finding that is still present keeps a stale <c>Snoozed</c> row forever.</para>
///
/// <para>⚠️ A certificate snoozed for a day in March was therefore excluded from the digest for
/// the rest of the year. The digest is the worse of the two sites: the overview tile under-counted
/// a number somebody was already looking at, while <c>MaybeSendDigestAsync</c> returns false when
/// nothing is actionable and <b>stays quiet</b> — so the one mechanism that reaches somebody who
/// is not looking at the screen went silent about overdue work, with no symptom.</para>
///
/// <para>The two sites that had it right — <c>OperationsAdvisorTab</c>'s filter and
/// <c>FleetAdvisor</c>'s <c>Visible</c> — are the controls this was confirmed against, and the
/// pairing now lives on the record so a fifth copy cannot get it wrong.</para>
/// </summary>
public class ExpiredSnoozeTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static OperationsFinding Finding(
        AdvisorFindingStatus state,
        DateTime? snoozedUntil = null,
        AdvisorHorizon horizon = AdvisorHorizon.Overdue) =>
        new()
        {
            Id = "cert:abc",
            Category = AdvisorCategory.Security,
            Severity = AdvisorSeverity.Critical,
            Horizon = horizon,
            Title = "Certificate expires",
            State = state,
            SnoozedUntil = snoozedUntil,
        };

    /// <summary>⭐ The defect, stated as the thing that goes wrong.</summary>
    [Fact]
    public void A_snooze_that_has_already_run_out_needs_attention_again()
    {
        OperationsFinding f = Finding(AdvisorFindingStatus.Snoozed, Now.AddDays(-7));

        f.IsSnoozeInForceAt(Now).Should().BeFalse("the date passed a week ago");
        f.NeedsAttentionAt(Now).Should().BeTrue(
            "nothing rewrites the status when SnoozedUntil passes, so reading the status alone "
            + "hides an overdue finding permanently");
    }

    /// <summary>The positive control: a live snooze really does suppress.</summary>
    [Fact]
    public void A_snooze_that_is_still_running_suppresses()
    {
        OperationsFinding f = Finding(AdvisorFindingStatus.Snoozed, Now.AddDays(3));

        f.IsSnoozeInForceAt(Now).Should().BeTrue();
        f.NeedsAttentionAt(Now).Should().BeFalse(
            "somebody said 'not until Friday' and it is Tuesday — without this the fix would just "
            + "be 'ignore snoozes', which is a different defect");
    }

    /// <summary>
    /// ⚠️ A snooze with no end date is not a snooze. <c>null &gt; now</c> is false, which is
    /// deliberate and matches both of the sites that already had this right.
    /// </summary>
    [Fact]
    public void A_snoozed_finding_with_no_end_date_is_not_suppressed()
    {
        OperationsFinding f = Finding(AdvisorFindingStatus.Snoozed, snoozedUntil: null);

        f.IsSnoozeInForceAt(Now).Should().BeFalse();
        f.NeedsAttentionAt(Now).Should().BeTrue();
    }

    /// <summary>Dismissed is the one that genuinely has no end — it is a decision, not a delay.</summary>
    [Fact]
    public void Dismissed_stays_out_regardless_of_dates()
    {
        Finding(AdvisorFindingStatus.Dismissed).NeedsAttentionAt(Now).Should().BeFalse();
        Finding(AdvisorFindingStatus.Dismissed, Now.AddDays(-7)).NeedsAttentionAt(Now)
            .Should().BeFalse();
    }

    /// <summary>
    /// Acknowledged still counts, which the horizon test below would otherwise mask: somebody
    /// picking a finding up is not the same as the work being done, and the advisor's own doc says
    /// it "still counts, but flagged as handled".
    /// </summary>
    [Fact]
    public void Acknowledged_still_needs_attention()
        => Finding(AdvisorFindingStatus.Acknowledged).NeedsAttentionAt(Now).Should().BeTrue();

    /// <summary>
    /// The horizon still decides. ⚠️ Asserted so the fix cannot be read as "everything not
    /// dismissed is urgent" — a finding due next month is not due now, whatever its snooze says.
    /// </summary>
    [Theory]
    [InlineData(AdvisorHorizon.Overdue, true)]
    [InlineData(AdvisorHorizon.Today, true)]
    public void Only_due_or_late_findings_need_attention(AdvisorHorizon horizon, bool expected)
        => Finding(AdvisorFindingStatus.Active, horizon: horizon)
            .NeedsAttentionAt(Now).Should().Be(expected);

    /// <summary>
    /// ⭐ The boundary, which a <c>&gt;=</c> here would get wrong in the quiet direction: at the
    /// exact instant the snooze ends it is over, so the finding is back.
    /// </summary>
    [Fact]
    public void At_the_instant_the_snooze_ends_it_is_over()
    {
        Finding(AdvisorFindingStatus.Snoozed, Now).IsSnoozeInForceAt(Now)
            .Should().BeFalse("SnoozedUntil is the moment it stops suppressing, not the last "
                + "moment it still does");

        Finding(AdvisorFindingStatus.Snoozed, Now.AddTicks(1)).IsSnoozeInForceAt(Now)
            .Should().BeTrue();
    }
}
