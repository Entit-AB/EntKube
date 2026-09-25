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
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
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
        // else on the page entirely. Both fields bind on change — the body used to bind on
        // input, which re-sent the whole document over the circuit on every keystroke.
        panel.Find("input[placeholder='Title']").Change("Restarting the journal service");
        panel.Find("textarea").Change("Scale the deployment to zero and back.");

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

        panel.Find("textarea").Change("Scale the deployment to zero and back.");

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

    // ---- A whole document, pasted -------------------------------------------------------

    /// <summary>
    /// A realistic architecture guide: headings, prose, tables, fenced code, and well past
    /// the 32 KB a Blazor circuit accepts by default.
    /// </summary>
    private static string AnArchitectureGuide()
    {
        System.Text.StringBuilder md = new();
        md.AppendLine("# Journalportalen — architecture");

        for (int section = 1; section <= 200; section++)
        {
            md.AppendLine().AppendLine($"## {section}. Component {section}");
            md.AppendLine("Runs in its own namespace and talks to the record store over mTLS.");
            md.AppendLine();
            md.AppendLine("| Setting | Value |");
            md.AppendLine("|---|---|");
            md.AppendLine($"| Replicas | {section % 5 + 1} |");
            md.AppendLine();
            md.AppendLine("```bash");
            md.AppendLine($"kubectl -n journal rollout restart deploy/component-{section}");
            md.AppendLine("```");
        }

        return md.ToString();
    }

    /// <summary>
    /// <b>The bug.</b> Pasting a document into a section did not save it. The circuit
    /// refuses a message over 32 KB by default, and the editor sent the whole body on every
    /// keystroke — so the text never reached the server and there was nothing to save, with
    /// no error anywhere to explain it.
    ///
    /// <para>This test cannot see the transport; bUnit does not have one. What it pins is
    /// the half that is ours: nothing in the save path truncates, rejects or mangles a
    /// document of that size, so once the circuit lets it through it arrives intact.</para>
    /// </summary>
    [Fact]
    public async Task A_pasted_architecture_guide_is_saved_whole()
    {
        string guide = AnArchitectureGuide();
        guide.Length.Should().BeGreaterThan(32 * 1024, "otherwise this is not the case that broke");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Contains("Add a section", StringComparison.Ordinal))
            .Click());

        panel.Find("input[placeholder='Title']").Change("Architecture");
        panel.Find("textarea").Change(guide);

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Trim() == "Save").Click());

        db.ChangeTracker.Clear();

        KnowledgeSection saved = db.KnowledgeSections.Single(s => s.AppId == app.Id);

        saved.Body.Should().Be(guide, "every byte of it, not a truncation");
        saved.Body.Should().Contain("```bash", "fenced code survives");
        saved.Body.Should().Contain("| Setting | Value |", "so do tables");
    }

    /// <summary>
    /// And it renders. Markdig is given the whole thing, and raw HTML stays disabled — a
    /// document pasted from somewhere else is exactly where a stray script tag comes from.
    /// </summary>
    [Fact]
    public void A_pasted_document_renders_without_letting_html_through()
    {
        string withHtml = AnArchitectureGuide()
            + "\n\n<script>alert('x')</script>\n\n<b>not bold</b>\n";

        Microsoft.AspNetCore.Components.MarkupString rendered =
            EntKube.Web.Services.Knowledge.KnowledgeMarkdown.Render(withHtml);

        rendered.Value.Should().NotContain("<script>");
        rendered.Value.Should().Contain("&lt;script&gt;", "it is shown as text, not run");
        rendered.Value.Should().Contain("<h1", "the document itself still renders");
    }
}
