using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace EntKube.Web.Tests;

/// <summary>
/// Annex A lives on the customer's Contract page (it used to be a button on each application):
/// every application is listed, including those with no Annex A, and each opens its editor.
/// </summary>
public class AnnexATests : BunitContext, IDisposable
{
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ContractService contracts;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Customer acme;
    private readonly Customer boliden;
    private readonly Guid theirCustomerId = Guid.NewGuid();

    private EffectivePermissions permissions = EffectivePermissions.Everything;

    public AnnexATests()
    {
        // What the person may do, as the customer page hands it down: everything, unless a test
        // says otherwise.
        Services.AddCascadingValue(_ => permissions);

        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        acme = new Customer { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Acme AB" };
        boliden = new Customer { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Boliden" };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other", Slug = "other" });
        db.Customers.AddRange(acme, boliden, new Customer { Id = theirCustomerId, TenantId = otherTenantId, Name = "Regionen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        contracts = new ContractService(factory);

        Services.AddEveryContext(factory);
        Services.AddSingleton(contracts);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils"));
        Services.AddScoped<CurrentActor>();
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string name) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))));
    }

    private App App(Customer customer, string name)
    {
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = name };
        db.Apps.Add(app);
        db.SaveChanges();
        return app;
    }

    private async Task<ApplicationContract> Contract(App app, DateTime? onboarded, DateTime? ended = null, Guid? parent = null) =>
        await contracts.SaveContractAsync(tenantId, new ApplicationContract
        {
            AppId = app.Id, TenantId = tenantId, Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = onboarded, ManagementEndedAt = ended, ParentAppId = parent,
        });

    // ───────────────────────────── The service ─────────────────────────────

    [Fact]
    public async Task Annex_A_lists_every_application_including_those_without_one()
    {
        App none = App(acme, "A no contract");
        App managed = App(acme, "B managed");
        App future = App(acme, "C future");
        App ended = App(acme, "D ended");
        App(boliden, "Boliden's app");
        await Contract(managed, Today.AddDays(-30));
        await Contract(future, Today.AddDays(30));
        await Contract(ended, Today.AddDays(-60), Today.AddDays(-1));
        _ = none;

        List<AnnexARow> rows = await contracts.ListAnnexAAsync(tenantId, acme.Id, DateTime.UtcNow);

        rows.Select(r => (r.AppName, r.Status)).Should().Equal(
            ("A no contract", AnnexAStatus.NotUnderManagement),
            ("B managed", AnnexAStatus.UnderManagement),
            ("C future", AnnexAStatus.NotYetStarted),
            ("D ended", AnnexAStatus.Ended));
    }

    [Fact]
    public async Task Another_tenants_customer_lists_nothing()
    {
        // Their customer HAS an application, so an unscoped list would show it.
        db.Apps.Add(new App { Id = Guid.NewGuid(), CustomerId = theirCustomerId, Name = "Their secret app" });
        db.SaveChanges();

        (await contracts.ListAnnexAAsync(tenantId, theirCustomerId, DateTime.UtcNow)).Should().BeEmpty();
        (await contracts.ListAnnexAAsync(otherTenantId, acme.Id, DateTime.UtcNow)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_parent_from_another_tenant_or_the_application_itself_is_refused()
    {
        App mine = App(acme, "Mine");
        App theirs = new() { Id = Guid.NewGuid(), CustomerId = theirCustomerId, Name = "Theirs" };
        db.Apps.Add(theirs);
        db.SaveChanges();

        Func<Task> foreignParent = () => Contract(mine, Today, parent: theirs.Id);
        await foreignParent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*parent application does not belong*");

        Func<Task> ownParent = () => Contract(mine, Today, parent: mine.Id);
        await ownParent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*its own parent*");
    }

    /// <summary>§10.2.1 inheritance may cross customers within a tenant; the service has always allowed it.</summary>
    [Fact]
    public async Task A_parent_at_another_customer_of_the_tenant_is_accepted()
    {
        App parent = App(boliden, "Boliden platform");
        App instance = App(acme, "Acme instance");

        ApplicationContract saved = await Contract(instance, Today, parent: parent.Id);

        saved.ParentAppId.Should().Be(parent.Id);
    }

    // ───────────────────────────── The customer's Contract page ─────────────────────────────

    private IRenderedComponent<PortfolioContractPanel> RenderPortfolio(Customer customer) =>
        Render<PortfolioContractPanel>(p => p.Add(c => c.Customer, customer).Add(c => c.TenantId, tenantId));

    private static void Press(IRenderedComponent<PortfolioContractPanel> view, string text) =>
        view.FindAll("button").First(b => b.TextContent.Trim().StartsWith(text, StringComparison.Ordinal)).Click();

    /// <summary>
    /// An Annex A is what the customer pays for an application, so recording one is the
    /// agreements permission's — editing the customer's details is not enough. The check is in
    /// the editor's handler; the editor is two components below the customer page.
    /// </summary>
    [Fact]
    public async Task Recording_an_Annex_A_needs_Edit_on_agreements()
    {
        App app = App(acme, "Journalportalen");
        permissions = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.Customers] = AccessLevel.Edit,
            [TenantFeature.CustomerAgreements] = AccessLevel.View,
        });

        IRenderedComponent<PortfolioContractPanel> page = RenderPortfolio(acme);
        page.WaitForAssertion(() => page.Find("[data-test=annex-a-nav]"));

        await page.InvokeAsync(() => page.Find("[data-test=annex-a-nav]").Click());
        await page.InvokeAsync(() => Press(page, "Record Annex A"));
        page.WaitForAssertion(() => page.Find("[data-test=annex-a-editor]"));
        await page.InvokeAsync(() => Press(page, "Record Annex A"));
        await page.InvokeAsync(() => Press(page, "Save Annex A"));

        db.ChangeTracker.Clear();
        db.ApplicationContracts.AsNoTracking().Should().NotContain(c => c.AppId == app.Id);
    }

    [Fact]
    public async Task An_application_with_no_Annex_A_is_listed_and_can_be_put_under_management()
    {
        App app = App(acme, "Journalportalen");
        IRenderedComponent<PortfolioContractPanel> page = RenderPortfolio(acme);
        page.WaitForAssertion(() => page.Find("[data-test=annex-a-nav]"));

        await page.InvokeAsync(() => page.Find("[data-test=annex-a-nav]").Click());
        page.Find("[data-test=annex-a-list]").TextContent.Should().Contain("Journalportalen").And.Contain("Not under management");

        await page.InvokeAsync(() => Press(page, "Record Annex A"));
        page.WaitForAssertion(() => page.Find("[data-test=annex-a-editor]"));
        await page.InvokeAsync(() => Press(page, "Record Annex A"));

        // Not saved yet: a classification cannot be recorded against it (it used to fail with a tenancy error).
        page.Find("[data-test=save-annex-first]");
        page.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Record a change"));

        await page.InvokeAsync(() => Press(page, "Save Annex A"));

        page.WaitForAssertion(() => db.ApplicationContracts.AsNoTracking().Should().ContainSingle(c => c.AppId == app.Id && c.TenantId == tenantId));
        page.WaitForAssertion(() => page.FindAll("button").Should().Contain(b => b.TextContent.Contains("Record a change")));
    }

    [Fact]
    public async Task Recording_a_service_level_change_names_who_recorded_it()
    {
        App app = App(acme, "Journalportalen");
        await Contract(app, Today.AddDays(-10));
        IRenderedComponent<PortfolioContractPanel> page = RenderPortfolio(acme);
        page.WaitForAssertion(() => page.Find("[data-test=annex-a-nav]"));
        await page.InvokeAsync(() => page.Find("[data-test=annex-a-nav]").Click());
        await page.InvokeAsync(() => Press(page, "Open"));
        page.WaitForAssertion(() => page.FindAll("button").Should().Contain(b => b.TextContent.Contains("Record a change")));

        await page.InvokeAsync(() => Press(page, "Record a change"));
        await page.InvokeAsync(() => page.Find("input[placeholder^='e.g.']").Change("Start protocol signed"));
        await page.InvokeAsync(() => page.FindAll("button").Last(b => b.TextContent.Trim() == "Record").Click());

        page.WaitForAssertion(() => db.ApplicationServiceLevels.AsNoTracking().Should().ContainSingle()
            .Which.RecordedBy.Should().Be("nils"));
    }

    [Fact]
    public async Task The_overviews_gap_opens_the_Annex_A_pane()
    {
        App app = App(acme, "Unclassified app");
        await Contract(app, Today.AddDays(-10));
        IRenderedComponent<PortfolioContractPanel> page = RenderPortfolio(acme);

        page.WaitForAssertion(() => page.Markup.Should().Contain("Not being charged for"));
        page.Find("[data-test=annex-a-nav] .pane-dot.critical");

        await page.InvokeAsync(() => Press(page, "Classify these"));

        page.Find("[data-test=annex-a-list]").TextContent.Should().Contain("Unclassified app").And.Contain("Not classified");
    }
}
