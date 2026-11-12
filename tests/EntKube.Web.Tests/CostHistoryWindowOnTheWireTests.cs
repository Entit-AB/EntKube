using System.Net;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /cost/history</c>'s window, over real HTTP, because two of the three defects behind this
/// change were in the HANDLER rather than the service — and both were reachable before the route
/// touches the ledger at all, so they answered <b>500</b> to a request that merely named an
/// unusual day.
///
/// <para>⚠️ <b>These cases need no ledger data.</b> Parsing, the ordering and the 400-day cap all
/// run before <c>HasAnyAsync</c>, so a tenant with an empty ledger exercises them and then stops
/// at the documented 503. That is what makes this cheap enough to assert over HTTP.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class CostHistoryWindowOnTheWireTests(GeneratedOpenApiDocumentFixture fixture)
{
    private async Task<HttpStatusCode> GetAsync(string query) =>
        (await fixture.SendForResponseAsync("GET", $"/api/v1/cost/history{query}", "ops:read")).Status;

    /// <summary>
    /// ⚠️ <b>The cap was bypassable by exchanging two query parameters.</b> It is a subtraction,
    /// so a reversed pair gives a NEGATIVE span that sails past <c>&gt; 400</c> — and
    /// <c>CostLedgerService</c> puts the ends back in order itself, so the oversized window was
    /// then served in full rather than coming back empty.
    ///
    /// <para>Asserted in BOTH directions against the SAME six-year span, which is what makes this
    /// a statement about the cap rather than about one URL: refusing both is the fix, and
    /// refusing only the forward one is the defect.</para>
    /// </summary>
    [Fact]
    public async Task A_window_over_the_cap_is_refused_whichever_way_round_it_is_written()
    {
        HttpStatusCode forward = await GetAsync("?from=2020-01-01&to=2026-01-01");
        HttpStatusCode reversed = await GetAsync("?from=2026-01-01&to=2020-01-01");

        forward.Should().Be(HttpStatusCode.BadRequest, "six years is over the 400-day cap");

        reversed.Should().Be(
            HttpStatusCode.BadRequest,
            "the same six years written backwards is the same six years — before the fix this "
            + "was answered in full, because the cap measured a negative difference");
    }

    /// <summary>
    /// ⚠️ A window WITHIN the cap must still be let through, or "fix the cap" could be "refuse
    /// everything" and both assertions above would pass.
    /// </summary>
    [Fact]
    public async Task An_ordinary_window_is_not_refused_by_the_cap()
    {
        HttpStatusCode status = await GetAsync("?from=2026-01-01&to=2026-01-31");

        status.Should().NotBe(
            HttpStatusCode.BadRequest,
            "thirty days is well inside the cap; this tenant's empty ledger answers the "
            + "documented 503 instead, which is not the cap talking");
    }

    /// <summary>
    /// ⚠️ <b>The boundary, pinned because the limit is off by one against the word "window" and I
    /// chose NOT to change that.</b> The test is on the DIFFERENCE between the ends, so 400 days
    /// apart is 401 inclusive days of data. Tightening it to a 400-day window would refuse
    /// requests that work today, so the message was corrected to say "at most 400 days apart"
    /// instead — and this fact is what stops the number drifting afterwards in either direction.
    /// </summary>
    [Theory]
    [InlineData("2026-01-01", "2027-02-05", false, "400 days apart — the last window admitted")]
    [InlineData("2026-01-01", "2027-02-06", true, "401 days apart — the first refused")]
    public async Task The_cap_admits_exactly_four_hundred_days_between_the_ends(
        string from, string to, bool refused, string why)
    {
        // The dates are stated literally rather than computed, so that an edit to the cap cannot
        // move the expectation with it.
        (DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture)
            - DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture))
            .TotalDays.Should().Be(
                refused ? 401 : 400, "otherwise this fact is not about the boundary at all");

        HttpStatusCode status = await GetAsync($"?from={from}&to={to}");

        if (refused)
        {
            status.Should().Be(HttpStatusCode.BadRequest, why);
        }
        else
        {
            status.Should().NotBe(HttpStatusCode.BadRequest, why);
        }
    }

    /// <summary>
    /// ⚠️ <b>The crash, as a client saw it.</b> <c>?to=9999-12-31</c> is an ordinary THIRTY-day
    /// request — <c>from</c> defaults to <c>to</c> minus 29 — that happens to end on the last day
    /// <c>DateTime</c> can represent. The per-day loop incremented past it and threw, so the route
    /// answered 500. <c>?to=0001-01-01</c> is the same defect at the other end, in the handler's
    /// own default for <c>from</c>.
    /// </summary>
    [Theory]
    [InlineData("?to=9999-12-31", "the last representable day")]
    [InlineData("?to=0001-01-01", "the first representable day")]
    [InlineData("?from=0001-01-01&to=0001-01-30", "a window hard against the first day")]
    public async Task A_window_at_the_edge_of_the_calendar_is_answered_rather_than_crashing(
        string query, string why)
    {
        HttpStatusCode status = await GetAsync(query);

        status.Should().NotBe(
            HttpStatusCode.InternalServerError,
            "a query string must not be able to crash the route — {0}", why);
    }
}
