using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Customers;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The customer pages, rendered: whose customer can be opened, which tabs refuse, and who may
/// change the two things on them that reach outside the organisation — the customer list itself,
/// and who among the customer's people may open the portal.
/// </summary>
public class CustomerPageRenderTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid acme = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();
    private readonly string slug;
    private readonly Guid ourEnvironment = Guid.NewGuid();
    private readonly Guid theirEnvironment = Guid.NewGuid();

    public CustomerPageRenderTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        slug = $"entit-{ours:N}";

        db.Tenants.AddRange(
            new Tenant { Id = ours, Name = "ENTIT", Slug = slug },
            new Tenant { Id = theirs, Name = "Stranger", Slug = $"stranger-{theirs:N}" });
        db.Customers.AddRange(
            new Customer { Id = acme, TenantId = ours, Name = "Acme" },
            new Customer { Id = Guid.NewGuid(), TenantId = ours, Name = "Nowhere AB" },
            new Customer { Id = theirCustomer, TenantId = theirs, Name = "Not yours" });
        db.Set<Data.Environment>().AddRange(
            new Data.Environment { Id = ourEnvironment, TenantId = ours, Name = "ours-production" },
            new Data.Environment { Id = theirEnvironment, TenantId = theirs, Name = "their-production" });
        db.Apps.Add(new App { Id = Guid.NewGuid(), CustomerId = acme, Name = "Journalportalen" });
        db.CustomerEnvironments.Add(new CustomerEnvironment { CustomerId = acme, EnvironmentId = ourEnvironment });
        db.Users.Add(new ApplicationUser { Id = "user-karin", UserName = "karin@acme.example", Email = "karin@acme.example", NormalizedEmail = "KARIN@ACME.EXAMPLE" });
        db.SaveChanges();

        Services.AddSingleton(new TenantService(
            testDb.Factory, testDb.CreateVaultService(), new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory)));
        Services.AddSingleton(new TenantRoleService(testDb.Factory));
        Services.AddSingleton(new CustomerAccessService(testDb.Factory));
        Services.AddSingleton(new ToastService());
        Services.AddSingleton(new PrometheusService(
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, new Moq.Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.Telemetry.PromQueryCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PrometheusService>.Instance));
    }

    public new void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SignedInHolding(params (TenantFeature Feature, AccessLevel Level)[] grants)
    {
        string userId = $"user-{Guid.NewGuid():N}";
        Guid role = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = userId, UserName = "anna" });
        db.TenantRoles.Add(new TenantRole
        {
            Id = role, TenantId = ours, Name = "Account manager",
            PermissionsJson = TenantRoleService.EncodePermissions(grants.ToDictionary(g => g.Feature, g => g.Level)),
        });
        db.TenantMemberships.Add(new TenantMembership { UserId = userId, TenantId = ours, RoleId = role });
        db.SaveChanges();

        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs(userId));
    }

    private sealed class SignedInAs(string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "anna"), new Claim(ClaimTypes.NameIdentifier, userId)], "test"))));
    }

    private IRenderedComponent<CustomerPage> Open(Guid customerId, string tab) =>
        Render<CustomerPage>(p => p.Add(c => c.Slug, slug).Add(c => c.CustomerId, customerId).Add(c => c.Tab, tab));

    // ───────────────────────────── Whose customer ─────────────────────────────

    /// <summary>
    /// The customer comes from the address, and an address can be edited. One that belongs to
    /// another tenant is not found here — not shown with this tenant's permissions.
    /// </summary>
    [Fact]
    public void Another_tenants_customer_is_not_found()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.Manage), (TenantFeature.CustomerPortalAccess, AccessLevel.Manage));

        IRenderedComponent<CustomerPage> page = Open(theirCustomer, "portal");

        page.WaitForAssertion(() => page.Find("[data-test=customer-not-found]"));
        page.Markup.Should().NotContain("Not yours");
    }

    [Fact]
    public void A_member_whose_role_includes_no_customers_has_no_customer_pages()
    {
        SignedInHolding((TenantFeature.SupportTickets, AccessLevel.Manage));

        IRenderedComponent<CustomerPage> page = Open(acme, "portal");

        page.WaitForAssertion(() => page.Find("[data-test=customer-no-access]"));
        page.Markup.Should().NotContain("Acme");
    }

    // ───────────────────────────── Tabs ─────────────────────────────

    [Fact]
    public void A_tab_opened_by_its_address_without_the_permission_is_refused()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.View));

        IRenderedComponent<CustomerPage> page = Open(acme, "portal");

        page.WaitForAssertion(() => page.Find("[data-test=customer-refused]").TextContent.Should().Contain("Portal access: View"));
        page.FindAll("[data-tab]").Select(a => a.GetAttribute("data-tab")).Should().Equal("overview", "apps");
    }

    // ───────────────────────────── Portal access ─────────────────────────────

    [Fact]
    public void Who_may_open_the_portal_can_be_seen_at_View_but_not_changed()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.View), (TenantFeature.CustomerPortalAccess, AccessLevel.View));

        IRenderedComponent<CustomerPage> page = Open(acme, "portal");

        page.WaitForAssertion(() => page.Find("[data-test=portal-access]"));
        page.FindAll("[data-test=portal-grant]").Should().BeEmpty();
    }

    [Fact]
    public async Task Somebody_who_runs_portal_access_can_grant_it()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.View), (TenantFeature.CustomerPortalAccess, AccessLevel.Manage));

        IRenderedComponent<CustomerPage> page = Open(acme, "portal");

        page.WaitForAssertion(() => page.Find("[data-test=portal-grant] input"));
        page.Find("[data-test=portal-grant] input").Input("karin@acme.example");
        await page.InvokeAsync(() => page.Find("[data-test=portal-grant] button").Click());

        db.ChangeTracker.Clear();
        db.CustomerAccesses.AsNoTracking().Should().ContainSingle(a => a.CustomerId == acme && a.UserId == "user-karin");
    }

    // ───────────────────────────── The list ─────────────────────────────

    [Fact]
    public void Creating_a_customer_is_not_offered_below_Manage()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.Edit));

        IRenderedComponent<CustomerList> list = Render<CustomerList>(p => p.Add(c => c.Slug, slug));

        list.WaitForAssertion(() => list.Markup.Should().Contain("Acme"));
        list.FindAll("[data-test=customer-create]").Should().BeEmpty();
        list.Markup.Should().NotContain("Not yours");
    }

    [Fact]
    public async Task Somebody_with_Manage_creates_a_customer()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.Manage));

        IRenderedComponent<CustomerList> list = Render<CustomerList>(p => p.Add(c => c.Slug, slug));

        list.WaitForAssertion(() => list.Find("[data-test=customer-create] input"));
        list.Find("[data-test=customer-create] input").Input("Contoso");
        await list.InvokeAsync(() => list.Find("[data-test=customer-create] button").Click());

        db.ChangeTracker.Clear();
        db.Customers.AsNoTracking().Should().Contain(c => c.TenantId == ours && c.Name == "Contoso");
    }

    /// <summary>
    /// The environment choice arrives from the browser as an id. One the page never offered —
    /// another tenant's — is refused, or the new customer would join an environment that is not
    /// this tenant's.
    /// </summary>
    [Fact]
    public async Task A_new_customer_cannot_be_put_in_another_tenants_environment()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.Manage));

        IRenderedComponent<CustomerList> list = Render<CustomerList>(p => p.Add(c => c.Slug, slug));

        list.WaitForAssertion(() => list.Find("[data-test=customer-create] input"));
        list.Find("[data-test=customer-create] input").Input("Contoso");
        list.Find("[data-test=customer-create] select").Change(theirEnvironment.ToString());
        await list.InvokeAsync(() => list.Find("[data-test=customer-create] button").Click());

        db.ChangeTracker.Clear();
        db.Customers.AsNoTracking().Should().NotContain(c => c.Name == "Contoso");
        db.CustomerEnvironments.AsNoTracking().Should().NotContain(ce => ce.EnvironmentId == theirEnvironment);
    }

    [Fact]
    public async Task A_new_customer_joins_this_tenants_environment_by_default()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.Manage));

        IRenderedComponent<CustomerList> list = Render<CustomerList>(p => p.Add(c => c.Slug, slug));

        list.WaitForAssertion(() => list.Find("[data-test=customer-create] input"));
        list.Find("[data-test=customer-create] input").Input("Contoso");
        await list.InvokeAsync(() => list.Find("[data-test=customer-create] button").Click());

        db.ChangeTracker.Clear();
        Customer made = db.Customers.AsNoTracking().Single(c => c.Name == "Contoso");
        db.CustomerEnvironments.AsNoTracking().Where(ce => ce.CustomerId == made.Id).Select(ce => ce.EnvironmentId)
            .Should().Equal(ourEnvironment);
    }

    // ───────────────────────────── What a customer's page shows ─────────────────────────────

    /// <summary>
    /// Opened without a tab, as the list links it, a customer's page opens on the first tab this
    /// person may see — the overview — and not on a bare row of tabs.
    /// </summary>
    [Fact]
    public void A_customer_opened_without_a_tab_shows_its_overview()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.View));

        IRenderedComponent<CustomerPage> page = Render<CustomerPage>(p => p.Add(c => c.Slug, slug).Add(c => c.CustomerId, acme));

        page.WaitForAssertion(() => page.Markup.Should().Contain("ours-production"));
        page.Find("[data-tab=overview]").ClassList.Should().Contain("active");
    }

    [Fact]
    public void The_apps_tab_lists_the_customers_apps_and_opens_each_on_the_tenant_page()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.View));

        IRenderedComponent<CustomerPage> page = Open(acme, "apps");

        page.WaitForAssertion(() => page.Markup.Should().Contain("Journalportalen"));
        Guid app = db.Apps.AsNoTracking().Single(a => a.Name == "Journalportalen").Id;
        page.FindAll("a").Select(a => a.GetAttribute("href")).Should().Contain($"/tenants/{Uri.EscapeDataString(slug)}?app={app}");
    }

    /// <summary>
    /// The list says which customers belong to no environment — they appear under none in the
    /// tenant tree, so this is the one place they are seen — and how many apps each has.
    /// </summary>
    [Fact]
    public void The_list_flags_a_customer_in_no_environment_and_counts_apps()
    {
        SignedInHolding((TenantFeature.Customers, AccessLevel.View));

        IRenderedComponent<CustomerList> list = Render<CustomerList>(p => p.Add(c => c.Slug, slug));

        list.WaitForAssertion(() => list.Markup.Should().Contain("Nowhere AB"));
        list.FindAll(".list-group-item").Single(r => r.TextContent.Contains("Nowhere AB")).TextContent.Should().Contain("No environment");
        list.FindAll(".list-group-item").Single(r => r.TextContent.Contains("Acme")).TextContent.Should().Contain("1 app");
    }

    /// <summary>The tab title names the tenant only for those who may see its customers.</summary>
    [Fact]
    public void A_stranger_is_not_told_the_tenants_name_even_in_the_tab_title()
    {
        SignedInHolding((TenantFeature.SupportTickets, AccessLevel.Manage));

        IRenderedComponent<CustomerList> list = Render<CustomerList>(p => p.Add(c => c.Slug, slug));

        list.WaitForAssertion(() => list.Find("[data-test=customer-no-access]"));
        list.Markup.Should().NotContain("ENTIT");

        // The browser tab's title is rendered outside the component, so it is asked directly.
        Tenant tenant = db.Tenants.AsNoTracking().Single(t => t.Id == ours);
        CustomerList.TitleFor(tenant, EffectivePermissions.Nothing).Should().Be("Customers");
        CustomerList.TitleFor(null, EffectivePermissions.Everything).Should().Be("Customers");
        CustomerList.TitleFor(tenant, EffectivePermissions.Everything).Should().Be("Customers · ENTIT");
    }
}
