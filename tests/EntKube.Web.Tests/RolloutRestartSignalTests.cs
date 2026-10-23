using System.Text.RegularExpressions;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The restart signal a rollout verdict is judged on.
///
/// <para><b>The defect.</b> <c>RolloutService.MeasureAsync</c> took its restart count from
/// <c>PrometheusService.GetDeploymentMetricsAsync</c>, whose <c>RestartCount</c> is an instant
/// <c>sum(kube_pod_container_status_restarts_total{namespace="…"})</c>. That metric is a
/// <b>cumulative counter</b>, so the figure is every restart every container in the namespace has
/// ever had — not the restarts of the version being rolled out.</para>
///
/// <para>⚠️ <b>What that cost.</b> <c>RolloutAnalysis</c> breaches when
/// <c>restarts &gt; policy.MaxRestarts</c>, which defaults to <b>3</b>. So any namespace that had
/// ever accumulated four container restarts breached on <em>every</em> subsequent rollout, however
/// healthy the new version was — and <c>RolloutWatcherService</c> rolls back automatically on a
/// breach, unattended. Deploys into a mature namespace were undone without anyone asked, and the
/// recorded verdict read "1247 container restarts (limit 3)": absurd on its face, yet internally
/// consistent, so it reads as a flapping deployment rather than a broken signal.</para>
///
/// <para><c>MeasureAsync</c> is handed <c>windowStart</c> and <c>now</c> and already uses them for
/// the error-rate and latency signals. The restart signal was the only one that ignored them.</para>
///
/// <para><b>The analysis was never wrong.</b> <see cref="RolloutAnalysisTests"/> covers the
/// comparison properly — <c>Restarts = 7</c> against a limit of 3 fails, as it should. Only the
/// number reaching it was wrong, which is why no existing test could have caught this.</para>
/// </summary>
public class RolloutRestartSignalTests
{
    [Fact]
    public void The_rollout_restart_query_is_an_increase_over_the_window()
    {
        string q = PrometheusService.RestartsInWindowQuery("billing", 600);

        q.Should().Contain("increase(",
            "a cumulative counter answers 'did this rollout restart' only as a rate");

        q.Should().Contain("[600s]", "the range is the analysis window it was given");
        q.Should().Contain("namespace=\"billing\"");

        // ⚠️ The precise shape of the defect: the instant form is sum( immediately wrapping the
        // metric. Asserting the absence of that is what distinguishes the fix from a query that
        // merely mentions increase() somewhere.
        q.Should().NotContain("sum(kube_pod_container_status_restarts_total",
            "that is the instant, lifetime form this replaced");

        Regex.IsMatch(q, @"sum\(\s*increase\(").Should().BeTrue(
            "the increase has to be inside the sum, not applied to an already-summed series");
    }

    /// <summary>
    /// A window shorter than a scrape interval still has to produce a number. Prometheus cannot
    /// compute an increase over a range of zero, and a rollout measured moments after it began
    /// would otherwise return nothing — which <c>RolloutAnalysis</c> reads as "restarts
    /// unavailable" and drops from the verdict, quietly losing the check instead of passing it.
    /// </summary>
    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 60)]
    [InlineData(59, 60)]
    [InlineData(60, 60)]
    [InlineData(600, 600)]
    [InlineData(3600, 3600)]
    public void A_window_shorter_than_a_scrape_interval_is_floored(int given, int expected) =>
        PrometheusService.RestartWindowSeconds(TimeSpan.FromSeconds(given))
            .Should().Be(expected);

    /// <summary>
    /// The wiring, pinned by reading the source: there is no harness for a Prometheus round trip,
    /// so that the rollout asks for the windowed figure rather than the lifetime one is not
    /// otherwise observable from a test.
    /// </summary>
    [Fact]
    public void The_rollout_asks_for_restarts_in_its_window_not_the_lifetime_total()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/EntKube.Web/Services/Rollouts/RolloutService.cs"));

        File.Exists(path).Should().BeTrue(
            "this test reads the rollout service's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        // ⚠️ Comment lines stripped before asserting, because the explanation left at the call
        // site NAMES GetDeploymentMetricsAsync in order to say it is the wrong one — and the first
        // version of this test matched that comment and failed against correct code. A comment
        // naming a symbol is not a call to it; every ratchet in this repo strips comments for the
        // same reason, and this file is not exempt from the trap it is testing around.
        string source = string.Join('\n', File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && !l.TrimStart().StartsWith('*')));

        source.Should().Contain("GetRestartsInWindowAsync(",
            "the verdict needs restarts during the rollout, not since the namespace was created");

        source.Should().Contain("now - windowStart",
            "and the window it already receives is the range to ask over");

        source.Should().NotContain("GetDeploymentMetricsAsync",
            "that method's RestartCount is the lifetime total the dashboards want; reaching for "
            + "it here is what made the restart check permanently unsatisfiable");

    }
}
