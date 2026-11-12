using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// What <c>GetHistoryAsync</c> does with a window whose ends are the wrong way round, and with one
/// that reaches the last day <c>DateTime</c> can represent.
///
/// <para>⚠️ Both are reachable from <c>GET /cost/history</c>'s query string, which takes
/// <c>from</c> and <c>to</c> as free text and parses them.</para>
/// </summary>
public sealed class CostHistoryWindowTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly CostLedgerService history;
    private readonly Guid tenantId = Guid.NewGuid();

    public CostHistoryWindowTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        db.SaveChanges();

        history = new CostLedgerService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ⚠️ <b>The last day <c>DateTime</c> can hold.</b> The per-day loop is
    /// <c>for (day = start; day &lt;= end; day = day.AddDays(1))</c>, so on the final iteration it
    /// increments PAST <c>end</c> before the condition is re-tested — and when <c>end</c> is
    /// <c>9999-12-31</c> that increment leaves the representable range and throws.
    ///
    /// <para>This needs no absurd window: the default <c>from</c> is <c>to</c> minus 29 days, so
    /// <c>?to=9999-12-31</c> alone is an ordinary THIRTY-day request that happens to end on the
    /// last day, and the 400-day cap has nothing to object to.</para>
    /// </summary>
    [Fact]
    public async Task A_window_ending_on_the_last_representable_day_does_not_throw()
    {
        DateTime last = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        Func<Task> reading = async () => await history.GetHistoryAsync(
            tenantId, last.AddDays(-29), last, CostGroupBy.Customer, DateTime.UtcNow);

        await reading.Should().NotThrowAsync(
            "a query string cannot be allowed to crash the route — `?to=9999-12-31` is a thirty "
            + "day window, not an abusive one");
    }

    /// <summary>
    /// ⚠️ <b>A reversed window is SWAPPED, not refused</b> — <c>GetHistoryAsync</c> puts the ends
    /// back in order itself. That is forgiving behaviour and it is not the defect; the defect is
    /// that the route's 400-day cap is computed on the SIGNED difference BEFORE the swap, so
    /// <c>(to - from)</c> for a reversed pair is NEGATIVE and sails past <c>&gt; 400</c>.
    ///
    /// <para>So the cap is bypassed by exchanging two query parameters: a six-year window refused
    /// as <c>from=2020&amp;to=2026</c> is served as <c>from=2026&amp;to=2020</c>. This fact pins
    /// the swap, so that the cap fix in the route cannot be "reject reversed windows" without
    /// something here noticing the behaviour change.</para>
    /// </summary>
    [Fact]
    public async Task A_reversed_window_covers_the_same_days_as_the_forward_one()
    {
        DateTime from = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        DateTime now = new(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc);

        CostHistoryReport forward =
            await history.GetHistoryAsync(tenantId, from, to, CostGroupBy.Customer, now);
        CostHistoryReport reversed =
            await history.GetHistoryAsync(tenantId, to, from, CostGroupBy.Customer, now);

        reversed.From.Should().Be(forward.From);
        reversed.To.Should().Be(forward.To);
        reversed.Daily.Count.Should().Be(
            forward.Daily.Count, "the ends are put back in order rather than yielding nothing");
    }

    /// <summary>
    /// ⚠️ The first day, the other end of the same defect: <c>previousStart</c> is
    /// <c>start</c> minus the window length, so a window starting on <c>0001-01-01</c> reached
    /// before the first representable day.
    /// </summary>
    [Fact]
    public async Task A_window_starting_on_the_first_representable_day_does_not_throw()
    {
        DateTime first = new(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Func<Task> reading = async () => await history.GetHistoryAsync(
            tenantId, first, first.AddDays(29), CostGroupBy.Customer, DateTime.UtcNow);

        await reading.Should().NotThrowAsync(
            "the previous window is a comparison; there is no cost data before year one for a "
            + "clamp to hide");
    }
}
