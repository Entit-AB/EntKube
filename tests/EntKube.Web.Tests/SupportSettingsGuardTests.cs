using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Two of the support desk's settings that are changed far less often than they are looked at:
/// the phrases that triage incoming mail, and the on-call schedules. Each can be seen at View and
/// changed only at its permission's top level — seeing how mail is sorted is not the same as
/// deciding it, and working an incident is not the same as deciding who is on call for the next.
/// </summary>
public class SupportSettingsGuardTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly Guid tenantId = Guid.NewGuid();

    private EffectivePermissions permissions = EffectivePermissions.Everything;

    public SupportSettingsGuardTests()
    {
        Services.AddCascadingValue(_ => permissions);

        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        Services.AddSingleton(new MailTriageRuleService(factory));
        Services.AddSingleton(new OnCallService(factory));
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "anna"), new Claim(ClaimTypes.NameIdentifier, "user-anna")], "test"))));
    }

    private static EffectivePermissions Holding(TenantFeature feature, AccessLevel level) =>
        EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel> { [feature] = level });

    // ───────────────────────────── Mail triage phrases ─────────────────────────────

    private async Task CopyTheBuiltInPhrasesInAsync()
    {
        IRenderedComponent<MailTriageRulesTab> tab = Render<MailTriageRulesTab>(p => p.Add(c => c.TenantId, tenantId));
        tab.WaitForAssertion(() => tab.FindAll("button").Should().Contain(b => b.TextContent.Contains("Copy them in")));

        await tab.InvokeAsync(() => tab.FindAll("button").First(b => b.TextContent.Contains("Copy them in")).Click());
    }

    [Fact]
    public async Task Seeing_the_triage_phrases_is_not_enough_to_adopt_them()
    {
        permissions = Holding(TenantFeature.SupportChannels, AccessLevel.View);

        await CopyTheBuiltInPhrasesInAsync();

        db.MailTriageRules.AsNoTracking().Where(r => r.TenantId == tenantId).Should().BeEmpty();
    }

    [Fact]
    public async Task Whoever_runs_the_channels_adopts_them()
    {
        permissions = Holding(TenantFeature.SupportChannels, AccessLevel.Manage);

        await CopyTheBuiltInPhrasesInAsync();

        db.MailTriageRules.AsNoTracking().Where(r => r.TenantId == tenantId).Should().NotBeEmpty();
    }

    // ───────────────────────────── On-call schedules ─────────────────────────────

    private async Task CreateAScheduleAsync()
    {
        IRenderedComponent<OnCallTab> tab = Render<OnCallTab>(p => p.Add(c => c.TenantId, tenantId));
        tab.WaitForAssertion(() => tab.FindAll("button").Should().Contain(b => b.TextContent.Contains("New Schedule")));

        await tab.InvokeAsync(() => tab.FindAll("button").First(b => b.TextContent.Contains("New Schedule")).Click());
        tab.Find("input[placeholder='Primary On-Call']").Change("Primary On-Call");
        await tab.InvokeAsync(() => tab.Find("form").Submit());
    }

    [Fact]
    public async Task Working_incidents_is_not_enough_to_change_who_is_on_call()
    {
        permissions = Holding(TenantFeature.Incidents, AccessLevel.Edit);

        await CreateAScheduleAsync();

        db.OnCallSchedules.AsNoTracking().Where(s => s.TenantId == tenantId).Should().BeEmpty();
    }

    [Fact]
    public async Task Whoever_runs_on_call_creates_a_schedule()
    {
        permissions = Holding(TenantFeature.Incidents, AccessLevel.Manage);

        await CreateAScheduleAsync();

        db.OnCallSchedules.AsNoTracking().Where(s => s.TenantId == tenantId).Should().ContainSingle();
    }
}
