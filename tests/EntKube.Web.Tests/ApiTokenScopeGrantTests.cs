using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// An API token acts for whoever minted it, so it can carry no more than they hold. The ticket
/// scopes reach the same writes the support desk guards — notes, tags, merges — and a role that
/// the desk refuses must not be able to mint its way around that.
/// </summary>
public class ApiTokenScopeGrantTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly Guid tenantId = Guid.NewGuid();

    public ApiTokenScopeGrantTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        Services.AddSingleton(new ApiTokenService(factory, NullLogger<ApiTokenService>.Instance));
        Services.AddSingleton(new TenantRoleService(factory));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "bob"), new Claim(ClaimTypes.NameIdentifier, userId)], "test"))));
    }

    private void SignedInHolding(AccessLevel tickets)
    {
        Guid role = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = "user-bob", UserName = "bob" });
        db.TenantRoles.Add(new TenantRole
        {
            Id = role, TenantId = tenantId, Name = "Agent",
            PermissionsJson = TenantRoleService.EncodePermissions(new Dictionary<TenantFeature, AccessLevel> { [TenantFeature.SupportTickets] = tickets }),
        });
        db.TenantMemberships.Add(new TenantMembership { UserId = "user-bob", TenantId = tenantId, RoleId = role });
        db.SaveChanges();
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("user-bob"));
    }

    [Fact]
    public void The_ticket_scopes_need_what_the_desk_needs_for_the_same_writes()
    {
        // tickets:write can merge, which the desk keeps at Manage.
        ApiScopes.RoleNeededFor(ApiScopes.TicketsRead).Should().Be((TenantFeature.SupportTickets, AccessLevel.View));
        ApiScopes.RoleNeededFor(ApiScopes.TicketsWrite).Should().Be((TenantFeature.SupportTickets, AccessLevel.Manage));
        ApiScopes.RoleNeededFor(ApiScopes.FleetRead).Should().BeNull("platform permissions are not checked yet");
    }

    [Fact]
    public void A_scope_the_role_does_not_hold_is_named_as_not_grantable()
    {
        EffectivePermissions agent = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.SupportTickets] = AccessLevel.Edit,
        });

        ApiScopes.NotGrantableBy(agent, [ApiScopes.TicketsRead, ApiScopes.TicketsWrite, ApiScopes.FleetRead])
            .Should().Equal(ApiScopes.TicketsWrite);
        ApiScopes.NotGrantableBy(EffectivePermissions.Everything, ApiScopes.All).Should().BeEmpty();
    }

    private async Task MintAsync(string scope)
    {
        IRenderedComponent<ApiTokensTab> tab = Render<ApiTokensTab>(p => p.Add(c => c.TenantId, tenantId));
        tab.WaitForAssertion(() => tab.FindAll("button").Should().Contain(b => b.TextContent.Contains("New token")));

        await tab.InvokeAsync(() => tab.FindAll("button").First(b => b.TextContent.Contains("New token")).Click());
        tab.Find("input[placeholder='e.g. CI pipeline']").Change("CI");
        await tab.InvokeAsync(() => tab.Find($"#scope-{scope.Replace(":", "\\:")}").Change(true));
        await tab.InvokeAsync(() => tab.FindAll("button").First(b => b.TextContent.Contains("Create token")).Click());
    }

    [Fact]
    public async Task A_role_that_may_only_work_tickets_cannot_mint_a_token_that_merges_them()
    {
        SignedInHolding(AccessLevel.Edit);

        await MintAsync(ApiScopes.TicketsWrite);

        db.ApiTokens.AsNoTracking().Where(t => t.TenantId == tenantId).Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_that_runs_tickets_mints_one()
    {
        SignedInHolding(AccessLevel.Manage);

        await MintAsync(ApiScopes.TicketsWrite);

        db.ApiTokens.AsNoTracking().Where(t => t.TenantId == tenantId).Should().ContainSingle();
    }
}
