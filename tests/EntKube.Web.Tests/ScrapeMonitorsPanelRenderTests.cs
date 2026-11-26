using System.Text.Json.Nodes;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ScrapeMonitors;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What the Monitors panel tells an operator about EntKube's ServiceMonitors and PodMonitors on a
/// cluster, and what its one button does.
///
/// <para>The panel is a reading of <see cref="IScrapeMonitors.PlanAsync"/> and nothing else, so the
/// plan is the test's input and the markup its output. The decisions behind a row are
/// <see cref="ScrapeMonitorPlannerTests"/>'; how an apply is carried out is
/// <see cref="ScrapeMonitorServiceTests"/>'.</para>
///
/// <para><b>Unknown means do nothing, on the page too.</b> A plan that could not be read is an
/// error that says nothing was changed. It is never an empty table: an empty table reads as "this
/// cluster has no monitors", which is a claim about the cluster the panel cannot make.</para>
/// </summary>
public sealed class ScrapeMonitorsPanelRenderTests : BunitContext
{
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Mock<IScrapeMonitors> monitors = new(MockBehavior.Strict);

    public ScrapeMonitorsPanelRenderTests()
    {
        Services.AddSingleton(monitors.Object);
    }

    // ──────── Plans ────────

    private static MonitorIdentity Pod(string ns, string name) => new(MonitorKind.PodMonitor, ns, name);

    private static MonitorPlanRow Row(
        string key, MonitorState state, MonitorChange change = MonitorChange.None,
        MonitorIdentity? monitor = null, string? detail = null, int? up = null, int? total = null,
        params PlannedEndpoint[] endpoints)
        => new(key, $"Title of {key}", "Database", "shop", monitor, state, change,
            detail ?? $"The sentence about {key}.", endpoints, up, total);

    private static PlannedMonitor Planned(MonitorIdentity identity, MonitorChange change)
        => new(identity, new JsonObject(), change);

    private static ScrapeMonitorPlan Plan(
        IEnumerable<MonitorPlanRow> rows,
        IEnumerable<PlannedMonitor>? apply = null,
        IEnumerable<MonitorIdentity>? prune = null)
        => new([.. rows], [.. apply ?? []], [.. prune ?? []]);

    /// <summary>A plan with one row per state, and nothing to change.</summary>
    private static ScrapeMonitorPlan EveryState() => Plan(
    [
        Row("managed", MonitorState.Managed, monitor: Pod("shop", "entkube-cnpg-orders"), up: 2, total: 3,
            endpoints: [new("orders-1", "10.0.0.5:9187", "/metrics"), new("orders-2", "10.0.0.6:9187", "/metrics")]),
        Row("covered", MonitorState.CoveredElsewhere),
        Row("annotated", MonitorState.Annotated),
        Row("notrunning", MonitorState.NotRunning),
        Row("notexposed", MonitorState.NotExposed),
        Row("blocked", MonitorState.BlockedByNetworkPolicy),
        Row("notselectable", MonitorState.NotSelectable),
        Row("info", MonitorState.Info),
    ]);

    /// <summary>Two to create, one to update, one to replace, one to remove.</summary>
    private static ScrapeMonitorPlan WithChanges() => Plan(
        [
            Row("a", MonitorState.Managed, MonitorChange.Create, Pod("shop", "entkube-a")),
            Row("b", MonitorState.Managed, MonitorChange.Create, Pod("shop", "entkube-b")),
            Row("c", MonitorState.Managed, MonitorChange.Update, Pod("shop", "entkube-c")),
            Row("d", MonitorState.Managed, MonitorChange.Replace, Pod("shop", "entkube-d")),
            Row("e", MonitorState.CoveredElsewhere, MonitorChange.Remove, Pod("shop", "entkube-e")),
        ],
        [
            Planned(Pod("shop", "entkube-a"), MonitorChange.Create),
            Planned(Pod("shop", "entkube-b"), MonitorChange.Create),
            Planned(Pod("shop", "entkube-c"), MonitorChange.Update),
            Planned(Pod("shop", "entkube-d"), MonitorChange.Replace),
        ],
        [Pod("shop", "entkube-e")]);

    private static KubernetesOperationResult<ScrapeMonitorPlan> Ok(ScrapeMonitorPlan plan)
        => KubernetesOperationResult<ScrapeMonitorPlan>.Success(plan);

    private static KubernetesOperationResult<ScrapeMonitorPlan> Failed(string error)
        => KubernetesOperationResult<ScrapeMonitorPlan>.Failure(error);

    private void Plans(params KubernetesOperationResult<ScrapeMonitorPlan>[] results)
    {
        Queue<KubernetesOperationResult<ScrapeMonitorPlan>> queue = new(results);
        monitors.Setup(m => m.PlanAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    private IRenderedComponent<ScrapeMonitorsPanel> Panel()
    {
        IRenderedComponent<ScrapeMonitorsPanel> cut = Render<ScrapeMonitorsPanel>(ps => ps
            .Add(p => p.TenantId, tenantId).Add(p => p.ClusterId, clusterId));
        cut.WaitForAssertion(() => cut.FindAll("[data-loading]").Should().BeEmpty());
        return cut;
    }

    private static IElement RowOf(IRenderedComponent<ScrapeMonitorsPanel> cut, string key)
        => cut.Find($"[data-monitor-row='{key}']");

    private static IElement ApplyButton(IRenderedComponent<ScrapeMonitorsPanel> cut)
        => cut.FindAll("button").Single(b => b.TextContent.Contains("Apply monitors now"));

    // ──────── Rows ────────

    [Theory]
    [InlineData("managed", "Scraped by EntKube")]
    [InlineData("covered", "Scraped elsewhere")]
    [InlineData("annotated", "Scraped by annotation")]
    [InlineData("notrunning", "Not running")]
    [InlineData("notexposed", "No metrics port")]
    [InlineData("blocked", "Blocked by a NetworkPolicy")]
    [InlineData("notselectable", "Not picked up by Prometheus")]
    [InlineData("info", "Nothing to create")]
    public void Each_state_is_a_badge_a_person_can_read(string key, string badge)
    {
        Plans(Ok(EveryState()));

        IElement row = RowOf(Panel(), key);

        row.QuerySelector("[data-state]")!.TextContent.Trim().Should().Be(badge);
        row.TextContent.Should().Contain($"Title of {key}").And.Contain($"The sentence about {key}.")
            .And.Contain("Database").And.Contain("shop");
    }

    [Fact]
    public void There_is_one_row_per_target_in_the_order_the_plan_gives()
    {
        Plans(Ok(EveryState()));

        Panel().FindAll("[data-monitor-row]").Select(r => r.GetAttribute("data-monitor-row")).Should().Equal(
            "managed", "covered", "annotated", "notrunning", "notexposed", "blocked", "notselectable", "info");
    }

    [Fact]
    public void A_managed_row_names_its_monitor_and_how_many_of_its_targets_are_up()
    {
        Plans(Ok(EveryState()));

        IElement row = RowOf(Panel(), "managed");

        row.TextContent.Should().Contain("entkube-cnpg-orders").And.Contain("PodMonitor");
        row.QuerySelector("[data-targets]")!.TextContent.Trim().Should().Be("2/3 up");
    }

    [Fact]
    public void Only_a_managed_row_has_a_target_count()
    {
        ScrapeMonitorPlan plan = Plan([Row("covered", MonitorState.CoveredElsewhere, up: 1, total: 1)]);
        Plans(Ok(plan));

        RowOf(Panel(), "covered").QuerySelector("[data-targets]").Should().BeNull(
            "a count beside a target EntKube does not scrape would read as EntKube's");
    }

    [Fact]
    public void A_rows_endpoints_are_there_but_folded_away()
    {
        Plans(Ok(EveryState()));

        IElement details = RowOf(Panel(), "managed").QuerySelector("details")!;

        details.HasAttribute("open").Should().BeFalse();
        details.QuerySelector("summary")!.TextContent.Should().Contain("2 endpoints");
        details.TextContent.Should().Contain("orders-1").And.Contain("10.0.0.5:9187").And.Contain("/metrics");
    }

    [Theory]
    [InlineData(MonitorChange.Create, "Create")]
    [InlineData(MonitorChange.Update, "Update")]
    [InlineData(MonitorChange.Replace, "Replace")]
    [InlineData(MonitorChange.Remove, "Remove")]
    [InlineData(MonitorChange.None, "No change")]
    public void A_rows_change_is_named(MonitorChange change, string text)
    {
        Plans(Ok(Plan([Row("a", MonitorState.Managed, change, Pod("shop", "entkube-a"))])));

        RowOf(Panel(), "a").QuerySelector("[data-change]")!.TextContent.Trim().Should().Be(text);
    }

    [Fact]
    public void The_plan_is_asked_for_this_tenants_cluster_and_nothing_is_applied_by_looking()
    {
        Plans(Ok(EveryState()));

        Panel();

        monitors.Verify(m => m.PlanAsync(tenantId, clusterId, It.IsAny<CancellationToken>()), Times.Once);
        monitors.Verify(m => m.ApplyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The Monitoring page re-renders every fifteen seconds for its own charts, and a plan reads
    /// every namespace a target is in. The same cluster is not read again for that.
    /// </summary>
    [Fact]
    public void The_page_re_rendering_around_the_panel_does_not_read_the_cluster_again()
    {
        Plans(Ok(EveryState()));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        cut.Render(ps => ps.Add(p => p.TenantId, tenantId).Add(p => p.ClusterId, clusterId));

        monitors.Verify(m => m.PlanAsync(tenantId, clusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────── Nothing to plan ────────

    [Fact]
    public void A_cluster_without_Prometheus_gets_a_sentence_and_no_table()
    {
        Plans(Ok(ScrapeMonitorPlan.Nothing(MonitorPlanScope.NoPrometheus)));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();

        cut.Find("[data-scope='NoPrometheus']").TextContent.Should().Contain("no Prometheus installed by EntKube");
        cut.FindAll("table").Should().BeEmpty();
        cut.FindAll("button").Where(b => b.TextContent.Contains("Apply monitors now")).Should().BeEmpty();
    }

    [Fact]
    public void A_cluster_without_the_monitor_kinds_gets_its_own_sentence_and_no_table()
    {
        Plans(Ok(ScrapeMonitorPlan.Nothing(MonitorPlanScope.NoMonitorKinds)));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();

        cut.Find("[data-scope='NoMonitorKinds']").TextContent.Should().Contain("ServiceMonitor and PodMonitor");
        cut.FindAll("[data-scope='NoPrometheus']").Should().BeEmpty();
        cut.FindAll("table").Should().BeEmpty();
    }

    // ──────── A plan that could not be read ────────

    /// <summary>⭐ The panel's version of "unknown means do nothing".</summary>
    [Fact]
    public void A_plan_that_could_not_be_read_is_an_error_saying_nothing_changed_and_not_an_empty_table()
    {
        Plans(Failed("Forbidden: podmonitors is forbidden"));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();

        cut.Find("[data-plan-error]").TextContent.Should().Contain("Forbidden: podmonitors is forbidden")
            .And.Contain("Nothing was changed");
        cut.FindAll("table").Should().BeEmpty();
        cut.FindAll("[data-monitor-row]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Nothing to change", "that is a claim about the cluster it could not read");
        cut.FindAll("button").Where(b => b.TextContent.Contains("Apply monitors now")).Should().BeEmpty();
    }

    [Fact]
    public void A_plan_that_throws_is_an_error_too()
    {
        monitors.Setup(m => m.PlanAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The vault is sealed."));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();

        cut.Find("[data-plan-error]").TextContent.Should().Contain("The vault is sealed.").And.Contain("Nothing was changed");
        cut.FindAll("table").Should().BeEmpty();
    }

    // ──────── Apply monitors now ────────

    [Fact]
    public void A_plan_with_nothing_to_change_has_its_button_disabled_and_says_so()
    {
        Plans(Ok(EveryState()));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();

        ApplyButton(cut).HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Nothing to change");
    }

    [Fact]
    public void A_plan_with_changes_has_its_button_enabled_and_counts_them()
    {
        Plans(Ok(WithChanges()));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();

        ApplyButton(cut).HasAttribute("disabled").Should().BeFalse();
        cut.Markup.Should().NotContain("Nothing to change").And.Contain("5 changes");
    }

    /// <summary>⭐ One press, one apply, and what is shown afterwards is what the apply returned.</summary>
    [Fact]
    public void Applying_calls_apply_once_and_shows_the_plan_it_returned()
    {
        Plans(Ok(WithChanges()));
        ScrapeMonitorPlan after = Plan([Row("after", MonitorState.Managed, monitor: Pod("shop", "entkube-after"), up: 1, total: 1)]);
        monitors.Setup(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(after));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        ApplyButton(cut).Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-monitor-row]").Select(r => r.GetAttribute("data-monitor-row"))
            .Should().Equal("after"));
        monitors.Verify(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>()), Times.Once);
        monitors.Verify(m => m.PlanAsync(tenantId, clusterId, It.IsAny<CancellationToken>()), Times.Once,
            "the apply returns the plan as it stands afterwards; reading it again would be a second read for nothing");
        ApplyButton(cut).HasAttribute("disabled").Should().BeTrue("the returned plan has nothing left to change");
    }

    [Fact]
    public void What_the_apply_did_is_counted_from_the_plan_before_and_after()
    {
        Plans(Ok(WithChanges()));
        monitors.Setup(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Plan([])));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        ApplyButton(cut).Click();

        cut.WaitForAssertion(() => cut.Find("[data-apply-outcome]").TextContent.Trim()
            .Should().Be("Applied: 2 created, 1 updated, 1 replaced, 1 removed."));
    }

    [Fact]
    public void A_change_still_planned_after_the_apply_is_outstanding_and_not_done()
    {
        Plans(Ok(WithChanges()));
        ScrapeMonitorPlan after = Plan(
            [Row("a", MonitorState.Managed, MonitorChange.Create, Pod("shop", "entkube-a"))],
            [Planned(Pod("shop", "entkube-a"), MonitorChange.Create)]);
        monitors.Setup(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(after));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        ApplyButton(cut).Click();

        cut.WaitForAssertion(() => cut.Find("[data-apply-outcome]").TextContent.Trim()
            .Should().Be("Applied: 1 created, 1 updated, 1 replaced, 1 removed. 1 change is still outstanding."));
    }

    [Fact]
    public void The_button_is_disabled_while_the_apply_runs()
    {
        Plans(Ok(WithChanges()));
        TaskCompletionSource<KubernetesOperationResult<ScrapeMonitorPlan>> running = new();
        monitors.Setup(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>())).Returns(running.Task);

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        ApplyButton(cut).Click();

        cut.WaitForAssertion(() => ApplyButton(cut).HasAttribute("disabled").Should().BeTrue());

        running.SetResult(Ok(WithChanges()));
        cut.WaitForAssertion(() => ApplyButton(cut).HasAttribute("disabled").Should().BeFalse());
        monitors.Verify(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A failed apply may have written part of what it meant to, so the rows from before it are no
    /// longer known to be true. The failure is shown, and the plan read again.
    /// </summary>
    [Fact]
    public void An_apply_that_failed_is_shown_and_the_plan_is_read_again()
    {
        ScrapeMonitorPlan reread = Plan([Row("reread", MonitorState.Managed, MonitorChange.Create, Pod("shop", "entkube-r"))],
            [Planned(Pod("shop", "entkube-r"), MonitorChange.Create)]);
        Plans(Ok(WithChanges()), Ok(reread));
        monitors.Setup(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failed("The operator declined the change."));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        ApplyButton(cut).Click();

        cut.WaitForAssertion(() => cut.Find("[data-apply-error]").TextContent
            .Should().Contain("The operator declined the change."));
        cut.WaitForAssertion(() => cut.FindAll("[data-monitor-row]").Select(r => r.GetAttribute("data-monitor-row"))
            .Should().Equal("reread"));
        cut.FindAll("[data-apply-outcome]").Should().BeEmpty("a failed apply did not do what an outcome would count");
    }

    [Fact]
    public void An_apply_that_throws_is_shown_as_a_failure()
    {
        Plans(Ok(WithChanges()));
        monitors.Setup(m => m.ApplyAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The vault is sealed."));

        IRenderedComponent<ScrapeMonitorsPanel> cut = Panel();
        ApplyButton(cut).Click();

        cut.WaitForAssertion(() => cut.Find("[data-apply-error]").TextContent.Should().Contain("The vault is sealed."));
    }

    // ──────── The arithmetic on its own ────────

    [Fact]
    public void Nothing_planned_and_nothing_left_is_nothing_needed()
        => MonitorApplyOutcome.Between(Plan([]), Plan([])).Sentence.Should().Be("Nothing needed changing.");

    [Fact]
    public void Nothing_done_and_something_left_does_not_claim_nothing_was_written()
    {
        ScrapeMonitorPlan same = WithChanges();

        MonitorApplyOutcome.Between(same, same).Sentence
            .Should().Be("None of the planned changes took effect. 5 changes are still outstanding.");
    }
}

/// <summary>
/// The Monitors panel is on the cluster's Monitoring page, and it is there when Prometheus is not.
/// A cluster whose Prometheus is down is the one whose operator most needs to see what EntKube's
/// monitors are, and why.
/// </summary>
public sealed class ClusterMonitoringMonitorsPanelTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Mock<IScrapeMonitors> monitors = new();

    public ClusterMonitoringMonitorsPanelTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        TestDbContextFactory factory = new(connection);

        // No cluster and no Prometheus behind it: every metric read fails, and the page shows its error.
        Services.AddSingleton(new PrometheusService(
            new EntKube.Web.Modules.Api.DeliveryApi(factory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                factory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.CatalogApi(factory),
            new PromQueryCache(),
            NullLogger<PrometheusService>.Instance));
        Services.AddSingleton(monitors.Object);
    }

    public new void Dispose()
    {
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_cluster_whose_Prometheus_cannot_be_reached_still_shows_its_monitors()
    {
        Guid tenantId = Guid.NewGuid(), clusterId = Guid.NewGuid();
        monitors.Setup(m => m.PlanAsync(tenantId, clusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(KubernetesOperationResult<ScrapeMonitorPlan>.Success(new ScrapeMonitorPlan(
                [new("cnpg/shop/orders", "orders", "Database", "shop", null, MonitorState.BlockedByNetworkPolicy,
                    MonitorChange.None, "A NetworkPolicy keeps Prometheus out.", [])], [], [])));

        IRenderedComponent<ClusterMonitoring> cut = Render<ClusterMonitoring>(ps => ps
            .Add(p => p.TenantId, tenantId).Add(p => p.ClusterId, clusterId));

        cut.WaitForAssertion(() => cut.FindAll(".alert-warning").Should().NotBeEmpty("Prometheus is not there"));
        cut.WaitForAssertion(() => cut.Find("[data-monitor-row='cnpg/shop/orders']").TextContent
            .Should().Contain("A NetworkPolicy keeps Prometheus out."));
    }
}

/// <summary>How the panel gets its service.</summary>
public sealed class ScrapeMonitorsRegistrationTests
{
    /// <summary>
    /// Scoped, so that in a circuit it is the circuit's: the gate it opens an operation on is the one
    /// whose acknowledgment dialog this operator sees. A singleton would hold the first circuit's.
    /// </summary>
    [Fact]
    public void The_panels_seam_is_the_monitor_service_for_the_scope()
    {
        ServiceCollection services = [];
        typeof(EntKube.Web.Modules.ModuleMap).Assembly
            .GetType("EntKube.Web.Modules.Composition.TelemetryServices")!
            .GetMethod("AddTelemetryModule",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [services]);

        ServiceDescriptor seam = services.Should().ContainSingle(d => d.ServiceType == typeof(IScrapeMonitors)).Subject;
        seam.Lifetime.Should().Be(ServiceLifetime.Scoped);

        // It hands out the scope's own ScrapeMonitorService, not a second one beside it.
        ScrapeMonitorService scopes = new(null!, null!, null!, null!, null!, null!, null!);
        Mock<IServiceProvider> scope = new(MockBehavior.Strict);
        scope.Setup(p => p.GetService(typeof(ScrapeMonitorService))).Returns(scopes);
        seam.ImplementationFactory.Should().NotBeNull();
        seam.ImplementationFactory!(scope.Object).Should().BeSameAs(scopes);
    }
}
