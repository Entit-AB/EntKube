using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using EntKube.Telemetry;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>Which errors count as the same error. Both directions matter equally.</summary>
public class ErrorFingerprintTests
{
    [Theory]
    [InlineData("Order 1042 failed", "Order 7731 failed")]
    [InlineData("user 3f2504e0-4f89-11d3-9a0c-0305e82c3301 denied", "user 9a7b330a-a736-51e5-af7f-feaf819cdc9f denied")]
    [InlineData("connect to 10.0.0.4:5432 failed", "connect to 192.168.1.9:6379 failed")]
    [InlineData("at 2026-11-06T10:00:00.123Z request failed", "at 2026-11-07 11:30:15,456 request failed")]
    [InlineData("object 0xdeadbeef freed", "object 0x1f freed")]
    [InlineData("hash abc123def456 mismatch", "hash 9f8e7d6c5b4a mismatch")]
    [InlineData("no such key \"alpha\"", "no such key \"beta\"")]
    [InlineData("no such key 'alpha'", "no such key 'beta'")]
    [InlineData("took 12.5 seconds", "took 9 seconds")]
    [InlineData("failed   with  spaces", "failed with spaces")]
    public void Two_occurrences_of_one_fault_normalize_alike(string a, string b)
    {
        ErrorFingerprint.Normalize(a).Should().Be(ErrorFingerprint.Normalize(b));
    }

    [Theory]
    [InlineData("connection refused", "connection reset")]
    [InlineData("Order failed", "Payment failed")]
    [InlineData("NullReferenceException at Checkout", "NullReferenceException at Basket")]
    public void Two_different_faults_stay_apart(string a, string b)
    {
        ErrorFingerprint.Normalize(a).Should().NotBe(ErrorFingerprint.Normalize(b));
    }

    [Fact]
    public void Only_the_first_line_counts_because_stack_frames_change_with_every_release()
    {
        ErrorFingerprint.Normalize("TypeError: x is null\n  at a.js:1\n  at b.js:2")
            .Should().Be(ErrorFingerprint.Normalize("TypeError: x is null\r\n  at c.js:9"));
    }

    [Fact]
    public void An_apostrophe_in_prose_is_not_a_quoted_string()
    {
        ErrorFingerprint.Normalize("can't reach db, won't retry").Should().Be("can't reach db, won't retry");
    }

    [Fact]
    public void A_plain_word_of_hex_letters_is_a_word()
    {
        ErrorFingerprint.Normalize("the defaced database").Should().Be("the defaced database");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Nothing_normalizes_to_nothing(string? message, string expected)
    {
        ErrorFingerprint.Normalize(message).Should().Be(expected);
    }

    [Fact]
    public void A_huge_line_is_cut_and_a_pathological_one_stays_fast()
    {
        string line = new string('"', 200_000) + string.Concat(Enumerable.Repeat("1.", 100_000));

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        ErrorFingerprint.Normalize(line);
        watch.Stop();

        watch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Theory]
    [InlineData("api-7d9f8c6b5-x2x4z", "api")]
    [InlineData("checkout-api-5c8f7b9d4f-bcdfg", "checkout-api")]
    [InlineData("postgres-0", "postgres")]
    [InlineData("kafka-broker-12", "kafka-broker")]
    [InlineData("node-exporter-x7k2p", "node-exporter")]
    [InlineData("standalone", "standalone")]
    [InlineData("my-web-app", "my-web-app")]            // real words, however it is split
    [InlineData("cert-manager-webhook", "cert-manager-webhook")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_pod_reduces_to_its_workload(string? pod, string expected)
    {
        ErrorFingerprint.Workload(pod).Should().Be(expected);
    }

    [Fact]
    public void Parts_are_kept_apart_so_shifting_a_boundary_is_a_different_id()
    {
        ErrorFingerprint.Compute("ab", "c").Should().NotBe(ErrorFingerprint.Compute("a", "bc"));
        ErrorFingerprint.Compute("ab", "c").Should().Be(ErrorFingerprint.Compute("ab", "c"));
        ErrorFingerprint.Compute("x").Should().HaveLength(32);
    }
}

public sealed class ErrorTrackingServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ErrorTrackingService service;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly DateTime t0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    public ErrorTrackingServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);
        service = new ErrorTrackingService(factory, new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static LokiLogStream Stream(string pod, params (DateTime At, string Line)[] lines) => new()
    {
        Labels = new() { ["namespace"] = "shop", ["pod"] = pod, ["container"] = "app" },
        Entries = [.. lines.Select(l => new LokiLogEntry { Timestamp = l.At, Line = l.Line, DetectedLevel = LogLevel.Error })],
    };

    private ErrorObservation Seen(DateTime at, string fingerprint = "f1", long count = 1, string sample = "boom 1") =>
        new(fingerprint, "boom <n>", "shop", "api", sample, null, count, at, at);

    private Task<int> ApplyAsync(params ErrorObservation[] observations) =>
        service.ApplyAsync(tenantId, ErrorIssueSource.Log, clusterId, "prod", observations, partial: false, t0);

    private async Task<ErrorIssue> OnlyAsync() => (await service.ListAsync(tenantId)).Single();

    [Fact]
    public void Replicas_of_a_workload_and_varying_ids_fold_into_one_observation()
    {
        List<ErrorObservation> seen = ErrorTrackingService.ObserveLogs(
        [
            Stream("api-7d9f8c6b5-ccccc", (t0, "Order 1 failed"), (t0.AddSeconds(5), "Order 2 failed")),
            Stream("api-7d9f8c6b5-bbbbb", (t0.AddSeconds(2), "Order 3 failed")),
        ]);

        ErrorObservation one = seen.Should().ContainSingle().Subject;
        one.Count.Should().Be(3);
        one.FirstAt.Should().Be(t0);
        one.LastAt.Should().Be(t0.AddSeconds(5));
        one.Sample.Should().Be("Order 2 failed", "the sample is the latest real occurrence");
        one.Workload.Should().Be("api");
    }

    [Fact]
    public void The_same_message_from_two_workloads_is_two_issues()
    {
        ErrorTrackingService.ObserveLogs(
        [
            Stream("api-7d9f8c6b5-ccccc", (t0, "connection refused")),
            Stream("worker-7d9f8c6b5-ccccc", (t0, "connection refused")),
        ]).Should().HaveCount(2);
    }

    [Fact]
    public void A_browser_error_s_counts_are_summed_when_messages_normalize_alike()
    {
        ErrorObservation one = ErrorTrackingService.ObserveBrowser(
        [
            new RumTopError("Item 5 missing", 3, t0),
            new RumTopError("Item 9 missing", 4, t0.AddMinutes(1)),
        ]).Should().ContainSingle().Subject;

        one.Count.Should().Be(7);
        one.Sample.Should().Be("Item 9 missing");
    }

    [Fact]
    public async Task A_first_sighting_creates_an_open_issue()
    {
        (await ApplyAsync(Seen(t0, count: 3))).Should().Be(1);

        ErrorIssue issue = await OnlyAsync();
        issue.TenantId.Should().Be(tenantId);
        issue.Status.Should().Be(ErrorIssueStatus.Open);
        issue.Occurrences.Should().Be(3);
        issue.FirstSeenAt.Should().Be(t0);
        issue.LastSeenAt.Should().Be(t0);
    }

    [Fact]
    public async Task Later_sightings_add_up_and_keep_the_first_time()
    {
        await ApplyAsync(Seen(t0, count: 3));
        (await ApplyAsync(Seen(t0.AddMinutes(5), count: 4, sample: "boom 2"))).Should().Be(0, "this is not a new issue");

        ErrorIssue issue = await OnlyAsync();
        issue.Occurrences.Should().Be(7);
        issue.FirstSeenAt.Should().Be(t0);
        issue.LastSeenAt.Should().Be(t0.AddMinutes(5));
        issue.SampleMessage.Should().Be("boom 2");
    }

    [Fact]
    public async Task An_older_sighting_does_not_move_last_seen_or_replace_the_sample()
    {
        await ApplyAsync(Seen(t0, sample: "newest"));
        await ApplyAsync(Seen(t0.AddMinutes(-5), sample: "older"));

        ErrorIssue issue = await OnlyAsync();
        issue.LastSeenAt.Should().Be(t0);
        issue.SampleMessage.Should().Be("newest");
        issue.FirstSeenAt.Should().Be(t0.AddMinutes(-5));
    }

    [Fact]
    public async Task An_occurrence_after_resolving_reopens_the_issue_as_a_regression()
    {
        await ApplyAsync(Seen(t0));
        ErrorIssue issue = await OnlyAsync();
        await service.SetStatusAsync(tenantId, issue.Id, ErrorIssueStatus.Resolved, "me");

        DateTime later = DateTime.UtcNow.AddMinutes(10);
        await ApplyAsync(Seen(later));

        ErrorIssue after = await OnlyAsync();
        after.Status.Should().Be(ErrorIssueStatus.Open);
        after.Regressions.Should().Be(1);
        after.RegressedAt.Should().Be(later);
        after.ResolvedAt.Should().BeNull();
        after.ResolvedBy.Should().BeNull();
    }

    /// <summary>A scan looks back, so it can read lines from before Resolve was pressed.</summary>
    [Fact]
    public async Task An_occurrence_from_before_resolving_does_not_reopen_it()
    {
        await ApplyAsync(Seen(t0));
        await service.SetStatusAsync(tenantId, (await OnlyAsync()).Id, ErrorIssueStatus.Resolved, "me");

        await ApplyAsync(Seen(DateTime.UtcNow.AddMinutes(-1)));

        ErrorIssue after = await OnlyAsync();
        after.Status.Should().Be(ErrorIssueStatus.Resolved);
        after.Regressions.Should().Be(0);
        after.Occurrences.Should().Be(2, "it still happened");
    }

    [Fact]
    public async Task An_ignored_issue_counts_on_and_stays_ignored()
    {
        await ApplyAsync(Seen(t0));
        await service.SetStatusAsync(tenantId, (await OnlyAsync()).Id, ErrorIssueStatus.Ignored, "me");

        await ApplyAsync(Seen(DateTime.UtcNow.AddMinutes(10)));

        ErrorIssue after = await OnlyAsync();
        after.Status.Should().Be(ErrorIssueStatus.Ignored);
        after.Occurrences.Should().Be(2);
        after.Regressions.Should().Be(0);
    }

    [Fact]
    public async Task Resolving_records_who_and_reopening_clears_it()
    {
        await ApplyAsync(Seen(t0));
        Guid id = (await OnlyAsync()).Id;

        await service.SetStatusAsync(tenantId, id, ErrorIssueStatus.Resolved, "me");
        (await OnlyAsync()).ResolvedBy.Should().Be("me");

        await service.SetStatusAsync(tenantId, id, ErrorIssueStatus.Open, "me");
        (await OnlyAsync()).ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task One_partial_scan_marks_the_total_a_lower_bound_for_good()
    {
        await service.ApplyAsync(tenantId, ErrorIssueSource.Log, clusterId, "prod", [Seen(t0)], partial: true, t0);
        await service.ApplyAsync(tenantId, ErrorIssueSource.Log, clusterId, "prod", [Seen(t0)], partial: false, t0);

        (await OnlyAsync()).CountIsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task A_scope_stops_gaining_issues_at_its_limit_but_existing_ones_still_count()
    {
        await using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.ErrorIssues.AddRange(Enumerable.Range(0, ErrorTrackingService.MaxIssuesPerScope).Select(i => new ErrorIssue
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ScopeId = clusterId, ScopeName = "prod", Fingerprint = $"x{i}",
                Title = "t", SampleMessage = "s", Occurrences = 1, FirstSeenAt = t0, LastSeenAt = t0,
            }));
            await db.SaveChangesAsync();
        }

        (await ApplyAsync(Seen(t0, fingerprint: "brand-new"))).Should().Be(0);
        await ApplyAsync(Seen(t0, fingerprint: "x0", count: 5));

        await using ApplicationDbContext check = factory.CreateDbContext();
        (await check.ErrorIssues.CountAsync()).Should().Be(ErrorTrackingService.MaxIssuesPerScope);
        (await check.ErrorIssues.SingleAsync(i => i.Fingerprint == "x0")).Occurrences.Should().Be(6);
    }

    [Fact]
    public async Task Long_text_is_cut_to_the_column()
    {
        await ApplyAsync(new ErrorObservation("f", new string('t', 900), null, null, new string('s', 9000), null, 1, t0, t0));

        ErrorIssue issue = await OnlyAsync();
        issue.Title.Should().HaveLength(ErrorTrackingService.MaxTitleLength);
        issue.SampleMessage.Should().HaveLength(ErrorTrackingService.MaxSampleLength);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_or_change_an_issue_and_does_not_share_its_fingerprint()
    {
        await ApplyAsync(Seen(t0));
        Guid id = (await OnlyAsync()).Id;

        (await service.ListAsync(strangerId)).Should().BeEmpty();
        (await service.SetStatusAsync(strangerId, id, ErrorIssueStatus.Ignored, "x")).Should().BeFalse();
        (await OnlyAsync()).Status.Should().Be(ErrorIssueStatus.Open);

        await service.ApplyAsync(strangerId, ErrorIssueSource.Log, clusterId, "theirs", [Seen(t0)], false, t0);

        (await OnlyAsync()).Occurrences.Should().Be(1, "the same fingerprint elsewhere is another tenant's issue");
    }

    [Fact]
    public async Task The_list_narrows_by_status_with_the_most_recent_first()
    {
        await ApplyAsync(Seen(t0, fingerprint: "old"));
        await ApplyAsync(Seen(t0.AddHours(1), fingerprint: "new"));
        await service.SetStatusAsync(tenantId, (await service.ListAsync(tenantId))[1].Id, ErrorIssueStatus.Ignored, "me");

        (await service.ListAsync(tenantId)).Select(i => i.Fingerprint).Should().Equal("new", "old");
        (await service.ListAsync(tenantId, ErrorIssueStatus.Open)).Select(i => i.Fingerprint).Should().Equal("new");
    }

    [Fact]
    public async Task A_cursor_is_claimed_once_however_many_instances_try()
    {
        (await service.TryClaimAsync(tenantId, clusterId, null, t0)).Should().BeTrue();
        (await service.TryClaimAsync(tenantId, clusterId, null, t0)).Should().BeFalse("the row exists");

        (await service.TryClaimAsync(tenantId, clusterId, t0, t0.AddMinutes(1))).Should().BeTrue();
        (await service.TryClaimAsync(tenantId, clusterId, t0, t0.AddMinutes(1))).Should().BeFalse("it has moved on");

        (await service.CursorAsync(tenantId, clusterId)).Should().Be(t0.AddMinutes(1));
    }

    [Fact]
    public async Task Another_tenant_cannot_read_or_move_a_cursor()
    {
        await service.TryClaimAsync(tenantId, clusterId, null, t0);

        (await service.CursorAsync(strangerId, clusterId)).Should().BeNull();
        (await service.TryClaimAsync(strangerId, clusterId, t0, t0.AddHours(1))).Should().BeFalse();
        (await service.CursorAsync(tenantId, clusterId)).Should().Be(t0);
    }

    [Fact]
    public async Task New_and_regressed_issues_count_for_the_alert_and_old_or_closed_ones_do_not()
    {
        DateTime since = t0;
        Guid otherCluster = Guid.NewGuid();

        await using ApplicationDbContext seed = factory.CreateDbContext();
        ErrorIssue Issue(string f, DateTime first, ErrorIssueStatus status = ErrorIssueStatus.Open, DateTime? regressed = null, Guid? scope = null) => new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ScopeId = scope ?? clusterId, ScopeName = "p", Fingerprint = f, Title = "t",
            SampleMessage = "s", FirstSeenAt = first, LastSeenAt = first, Status = status, RegressedAt = regressed,
        };
        seed.ErrorIssues.AddRange(
            Issue("new", t0.AddMinutes(1)),
            Issue("regressed", t0.AddDays(-5), regressed: t0.AddMinutes(1)),
            Issue("old", t0.AddDays(-5)),
            Issue("new-but-ignored", t0.AddMinutes(1), ErrorIssueStatus.Ignored),
            Issue("other-cluster", t0.AddMinutes(1), scope: otherCluster));
        await seed.SaveChangesAsync();

        await using TelemetryDbContext db = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();

        (await ErrorTrackingService.CountNewOrRegressedAsync(db, tenantId, clusterId, since, default)).Should().Be(2);
        (await ErrorTrackingService.CountNewOrRegressedAsync(db, strangerId, clusterId, since, default)).Should().Be(0);
    }

    [Fact]
    public async Task Issues_not_seen_for_a_long_time_age_out()
    {
        await ApplyAsync(Seen(t0.AddDays(-91), fingerprint: "stale"));
        await ApplyAsync(Seen(t0.AddDays(-89), fingerprint: "recent"));

        (await service.PruneAsync(t0)).Should().Be(1);

        (await OnlyAsync()).Fingerprint.Should().Be("recent");
    }

    [Fact]
    public void A_window_stops_short_of_now_and_is_bounded()
    {
        DateTime now = t0;

        (DateTime From, DateTime To) first = ErrorScanSweep.Window(null, now);
        first.To.Should().Be(now - ErrorScanSweep.IngestLag);
        first.From.Should().Be(first.To - ErrorScanSweep.FirstScanLookBack);

        DateTime recent = now.AddMinutes(-3);
        ErrorScanSweep.Window(recent, now).Should().Be((recent, now - ErrorScanSweep.IngestLag));

        DateTime behind = now.AddHours(-5);
        ErrorScanSweep.Window(behind, now).Should().Be((behind, behind + ErrorScanSweep.MaxWindow));
    }

    // ── A pass over a cluster or site, against a mocked store ──

    private Mock<ILogBackend> Backend(List<LokiLogStream>? streams = null)
    {
        Mock<ILogBackend> log = new();
        log.Setup(l => l.GetNamespacesAsync(clusterId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(KubernetesOperationResult<List<string>>.Success(["shop"]));
        log.Setup(l => l.QueryAsync(clusterId, It.IsAny<LogQueryFilter>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(KubernetesOperationResult<List<LokiLogStream>>.Success(streams ?? []));
        return log;
    }

    private Task<int> ScanAsync(Mock<ILogBackend> log, DateTime now) =>
        ErrorScanSweep.ScanClusterAsync(service, log.Object, tenantId, clusterId, "prod", now, default);

    [Fact]
    public async Task A_pass_asks_only_for_errors_and_moves_the_cursor()
    {
        Mock<ILogBackend> log = Backend([Stream("api-7d9f8c6b5-ccccc", (t0, "boom"))]);

        (await ScanAsync(log, t0.AddMinutes(1))).Should().Be(1);

        log.Verify(l => l.QueryAsync(clusterId, It.Is<LogQueryFilter>(f => f.MinLevel == LogLevel.Error && f.Namespaces.Contains("shop")),
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), ErrorScanSweep.MaxLinesPerScan, It.IsAny<CancellationToken>()));

        (await service.CursorAsync(tenantId, clusterId)).Should().Be(t0.AddMinutes(1) - ErrorScanSweep.IngestLag);
    }

    /// <summary>⚠️ Moving the cursor past a read that failed would lose those errors for good.</summary>
    [Fact]
    public async Task A_failed_read_leaves_the_cursor_so_the_window_is_tried_again()
    {
        Mock<ILogBackend> log = Backend();
        log.Setup(l => l.QueryAsync(clusterId, It.IsAny<LogQueryFilter>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(KubernetesOperationResult<List<LokiLogStream>>.Failure("down"));

        await ScanAsync(log, t0);

        (await service.CursorAsync(tenantId, clusterId)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_namespace_listing_leaves_the_cursor_too()
    {
        Mock<ILogBackend> log = Backend();
        log.Setup(l => l.GetNamespacesAsync(clusterId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(KubernetesOperationResult<List<string>>.Failure("down"));

        await ScanAsync(log, t0);

        (await service.CursorAsync(tenantId, clusterId)).Should().BeNull();
    }

    [Fact]
    public async Task A_quiet_window_still_moves_the_cursor()
    {
        await ScanAsync(Backend(), t0);

        (await service.CursorAsync(tenantId, clusterId)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_second_instance_that_read_the_same_window_counts_nothing()
    {
        Mock<ILogBackend> log = Backend([Stream("api-7d9f8c6b5-ccccc", (t0, "boom"))]);
        DateTime through = t0.AddMinutes(1) - ErrorScanSweep.IngestLag;

        // The other instance claims between this one's read and its claim.
        log.Setup(l => l.QueryAsync(clusterId, It.IsAny<LogQueryFilter>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await service.TryClaimAsync(tenantId, clusterId, null, through);
                return KubernetesOperationResult<List<LokiLogStream>>.Success([Stream("api-7d9f8c6b5-ccccc", (t0, "boom"))]);
            });

        (await ScanAsync(log, t0.AddMinutes(1))).Should().Be(0);

        (await service.ListAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Hitting_the_line_limit_marks_the_counts_as_a_lower_bound()
    {
        Mock<ILogBackend> log = Backend([Stream(
            "api-7d9f8c6b5-ccccc",
            [.. Enumerable.Range(0, ErrorScanSweep.MaxLinesPerScan).Select(_ => (t0, "boom"))])]);

        await ScanAsync(log, t0.AddMinutes(1));

        (await OnlyAsync()).CountIsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task One_line_short_of_the_limit_is_a_true_count()
    {
        Mock<ILogBackend> log = Backend([Stream(
            "api-7d9f8c6b5-ccccc",
            [.. Enumerable.Range(0, ErrorScanSweep.MaxLinesPerScan - 1).Select(_ => (t0, "boom"))])]);

        await ScanAsync(log, t0.AddMinutes(1));

        (await OnlyAsync()).CountIsPartial.Should().BeFalse();
    }

    private Task<int> ScanSiteAsync(Mock<IRumQueryService> rum, Guid siteId, DateTime now) =>
        ErrorScanSweep.ScanSiteAsync(service, rum.Object, tenantId, siteId, "Shop", now, default);

    [Fact]
    public async Task A_site_s_errors_become_browser_issues()
    {
        Guid siteId = Guid.NewGuid();
        Mock<IRumQueryService> rum = new();
        rum.Setup(r => r.TryGetTopErrorsAsync(tenantId, siteId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), ErrorScanSweep.MaxBrowserGroups, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RumTopError("TypeError: x", 4, t0)]);

        (await ScanSiteAsync(rum, siteId, t0.AddMinutes(1))).Should().Be(1);

        ErrorIssue issue = await OnlyAsync();
        issue.Source.Should().Be(ErrorIssueSource.Browser);
        issue.ScopeId.Should().Be(siteId);
        issue.ScopeName.Should().Be("Shop");
        issue.Occurrences.Should().Be(4);
    }

    /// <summary>
    /// ⚠️ The RUM store's own read answers an empty list on failure, which is why the sweep uses the
    /// one that can say so: an empty list would have moved the cursor and lost the window.
    /// </summary>
    [Fact]
    public async Task A_site_read_that_failed_leaves_the_cursor()
    {
        Guid siteId = Guid.NewGuid();
        Mock<IRumQueryService> rum = new();
        rum.Setup(r => r.TryGetTopErrorsAsync(tenantId, siteId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<RumTopError>?)null);

        await ScanSiteAsync(rum, siteId, t0);

        (await service.CursorAsync(tenantId, siteId)).Should().BeNull();
    }
}

/// <summary>The real sweep, the real store, and a real error line.</summary>
public sealed class ErrorScanLiveTests(LogPipelineLiveFixture host) : IClassFixture<LogPipelineLiveFixture>
{
    [Fact]
    public async Task Error_lines_in_the_real_store_become_issues_and_info_lines_do_not()
    {
        using IServiceScope scope = host.Factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        ErrorTrackingService issues = services.GetRequiredService<ErrorTrackingService>();
        ILogBackend log = services.GetRequiredService<ITelemetryBackends>().LogsFor(host.TenantId);

        await ErrorScanSweep.ScanClusterAsync(issues, log, host.TenantId, host.ClusterId, "c1", host.Now.AddMinutes(2), default);

        List<ErrorIssue> found = await issues.ListAsync(host.TenantId);

        // Three error-or-fatal lines were written, and the warn and info ones must not appear.
        found.Should().HaveCount(3);
        found.Select(i => i.Workload).Should().BeEquivalentTo(["api", "api", "worker"]);
        found.Should().OnlyContain(i => i.Occurrences == 1 && i.Status == ErrorIssueStatus.Open);
    }
}

[Collection(ApiSurfaceCollection.Name)]
public sealed class ErrorIssueApiTests(GeneratedOpenApiDocumentFixture fixture)
{
    [Fact]
    public async Task It_lists_this_tenant_s_issues_and_never_another_s()
    {
        string marker = Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;

        await using (ApplicationDbContext db = fixture.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
        {
            db.ErrorIssues.AddRange(
                new ErrorIssue
                {
                    Id = Guid.NewGuid(), TenantId = fixture.TokenTenantId, ScopeId = Guid.NewGuid(), ScopeName = "c", Fingerprint = marker,
                    Title = marker, SampleMessage = "s", Occurrences = 7, FirstSeenAt = now, LastSeenAt = now,
                },
                new ErrorIssue
                {
                    Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ScopeId = Guid.NewGuid(), ScopeName = "c", Fingerprint = marker,
                    Title = "stranger-" + marker, SampleMessage = "s", FirstSeenAt = now, LastSeenAt = now,
                });
            await db.SaveChangesAsync();
        }

        (HttpStatusCode status, _, string body) = await fixture.SendForResponseAsync("GET", "/api/v1/error-issues?status=open", "ops:read");

        status.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("stranger-" + marker);

        JsonElement mine = JsonDocument.Parse(body).RootElement.EnumerateArray().Single(e => e.GetProperty("title").GetString() == marker);
        mine.GetProperty("status").GetString().Should().Be("Open");
        mine.GetProperty("source").GetString().Should().Be("Log");
        mine.GetProperty("occurrences").GetInt64().Should().Be(7);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("1")]
    public async Task A_status_the_enum_does_not_name_is_400_and_never_a_500(string status)
    {
        (HttpStatusCode code, _, _) = await fixture.SendForResponseAsync("GET", $"/api/v1/error-issues?status={status}", "ops:read");

        code.Should().Be(HttpStatusCode.BadRequest);
    }
}

public class ErrorTrackingRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ErrorTrackingService service;
    private readonly Guid tenantId = Guid.NewGuid();

    public ErrorTrackingRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new ErrorTrackingService(new TestDbContextFactory(connection), new ConfigurationBuilder().Build());
        Services.AddSingleton(service);
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn());
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test"))));
    }

    public new void Dispose()
    {
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task SeedAsync(string message = "boom", bool partial = false, long count = 1234) =>
        service.ApplyAsync(tenantId, ErrorIssueSource.Log, Guid.NewGuid(), "prod",
            [new ErrorObservation("f" + message, message, "shop", "api", message, null, count, DateTime.UtcNow, DateTime.UtcNow)],
            partial, DateTime.UtcNow);

    private IRenderedComponent<ErrorTracking> Page()
    {
        IRenderedComponent<ErrorTracking> cut = Render<ErrorTracking>(p => p.Add(c => c.TenantId, tenantId));
        return cut;
    }

    [Fact]
    public async Task An_issue_shows_where_it_happens_and_how_often()
    {
        await SeedAsync();

        IRenderedComponent<ErrorTracking> cut = Page();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("prod / shop / api").And.Contain("1,234"));
    }

    [Fact]
    public async Task A_lower_bound_is_marked_as_one()
    {
        await SeedAsync(partial: true);

        IRenderedComponent<ErrorTracking> cut = Page();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1,234+"));
    }

    [Fact]
    public async Task Markup_in_an_error_stays_text()
    {
        await SeedAsync("<img src=x onerror=alert(1)>");

        IRenderedComponent<ErrorTracking> cut = Page();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());
        cut.Find("tbody tr").Click();

        cut.FindAll("img").Should().BeEmpty();
        cut.Find("pre").TextContent.Should().Be("<img src=x onerror=alert(1)>");
    }

    [Fact]
    public async Task Only_open_issues_show_at_first_and_resolving_one_takes_it_off_the_list()
    {
        await SeedAsync();

        IRenderedComponent<ErrorTracking> cut = Page();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(1));

        cut.Find("tbody tr").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Resolve").Click();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().BeEmpty());

        ErrorIssue issue = (await service.ListAsync(tenantId)).Single();
        issue.Status.Should().Be(ErrorIssueStatus.Resolved);
        issue.ResolvedBy.Should().Be("operator");
    }
}
