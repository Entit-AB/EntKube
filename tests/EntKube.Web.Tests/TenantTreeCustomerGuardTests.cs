using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The tenant tree still shows customers, as the way apps are grouped under each environment, and
/// still offers a few changes to them right there. Each is the same change the customer pages
/// guard, and asks for the same permission: a new customer is Customers: Manage; a new app, or
/// taking a customer into or out of an environment, is Customers: Edit.
/// </summary>
public class TenantTreeCustomerGuardTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly string slug;

    public TenantTreeCustomerGuardTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        slug = $"entit-{tenantId:N}";

        Guid acme = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = slug });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.Customers.Add(new Customer { Id = acme, TenantId = tenantId, Name = "Acme" });
        db.CustomerEnvironments.Add(new CustomerEnvironment { CustomerId = acme, EnvironmentId = envId });
        db.SaveChanges();

        Services.AddSingleton(new TenantService(
            testDb.Factory, testDb.CreateVaultService(), new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory)));
        Services.AddSingleton(new UserAccessService(testDb.Factory));
        Services.AddSingleton(new TenantRoleService(testDb.Factory));
        Services.AddSingleton(new ToastService());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public new void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "anna"), new Claim(ClaimTypes.NameIdentifier, userId)], "test"))));
    }

    private async Task<IRenderedComponent<TenantExplorer>> OpenTheEnvironmentHolding(AccessLevel customers)
    {
        string userId = $"user-{Guid.NewGuid():N}";
        Guid role = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "anna" });
        db.TenantRoles.Add(new TenantRole
        {
            Id = role, TenantId = tenantId, Name = "Operator",
            PermissionsJson = TenantRoleService.EncodePermissions(new Dictionary<TenantFeature, AccessLevel> { [TenantFeature.Customers] = customers }),
        });
        db.TenantMemberships.Add(new TenantMembership { UserId = userId, TenantId = tenantId, RoleId = role });
        db.SaveChanges();
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs(userId));

        IRenderedComponent<TenantExplorer> page = Render<TenantExplorer>(p => p.Add(c => c.Slug, slug));
        page.WaitForAssertion(() => page.Markup.Should().Contain("production"));

        await page.InvokeAsync(() => page.FindAll("button.node.group").First(b => b.TextContent.Contains("production")).Click());
        page.WaitForAssertion(() => page.Markup.Should().Contain("Acme"));

        return page;
    }

    private static IEnumerable<string?> Offered(IRenderedComponent<TenantExplorer> page) =>
        page.FindAll("button[aria-label]").Select(b => b.GetAttribute("aria-label"));

    [Fact]
    public async Task Somebody_who_may_only_see_customers_is_offered_no_change_to_them()
    {
        IRenderedComponent<TenantExplorer> page = await OpenTheEnvironmentHolding(AccessLevel.View);

        Offered(page).Should().NotContain(["Add app", "Add customer", "Remove customer from environment"]);
    }

    [Fact]
    public async Task Somebody_who_may_change_customers_adds_apps_but_not_customers()
    {
        IRenderedComponent<TenantExplorer> page = await OpenTheEnvironmentHolding(AccessLevel.Edit);

        Offered(page).Should().Contain("Add app");
        Offered(page).Should().NotContain("Add customer");
    }

    [Fact]
    public async Task Whoever_runs_customers_is_offered_a_new_one()
    {
        IRenderedComponent<TenantExplorer> page = await OpenTheEnvironmentHolding(AccessLevel.Manage);

        Offered(page).Should().Contain(["Add app", "Add customer"]);
    }
}
