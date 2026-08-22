using EntKube.Web.Data;
using EntKube.Web.Services.Rollouts;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for rollout analysis — the rules that decide whether a production release is
/// rolled back automatically.
///
/// The property most of these defend: an unmeasurable signal never counts as a pass.
/// Treating silence as success is how an automated rollback system quietly stops
/// protecting anything.
/// </summary>
public class RolloutAnalysisTests
{
    /// <summary>A policy with every check disabled; each test enables only what it is about.</summary>
    private static RolloutPolicy Policy(
        double? errorRate = null, double? latency = null, int? restarts = null,
        double? readyFraction = null, double? burnRate = null,
        RolloutFailureAction onFailure = RolloutFailureAction.Alert,
        RolloutInconclusiveAction onInconclusive = RolloutInconclusiveAction.Promote) => new()
    {
        Id = Guid.NewGuid(),
        DeploymentId = Guid.NewGuid(),
        MaxErrorRatePercent = errorRate,
        MaxLatencyP95Ms = latency,
        MaxRestarts = restarts,
        MinReadyFraction = readyFraction,
        MaxErrorBudgetBurnRate = burnRate,
        OnFailure = onFailure,
        OnInconclusive = onInconclusive,
    };

    // ── The central rule: unmeasured is not passed ──

    [Fact]
    public void A_policy_whose_signals_cannot_be_measured_is_inconclusive_not_healthy()
    {
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals(), Policy(errorRate: 5, latency: 500, restarts: 3, readyFraction: 1.0));

        judgement.Verdict.Should().Be(RolloutVerdict.Inconclusive);
        judgement.Checked.Should().BeEmpty();
        judgement.Unavailable.Should().HaveCount(4);
    }

    [Fact]
    public void A_service_with_no_requests_has_an_unknown_error_rate_not_a_zero_one()
    {
        // Nobody called it during the window, so it has told us nothing about whether it works.
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { RequestCount = 0, ErrorCount = 0 },
            Policy(errorRate: 5));

        judgement.Verdict.Should().Be(RolloutVerdict.Inconclusive);
        judgement.Unavailable.Should().Contain("error rate");
    }

    [Fact]
    public void Unavailable_signals_are_reported_even_when_the_verdict_is_healthy()
    {
        // A policy that silently checks almost nothing must be visible, not look like a clean pass.
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 1.0 },
            Policy(readyFraction: 1.0, errorRate: 5, latency: 300));

        judgement.Verdict.Should().Be(RolloutVerdict.Healthy);
        judgement.Checked.Should().BeEquivalentTo(["readiness"]);
        judgement.Unavailable.Should().BeEquivalentTo(["error rate", "latency"]);
        judgement.Summary.Should().Contain("could not measure");
    }

    [Fact]
    public void A_policy_with_no_thresholds_at_all_is_inconclusive()
    {
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 1.0, Restarts = 0 }, Policy());

        judgement.Verdict.Should().Be(RolloutVerdict.Inconclusive);
        judgement.Summary.Should().Contain("nothing to check");
    }

    [Fact]
    public void A_null_threshold_is_not_checked_and_is_not_reported_unavailable()
    {
        // "Do not judge on this" is different from "wanted to judge but could not".
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 1.0 }, Policy(readyFraction: 1.0));

        judgement.Unavailable.Should().BeEmpty();
        judgement.Checked.Should().BeEquivalentTo(["readiness"]);
    }

    // ── Individual thresholds ──

    [Fact]
    public void Error_rate_above_the_threshold_fails()
    {
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { RequestCount = 1000, ErrorCount = 80 }, Policy(errorRate: 5));

        judgement.Verdict.Should().Be(RolloutVerdict.Failing);
        judgement.Breaches.Should().ContainSingle().Which.Should().Contain("8.0%");
    }

    [Fact]
    public void Error_rate_at_the_threshold_passes()
    {
        // The threshold is a limit, not an exclusive bound — 5% with a 5% limit is fine.
        RolloutAnalysis.Evaluate(
            new RolloutSignals { RequestCount = 100, ErrorCount = 5 }, Policy(errorRate: 5))
            .Verdict.Should().Be(RolloutVerdict.Healthy);
    }

    [Fact]
    public void Latency_above_the_threshold_fails()
    {
        RolloutAnalysis.Evaluate(new RolloutSignals { LatencyP95Ms = 850 }, Policy(latency: 500))
            .Breaches.Should().ContainSingle().Which.Should().Contain("850");
    }

    [Fact]
    public void Restarts_above_the_threshold_fail()
    {
        RolloutAnalysis.Evaluate(new RolloutSignals { Restarts = 7 }, Policy(restarts: 3))
            .Breaches.Should().ContainSingle().Which.Should().Contain("7 container restarts");
    }

    [Fact]
    public void Readiness_below_the_threshold_fails()
    {
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 0.5 }, Policy(readyFraction: 1.0));

        judgement.Verdict.Should().Be(RolloutVerdict.Failing);
        judgement.Breaches.Should().ContainSingle().Which.Should().Contain("50%");
    }

    [Fact]
    public void Error_budget_burning_too_fast_fails()
    {
        RolloutAnalysis.Evaluate(
            new RolloutSignals { ErrorBudgetBurnRate = 14.2 }, Policy(burnRate: 10))
            .Breaches.Should().ContainSingle().Which.Should().Contain("14.2×");
    }

    [Fact]
    public void Every_breached_signal_is_reported_not_just_the_first()
    {
        // An operator deciding whether to trust an automatic rollback needs the whole picture.
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { RequestCount = 100, ErrorCount = 50, LatencyP95Ms = 900, Restarts = 9 },
            Policy(errorRate: 5, latency: 500, restarts: 3));

        judgement.Breaches.Should().HaveCount(3);
    }

    [Fact]
    public void One_breach_among_passing_signals_still_fails_the_rollout()
    {
        RolloutAnalysis.Evaluate(
            new RolloutSignals { RequestCount = 100, ErrorCount = 0, ReadyFraction = 1.0, Restarts = 99 },
            Policy(errorRate: 5, readyFraction: 1.0, restarts: 3))
            .Verdict.Should().Be(RolloutVerdict.Failing);
    }

    // ── Turning a verdict into an action ──

    [Fact]
    public void A_healthy_rollout_is_promoted()
    {
        RolloutJudgement healthy = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 1.0 }, Policy(readyFraction: 1.0));

        RolloutAnalysis.Decide(healthy, Policy()).Should().Be(DeploymentRolloutStatus.Promoted);
    }

    [Fact]
    public void A_failing_rollout_rolls_back_only_when_the_policy_says_so()
    {
        RolloutJudgement failing = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 0.1 }, Policy(readyFraction: 1.0));

        RolloutAnalysis.Decide(failing, Policy(onFailure: RolloutFailureAction.Rollback))
            .Should().Be(DeploymentRolloutStatus.RolledBack);

        // Alert is the default: rolling back a production workload automatically is
        // something an operator should opt into, not inherit.
        RolloutAnalysis.Decide(failing, Policy(onFailure: RolloutFailureAction.Alert))
            .Should().Be(DeploymentRolloutStatus.Alerted);
    }

    [Fact]
    public void Alert_is_the_default_failure_action()
    {
        new RolloutPolicy { DeploymentId = Guid.NewGuid() }
            .OnFailure.Should().Be(RolloutFailureAction.Alert);
    }

    [Fact]
    public void An_inconclusive_rollout_is_never_rolled_back()
    {
        // Rolling back a production release on no evidence would be worse than the risk
        // it is trying to avoid.
        RolloutJudgement inconclusive = RolloutAnalysis.Evaluate(new RolloutSignals(), Policy(errorRate: 5));

        RolloutAnalysis.Decide(inconclusive, Policy(onFailure: RolloutFailureAction.Rollback))
            .Should().NotBe(DeploymentRolloutStatus.RolledBack);
    }

    [Fact]
    public void An_inconclusive_rollout_follows_the_configured_inconclusive_action()
    {
        RolloutJudgement inconclusive = RolloutAnalysis.Evaluate(new RolloutSignals(), Policy(errorRate: 5));

        RolloutAnalysis.Decide(inconclusive, Policy(onInconclusive: RolloutInconclusiveAction.Promote))
            .Should().Be(DeploymentRolloutStatus.Promoted);

        RolloutAnalysis.Decide(inconclusive, Policy(onInconclusive: RolloutInconclusiveAction.Hold))
            .Should().Be(DeploymentRolloutStatus.Inconclusive);
    }

    // ── Summary text ──

    [Fact]
    public void The_summary_of_a_failing_rollout_lists_the_breaches()
    {
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { RequestCount = 10, ErrorCount = 10 }, Policy(errorRate: 1));

        judgement.Summary.Should().Contain("error rate 100.0%");
    }

    [Fact]
    public void The_summary_of_a_fully_measured_healthy_rollout_says_so_plainly()
    {
        RolloutJudgement judgement = RolloutAnalysis.Evaluate(
            new RolloutSignals { ReadyFraction = 1.0, Restarts = 0 },
            Policy(readyFraction: 1.0, restarts: 3));

        judgement.Summary.Should().Be("All 2 checked signal(s) within threshold.");
    }
}

/// <summary>
/// Tests for which rollout outcomes reach a human.
///
/// This exists because the policy's "Alert" failure action originally did nothing but
/// write a log line, while sitting in the UI next to "Roll back" implying somebody
/// gets told. An option that promises a notification and delivers none is worse than
/// not offering it.
/// </summary>
public class RolloutNotificationTests
{
    [Fact]
    public void A_failed_rollback_is_critical()
    {
        // The bad release is still live and the automatic remedy did not work.
        RolloutWatcherService.NotificationSeverityFor(DeploymentRolloutStatus.RollbackFailed)
            .Should().Be("critical");
    }

    [Fact]
    public void An_automatic_rollback_is_critical_even_though_it_succeeded()
    {
        // Production was reverted without a human deciding to. The outcome is the safe
        // one, but nobody should discover it from a dashboard days later.
        RolloutWatcherService.NotificationSeverityFor(DeploymentRolloutStatus.RolledBack)
            .Should().Be("critical");
    }

    [Fact]
    public void A_failed_analysis_with_no_rollback_configured_warns()
    {
        // This is the case the "Alert" action exists for.
        RolloutWatcherService.NotificationSeverityFor(DeploymentRolloutStatus.Alerted)
            .Should().Be("warning");
    }

    [Fact]
    public void An_inconclusive_release_is_reported_quietly()
    {
        RolloutWatcherService.NotificationSeverityFor(DeploymentRolloutStatus.Inconclusive)
            .Should().Be("info");
    }

    [Theory]
    [InlineData(DeploymentRolloutStatus.Promoted)]
    [InlineData(DeploymentRolloutStatus.Superseded)]
    [InlineData(DeploymentRolloutStatus.Watching)]
    public void Uneventful_outcomes_stay_silent(DeploymentRolloutStatus outcome)
    {
        // A channel that fires on every successful deploy is muted within a week — and
        // the rollback notification is muted along with it.
        RolloutWatcherService.NotificationSeverityFor(outcome).Should().BeNull();
    }

    [Fact]
    public void Every_outcome_the_policy_can_produce_has_a_deliberate_decision()
    {
        // A new terminal status must not silently default to "stay quiet".
        foreach (DeploymentRolloutStatus outcome in Enum.GetValues<DeploymentRolloutStatus>())
        {
            bool shouldNotify = outcome is DeploymentRolloutStatus.RolledBack
                or DeploymentRolloutStatus.RollbackFailed
                or DeploymentRolloutStatus.Alerted
                or DeploymentRolloutStatus.Inconclusive;

            (RolloutWatcherService.NotificationSeverityFor(outcome) is not null)
                .Should().Be(shouldNotify, $"outcome {outcome} should have a considered notification decision");
        }
    }
}
