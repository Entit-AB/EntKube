using System.Text.Json;
using EntKube.Web.Services.Cost;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The cost-ledger response records serialise exactly as the anonymous objects they replaced.
///
/// <para>⚠️ <b>The oracle is the OLD code, extracted from git rather than retyped.</b> If the
/// record's mapping and its expected values were both written from the same reading of the
/// handler, one consistent misreading would pass. The anonymous literals below were copied out of
/// <c>origin/main</c>'s <c>PublicApiEndpoints.cs</c> verbatim, so they are an INDEPENDENT
/// statement of the wire contract. They are built from the same report object the record is built
/// from, so any rename shows as a differing JSON key and any transposition as a differing
/// value.</para>
///
/// <para>⚠️ <b>Unlike the cluster-rate records, these are NOT identity projections.</b> The
/// handler renamed as it mapped — <c>complete</c> from <c>IsComplete</c>, <c>unattributed</c> from
/// <c>IsUnattributed</c>, <c>ns</c> from <c>Namespace</c> — and flattened the grouping enum to a
/// lowercase string. Every one of those renames is the contract a client already reads, which is
/// why reproducing them is the whole job.</para>
///
/// <para>The report below gives every settable figure a DISTINCT value, so a swap between two
/// same-typed fields changes the JSON. The derived ones follow from them deterministically:
/// <c>Change</c> is 1000-800, coverage completeness is 700/900, and <c>DailyAverage</c> is 1000
/// divided by the single day's 20/24 of a whole day — which is <c>MeasuredDayFraction</c> rather
/// than <c>Completeness</c>, and the two agree here only because this fixture's day is neither
/// today nor a cluster's opening day.</para>
///
/// <para>⚠️ <b><c>ChangeFraction</c> is no longer 1000/800 — 1, and this class could not have
/// noticed.</b> It is now a ratio of daily RATES, 1200 against 800/2, because dividing two totals
/// the ledger measured to different extents reported the difference in measurement as a difference
/// in spending. Both sides of the comparison below are built from the same report object, so the
/// derivation can change under this test without moving it: the figure itself is pinned by
/// literal in <c>CostPreviousWindowTests</c>, and what is pinned HERE is only that the wire
/// carries whatever the report says.</para>
/// </summary>
public class CostWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    private static CostHistoryReport Report() => new()
    {
        From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
        GroupBy = CostGroupBy.Customer,
        Currency = "SEK",
        TotalCost = 1000m,
        PreviousTotalCost = 800m,
        UnattributedCost = 11m,
        MultiAppCost = 22m,
        PlatformCost = 33m,
        SharedChargedOutCost = 44m,
        CoveredHours = 700m,
        ExpectedHours = 900m,
        GapHours = 55m,

        // The earlier window, with figures distinct from the requested one's — these used not to
        // be fetched at all, so a response that silently reused the requested window's coverage
        // for both would otherwise look right.
        PreviousCoveredHours = 600m,
        PreviousExpectedHours = 750m,
        PreviousGapHours = 66m,
        PreviousMeasuredDays = 2m,
        PreviousUnmeasuredDays = 3,
        PreviousDays = 31,

        Warnings = ["one cluster could not be priced"],
        Daily =
        [
            new CostHistoryPoint
            {
                Day = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                Cost = 7m,
                CoveredHours = 20m,
                ExpectedHours = 24m,

                // ⚠️ Set, not defaulted, or the statement in this class's remarks about
                // DailyAverage becomes false while every assertion stays green. The denominator
                // is now MeasuredDayFraction — covered over a whole day's hours — and this
                // comparison builds both sides from the same report object, so a zero here would
                // make DailyAverage 0 on both sides and the test could not notice.
                FullDayHours = 24m,
            },
        ],
        Groups =
        [
            new CostHistoryGroup
            {
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Label = "Acme AB",
                Cost = 300m,
                PreviousCost = 250m,

                // Window-level, so every line of one report carries the same pair; set to
                // something other than the report's own so a mix-up between the two shows.
                MeasuredDays = 3m,
                PreviousMeasuredDays = 5m,

                CpuCoreHours = 1.5d,
                MemoryGiBHours = 2.5d,
                StorageGiBHours = 3.5d,
                IsUnattributed = true,
            },
        ],
    };

    [Fact]
    public void The_history_report_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        CostHistoryReport report = Report();

        // Copied verbatim from origin/main's handler — the independent oracle.
        object asItWas = new
        {
        from = report.From,
        to = report.To,
        currency = report.Currency,
        groupBy = report.GroupBy.ToString().ToLowerInvariant(),
        totalCost = report.TotalCost,
        previousTotalCost = report.PreviousTotalCost,
        change = report.Change,
        changeFraction = report.ChangeFraction,
        unattributedCost = report.UnattributedCost,
        multiAppCost = report.MultiAppCost,
        platformCost = report.PlatformCost,
        sharedChargedOutCost = report.SharedChargedOutCost,
        dailyAverage = report.DailyAverage,

        // ⚠️ FROM HERE THE ORACLE IS NOT origin/main's HANDLER. `previousDailyAverage`,
        // `previousCoverage`, and `measuredDays`/`unmeasuredDays` inside both coverage objects are
        // ADDITIONS, so no earlier version of the handler can state them. They are written out
        // here anyway, in position, because this test's job is the key ORDER as well as the names
        // — a client reading the document generated from this record sees the order — and an
        // addition dropped into the wrong place is the kind of change nothing else would catch.
        previousDailyAverage = report.PreviousDailyAverage,

        // Coverage travels with every figure: an under-measured window
        // under-states cost, and a caller that cannot see that will read the
        // shortfall as a saving.
        coverage = new
        {
            coveredHours = report.CoveredHours,
            expectedHours = report.ExpectedHours,
            gapHours = report.GapHours,
            measuredDays = report.MeasuredDays,
            unmeasuredDays = report.UnmeasuredDays,
            completeness = report.Completeness,
            complete = report.IsComplete,
        },

        // … including the one total that used to have none beside it.
        previousCoverage = new
        {
            coveredHours = report.PreviousCoveredHours,
            expectedHours = report.PreviousExpectedHours,
            gapHours = report.PreviousGapHours,
            measuredDays = report.PreviousMeasuredDays,
            unmeasuredDays = report.PreviousUnmeasuredDays,
            completeness = report.PreviousCompleteness,
            complete = report.PreviousIsComplete,
        },
        warnings = report.Warnings,
        daily = report.Daily.Select(d => new
        {
            day = d.Day,
            cost = d.Cost,
            coveredHours = d.CoveredHours,
            expectedHours = d.ExpectedHours,
            complete = d.IsComplete,
        }),
        groups = report.Groups.Select(g => new
        {
            id = g.Id,
            label = g.Label,
            cost = g.Cost,
            previousCost = g.PreviousCost,
            change = g.Change,
            changeFraction = g.ChangeFraction,
            isNew = g.IsNew,
            isGone = g.IsGone,
            cpuCoreHours = g.CpuCoreHours,
            memoryGiBHours = g.MemoryGiBHours,
            storageGiBHours = g.StorageGiBHours,
            unattributed = g.IsUnattributed,
        }),

        };

        JsonSerializer.Serialize(CostHistoryResponse.FromReport(report), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named as well as compared, so a failure says WHICH key moved instead of printing two long
    /// strings. The nested objects are checked too — <c>coverage</c> is a shape of its own, and a
    /// client reading <c>coverage.complete</c> breaks just as hard as one reading <c>totalCost</c>.
    /// </summary>
    [Fact]
    public void The_history_keys_are_the_ones_clients_already_read()
    {
        System.Text.Json.Nodes.JsonObject root =
            JsonSerializer.SerializeToNode(
                CostHistoryResponse.FromReport(Report()), AsTheApiSerialises)!.AsObject();

        root.Select(p => p.Key).Should().Equal(
            "from", "to", "currency", "groupBy", "totalCost", "previousTotalCost", "change",
            "changeFraction", "unattributedCost", "multiAppCost", "platformCost",
            "sharedChargedOutCost", "dailyAverage", "previousDailyAverage", "coverage",
            "previousCoverage", "warnings", "daily", "groups");

        root["coverage"]!.AsObject().Select(p => p.Key).Should().Equal(
            "coveredHours", "expectedHours", "gapHours", "measuredDays", "unmeasuredDays",
            "completeness", "complete");

        // The same shape, so a client reads one parser for both windows — named separately
        // because an `previousCoverage` that quietly differed from `coverage` would be worse than
        // not having it.
        root["previousCoverage"]!.AsObject().Select(p => p.Key).Should().Equal(
            "coveredHours", "expectedHours", "gapHours", "measuredDays", "unmeasuredDays",
            "completeness", "complete");

        root["daily"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "day", "cost", "coveredHours", "expectedHours", "complete");

        root["groups"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "id", "label", "cost", "previousCost", "change", "changeFraction", "isNew", "isGone",
            "cpuCoreHours", "memoryGiBHours", "storageGiBHours", "unattributed");
    }

    /// <summary>
    /// ⚠️ The renames, asserted as VALUES. The key lists above would be satisfied by a mapping
    /// that put the right names on the wrong figures; these pin the three that are easiest to get
    /// wrong because the source member is spelled differently from the wire name.
    /// </summary>
    [Fact]
    public void The_renamed_fields_carry_the_value_their_source_member_holds()
    {
        CostHistoryReport report = Report();
        CostHistoryResponse subject = CostHistoryResponse.FromReport(report);

        subject.GroupBy.Should().Be(
            "customer", "the enum is flattened to a lowercase string, not serialised as 0");

        subject.Coverage.Complete.Should().Be(
            report.IsComplete, "`complete` is IsComplete, and 700 of 900 hours is not complete");
        subject.Coverage.Complete.Should().BeFalse();

        subject.Groups.Single().Unattributed.Should().Be(
            report.Groups[0].IsUnattributed, "`unattributed` is IsUnattributed");
        subject.Groups.Single().Unattributed.Should().BeTrue();

        // Derived figures, so a mapping that read the wrong source member would differ.
        subject.Change.Should().Be(200m);
        subject.Coverage.Completeness.Should().BeApproximately(700d / 900d, 1e-12);

        // ⚠️ Rates, not totals. 1000 over 20/24 of a day against 800 over two days, and
        // 300 over three days against 250 over five — so a mapping that still divided the totals
        // would read 0.25 and 0.2 here, which is what both of these said.
        subject.PreviousDailyAverage.Should().Be(400m);
        subject.ChangeFraction.Should().BeApproximately(2d, 1e-9);
        subject.Groups.Single().ChangeFraction.Should().BeApproximately(1d, 1e-9);

        // ⚠️ `previousCoverage` must read the Previous* members and not the requested
        // window's. Every figure in the fixture differs between the two windows precisely so that
        // a FromPrevious copied from From and left unedited shows here rather than passing.
        subject.PreviousCoverage.CoveredHours.Should().Be(600m);
        subject.PreviousCoverage.ExpectedHours.Should().Be(750m);
        subject.PreviousCoverage.GapHours.Should().Be(66m);
        subject.PreviousCoverage.MeasuredDays.Should().Be(2m);
        subject.PreviousCoverage.UnmeasuredDays.Should().Be(3);
        subject.PreviousCoverage.Completeness.Should().BeApproximately(0.8d, 1e-12);
        subject.PreviousCoverage.Complete.Should().BeFalse();

        subject.Coverage.MeasuredDays.Should().Be(
            20m / 24m, "the requested window's own figure, not the earlier window's 2");
        subject.Coverage.UnmeasuredDays.Should().Be(0);
    }

    [Fact]
    public void A_month_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        CostMonth m = new()
        {
            Year = 2026,
            Month = 10,
            Cost = 1234.56m,
            CoveredHours = 700m,
            ExpectedHours = 744m,
            Currency = "SEK",
        };

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            year = m.Year,
            month = m.Month,
            label = m.Label,
            cost = m.Cost,
            currency = m.Currency,
            completeness = m.Completeness,
            complete = m.IsComplete,
        };

        JsonSerializer.Serialize(CostMonthResponse.From(m), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// <c>Label</c> comes from <c>Start.ToString("MMM yyyy")</c>, which is culture-dependent. That
    /// is pre-existing behaviour and not changed here, but it is asserted to come from the source
    /// member rather than being rebuilt in the mapping — rebuilding it is how the two would drift.
    /// </summary>
    [Fact]
    public void A_months_label_is_the_source_members_label()
    {
        CostMonth m = new() { Year = 2026, Month = 10 };

        CostMonthResponse.From(m).Label.Should().Be(m.Label);
    }
}
