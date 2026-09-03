using EntKube.TelemetryNode;
using EntKube.Web.Data;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// One suite run against BOTH <see cref="ISegmentCatalog"/> implementations — the management plane's EF
/// catalog and the in-cluster node's SQLite-on-a-volume catalog.
///
/// This matters more than testing either one alone. The catalog decides which segments a query opens, so
/// a behavioural difference between the two would not show up as an error: it would show up as logs that
/// are present when read through one deployment and missing when read through the other. Running identical
/// assertions against both is what stops the in-cluster path quietly diverging from the one in production
/// today.
/// </summary>
public sealed class SegmentCatalogContractTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _tempFiles = [];

    public static TheoryData<string> Implementations => ["ef", "sqlite"];

    private ISegmentCatalog Create(string kind)
    {
        if (kind == "ef")
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            _disposables.Add(connection);
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            context.Database.EnsureCreated();
            _disposables.Add(context);
            return new EfSegmentCatalog(new TestDbContextFactory(connection));
        }

        string path = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return new SqliteSegmentCatalog(path);
    }

    private static TelemetrySegment Segment(
        Guid tenantId, string signal, DateTime min, DateTime max, long docs = 10) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Signal = signal,
        MinTs = min,
        MaxTs = max,
        DocCount = docs,
        ObjectKey = $"{tenantId:N}/{signal}/{min:yyyy/MM/dd}/{Guid.NewGuid():N}.tar.zst",
        SizeBytes = 4096,
        SealedAt = max,
    };

    [Theory, MemberData(nameof(Implementations))]
    public async Task A_sealed_segment_round_trips_with_every_field_intact(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime min = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        DateTime max = new(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc);

        TelemetrySegment written = Segment(tenant, "logs", min, max, docs: 12_345);
        await catalog.AddAsync(written);

        IReadOnlyList<TelemetrySegment> found = await catalog.ListOverlappingAsync(tenant, "logs", null, null);

        found.Should().ContainSingle();
        TelemetrySegment read = found[0];
        read.Id.Should().Be(written.Id);
        read.DocCount.Should().Be(12_345);
        read.ObjectKey.Should().Be(written.ObjectKey);
        read.SizeBytes.Should().Be(written.SizeBytes);
        // The object key is how the archive is found in storage; a mangled timestamp would make the
        // pruning bounds wrong, which is silent data loss at query time rather than an error.
        read.MinTs.Should().BeCloseTo(min, TimeSpan.FromMilliseconds(1));
        read.MaxTs.Should().BeCloseTo(max, TimeSpan.FromMilliseconds(1));
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task Only_segments_overlapping_the_window_are_returned(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime day = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(0), day.AddHours(1)));   // before
        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(5), day.AddHours(6)));   // inside
        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(20), day.AddHours(21))); // after

        IReadOnlyList<TelemetrySegment> found =
            await catalog.ListOverlappingAsync(tenant, "logs", day.AddHours(4), day.AddHours(7));

        found.Should().ContainSingle("only one segment's time range overlaps the window");
        found[0].MinTs.Should().Be(day.AddHours(5));
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task A_segment_straddling_the_window_edge_is_included(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime day = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        // Starts before the window and ends inside it — the case a naive "MinTs >= from" would drop,
        // losing exactly the lines at the start of the range the user asked for.
        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(3), day.AddHours(5)));

        IReadOnlyList<TelemetrySegment> found =
            await catalog.ListOverlappingAsync(tenant, "logs", day.AddHours(4), day.AddHours(6));

        found.Should().ContainSingle();
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task Results_are_ordered_by_start_time(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime day = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(9), day.AddHours(10)));
        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(1), day.AddHours(2)));
        await catalog.AddAsync(Segment(tenant, "logs", day.AddHours(5), day.AddHours(6)));

        IReadOnlyList<TelemetrySegment> found = await catalog.ListOverlappingAsync(tenant, "logs", null, null);

        found.Select(s => s.MinTs).Should().BeInAscendingOrder();
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task Segments_are_isolated_by_tenant_and_by_signal(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        DateTime day = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await catalog.AddAsync(Segment(mine, "logs", day, day.AddHours(1)));
        await catalog.AddAsync(Segment(mine, "spans", day, day.AddHours(1)));
        await catalog.AddAsync(Segment(theirs, "logs", day, day.AddHours(1)));

        // Tenant isolation is a security property here, not just a filter: segments hold raw log bodies.
        (await catalog.ListOverlappingAsync(mine, "logs", null, null)).Should().ContainSingle();
        (await catalog.ListOverlappingAsync(theirs, "logs", null, null)).Should().ContainSingle();
        (await catalog.ListOverlappingAsync(mine, "logs_debug", null, null)).Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task Expiry_removes_only_what_aged_out_and_reports_it_back(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        TelemetrySegment old = Segment(tenant, "logs", now.AddDays(-40), now.AddDays(-39));
        TelemetrySegment recent = Segment(tenant, "logs", now.AddHours(-2), now.AddHours(-1));
        await catalog.AddAsync(old);
        await catalog.AddAsync(recent);

        IReadOnlyList<TelemetrySegment> removed =
            await catalog.RemoveExpiredAsync(tenant, "logs", now.AddDays(-30));

        // The caller deletes the returned objects from storage, so the list has to be exactly right:
        // too few leaks objects forever, too many deletes archives that are still cataloged.
        removed.Should().ContainSingle();
        removed[0].Id.Should().Be(old.Id);
        removed[0].ObjectKey.Should().Be(old.ObjectKey);

        IReadOnlyList<TelemetrySegment> left = await catalog.ListOverlappingAsync(tenant, "logs", null, null);
        left.Should().ContainSingle();
        left[0].Id.Should().Be(recent.Id);
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task Expiry_with_nothing_to_remove_is_a_no_op(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        await catalog.AddAsync(Segment(tenant, "logs", now.AddHours(-2), now.AddHours(-1)));

        (await catalog.RemoveExpiredAsync(tenant, "logs", now.AddDays(-30))).Should().BeEmpty();
        (await catalog.ListOverlappingAsync(tenant, "logs", null, null)).Should().ContainSingle();
    }

    [Theory, MemberData(nameof(Implementations))]
    public async Task The_earliest_indexed_time_is_reported_per_signal(string kind)
    {
        ISegmentCatalog catalog = Create(kind);
        Guid tenant = Guid.NewGuid();
        DateTime day = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        // Null means "this signal has no sealed history", which is what makes the trace list fall back to
        // aggregating raw spans instead of trusting an index that does not reach that far back.
        (await catalog.GetMinTsAsync(tenant, "traces")).Should().BeNull();

        await catalog.AddAsync(Segment(tenant, "traces", day.AddHours(8), day.AddHours(9)));
        await catalog.AddAsync(Segment(tenant, "traces", day.AddHours(2), day.AddHours(3)));

        (await catalog.GetMinTsAsync(tenant, "traces"))
            .Should().BeCloseTo(day.AddHours(2), TimeSpan.FromMilliseconds(1));
    }

    public void Dispose()
    {
        foreach (IDisposable d in _disposables) d.Dispose();
        foreach (string f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp */ }
    }
}
