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
/// <c>Change</c> is 1000-800, <c>ChangeFraction</c> is 0.25, coverage completeness is 700/900, and
/// <c>DailyAverage</c> is 1000 divided by the single day's 20/24 of coverage.</para>
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
        Warnings = ["one cluster could not be priced"],
        Daily =
        [
            new CostHistoryPoint
            {
                Day = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                Cost = 7m,
                CoveredHours = 20m,
                ExpectedHours = 24m,
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
        // Coverage travels with every figure: an under-measured window
        // under-states cost, and a caller that cannot see that will read the
        // shortfall as a saving.
        coverage = new
        {
            coveredHours = report.CoveredHours,
            expectedHours = report.ExpectedHours,
            gapHours = report.GapHours,
            completeness = report.Completeness,
            complete = report.IsComplete,
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
            "sharedChargedOutCost", "dailyAverage", "coverage", "warnings", "daily", "groups");

        root["coverage"]!.AsObject().Select(p => p.Key).Should().Equal(
            "coveredHours", "expectedHours", "gapHours", "completeness", "complete");

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
        subject.ChangeFraction.Should().BeApproximately(0.25d, 1e-12);
        subject.Coverage.Completeness.Should().BeApproximately(700d / 900d, 1e-12);
        subject.Groups.Single().ChangeFraction.Should().BeApproximately(0.2d, 1e-12);
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
