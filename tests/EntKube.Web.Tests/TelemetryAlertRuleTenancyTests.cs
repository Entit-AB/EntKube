using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose alert rule an upsert acts on.
///
/// <para>A rule decides what fires an incident — the threshold, the window, the severity, the
/// text it matches — so editing somebody else's changes when they are paged, and clearing
/// IsEnabled stops them being paged at all.</para>
///
/// <para>⚠️ <b>The method's summary used to claim it "enforces tenant ownership".</b> Its
/// predicate was <c>r.Id == rule.Id &amp;&amp; r.TenantId == rule.TenantId</c> — both sides off
/// the caller's own entity, so it asked whether the caller agreed with itself. The comment was
/// the reason nobody looked.</para>
/// </summary>
public class TelemetryAlertRuleTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TelemetryDbContext db;
    private readonly TelemetryAlertRuleService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public TelemetryAlertRuleTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        TestDbContextFactory factory = new(connection);
        db = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();
        db.Database.EnsureCreated();

        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Tenants.AddRange(
                new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
                new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
            seed.SaveChanges();
        }

        sut = new TelemetryAlertRuleService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private TelemetryAlertRule Theirs()
    {
        TelemetryAlertRule rule = new()
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenantId,
            Name = "Journal p95",
            Kind = TelemetryAlertKind.TraceLatencyP95,
            Threshold = 500,
            WindowMinutes = 5,
            Severity = "critical",
            IsEnabled = true,
        };
        db.Set<TelemetryAlertRule>().Add(rule);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return rule;
    }

    /// <summary>
    /// Editing by id alone moved somebody else's threshold and silenced the rule. Both of
    /// those are changes to when they get paged.
    /// </summary>
    [Fact]
    public async Task Another_tenants_alert_rule_cannot_be_edited_or_silenced()
    {
        TelemetryAlertRule theirs = Theirs();

        await sut.UpsertAsync(tenantId, new TelemetryAlertRule
        {
            Id = theirs.Id,
            TenantId = otherTenantId,       // the claim the old predicate believed
            Name = "Journal p95",
            Kind = TelemetryAlertKind.TraceLatencyP95,
            Threshold = 999_999,
            WindowMinutes = 5,
            Severity = "info",
            IsEnabled = false,
        });

        TelemetryAlertRule stored = await db.Set<TelemetryAlertRule>().AsNoTracking()
            .FirstAsync(r => r.Id == theirs.Id);

        stored.Threshold.Should().Be(500);
        stored.Severity.Should().Be("critical");
        stored.IsEnabled.Should().BeTrue();

        // Nothing of ours was created in its place either — the update path returns quietly.
        (await sut.ListAsync(tenantId)).Should().BeEmpty();
    }

    /// <summary>
    /// The create path has no row to compare against, only a value to normalise: a rule
    /// carrying somebody else's TenantId would have been filed in their list, where it fires
    /// incidents at them.
    /// </summary>
    [Fact]
    public async Task A_new_rule_cannot_be_filed_under_another_tenant()
    {
        await sut.UpsertAsync(tenantId, new TelemetryAlertRule
        {
            TenantId = otherTenantId,       // the lie
            Name = "Planted",
            Kind = TelemetryAlertKind.LogErrorRate,
            Threshold = 1,
            WindowMinutes = 5,
            Severity = "critical",
        });

        (await sut.ListAsync(otherTenantId)).Should().BeEmpty();
        (await sut.ListAsync(tenantId)).Should().ContainSingle(r => r.Name == "Planted");
    }

    /// <summary>Our own edits still land, or the refusals above prove nothing.</summary>
    [Fact]
    public async Task Our_own_rule_is_still_editable()
    {
        await sut.UpsertAsync(tenantId, new TelemetryAlertRule
        {
            TenantId = tenantId,
            Name = "Ours",
            Kind = TelemetryAlertKind.TraceLatencyP95,
            Threshold = 300,
            WindowMinutes = 5,
            Severity = "warning",
        });

        TelemetryAlertRule ours = (await sut.ListAsync(tenantId)).Single();
        ours.Threshold = 450;
        await sut.UpsertAsync(tenantId, ours);

        (await sut.ListAsync(tenantId)).Single().Threshold.Should().Be(450);
    }
}
