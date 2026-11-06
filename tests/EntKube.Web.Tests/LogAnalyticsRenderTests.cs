using Bunit;
using System.Security.Claims;
using EntKube.Telemetry;
using EntKube.Telemetry.LogPipelines;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// What the page says about a result. It renders the data the engine gave it, so the concern here
/// is that a partial answer is called partial and a failure is not shown as an empty result.
/// </summary>
public class LogAnalyticsRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();

    public LogAnalyticsRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            db.Database.EnsureCreated();
            Guid env = Guid.NewGuid(), strangerEnv = Guid.NewGuid();
            db.Tenants.AddRange(new Tenant { Id = tenantId, Name = "Mine", Slug = "mine" }, new Tenant { Id = strangerId, Name = "Theirs", Slug = "theirs" });
            db.SaveChanges();
            db.Environments.AddRange(
                new EntKube.Web.Data.Environment { Id = env, TenantId = tenantId, Name = "e" },
                new EntKube.Web.Data.Environment { Id = strangerEnv, TenantId = strangerId, Name = "e" });
            db.SaveChanges();
            db.KubernetesClusters.AddRange(
                new KubernetesCluster { Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentId = env, Name = "my-cluster", ApiServerUrl = "https://a" },
                new KubernetesCluster { Id = Guid.NewGuid(), TenantId = strangerId, EnvironmentId = strangerEnv, Name = "their-cluster", ApiServerUrl = "https://b" });
            db.SaveChanges();
        }

        TestDbContextFactory factory = new(connection);
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<EntKube.Web.Data.Modules.TelemetryDbContext>>(factory);
        Services.AddSingleton(new SavedLogQueryService(factory));
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn());
        Services.AddSingleton(new LogPipelineService(null!));
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

    private IRenderedComponent<LogAnalytics> Page(LogPipelineResult? result = null, LogPipelineError? error = null, string? failure = null)
    {
        IRenderedComponent<LogAnalytics> cut = Render<LogAnalytics>(p => p.Add(c => c.TenantId, tenantId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("my-cluster"));

        Set(cut, "result", result);
        Set(cut, "syntaxError", error);
        Set(cut, "failure", failure);
        cut.Render();
        return cut;
    }

    private static void Set(IRenderedComponent<LogAnalytics> cut, string field, object? value) =>
        typeof(LogAnalytics).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(cut.Instance, value);

    private static LogPipelineResult Counts(bool partial, int lines = 20_000) => new(
        [new LogColumn("pod", LogColumnType.Text), new LogColumn("count_", LogColumnType.Number)],
        [["api-1", 12d]],
        lines,
        partial);

    [Fact]
    public void Only_the_tenant_s_own_clusters_are_offered()
    {
        IRenderedComponent<LogAnalytics> cut = Page();

        cut.Markup.Should().Contain("my-cluster");
        cut.Markup.Should().NotContain("their-cluster");
    }

    /// <summary>
    /// The select's value arrives from the browser. The service behind this page was given a null
    /// log layer, so reaching it would throw: the page must stop at its own list of clusters.
    /// </summary>
    [Fact]
    public void A_cluster_this_page_did_not_list_is_not_queried()
    {
        using EntKube.Web.Data.ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        Guid theirs = db.KubernetesClusters.Single(c => c.TenantId == strangerId).Id;

        IRenderedComponent<LogAnalytics> cut = Page();
        Set(cut, "clusterIdStr", theirs.ToString());
        cut.Render();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Run")).Click();

        cut.Markup.Should().NotContain("rows from");
    }

    [Fact]
    public void A_partial_result_says_so_and_a_complete_one_does_not()
    {
        Page(Counts(partial: true)).Markup.Should().Contain("Partial result").And.Contain("lower bounds");
        Page(Counts(partial: false)).Markup.Should().NotContain("Partial result");
    }

    [Fact]
    public void The_table_shows_the_query_s_own_columns_and_values()
    {
        IRenderedComponent<LogAnalytics> cut = Page(Counts(partial: false, lines: 1234));

        cut.FindAll("th").Select(h => h.TextContent).Should().Equal("pod", "count_");
        cut.FindAll("tbody td").Select(c => c.TextContent).Should().Equal("api-1", "12");
        cut.Markup.Should().Contain("1 rows from 1,234 lines");
    }

    [Fact]
    public void A_failure_is_stated_and_is_not_shown_as_no_rows()
    {
        IRenderedComponent<LogAnalytics> cut = Page(failure: "store down");

        cut.Markup.Should().Contain("could not run").And.Contain("store down");
        cut.Markup.Should().NotContain("No rows matched");
    }

    [Fact]
    public void No_matching_rows_is_said_plainly()
    {
        Page(new LogPipelineResult([new LogColumn("pod", LogColumnType.Text)], [], 40, false))
            .Markup.Should().Contain("No rows matched");
    }

    [Fact]
    public void A_syntax_error_points_at_the_character()
    {
        IRenderedComponent<LogAnalytics> cut = Render<LogAnalytics>(p => p.Add(c => c.TenantId, tenantId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("my-cluster"));

        Set(cut, "query", "logs\n| where podd == 1");
        Set(cut, "syntaxError", new LogPipelineError("There is no column 'podd' here.", 13));
        cut.Render();

        cut.Find(".alert pre").TextContent.Should().Be("| where podd == 1\n        ^");
    }

    [Fact]
    public void Markup_in_a_log_line_stays_text()
    {
        IRenderedComponent<LogAnalytics> cut = Page(new LogPipelineResult(
            [new LogColumn("body", LogColumnType.Text)], [["<img src=x onerror=alert(1)>"]], 1, false));

        cut.FindAll("td img").Should().BeEmpty();
        cut.Find("tbody td").TextContent.Should().Be("<img src=x onerror=alert(1)>");
    }
}
