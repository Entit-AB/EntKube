namespace EntKube.Web.Services;

/// <summary>
/// The <c>rate()</c> lookback window for a RANGE query, sized from that query's step.
///
/// <para><b>The defect this exists for.</b> Every range query in the product computed its step as
/// <c>Math.Max(15, duration / 100)</c> and then asked for <c>rate(...[5m])</c>, a window written
/// into the query text. Those two numbers are independent, and past about six hours the step
/// overtakes the window:</para>
///
/// <list type="table">
///   <item><term>1h</term><description>step 36s — window covers it 8× over</description></item>
///   <item><term>6h</term><description>step 216s — still inside 300s</description></item>
///   <item><term>12h</term><description>step 432s — <b>window covers 69% of each bucket</b></description></item>
///   <item><term>24h</term><description>step 864s — <b>window covers 35% of each bucket</b></description></item>
/// </list>
///
/// <para>⚠️ <b>Nothing fails and the chart still looks like a measurement.</b> Prometheus
/// evaluates <c>rate</c> over the last 5 minutes before each step, so on the 24h view roughly
/// two thirds of the timeline is never looked at. A namespace that burned three cores for ten
/// minutes between two sample points plots as zero. The line is smooth, plausible, and
/// systematically low — which is worse than a gap, because a gap is visible.</para>
///
/// <para><b>Why a token rather than rewriting every query.</b> Several of these range queries are
/// PromQL the <em>operator</em> typed into the metrics explorer. Silently rewriting somebody's
/// query is its own defect: if they asked for <c>[5m]</c> they may mean it, and a product that
/// edits the query it was given cannot be reasoned about. So the window is <b>opt-in</b>: a query
/// the product owns writes <see cref="Token"/> where its window goes, and
/// <see cref="Resolve"/> substitutes. A query without the token is passed through untouched,
/// which is every operator-typed one by construction.</para>
///
/// <para>This is the same idea as Grafana's <c>$__rate_interval</c>, and the deliberate choice of
/// the same leading <c>$__</c> is so that anyone who knows that convention reads this correctly
/// on sight.</para>
/// </summary>
public static class PromRateWindow
{
    /// <summary>
    /// The placeholder a product-owned range query writes in place of its rate window, as
    /// <c>rate(metric[$__rate])</c>.
    /// </summary>
    public const string Token = "$__rate";

    /// <summary>
    /// The floor, in seconds, and the window used when there is no step at all.
    ///
    /// <para>Five minutes, which is what every one of these queries already asked for, so an
    /// instant query and a short range keep exactly the behaviour they had. Prometheus wants at
    /// least four scrape intervals in a rate window to tolerate a missed scrape, and the usual
    /// 15–60s interval puts 300s comfortably above that.</para>
    /// </summary>
    public const int FloorSeconds = 300;

    /// <summary>
    /// The window for a given step: four steps, never below <see cref="FloorSeconds"/>.
    ///
    /// <para>Four rather than one because a window equal to the step still loses a counter reset
    /// and a missed scrape at the boundary; four is the ratio Grafana's <c>$__rate_interval</c>
    /// settles on for the same reason. The cost of overlap is a slightly smoothed line, and the
    /// cost of the gap it replaces was a line that read as zero load.</para>
    /// </summary>
    public static int WindowSeconds(int stepSeconds) =>
        Math.Max(FloorSeconds, 4 * Math.Max(0, stepSeconds));

    /// <summary>
    /// Substitutes <see cref="Token"/> for a window sized from <paramref name="stepSeconds"/>.
    ///
    /// <para>⚠️ A query with no token is returned unchanged, deliberately — see the note on
    /// operator-typed queries in the type summary. Pass <c>0</c> for an instant query, which has
    /// no step and so gets <see cref="FloorSeconds"/>, the window it already had.</para>
    /// </summary>
    public static string Resolve(string query, int stepSeconds) =>
        string.IsNullOrEmpty(query) || !query.Contains(Token, StringComparison.Ordinal)
            ? query
            : query.Replace(
                Token,
                $"{WindowSeconds(stepSeconds)}s",
                StringComparison.Ordinal);
}
