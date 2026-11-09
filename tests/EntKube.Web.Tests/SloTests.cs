using System.Net;
using System.Text.Json;
using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The arithmetic of an error budget. Every figure is chosen so that a sum in place of a ratio, an
/// off-by-one window or a strict comparison turned loose gives a different answer.
/// </summary>
public class SloArithmeticTests
{
    private static readonly DateOnly Today = new(2026, 11, 9);

    private static SloDailyTally Day(int daysAgo, long good, long total) =>
        new() { Day = Today.AddDays(-daysAgo), Good = good, Total = total };

    [Fact]
    public void A_budget_spent_exactly_still_meets_the_target()
    {
        SloStatus status = SloService.Compute(99.9, 30, [Day(0, 999, 1000)], Today);

        status.AttainedPercent.Should().BeApproximately(99.9, 1e-9);
        status.BudgetSpentFraction.Should().BeApproximately(1.0, 1e-9);
        status.IsBreached.Should().BeFalse("1 is exactly spent, and only over 1 is a breach");
    }

    /// <summary>
    /// ⚠️ The floating-point trap, at targets where the allowance is not a power of two. The first version
    /// computed in double and reported 99.9% over 1,000 events, exactly spent, as a breach.
    /// </summary>
    [Theory]
    [InlineData(99.9, 999, 1000)]
    [InlineData(99.99, 9999, 10000)]
    [InlineData(99.95, 19990, 20000)]
    [InlineData(99.5, 995, 1000)]
    [InlineData(95.0, 19, 20)]
    public void Exactly_spent_is_met_at_any_target(double target, long good, long total)
    {
        SloStatus status = SloService.Compute(target, 30, [Day(0, good, total)], Today);

        status.IsBreached.Should().BeFalse();
        status.BudgetSpentFraction.Should().Be(1.0);
    }

    [Fact]
    public void One_bad_event_more_is_a_breach()
    {
        SloStatus status = SloService.Compute(99.9, 30, [Day(0, 998, 1000)], Today);

        status.BudgetSpentFraction.Should().BeApproximately(2.0, 1e-9);
        status.IsBreached.Should().BeTrue();
    }

    [Fact]
    public void Days_are_summed_before_the_ratio_is_taken()
    {
        // Averaging per-day ratios would give 50%: (100% + 0%) / 2.
        SloStatus status = SloService.Compute(90, 30, [Day(0, 900, 900), Day(1, 0, 100)], Today);

        status.Good.Should().Be(900);
        status.Total.Should().Be(1000);
        status.AttainedPercent.Should().BeApproximately(90, 1e-9);
        status.BudgetSpentFraction.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void The_window_counts_today_and_the_days_before_it_and_nothing_older()
    {
        SloStatus status = SloService.Compute(99, 7, [Day(0, 10, 10), Day(6, 10, 10), Day(7, 0, 1000), Day(-1, 0, 1000)], Today);

        status.Total.Should().Be(20, "day 7 is outside a 7-day window and tomorrow is not in it at all");
    }

    [Fact]
    public void No_events_is_unknown_and_never_a_breach_or_a_perfect_score()
    {
        SloStatus status = SloService.Compute(99.9, 30, [Day(0, 0, 0)], Today);

        status.AttainedPercent.Should().BeNull();
        status.BudgetSpentFraction.Should().BeNull();
        status.IsBreached.Should().BeFalse();
    }

    [Fact]
    public void The_burn_rate_is_today_s_pace_against_what_the_target_allows()
    {
        // 1% bad today against a 1% allowance is exactly sustainable; the window's history does not move it.
        SloStatus status = SloService.Compute(99, 30, [Day(0, 990, 1000), Day(3, 0, 1000)], Today);

        status.BurnRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Early_in_a_day_with_nothing_yet_the_burn_rate_comes_from_yesterday()
    {
        SloService.Compute(99, 30, [Day(0, 0, 0), Day(1, 980, 1000)], Today).BurnRate.Should().BeApproximately(2.0, 1e-9);
        SloService.Compute(99, 30, [Day(2, 0, 1000)], Today).BurnRate.Should().BeNull("two days ago is not recent");
    }

    [Fact]
    public void Availability_is_requests_without_errors_out_of_requests()
    {
        (long good, long total) = SloService.Availability(
        [
            new RedBucket(default, 100, 3, 0, 0, 0),
            new RedBucket(default, 50, 0, 0, 0, 0),
            new RedBucket(default, 2, 5, 0, 0, 0),   // more errors than requests is clamped
        ]);

        total.Should().Be(152);
        good.Should().Be(147);
    }

    [Fact]
    public void Latency_counts_busy_slices_and_the_threshold_itself_is_good()
    {
        (long good, long total) = SloService.Latency(
        [
            new RedBucket(default, 10, 0, 0, 0, 200),   // exactly at the threshold
            new RedBucket(default, 10, 0, 0, 0, 201),
            new RedBucket(default, 10, 0, 0, 0, 50),
            new RedBucket(default, 0, 0, 0, 0, 9999),   // no traffic: neither good nor bad
        ], thresholdMs: 200);

        total.Should().Be(3);
        good.Should().Be(2);
    }

    [Fact]
    public void Today_and_yesterday_are_always_recomputed_and_older_days_only_once()
    {
        HashSet<DateOnly> computed = [Today, Today.AddDays(-1), Today.AddDays(-2)];

        SloService.DaysToCompute(Today, 7, 30, computed).Should().BeEquivalentTo(
            [Today, Today.AddDays(-1), Today.AddDays(-3), Today.AddDays(-4), Today.AddDays(-5), Today.AddDays(-6)]);
    }

    [Fact]
    public void Backfill_stops_where_the_source_s_data_ends()
    {
        List<DateOnly> days = SloService.DaysToCompute(Today, 30, 14, new HashSet<DateOnly>());

        days.Should().HaveCount(14);
        days.Min().Should().Be(Today.AddDays(-13));
    }
}

public sealed class SloServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly SloService service;
    private readonly TelemetryAlertRuleService rules;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid strangerClusterId = Guid.NewGuid();
    private readonly Guid checkId = Guid.NewGuid();
    private readonly Guid strangerCheckId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public SloServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            db.Database.EnsureCreated();
            Guid env = Guid.NewGuid(), strangerEnv = Guid.NewGuid();
            db.Tenants.AddRange(new Tenant { Id = tenantId, Name = "A", Slug = "a" }, new Tenant { Id = strangerId, Name = "B", Slug = "b" });
            db.SaveChanges();
            db.Environments.AddRange(
                new EntKube.Web.Data.Environment { Id = env, TenantId = tenantId, Name = "e" },
                new EntKube.Web.Data.Environment { Id = strangerEnv, TenantId = strangerId, Name = "e" });
            db.SaveChanges();
            db.KubernetesClusters.AddRange(
                new KubernetesCluster { Id = clusterId, TenantId = tenantId, EnvironmentId = env, Name = "c", ApiServerUrl = "https://a" },
                new KubernetesCluster { Id = strangerClusterId, TenantId = strangerId, EnvironmentId = strangerEnv, Name = "d", ApiServerUrl = "https://b" });
            db.SyntheticChecks.AddRange(
                new SyntheticCheck { Id = checkId, TenantId = tenantId, ClusterId = clusterId, Name = "home", Url = "https://8.8.8.8/" },
                new SyntheticCheck { Id = strangerCheckId, TenantId = strangerId, ClusterId = strangerClusterId, Name = "theirs", Url = "https://8.8.8.8/" });
            db.SaveChanges();
        }

        factory = new TestDbContextFactory(connection);
        rules = new TelemetryAlertRuleService(factory);
        service = new SloService(factory, rules);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ServiceLevelObjective Availability(string name = "checkout", double? alertAt = 50) => new()
    {
        Name = name, Kind = SloKind.ServiceAvailability, TargetPercent = 99.9, WindowDays = 30,
        ClusterId = clusterId, Service = "checkout", AlertAtBudgetSpentPercent = alertAt, Severity = "critical",
    };

    private async Task<ServiceLevelObjective> CreatedAsync(ServiceLevelObjective? input = null)
    {
        ServiceLevelObjective slo = input ?? Availability();
        (await service.SaveAsync(tenantId, slo)).Should().BeNull();
        return (await service.ListAsync(tenantId, DateTime.UtcNow)).Single(r => r.Slo.Name == slo.Name).Slo;
    }

    [Theory]
    [InlineData(100.0, 30, "100 leaves no budget")]
    [InlineData(49.0, 30, "from 50")]
    [InlineData(99.9, 10, "A window is")]
    public async Task An_objective_that_cannot_be_measured_is_refused(double target, int window, string expected)
    {
        ServiceLevelObjective slo = Availability();
        slo.TargetPercent = target;
        slo.WindowDays = window;

        (await service.SaveAsync(tenantId, slo)).Should().Contain(expected);
    }

    [Fact]
    public async Task A_latency_objective_needs_a_threshold()
    {
        ServiceLevelObjective slo = Availability();
        slo.Kind = SloKind.ServiceLatency;

        (await service.SaveAsync(tenantId, slo)).Should().Contain("threshold");
    }

    [Fact]
    public async Task Another_tenant_s_cluster_or_check_cannot_be_named()
    {
        ServiceLevelObjective onTheirCluster = Availability();
        onTheirCluster.ClusterId = strangerClusterId;
        (await service.SaveAsync(tenantId, onTheirCluster)).Should().Contain("cluster");

        ServiceLevelObjective onTheirCheck = new()
        {
            Name = "x", Kind = SloKind.SyntheticUptime, TargetPercent = 99, WindowDays = 30, CheckId = strangerCheckId,
        };
        (await service.SaveAsync(tenantId, onTheirCheck)).Should().Contain("check");
    }

    [Fact]
    public async Task A_synthetic_objective_files_its_incidents_where_its_check_does()
    {
        ServiceLevelObjective slo = await CreatedAsync(new ServiceLevelObjective
        {
            Name = "uptime", Kind = SloKind.SyntheticUptime, TargetPercent = 99, WindowDays = 7, CheckId = checkId,
            AlertAtBudgetSpentPercent = 80,
        });

        slo.ClusterId.Should().Be(clusterId);
        slo.Service.Should().BeNull();

        TelemetryAlertRule rule = (await rules.ListAsync(tenantId)).Single();
        rule.Kind.Should().Be(TelemetryAlertKind.SloBudget);
        rule.SloId.Should().Be(slo.Id);
        rule.ClusterId.Should().Be(clusterId);
        rule.Threshold.Should().Be(80);
    }

    [Fact]
    public async Task No_alert_means_no_rule_and_taking_the_alert_off_removes_it()
    {
        ServiceLevelObjective slo = await CreatedAsync();
        (await rules.ListAsync(tenantId)).Should().ContainSingle();

        slo.AlertAtBudgetSpentPercent = null;
        (await service.SaveAsync(tenantId, slo)).Should().BeNull();

        (await rules.ListAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_what_is_measured_forgets_the_old_days_and_renaming_does_not()
    {
        ServiceLevelObjective slo = await CreatedAsync();
        await service.SetTallyAsync(tenantId, slo.Id, Today, 10, 10);

        slo.Name = "renamed";
        await service.SaveAsync(tenantId, slo);
        (await service.ComputedDaysAsync(slo.Id)).Should().ContainSingle();

        slo.Service = "payments";
        await service.SaveAsync(tenantId, slo);
        (await service.ComputedDaysAsync(slo.Id)).Should().BeEmpty("those days measured another service");
    }

    [Fact]
    public async Task Deleting_takes_the_rule_and_the_tallies()
    {
        ServiceLevelObjective slo = await CreatedAsync();
        await service.SetTallyAsync(tenantId, slo.Id, Today, 10, 10);

        await service.DeleteAsync(tenantId, slo.Id);

        (await rules.ListAsync(tenantId)).Should().BeEmpty();
        (await service.ComputedDaysAsync(slo.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tally_is_replaced_rather_than_added_to()
    {
        ServiceLevelObjective slo = await CreatedAsync();

        await service.SetTallyAsync(tenantId, slo.Id, Today, 90, 100);
        await service.SetTallyAsync(tenantId, slo.Id, Today, 95, 100);

        SloStatus status = (await service.ListAsync(tenantId, DateTime.UtcNow)).Single().Status;
        status.Good.Should().Be(95);
        status.Total.Should().Be(100);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_write_or_delete_an_objective()
    {
        ServiceLevelObjective slo = await CreatedAsync();

        (await service.ListAsync(strangerId, DateTime.UtcNow)).Should().BeEmpty();
        await service.SetTallyAsync(strangerId, slo.Id, Today, 0, 1_000_000);
        await service.DeleteAsync(strangerId, slo.Id);

        await using TelemetryDbContext db = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();
        (await SloService.StatusAsync(db, strangerId, slo.Id, DateTime.UtcNow, default)).Should().BeNull();

        SloRow row = (await service.ListAsync(tenantId, DateTime.UtcNow)).Single();
        row.Status.Total.Should().Be(0, "the stranger's tally must not have landed");
    }

    [Fact]
    public async Task Synthetic_runs_are_counted_within_the_day_and_for_the_tenant_s_own_check()
    {
        DateTime midnight = Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        await using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.SyntheticCheckResults.AddRange(
                new SyntheticCheckResult { CheckId = checkId, At = midnight, Passed = true },
                new SyntheticCheckResult { CheckId = checkId, At = midnight.AddHours(23).AddMinutes(59), Passed = false },
                new SyntheticCheckResult { CheckId = checkId, At = midnight.AddDays(1), Passed = true },
                new SyntheticCheckResult { CheckId = checkId, At = midnight.AddTicks(-1), Passed = true });
            await db.SaveChangesAsync();
        }

        (await service.SyntheticDayAsync(tenantId, checkId, Today)).Should().Be((1L, 2L));
        (await service.SyntheticDayAsync(strangerId, checkId, Today)).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task Tallies_older_than_any_window_age_out()
    {
        ServiceLevelObjective slo = await CreatedAsync();
        await service.SetTallyAsync(tenantId, slo.Id, Today.AddDays(-(SloService.MaxWindowDays + 2)), 1, 1);
        await service.SetTallyAsync(tenantId, slo.Id, Today.AddDays(-SloService.MaxWindowDays), 1, 1);

        (await service.PruneAsync(DateTime.UtcNow)).Should().Be(1);
    }

    // ── A pass, against a mocked trace store ──

    private static Mock<ITraceQueryService> Traces(KubernetesOperationResult<List<RedBucket>> answer, List<int>? buckets = null)
    {
        Mock<ITraceQueryService> traces = new();
        traces.Setup(t => t.GetServiceRedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<string?>()))
            .Callback((Guid _, string _, DateTime _, DateTime _, int b, CancellationToken _, IReadOnlyList<string>? _, string? _) => buckets?.Add(b))
            .ReturnsAsync(answer);
        return traces;
    }

    [Fact]
    public async Task A_pass_writes_today_and_yesterday_from_the_trace_store()
    {
        ServiceLevelObjective slo = await CreatedAsync();
        Mock<ITraceQueryService> traces = Traces(KubernetesOperationResult<List<RedBucket>>.Success([new RedBucket(default, 1000, 2, 0, 0, 0)]));

        int written = await SloTallySweep.TallyAsync(service, traces.Object, slo, DateTime.UtcNow, 14, 2, default);

        written.Should().Be(2);
        SloStatus status = (await service.ListAsync(tenantId, DateTime.UtcNow)).Single().Status;
        status.Good.Should().Be(1996);
        status.Total.Should().Be(2000);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_good_figure()
    {
        ServiceLevelObjective slo = await CreatedAsync();
        await service.SetTallyAsync(tenantId, slo.Id, Today, 7, 10);

        await SloTallySweep.TallyAsync(service, Traces(KubernetesOperationResult<List<RedBucket>>.Failure("down")).Object,
            slo, DateTime.UtcNow, 14, 2, default);

        SloStatus status = (await service.ListAsync(tenantId, DateTime.UtcNow)).Single().Status;
        status.Good.Should().Be(7);
        status.Total.Should().Be(10);
    }

    [Fact]
    public async Task A_latency_objective_asks_for_five_minute_slices()
    {
        ServiceLevelObjective input = Availability();
        input.Kind = SloKind.ServiceLatency;
        input.LatencyThresholdMs = 200;
        ServiceLevelObjective slo = await CreatedAsync(input);

        List<int> asked = [];
        await SloTallySweep.TallyAsync(service, Traces(KubernetesOperationResult<List<RedBucket>>.Success([]), asked).Object,
            slo, DateTime.UtcNow, 14, 2, default);

        asked.Should().AllBeEquivalentTo(288);
    }
}

[Collection(ApiSurfaceCollection.Name)]
public sealed class SloApiTests(GeneratedOpenApiDocumentFixture fixture)
{
    [Fact]
    public async Task It_lists_this_tenant_s_objectives_and_never_another_s()
    {
        string marker = Guid.NewGuid().ToString("N");

        await using (ApplicationDbContext db = fixture.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
        {
            db.ServiceLevelObjectives.AddRange(
                new ServiceLevelObjective { Id = Guid.NewGuid(), TenantId = fixture.TokenTenantId, Name = marker, Kind = SloKind.ServiceAvailability, ClusterId = Guid.NewGuid(), Service = "s" },
                new ServiceLevelObjective { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "stranger-" + marker, Kind = SloKind.ServiceAvailability, ClusterId = Guid.NewGuid(), Service = "s" });
            await db.SaveChangesAsync();
        }

        (HttpStatusCode status, _, string body) = await fixture.SendForResponseAsync("GET", "/api/v1/slos", "ops:read");

        status.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("stranger-" + marker);

        JsonElement mine = JsonDocument.Parse(body).RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == marker);
        mine.GetProperty("attainedPercent").ValueKind.Should().Be(JsonValueKind.Null, "no events is unknown, not zero");
        mine.GetProperty("isBreached").GetBoolean().Should().BeFalse();
    }
}
