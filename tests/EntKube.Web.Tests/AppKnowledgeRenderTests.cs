using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Knowledge;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The knowledge panel, rendered the way the application page renders it.
///
/// <para>Since the knowledge merge its written sections are the application's
/// knowledge-base answers: the same rows the knowledge base lists under the application,
/// edited in the same rich-text editor. What a revision says about who wrote it still
/// matters as much as it did — §19 hands this material to the customer at off-boarding,
/// and a history in which every change was made by nobody looks like a record and is not
/// one.</para>
/// </summary>
public class AppKnowledgeRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly App app;
    private readonly App otherApp;
    private readonly KnowledgeBaseService kb;
    private readonly BunitJSModuleInterop editor;

    private readonly Guid tenantId = Guid.NewGuid();

    /// <summary>The navigator's "Runbook — not written yet" entry.</summary>
    private const string MissingRunbook = @">Runbook</span>\s*<span class=""pane-item-sub""[^>]*>not written yet";

    /// <summary>What the editor hands back when the panel asks it for its HTML; the latest setup wins.</summary>
    private void Type(string html) => editor.Setup<string>("html", _ => true).SetResult(html);

    public AppKnowledgeRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid customerId = Guid.NewGuid();
        app = new App { Id = Guid.NewGuid(), CustomerId = customerId, Name = "Journalportalen" };
        otherApp = new App { Id = Guid.NewGuid(), CustomerId = customerId, Name = "Remissen" };

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Apps.AddRange(app, otherApp);
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        kb = new KnowledgeBaseService(factory);

        Services.AddEveryContext(factory);
        Services.AddSingleton(new KnowledgeService(factory, new ContractService(factory)));
        Services.AddSingleton(kb);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils"));
        Services.AddScoped<CurrentActor>();

        // The editor is Quill in the browser; here it is a module that hands back what was "typed".
        editor = JSInterop.SetupModule("./js/ticket-editor.js");
        editor.SetupVoid("create", _ => true).SetVoidResult();
        editor.SetupVoid("setHtml", _ => true).SetVoidResult();
        editor.SetupVoid("dispose", _ => true).SetVoidResult();
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
    private IRenderedComponent<AppKnowledgePanel> RenderPanel(App? which = null) =>
        Render<AppKnowledgePanel>(p => p
            .Add(c => c.App, which ?? app)
            .Add(c => c.TenantId, tenantId));

    private async Task<KbAnswer> Runbook(string title, string html, string actor = "karin", App? which = null,
        KbVisibility visibility = KbVisibility.Operations) =>
        await kb.SaveAnswerAsync(tenantId, new KbAnswer
        {
            Title = title, BodyHtml = html, AppId = (which ?? app).Id, Section = KnowledgeSectionKind.Runbook,
            Visibility = visibility, Language = "sv", CategoryId = await kb.EnsureAppCategoryAsync(tenantId),
        }, actor);

    private static async Task Click(IRenderedComponent<AppKnowledgePanel> panel, Func<string, bool> text) =>
        await panel.InvokeAsync(() => panel.FindAll("button").First(b => text(b.TextContent.Trim())).Click());

    private async Task SaveFromThePanel(IRenderedComponent<AppKnowledgePanel> panel)
    {
        int before = db.KbAnswers.AsNoTracking().Count() + db.KbAnswerRevisions.AsNoTracking().Count();
        await Click(panel, t => t == "Save");

        // The click returns before the save lands.
        panel.WaitForAssertion(() =>
            (db.KbAnswers.AsNoTracking().Count() + db.KbAnswerRevisions.AsNoTracking().Count()).Should().BeGreaterThan(before));
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// <b>The regression it was written for, kept.</b> A section names whoever wrote it,
    /// and what the panel writes is an application answer: agents only, the application's
    /// customer, a section — never something a customer could be shown.
    /// </summary>
    [Fact]
    public async Task A_section_written_here_is_an_agents_only_answer_naming_who_wrote_it()
    {
        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();

        await Click(panel, t => t.Contains("Add a section", StringComparison.Ordinal));
        panel.Find("input[placeholder='Title']").Change("Restarting the journal service");
        Type("<p>Scale the deployment to zero and back.</p>");
        await SaveFromThePanel(panel);

        KbAnswer saved = db.KbAnswers.AsNoTracking().Single(a => a.AppId == app.Id);
        saved.Title.Should().Be("Restarting the journal service");
        saved.BodyHtml.Should().Contain("Scale the deployment to zero and back.");
        saved.UpdatedBy.Should().Be("nils");
        saved.UpdatedBy.Should().NotBe(CurrentActor.Unattributed);
        saved.Visibility.Should().Be(KbVisibility.Operations);
        saved.CustomerId.Should().Be(app.CustomerId);
        saved.Section.Should().Be(KnowledgeSectionKind.Other);
        saved.ReviewedBy.Should().Be("nils", "writing it is the first confirmation");

        panel.WaitForAssertion(() => panel.Markup.Should().Contain("Agents only"));
    }

    /// <summary>
    /// A revision keeps the <em>previous</em> text, so only the second save makes one —
    /// which is why asserting on revisions after a single save proved nothing at all.
    /// </summary>
    [Fact]
    public async Task A_second_save_records_who_made_the_change()
    {
        KbAnswer first = await Runbook("Restarting the journal service", "<p>The first version.</p>");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();

        await Click(panel, t => t.Contains("Restarting the journal service", StringComparison.Ordinal));
        await Click(panel, t => t == "Edit");
        Type("<p>Scale the deployment to zero and back.</p>");
        panel.Find("input[placeholder^='What changed']").Change("zero first");
        await SaveFromThePanel(panel);

        List<KbAnswerRevision> revisions = [.. db.KbAnswerRevisions.AsNoTracking().Where(r => r.AnswerId == first.Id)];

        revisions.Should().ContainSingle("the previous text is kept exactly once");
        revisions.Single().SavedBy.Should().Be("nils");
        revisions.Single().Body.Should().Be("<p>The first version.</p>");
        revisions.Single().Summary.Should().Be("zero first");
        db.KbAnswers.AsNoTracking().Single(a => a.Id == first.Id).BodyHtml.Should().Contain("zero and back");
    }

    /// <summary>The panel opens on something readable rather than an empty frame.</summary>
    [Fact]
    public void The_panel_offers_the_sections_an_application_ought_to_have()
    {
        string markup = RenderPanel().Markup;

        markup.Should().Contain("Add a section");
        markup.Should().Contain("Journalportalen");
        markup.Should().MatchRegex(MissingRunbook);
    }

    /// <summary>
    /// Only this application's part of the knowledge base: not another application's,
    /// not a tenant-wide help answer, and not one that was retired.
    /// </summary>
    [Fact]
    public async Task Only_this_applications_live_answers_are_listed()
    {
        await Runbook("Ours, current", "<p>a</p>");
        await Runbook("Ours, retired", "<p>b</p>", visibility: KbVisibility.Archived);
        await Runbook("Theirs", "<p>c</p>", which: otherApp);
        await kb.SaveAnswerAsync(tenantId, new KbAnswer
        {
            Title = "How do I reset my password", BodyHtml = "<p>d</p>", Visibility = KbVisibility.Public, Language = "sv",
            CategoryId = await kb.EnsureAppCategoryAsync(tenantId),
        }, "karin");

        string markup = RenderPanel().Markup;

        markup.Should().Contain("Ours, current");
        markup.Should().NotContain("Ours, retired");
        markup.Should().NotContain("Theirs");
        markup.Should().NotContain("reset my password");
        markup.Should().NotMatchRegex(MissingRunbook, "a written runbook is not offered as missing");
    }

    /// <summary>
    /// "Still true" is a review, not an edit: the reviewer is stamped, the text and its
    /// last-changed date stay, and no revision is written.
    /// </summary>
    [Fact]
    public async Task Still_true_stamps_the_reviewer_and_writes_no_version()
    {
        KbAnswer runbook = await Runbook("Restarting the journal service", "<p>Scale to zero.</p>");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        await Click(panel, t => t.Contains("Restarting the journal service", StringComparison.Ordinal));
        await Click(panel, t => t == "Still true");

        panel.WaitForAssertion(() =>
            db.KbAnswers.AsNoTracking().Single(a => a.Id == runbook.Id).ReviewedBy.Should().Be("nils"));

        KbAnswer after = db.KbAnswers.AsNoTracking().Single(a => a.Id == runbook.Id);
        after.UpdatedBy.Should().Be("karin");
        after.UpdatedAt.Should().Be(runbook.UpdatedAt);
        db.KbAnswerRevisions.AsNoTracking().Count(r => r.AnswerId == runbook.Id).Should().Be(0);
    }

    /// <summary>
    /// A moved section shows how it came across and what it said in markdown, and that
    /// version can be restored — converted, with what it said until then kept as well.
    /// </summary>
    [Fact]
    public async Task A_moved_section_shows_its_verdict_and_its_markdown_can_be_restored()
    {
        KbAnswer runbook = await Runbook("Restarting the journal service", "<p>Converted.</p>");

        using (SupportDbContext support = ((IDbContextFactory<SupportDbContext>)new TestDbContextFactory(connection)).CreateDbContext())
        {
            support.KbAnswerRevisions.Add(new KbAnswerRevision
            {
                Id = Guid.NewGuid(), AnswerId = runbook.Id, TenantId = tenantId, Title = runbook.Title,
                Body = "**Scale** to zero", BodyFormat = KbBodyFormat.Markdown, SavedAt = DateTime.UtcNow.AddDays(-1),
                SavedBy = KnowledgeMerge.MoverName,
            });
            support.KnowledgeSectionMoves.Add(new KnowledgeSectionMove
            {
                SectionId = runbook.Id, TenantId = tenantId, AppId = app.Id, AnswerId = runbook.Id, MovedAt = DateTime.UtcNow,
                Verdict = MoveVerdict.Fallback, Notes = "Kept as preformatted text.",
            });
            support.SaveChanges();
        }

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        await Click(panel, t => t.Contains("Restarting the journal service", StringComparison.Ordinal));

        panel.WaitForAssertion(() => panel.Find("[data-test=runbook-move-verdict]").TextContent.Should().Contain("fallback"));
        panel.Find("[data-test=runbook-history]").InnerHtml.Should().Contain("<strong>Scale</strong>", "the markdown is rendered");

        await Click(panel, t => t == "Restore");

        panel.WaitForAssertion(() =>
            db.KbAnswers.AsNoTracking().Single(a => a.Id == runbook.Id).BodyHtml.Should().Contain("<strong>Scale</strong>"));
        db.KbAnswerRevisions.AsNoTracking()
            .Any(r => r.AnswerId == runbook.Id && r.Body == "<p>Converted.</p>").Should().BeTrue("what it said until now is kept");
    }

    /// <summary>
    /// A section the mover has not reached is shown from the old store and cannot be
    /// edited there — an edit to the old row is exactly what the mover would then have to
    /// reconcile.
    /// </summary>
    [Fact]
    public async Task A_section_not_moved_yet_is_shown_read_only()
    {
        db.KnowledgeSections.Add(new KnowledgeSection
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AppId = app.Id, Kind = KnowledgeSectionKind.Runbook,
            Title = "Old runbook", Body = "**Still** in markdown", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        panel.Markup.Should().Contain("not moved to the knowledge base yet");
        panel.Markup.Should().NotMatchRegex(MissingRunbook, "a waiting runbook is not a missing one");

        await Click(panel, t => t.Contains("Old runbook", StringComparison.Ordinal));

        panel.Markup.Should().Contain("<strong>Still</strong>");
        panel.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Edit");
        panel.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Still true");
    }

    /// <summary>
    /// "Open in the knowledge base" leads there: the explorer opens an <c>app=</c> before a
    /// <c>section=</c>, so a link that kept the application would just reload this page.
    /// </summary>
    [Fact]
    public async Task Open_in_the_knowledge_base_leaves_the_application_page()
    {
        KbAnswer runbook = await Runbook("Restarting the journal service", "<p>Scale to zero.</p>");
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/tenants/entit?app={app.Id}&env={Guid.NewGuid()}&tab=knowledge");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        await Click(panel, t => t.Contains("Restarting the journal service", StringComparison.Ordinal));

        string href = panel.FindAll("a").Single(a => a.TextContent.Contains("Open in the knowledge base")).GetAttribute("href")!;

        href.Should().Contain("section=knowledge-base").And.Contain($"answer={runbook.Id}");
        href.Should().NotContain("app=").And.NotContain("env=").And.NotContain("tab=");
    }

    /// <summary>
    /// Once moved, a section is listed once — as the answer it became — and no longer as
    /// waiting: the old row stays behind until release C, so only the ledger can tell.
    /// </summary>
    [Fact]
    public async Task A_moved_section_is_listed_once_as_its_answer()
    {
        db.KnowledgeSections.Add(new KnowledgeSection
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AppId = app.Id, Kind = KnowledgeSectionKind.Runbook,
            Title = "Old runbook", Body = "Restart it.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        await new KnowledgeMerge(new TestDbContextFactory(connection), Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeMerge>.Instance).MoveAsync();

        string markup = RenderPanel().Markup;

        markup.Should().NotContain("not moved to the knowledge base yet");
        System.Text.RegularExpressions.Regex.Matches(markup, "Old runbook").Count.Should().Be(1);
    }

    /// <summary>Edit opens on the section the answer is filed under, not on an empty choice.</summary>
    [Fact]
    public async Task Editing_opens_on_the_sections_own_kind()
    {
        await Runbook("Restarting the journal service", "<p>Scale to zero.</p>");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        await Click(panel, t => t.Contains("Restarting the journal service", StringComparison.Ordinal));
        await Click(panel, t => t == "Edit");

        panel.Find("select[aria-label=Section]").GetAttribute("value").Should().Be(nameof(KnowledgeSectionKind.Runbook));
    }

    /// <summary>
    /// <b>Leaving the editor while it loads.</b> The first open in a circuit waits for Quill to
    /// download, and clicks are handled meanwhile. When it is ready, a left edit must stay left:
    /// no text pushed into an editor nobody is using (or, before, a null reference that ended the
    /// circuit), and the instance it just made forgotten again.
    /// </summary>
    [Fact]
    public async Task Leaving_the_editor_while_it_loads_leaves_nothing_behind()
    {
        JSRuntimeInvocationHandler create = editor.SetupVoid("create", _ => true); // stays pending: Quill is "loading"

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        await Click(panel, t => t.Contains("Add a section", StringComparison.Ordinal));
        panel.WaitForAssertion(() => editor.Invocations["create"].Should().HaveCount(1));

        await Click(panel, t => t == "Cancel");
        int disposedBefore = editor.Invocations["dispose"].Count;

        await panel.InvokeAsync(() => create.SetVoidResult());

        panel.WaitForAssertion(() => editor.Invocations["dispose"].Count.Should().BeGreaterThan(disposedBefore,
            "the instance made after the edit was left is forgotten"));
        editor.Invocations["setHtml"].Should().BeEmpty("nothing is written into an editor nobody is using");
        panel.Markup.Should().Contain("Add a section", "the panel is still there, not an ended circuit");
    }

    /// <summary>Deleting a section here deletes the answer, and it leaves the knowledge base.</summary>
    [Fact]
    public async Task Deleting_a_section_deletes_the_answer()
    {
        KbAnswer runbook = await Runbook("Restarting the journal service", "<p>Scale to zero.</p>");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();
        await Click(panel, t => t.Contains("Restarting the journal service", StringComparison.Ordinal));
        await panel.InvokeAsync(() => panel.Find("button[aria-label='Delete Restarting the journal service']").Click());
        await Click(panel, t => t == "Delete");

        panel.WaitForAssertion(() => db.KbAnswers.AsNoTracking().Any(a => a.Id == runbook.Id).Should().BeFalse());
    }

    // ---- A whole document, pasted -------------------------------------------------------

    /// <summary>
    /// A realistic architecture guide: headings, prose, tables, code, and well past the
    /// 32 KB a Blazor circuit accepts by default.
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
    /// <b>The bug it was written for.</b> Pasting a document did not save it: the circuit
    /// refused a message over 32 KB. The editor now hands its HTML over once, on save — and
    /// this pins the half that is ours: nothing in the save path truncates or mangles a
    /// document of that size, tables and code included.
    /// </summary>
    [Fact]
    public async Task A_pasted_architecture_guide_is_saved_whole()
    {
        string html = KnowledgeConversion.Convert(AnArchitectureGuide()).Html;
        html.Length.Should().BeGreaterThan(32 * 1024, "otherwise this is not the case that broke");

        IRenderedComponent<AppKnowledgePanel> panel = RenderPanel();

        await Click(panel, t => t.Contains("Add a section", StringComparison.Ordinal));
        panel.Find("input[placeholder='Title']").Change("Architecture");
        Type(html);
        await SaveFromThePanel(panel);

        KbAnswer saved = db.KbAnswers.AsNoTracking().Single(a => a.AppId == app.Id);

        saved.BodyHtml.Should().Be(RichText.Sanitize(html), "every byte of it, not a truncation");
        saved.BodyHtml.Should().Contain("component-200", "the end arrives");
        saved.BodyHtml.Should().Contain("<table", "tables survive");
        saved.BodyHtml.Should().Contain("<pre", "so does code");
    }

    /// <summary>
    /// The old store's markdown still renders (for sections not moved yet, and for
    /// versions in the history) with raw HTML disabled — a document pasted from somewhere
    /// else is exactly where a stray script tag comes from.
    /// </summary>
    [Fact]
    public void A_pasted_document_renders_without_letting_html_through()
    {
        string withHtml = AnArchitectureGuide()
            + "\n\n<script>alert('x')</script>\n\n<b>not bold</b>\n";

        Microsoft.AspNetCore.Components.MarkupString rendered = KnowledgeMarkdown.Render(withHtml);

        rendered.Value.Should().NotContain("<script>");
        rendered.Value.Should().Contain("&lt;script&gt;", "it is shown as text, not run");
        rendered.Value.Should().Contain("<h1", "the document itself still renders");
    }
}
