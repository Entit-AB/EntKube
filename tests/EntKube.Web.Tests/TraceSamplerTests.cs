using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The raw-span head sampler (<see cref="TraceSampler"/>): keep-all for error/slow traces, deterministic
/// per-trace-id sampling for the rest, and an exact passthrough at rate 100. These guarantee the trace
/// list stays complete (summaries are fed separately) while raw-span volume — the biggest telemetry cost —
/// is thinned without ever splitting a trace across the keep/drop line.
/// </summary>
public sealed class TraceSamplerTests
{
    private static SpanIngestRecord Span(string traceId, short status = 1, double durMs = 10) =>
        new(new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), traceId, Guid.NewGuid().ToString("N"),
            null, "GET /x", "api", 2, durMs, status, "prod", "api-1", null);

    [Fact]
    public void Rate100_ReturnsInputUnchanged()
    {
        var input = new[] { Span("a"), Span("b"), Span("c") };
        TraceSampler.Sample(input, ratePercent: 100, minKeepDurationMs: 500)
            .Should().BeSameAs(input);
    }

    [Fact]
    public void ErrorAndSlowTraces_AreAlwaysKept_EvenAtRate1()
    {
        var input = new[]
        {
            Span("err-trace", status: 2, durMs: 5),      // error → keep
            Span("slow-trace", status: 1, durMs: 900),   // slow  → keep
            Span("err-trace", status: 1, durMs: 5),      // same error trace, ok span → kept with its trace
        };
        var kept = TraceSampler.Sample(input, ratePercent: 1, minKeepDurationMs: 500);
        kept.Select(s => s.TraceId).Should().OnlyContain(t => t == "err-trace" || t == "slow-trace");
        kept.Count(s => s.TraceId == "err-trace").Should().Be(2); // both spans of the error trace survive together
        kept.Should().Contain(s => s.TraceId == "slow-trace");
    }

    [Fact]
    public void Sampling_IsDeterministic_AndWholeTrace()
    {
        // 400 distinct plain (non-error, fast) traces, 3 spans each. At 25% roughly a quarter survive, and
        // every span of a surviving trace survives (never a partial trace).
        var input = new List<SpanIngestRecord>();
        for (int t = 0; t < 400; t++)
            for (int s = 0; s < 3; s++)
                input.Add(Span($"trace-{t}", status: 1, durMs: 10));

        var kept = TraceSampler.Sample(input, ratePercent: 25, minKeepDurationMs: 500);

        // Whole-trace: each kept trace id appears exactly 3 times (all its spans), never 1 or 2.
        foreach (var g in kept.GroupBy(s => s.TraceId))
            g.Count().Should().Be(3);

        // Deterministic: same input → identical decision set on a re-run.
        var kept2 = TraceSampler.Sample(input, ratePercent: 25, minKeepDurationMs: 500);
        kept2.Select(s => s.TraceId).Distinct().Should().BeEquivalentTo(kept.Select(s => s.TraceId).Distinct());

        // Actually thinned, but not to nothing — sanity band around the 25% target over 400 traces.
        int keptTraces = kept.Select(s => s.TraceId).Distinct().Count();
        keptTraces.Should().BeInRange(60, 140);
    }
}
