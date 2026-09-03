using System.Globalization;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Rollouts;

/// <summary>
/// What was measured during a rollout's analysis window.
///
/// Every field is nullable, and null means <em>could not be measured</em> — not zero,
/// and not "fine". Distinguishing those is the whole point of this type: a deployment
/// with no traces has an unknown error rate, not a 0% one.
/// </summary>
public sealed record RolloutSignals
{
    /// <summary>Requests observed in the window. Null when the trace store has no data for the service.</summary>
    public long? RequestCount { get; init; }

    /// <summary>Errors observed. Null when requests are null.</summary>
    public long? ErrorCount { get; init; }

    /// <summary>Observed error rate as a percentage, when there were requests to divide by.</summary>
    public double? ErrorRatePercent =>
        RequestCount is > 0 && ErrorCount is not null
            ? (double)ErrorCount.Value / RequestCount.Value * 100d
            : null;

    public double? LatencyP95Ms { get; init; }

    /// <summary>Container restarts during the window.</summary>
    public int? Restarts { get; init; }

    /// <summary>Ready replicas ÷ desired replicas, 0..1.</summary>
    public double? ReadyFraction { get; init; }

    /// <summary>Error-budget burn rate as a multiple of the sustainable rate.</summary>
    public double? ErrorBudgetBurnRate { get; init; }
}

/// <summary>The outcome of judging a rollout.</summary>
public enum RolloutVerdict
{
    /// <summary>At least one signal was checked and every checked signal passed.</summary>
    Healthy = 0,
    /// <summary>At least one checked signal breached its threshold.</summary>
    Failing = 1,
    /// <summary>Nothing could be measured, so no judgement is possible.</summary>
    Inconclusive = 2,
}

/// <summary>A verdict plus the evidence behind it, so an operator can see why.</summary>
public sealed record RolloutJudgement
{
    public required RolloutVerdict Verdict { get; init; }

    /// <summary>Human-readable breaches, empty unless the verdict is Failing.</summary>
    public IReadOnlyList<string> Breaches { get; init; } = [];

    /// <summary>Signals that were configured and successfully measured.</summary>
    public IReadOnlyList<string> Checked { get; init; } = [];

    /// <summary>
    /// Signals the policy asked for but that could not be measured. Reported so a policy
    /// that silently checks nothing is visible rather than looking like a clean pass.
    /// </summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>One-line summary for the rollout record.</summary>
    public string Summary =>
        Verdict switch
        {
            RolloutVerdict.Failing => string.Join("; ", Breaches),
            RolloutVerdict.Healthy => Unavailable.Count == 0
                ? $"All {Checked.Count} checked signal(s) within threshold."
                : $"{Checked.Count} signal(s) within threshold; could not measure: {string.Join(", ", Unavailable)}.",
            _ => Unavailable.Count > 0
                ? $"No signal could be measured ({string.Join(", ", Unavailable)})."
                : "No thresholds are configured, so there was nothing to check.",
        };
}

/// <summary>
/// Judges a release from what was measured during its analysis window.
///
/// Pure, so the rule that decides whether a production deployment gets rolled back is
/// checkable without a cluster.
///
/// The rule that shapes everything here: <b>an unmeasurable signal never counts as a
/// pass</b>. A configured threshold whose signal is missing makes the result less
/// certain, not more reassuring — so a rollout where nothing at all could be measured
/// is Inconclusive, never Healthy. Treating silence as success is how an automated
/// rollback system quietly stops protecting anything.
/// </summary>
public static class RolloutAnalysis
{
    /// <summary>
    /// Formats a message culture-independently.
    ///
    /// These strings are persisted as the rollout's verdict and read by operators and the
    /// API alike, so they must not change with the server's locale — a Swedish host would
    /// otherwise render "8,0 %" where an American one renders "8.0%", for the same release.
    /// </summary>
    private static string Invariant(FormattableString message) =>
        message.ToString(CultureInfo.InvariantCulture);

    public static RolloutJudgement Evaluate(RolloutSignals signals, RolloutPolicy policy)
    {
        List<string> breaches = [];
        List<string> checkedSignals = [];
        List<string> unavailable = [];

        // ── Readiness: the only signal needing no instrumentation ──
        if (policy.MinReadyFraction is double minReady)
        {
            if (signals.ReadyFraction is double ready)
            {
                checkedSignals.Add("readiness");
                if (ready < minReady)
                {
                    // Percentages are computed rather than formatted with "P0": even under
                    // the invariant culture that inserts a space before the sign ("50 %").
                    breaches.Add(Invariant(
                        $"only {ready * 100:F0}% of replicas ready (needs {minReady * 100:F0}%)"));
                }
            }
            else
            {
                unavailable.Add("readiness");
            }
        }

        // ── Restarts ──
        if (policy.MaxRestarts is int maxRestarts)
        {
            if (signals.Restarts is int restarts)
            {
                checkedSignals.Add("restarts");
                if (restarts > maxRestarts)
                {
                    breaches.Add(Invariant($"{restarts} container restarts (limit {maxRestarts})"));
                }
            }
            else
            {
                unavailable.Add("restarts");
            }
        }

        // ── Error rate ──
        if (policy.MaxErrorRatePercent is double maxErrorRate)
        {
            if (signals.ErrorRatePercent is double errorRate)
            {
                checkedSignals.Add("error rate");
                if (errorRate > maxErrorRate)
                {
                    breaches.Add(Invariant($"error rate {errorRate:F1}% (limit {maxErrorRate:F1}%)"));
                }
            }
            else
            {
                // No requests at all is not a 0% error rate. A service nobody called during
                // the window has told us nothing about whether it works.
                unavailable.Add("error rate");
            }
        }

        // ── Latency ──
        if (policy.MaxLatencyP95Ms is double maxLatency)
        {
            if (signals.LatencyP95Ms is double latency)
            {
                checkedSignals.Add("latency");
                if (latency > maxLatency)
                {
                    breaches.Add(Invariant($"p95 latency {latency:F0} ms (limit {maxLatency:F0} ms)"));
                }
            }
            else
            {
                unavailable.Add("latency");
            }
        }

        // ── Error-budget burn ──
        if (policy.MaxErrorBudgetBurnRate is double maxBurn)
        {
            if (signals.ErrorBudgetBurnRate is double burn)
            {
                checkedSignals.Add("error budget burn");
                if (burn > maxBurn)
                {
                    breaches.Add(Invariant($"error budget burning at {burn:F1}× (limit {maxBurn:F1}×)"));
                }
            }
            else
            {
                unavailable.Add("error budget burn");
            }
        }

        RolloutVerdict verdict = breaches.Count > 0
            ? RolloutVerdict.Failing
            : checkedSignals.Count > 0
                ? RolloutVerdict.Healthy
                : RolloutVerdict.Inconclusive;

        return new RolloutJudgement
        {
            Verdict = verdict,
            Breaches = breaches,
            Checked = checkedSignals,
            Unavailable = unavailable,
        };
    }

    /// <summary>
    /// Maps a verdict to the terminal status for the rollout, given the policy's actions.
    /// Separated from <see cref="Evaluate"/> so the "what do we do about it" decision is
    /// testable independently of the measurement rules.
    /// </summary>
    public static DeploymentRolloutStatus Decide(RolloutJudgement judgement, RolloutPolicy policy) =>
        judgement.Verdict switch
        {
            RolloutVerdict.Healthy => DeploymentRolloutStatus.Promoted,
            RolloutVerdict.Failing => policy.OnFailure == RolloutFailureAction.Rollback
                ? DeploymentRolloutStatus.RolledBack
                : DeploymentRolloutStatus.Alerted,
            _ => policy.OnInconclusive == RolloutInconclusiveAction.Promote
                ? DeploymentRolloutStatus.Promoted
                : DeploymentRolloutStatus.Inconclusive,
        };
}
