using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Knowledge;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The knowledge panel, rendered the way the application page renders it.
///
/// <para>The last of the screens whose actor came from a parameter nobody passed. The
/// stakes here are lower than a priority confirmation — nothing is priced by who wrote a
/// runbook — but a revision history whose every entry says "unattributed" is not a
/// history, and §19 hands this material to the customer at off-boarding.</para>
/// </summary>
public class AppKnowledgeRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly App app;

    private readonly Guid tenantId = Guid.NewGuid();

    public AppKnowledgeRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid customerId = Guid.NewGuid();
        app = new App { Id = Guid.NewGuid(), CustomerId = customerId, Name = "Journalportalen" };

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(app);
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton(new KnowledgeService(factory, new ContractService(factory)));
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
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))));
    }

    /// <summary>Rendered as the application page renders it: an app and a tenant, no actor.</summary>
    private IRenderedComponent<AppKnowledgePanel> RenderPanel() =>
        Render<AppKnowledgePanel>(p => p
            .Add(c => c.App, app)
            .Add(c => c.TenantId, tenantId));

    /// <summary>
    /// <b>The regression.</b> A revision names whoever wrote it. §19 hands the runbook to
    /// the customer at off-boarding, and a history in which every change was made by
    /// nobody is worth less than no history at all — it looks like a record.
    /// </summary>
    [Fact]
    public async Task A_saved_section_names_who_wrote_it()
    {
        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Contains("Add a section", StringComparison.Ordinal))
            .Click());

        // Selected by placeholder, not by class: "input.form-control" matched something
        // else on the page entirely. And the two fields bind on different events — the
        // title on change, the body on input so its preview keeps up. Neither of those is
        // visible anywhere but in a render.
        panel.Find("input[placeholder='Title']").Change("Restarting the journal service");
        panel.Find("textarea").Input("Scale the deployment to zero and back.");

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Trim() == "Save").Click());

        db.ChangeTracker.Clear();

        KnowledgeSection saved = db.KnowledgeSections.Single(s => s.AppId == app.Id);
        saved.Title.Should().Be("Restarting the journal service");
        saved.UpdatedBy.Should().Be("nils");
        saved.UpdatedBy.Should().NotBe(CurrentActor.Unattributed);
    }

    /// <summary>
    /// A revision keeps the <em>previous</em> body, so only the second save makes one —
    /// which is why asserting on revisions after a single save proved nothing at all, and
    /// passed just as happily against the broken wiring.
    /// </summary>
    [Fact]
    public async Task A_second_save_records_who_made_the_change()
    {
        KnowledgeService knowledge = Services.GetRequiredService<KnowledgeService>();

        KnowledgeSection first = await knowledge.SaveSectionAsync(
            tenantId, app.Id, null, KnowledgeSectionKind.Other,
            "Restarting the journal service", "The first version.", "karin", null);

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Contains("Restarting the journal service", StringComparison.Ordinal))
            .Click());

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Contains("Edit", StringComparison.OrdinalIgnoreCase)).Click());

        panel.Find("textarea").Input("Scale the deployment to zero and back.");

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Trim() == "Save").Click());

        db.ChangeTracker.Clear();

        List<KnowledgeRevision> revisions =
            [.. db.KnowledgeRevisions.Where(r => r.SectionId == first.Id)];

        revisions.Should().ContainSingle("the previous body is kept exactly once");
        revisions.Single().SavedBy.Should().Be("nils");
        revisions.Single().Body.Should().Be("The first version.");
    }

    /// <summary>
    /// The panel opens on something readable rather than an empty frame — the rework that
    /// produced it was about someone arriving at three in the morning.
    /// </summary>
    [Fact]
    public void The_panel_offers_the_sections_an_application_ought_to_have()
    {
        string markup = RenderPanel().Markup;

        markup.Should().Contain("Add a section");
        markup.Should().Contain("Journalportalen");
    }
}
