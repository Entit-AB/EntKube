using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Who may change the support rota. Putting oneself on it, or standing down, is part of working
/// support. Changing who else is on it decides whose name new tickets land on, and is the roster
/// permission's top level.
/// </summary>
public class SupportRosterTabTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportDutyService duty;
    private readonly Guid tenantId = Guid.NewGuid();

    private EffectivePermissions permissions = EffectivePermissions.Everything;

    public SupportRosterTabTests()
    {
        Services.AddCascadingValue(_ => permissions);

        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        Guid role = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = role, TenantId = tenantId, Name = "Agent", PermissionsJson = TenantRoleService.EncodePermissions(new Dictionary<TenantFeature, AccessLevel> { [TenantFeature.SupportTickets] = AccessLevel.Edit }) });

        foreach (string name in new[] { "anna", "bertil" })
        {
            db.Users.Add(new ApplicationUser { Id = $"user-{name}", UserName = name, Email = $"{name}@entit.example" });
            db.TenantMemberships.Add(new TenantMembership { UserId = $"user-{name}", TenantId = tenantId, RoleId = role });
        }

        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        duty = new SupportDutyService(factory);
        duty.SetDutyAsync(tenantId, "user-bertil", true, null, "seed").GetAwaiter().GetResult();

        Services.AddSingleton(duty);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("anna", "user-anna"));
        Services.AddScoped<CurrentActor>();
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string name, string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.NameIdentifier, userId)], "test"))));
    }

    private static EffectivePermissions Holding(TenantFeature feature, AccessLevel level) =>
        EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel> { [feature] = level });

    private IRenderedComponent<SupportRosterTab> RenderRoster()
    {
        IRenderedComponent<SupportRosterTab> roster = Render<SupportRosterTab>(p => p.Add(c => c.TenantId, tenantId));
        roster.WaitForAssertion(() => roster.Markup.Should().Contain("bertil"));
        return roster;
    }

    private static IElement ButtonInRowOf(IRenderedComponent<SupportRosterTab> roster, string person, string text) =>
        roster.FindAll("tr").First(r => r.TextContent.Contains(person, StringComparison.Ordinal))
            .QuerySelectorAll("button").First(b => b.TextContent.Trim() == text);

    private bool Enrolled(string userId) =>
        db.SupportDuties.AsNoTracking().Any(d => d.TenantId == tenantId && d.UserId == userId);

    [Fact]
    public async Task Somebody_who_works_tickets_puts_themselves_on_the_rota()
    {
        permissions = Holding(TenantFeature.SupportTickets, AccessLevel.Edit);

        IRenderedComponent<SupportRosterTab> roster = RenderRoster();
        await roster.InvokeAsync(() => ButtonInRowOf(roster, "anna", "Add").Click());

        Enrolled("user-anna").Should().BeTrue();
    }

    [Fact]
    public async Task Somebody_who_works_tickets_cannot_take_a_colleague_off_the_rota()
    {
        permissions = Holding(TenantFeature.SupportTickets, AccessLevel.Edit);

        IRenderedComponent<SupportRosterTab> roster = RenderRoster();
        await roster.InvokeAsync(() => ButtonInRowOf(roster, "bertil", "Remove").Click());

        Enrolled("user-bertil").Should().BeTrue("taking somebody else off needs Support roster: Manage");
    }

    [Fact]
    public async Task Whoever_runs_the_rota_takes_a_colleague_off_it()
    {
        permissions = Holding(TenantFeature.SupportRoster, AccessLevel.Manage);

        IRenderedComponent<SupportRosterTab> roster = RenderRoster();
        await roster.InvokeAsync(() => ButtonInRowOf(roster, "bertil", "Remove").Click());

        Enrolled("user-bertil").Should().BeFalse();
    }
}
