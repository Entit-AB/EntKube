using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Knowledge;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Release A of the knowledge merge (docs/helpdesk.md, phase 11): the guards that keep an
/// application's runbook away from customers, the save rules every answer now follows, history and
/// review, the markdown converter, and the dry run — before any data moves.
/// </summary>
public class KnowledgeMergeReleaseATests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KnowledgeBaseService kb;
    private readonly TicketService tickets;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid vard = Guid.NewGuid();
    private readonly Guid skola = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();
    private readonly Guid vardApp = Guid.NewGuid();
    private readonly Guid theirApp = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    public KnowledgeMergeReleaseATests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = vard, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = skola, TenantId = ours, Name = "Skolbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.Apps.Add(new App { Id = vardApp, CustomerId = vard, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = theirApp, CustomerId = theirCustomer, Name = "Remissen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        kb = new KnowledgeBaseService(factory);
        tickets = new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> Category(Guid? tenant = null) =>
        ((await kb.CategoriesAsync(tenant ?? ours)).FirstOrDefault() ?? await kb.SaveCategoryAsync(tenant ?? ours, null, "Allmänt", null)).Id;

    private async Task<KbAnswer> Answer(string title, KbVisibility v, Guid? app = null, Guid? customer = null, string body = "<p>VPN steps</p>") =>
        await kb.SaveAnswerAsync(ours, new KbAnswer
        {
            Title = title, CategoryId = await Category(), Visibility = v, AppId = app, CustomerId = customer,
            Language = "sv", BodyHtml = body,
        }, "nils");

    // ───────────────────────────── Who may see what ─────────────────────────────

    /// <summary>
    /// The CHECK constraint makes an app's Public answer unstorable, so the AppId clause of each
    /// audience is proved here, in memory, where nothing else stands in the way.
    /// </summary>
    [Theory]
    [InlineData(KbVisibility.Public)]
    [InlineData(KbVisibility.Internal)]
    public void An_applications_answer_is_never_offered_nor_shown_whatever_its_visibility(KbVisibility visibility)
    {
        KbAnswer runbook = new() { Title = "Access", TenantId = ours, AppId = vardApp, Visibility = visibility, CustomerId = vard };

        KbAudience.OfferableInReplies(ours, vard, Now).Compile()(runbook).Should().BeFalse();
        KbAudience.VisibleInPortal(ours, vard, Now).Compile()(runbook).Should().BeFalse();

        KbAnswer help = new() { Title = "Access", TenantId = ours, AppId = null, Visibility = visibility, CustomerId = vard };
        KbAudience.OfferableInReplies(ours, vard, Now).Compile()(help).Should().BeTrue("the same answer without an app is a help answer");
    }

    [Fact]
    public async Task A_tenant_wide_operations_answer_is_never_offered_nor_shown_to_a_customer()
    {
        KbAnswer ops = await Answer("VPN gateway restart", KbVisibility.Operations);
        KbAnswer help = await Answer("VPN for users", KbVisibility.Public);
        await kb.SetRelatedAsync(ours, help.Id, [ops.Id]);

        (await kb.ForAgentAsync(ours, vard, "vpn", Now)).Select(a => a.Title).Should().Equal("VPN for users");
        (await kb.ForAgentAsync(ours, null, "vpn", Now)).Select(a => a.Title).Should().Equal("VPN for users");
        (await kb.ForCustomerAsync(ours, vard, "vpn", Now)).Select(a => a.Title).Should().Equal("VPN for users");
        (await kb.GetForCustomerAsync(ours, vard, ops.Id, Now)).Should().BeNull();
        (await kb.RelatedForCustomerAsync(ours, vard, help.Id, Now)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_applications_answer_is_never_offered_listed_opened_related_or_inserted()
    {
        KbAnswer runbook = await Answer("Restore the database", KbVisibility.Operations, app: vardApp);
        KbAnswer help = await Answer("Restore your password", KbVisibility.Public);
        await kb.SetRelatedAsync(ours, help.Id, [runbook.Id]);
        Ticket ticket = await tickets.CreateAsync(ours, vard, null, "x", "", TicketChannel.Email, TicketPriority.P3, Now);

        (await kb.ForAgentAsync(ours, vard, "restore", Now)).Select(a => a.Title).Should().Equal("Restore your password");
        (await kb.ForCustomerAsync(ours, vard, "restore", Now)).Select(a => a.Title).Should().Equal("Restore your password");
        (await kb.GetForCustomerAsync(ours, vard, runbook.Id, Now)).Should().BeNull();
        (await kb.RelatedForCustomerAsync(ours, vard, help.Id, Now)).Should().BeEmpty();
        (await kb.ForInsertAsync(ours, ticket.Id, runbook.Id, Now)).Should().BeNull();
        (await kb.ForInsertAsync(ours, ticket.Id, help.Id, Now)).Should().NotBeNull();
    }

    [Fact]
    public async Task Inserting_asks_again_and_refuses_another_customers_answer()
    {
        KbAnswer forSkola = await Answer("Skola only", KbVisibility.Public, customer: skola);
        Ticket vardTicket = await tickets.CreateAsync(ours, vard, null, "x", "", TicketChannel.Email, TicketPriority.P3, Now);

        (await kb.ForInsertAsync(ours, vardTicket.Id, forSkola.Id, Now)).Should().BeNull();
        (await kb.ForInsertAsync(theirs, vardTicket.Id, forSkola.Id, Now)).Should().BeNull("another tenant cannot name our ticket");
    }

    [Fact]
    public async Task A_runbook_is_never_recorded_as_given_on_a_ticket()
    {
        KbAnswer runbook = await Answer("Access", KbVisibility.Operations, app: vardApp);
        KbAnswer ops = await Answer("Ops note", KbVisibility.Operations);
        Ticket ticket = await tickets.CreateAsync(ours, vard, null, "x", "", TicketChannel.Email, TicketPriority.P3, Now);

        (await kb.LinkTicketAsync(ours, runbook.Id, ticket.Id, "nils")).Should().BeFalse();
        (await kb.LinkTicketAsync(ours, ops.Id, ticket.Id, "nils")).Should().BeFalse();
        db.KbAnswerTickets.AsNoTracking().Should().BeEmpty();
    }

    // ───────────────────────────── The CHECK constraint ─────────────────────────────

    private async Task RawInsertAsync(Guid? appId, int? section, KbVisibility visibility, Guid? customer, DateTime? publishAt = null)
    {
        Guid cat = await Category();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""KbAnswers"" (""Id"", ""TenantId"", ""CategoryId"", ""Title"", ""BodyHtml"", ""BodyText"", ""Language"",
                ""Visibility"", ""PublishAt"", ""CustomerId"", ""Keywords"", ""CreatedAt"", ""UpdatedAt"", ""AppId"", ""Section"")
            VALUES ({Guid.NewGuid().ToString().ToUpperInvariant()}, {ours.ToString().ToUpperInvariant()}, {cat.ToString().ToUpperInvariant()},
                'x', '', '', 'sv', {(int)visibility}, {publishAt}, {customer?.ToString().ToUpperInvariant()}, '',
                '2026-01-01 00:00:00', '2026-01-01 00:00:00', {appId?.ToString().ToUpperInvariant()}, {section})");
    }

    /// <summary>Raw SQL, past every service rule: the database itself refuses a customer-facing runbook.</summary>
    [Theory]
    [InlineData(KbVisibility.Draft, false)]
    [InlineData(KbVisibility.Internal, false)]
    [InlineData(KbVisibility.Public, false)]
    [InlineData(KbVisibility.Archived, true)]
    [InlineData(KbVisibility.Operations, true)]
    public async Task An_applications_answer_can_only_be_stored_operations_or_archived(KbVisibility visibility, bool stored)
    {
        Func<Task> insert = () => RawInsertAsync(vardApp, (int)KnowledgeSectionKind.Runbook, visibility, vard);

        if (stored) await insert.Should().NotThrowAsync(); else await insert.Should().ThrowAsync<System.Data.Common.DbException>().WithMessage("*CK_KbAnswers_AppShape*");
    }

    [Fact]
    public async Task Every_visibility_there_is_has_been_decided_for_applications_answers() =>
        Enum.GetValues<KbVisibility>().Should().BeEquivalentTo(
            [KbVisibility.Draft, KbVisibility.Internal, KbVisibility.Public, KbVisibility.Archived, KbVisibility.Operations],
            "a new visibility must be added to the CHECK constraint's positive list ON PURPOSE, and to the theory above");

    [Fact]
    public async Task The_database_refuses_an_applications_answer_with_no_customer_a_publish_date_or_no_section_and_a_help_answer_with_one()
    {
        Func<Task> noCustomer = () => RawInsertAsync(vardApp, 1, KbVisibility.Operations, null);
        Func<Task> scheduled = () => RawInsertAsync(vardApp, 1, KbVisibility.Operations, vard, Now);
        Func<Task> noSection = () => RawInsertAsync(vardApp, null, KbVisibility.Operations, vard);
        Func<Task> helpWithSection = () => RawInsertAsync(null, 1, KbVisibility.Public, null);

        foreach (Func<Task> insert in new[] { noCustomer, scheduled, noSection, helpWithSection })
        {
            await insert.Should().ThrowAsync<System.Data.Common.DbException>().WithMessage("*CK_KbAnswers_AppShape*");
        }
    }

    // ───────────────────────────── The save rules ─────────────────────────────

    [Theory]
    [InlineData(KbVisibility.Draft)]
    [InlineData(KbVisibility.Internal)]
    [InlineData(KbVisibility.Public)]
    public async Task An_applications_answer_cannot_be_saved_for_customers(KbVisibility visibility)
    {
        Func<Task> save = () => Answer("Access", visibility, app: vardApp);

        await save.Should().ThrowAsync<ArgumentException>().WithMessage("*for agents only*");
    }

    [Fact]
    public async Task An_applications_answer_carries_its_applications_customer_and_a_section_whatever_the_form_says()
    {
        KbAnswer saved = await Answer("Access", KbVisibility.Operations, app: vardApp, customer: skola);

        saved.CustomerId.Should().Be(vard);
        saved.Section.Should().Be(KnowledgeSectionKind.Other);
        saved.PublishAt.Should().BeNull();
    }

    [Fact]
    public async Task An_update_keeps_the_stored_application_whatever_the_form_says()
    {
        KbAnswer saved = await Answer("Access", KbVisibility.Operations, app: vardApp);

        saved.AppId = null;
        saved.Visibility = KbVisibility.Public;
        Func<Task> publish = () => kb.SaveAnswerAsync(ours, saved, "nils");

        await publish.Should().ThrowAsync<ArgumentException>().WithMessage("*for agents only*");
        db.KbAnswers.AsNoTracking().Single(a => a.Id == saved.Id).AppId.Should().Be(vardApp);
    }

    /// <summary>
    /// The other direction: a form that NAMES an application on an update must not turn a help
    /// answer into an app's runbook (or move a runbook to another app). Only creation names one.
    /// </summary>
    [Fact]
    public async Task Naming_an_application_on_an_update_does_not_move_the_answer()
    {
        KbAnswer help = await Answer("Ops note", KbVisibility.Operations);

        help.AppId = vardApp;
        help.Section = KnowledgeSectionKind.Access;
        KbAnswer saved = await kb.SaveAnswerAsync(ours, help, "nils");

        saved.AppId.Should().BeNull();
        saved.Section.Should().BeNull();
        db.KbAnswers.AsNoTracking().Single(a => a.Id == help.Id).AppId.Should().BeNull();
    }

    [Fact]
    public async Task Another_tenants_application_is_refused_and_nothing_is_written()
    {
        Func<Task> save = () => Answer("Theirs", KbVisibility.Operations, app: theirApp);

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not this tenant's*");
        db.KbAnswers.AsNoTracking().Should().BeEmpty();

        (await Answer("Ours", KbVisibility.Operations, app: vardApp)).AppId.Should().Be(vardApp, "the owner can, after the stranger could not");
    }

    [Fact]
    public async Task Saving_under_an_id_that_does_not_exist_is_refused_and_creates_nothing()
    {
        KbAnswer ghost = new() { Id = Guid.NewGuid(), Title = "Ghost", CategoryId = await Category(), Visibility = KbVisibility.Public, Language = "sv" };

        Func<Task> save = () => kb.SaveAnswerAsync(ours, ghost, "nils");

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no longer exists*");
        db.KbAnswers.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public void An_operations_answer_says_operations_not_draft() =>
        KnowledgeBaseService.StateOf(new KbAnswer { Title = "x", Visibility = KbVisibility.Operations }, Now).Should().Be("operations");

    // ───────────────────────────── History and review ─────────────────────────────

    [Fact]
    public async Task Saving_any_answer_keeps_what_it_said_before_and_the_same_text_adds_nothing()
    {
        KbAnswer a = await Answer("VPN", KbVisibility.Public, body: "<p>One</p>");

        a.BodyHtml = "<p>Two</p>";
        await kb.SaveAnswerAsync(ours, a, "anna", default, "Clarified");
        await kb.SaveAnswerAsync(ours, a, "anna");

        KbAnswerRevision only = (await kb.RevisionsAsync(ours, a.Id)).Should().ContainSingle().Subject;
        only.Body.Should().Be("<p>One</p>");
        only.Title.Should().Be("VPN");
        only.SavedBy.Should().Be("anna");
        only.Summary.Should().Be("Clarified");
        only.BodyFormat.Should().Be(KbBodyFormat.Html);
    }

    [Fact]
    public async Task A_review_is_not_an_edit()
    {
        KbAnswer a = await Answer("VPN", KbVisibility.Operations, app: vardApp);
        DateTime changed = db.KbAnswers.AsNoTracking().Single(x => x.Id == a.Id).UpdatedAt;

        (await kb.MarkReviewedAsync(ours, a.Id, "anna", Now)).Should().BeTrue();
        (await kb.MarkReviewedAsync(theirs, a.Id, "eve", Now)).Should().BeFalse();

        KbAnswer after = db.KbAnswers.AsNoTracking().Single(x => x.Id == a.Id);
        after.ReviewedAt.Should().Be(Now);
        after.ReviewedBy.Should().Be("anna");
        after.UpdatedAt.Should().Be(changed);
        (await kb.RevisionsAsync(ours, a.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Restoring_a_markdown_version_converts_it_and_keeps_the_current_one()
    {
        KbAnswer a = await Answer("Restore", KbVisibility.Operations, app: vardApp, body: "<p>Current</p>");
        db.KbAnswerRevisions.Add(new KbAnswerRevision
        {
            Id = Guid.NewGuid(), AnswerId = a.Id, TenantId = ours, Body = "Run **pg_restore**", BodyFormat = KbBodyFormat.Markdown, SavedAt = Now.AddDays(-1),
        });
        await db.SaveChangesAsync();
        Guid markdownVersion = db.KbAnswerRevisions.AsNoTracking().Single().Id;

        KbAnswer? restored = await kb.RestoreAsync(ours, a.Id, markdownVersion, "anna");

        restored!.BodyHtml.Should().Be("<p>Run <strong>pg_restore</strong></p>");
        (await kb.RevisionsAsync(ours, a.Id)).Should().Contain(r => r.Body == "<p>Current</p>");
        (await kb.RestoreAsync(theirs, a.Id, markdownVersion, "eve")).Should().BeNull();
    }

    // ───────────────────────────── The converter ─────────────────────────────

    [Fact]
    public void A_table_becomes_one_body_with_bold_header_cells()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("| Host | Role |\n|---|:---:|\n| db1 | primary |");

        c.Html.Should().Be("<table><tbody><tr><td><strong>Host</strong></td><td><strong>Role</strong></td></tr><tr><td>db1</td><td>primary</td></tr></tbody></table>");
        c.Verdict.Should().Be(MoveVerdict.Mapped);
    }

    [Fact]
    public void A_link_the_sanitiser_would_drop_keeps_its_target_as_text()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("Log in with [the jump host](ssh://jump.example.se) first.");

        c.Html.Should().Contain("the jump host (ssh://jump.example.se)");
        c.Verdict.Should().Be(MoveVerdict.Mapped);
    }

    [Fact]
    public void A_checklist_converts_without_falling_back()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("- [x] Stop the service\n- [ ] Restore the backup");

        c.Verdict.Should().Be(MoveVerdict.Mapped);
        c.Html.Should().Contain("☑").And.Contain("☐").And.Contain("Restore the backup");
    }

    [Theory]
    [InlineData("# Title\n\n###### Deep heading")]
    [InlineData("~~old~~ and ++new++ and ==marked== and H~2~O and x^2^")]
    [InlineData("```\nsystemctl restart nginx\n```")]
    [InlineData("> quoted\n> > nested")]
    [InlineData("1. one\n2. two\n   - nested")]
    [InlineData("Term\n:   Its definition")]
    [InlineData("A footnote[^1].\n\n[^1]: The note.")]
    [InlineData("![diagram](https://docs.example/a.png) and ![local](a.png)")]
    [InlineData("<https://status.example> and <script>raw html stays text</script>")]
    [InlineData("before\n\n---\n\nafter")]
    [InlineData("*[HTML]: Hyper Text\n\nHTML is used.")]
    public void Every_markdown_construct_keeps_all_its_words(string markdown)
    {
        KnowledgeConverted c = KnowledgeConversion.Convert(markdown);

        c.Verdict.Should().NotBe(MoveVerdict.Fallback, c.Html);
    }

    /// <summary>Unwrapped bare, "20[^1]" read "201" and "10^6^" read "106" — and the verifier passed it.</summary>
    [Theory]
    [InlineData("Set the pool size to 20[^1].\n\n[^1]: Per node.", "20[1]")]
    [InlineData("Allow 10^6^ connections.", "10^(6)")]
    [InlineData("Water is H~2~O.", "H_(2)O")]
    public void A_number_never_merges_with_a_footnote_or_superscript(string markdown, string kept)
    {
        KnowledgeConverted c = KnowledgeConversion.Convert(markdown);

        c.Html.Should().Contain(kept);
        c.Verdict.Should().Be(MoveVerdict.Mapped);
    }

    /// <summary>A badge: the parser closes a nested link, leaving the outer one empty and invisible.</summary>
    [Fact]
    public void A_linked_image_keeps_its_link_clickable()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("[![Build](https://ci.example/badge.svg)](https://ci.example/runs)");

        c.Html.Should().Contain("<a href=\"https://ci.example/runs\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">Build (image: https://ci.example/badge.svg)</a>");
    }

    [Fact]
    public void An_empty_link_does_not_count_as_kept()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("[status](https://status.example)",
            html => html.Replace(">status<", "><"));

        c.Verdict.Should().Be(MoveVerdict.Fallback);
    }

    [Fact]
    public void An_abbreviations_expansion_is_written_out()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("*[RTO]: Recovery time objective\n\nThe RTO is four hours.");

        c.Html.Should().Contain("RTO (Recovery time objective)");
    }

    [Fact]
    public void Converting_twice_gives_the_same_html()
    {
        const string md = "# Runbook\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [ ] check\n\n[x](ssh://h)";

        KnowledgeConversion.Convert(md).Should().BeEquivalentTo(KnowledgeConversion.Convert(md));
    }

    /// <summary>A sanitiser that loses words must send the section to the fallback, never lose them.</summary>
    [Fact]
    public void A_section_that_would_lose_words_is_kept_as_its_markdown()
    {
        const string md = "Keep **every** word <here>";

        KnowledgeConverted c = KnowledgeConversion.Convert(md, html => html.Replace("every", ""));

        c.Verdict.Should().Be(MoveVerdict.Fallback);
        c.Html.Should().Be("<pre>Keep **every** word &lt;here&gt;</pre>");
    }

    /// <summary>
    /// Words moved out of order are lost meaning too: a check that only counted characters would let
    /// "restore then stop" pass for "stop then restore".
    /// </summary>
    [Fact]
    public void Words_in_a_different_order_fall_back()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("stop then restore", _ => "<p>restore then stop</p>");

        c.Verdict.Should().Be(MoveVerdict.Fallback);
    }

    [Fact]
    public void A_link_whose_target_would_be_lost_falls_back()
    {
        KnowledgeConverted c = KnowledgeConversion.Convert("[status](https://status.example)", html => html.Replace("https://status.example", "https://elsewhere.example"));

        c.Verdict.Should().Be(MoveVerdict.Fallback);
    }

    // ───────────────────────────── The dry run ─────────────────────────────

    private void Section(Guid tenant, Guid app, string body)
    {
        db.KnowledgeSections.Add(new KnowledgeSection
        {
            Id = Guid.NewGuid(), TenantId = tenant, AppId = app, Kind = KnowledgeSectionKind.Runbook, Title = "Runbook", Body = body,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task The_dry_run_forecasts_per_tenant_and_writes_nothing()
    {
        Section(ours, vardApp, "Plain words.");
        Section(ours, vardApp, "| a | b |\n|---|---|\n| 1 | 2 |");
        Section(ours, theirApp, "Their application, filed under our tenant.");
        Section(theirs, theirApp, "Theirs.");
        int answersBefore = db.KbAnswers.Count();

        List<KnowledgeMergeForecast> forecast = await new KnowledgeMerge(new TestDbContextFactory(connection), Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeMerge>.Instance).ForecastAsync();

        forecast.Single(f => f.TenantId == ours).Should().BeEquivalentTo(new { Exact = 1, Mapped = 1, Fallback = 0, Detached = 1 });
        forecast.Single(f => f.TenantId == theirs).Exact.Should().Be(1);
        db.KbAnswers.Count().Should().Be(answersBefore);
        db.KnowledgeSectionMoves.Should().BeEmpty();
    }

    [Fact]
    public async Task A_section_already_moved_is_not_forecast_again()
    {
        Section(ours, vardApp, "Plain words.");
        Guid id = db.KnowledgeSections.AsNoTracking().Single().Id;
        db.KnowledgeSectionMoves.Add(new KnowledgeSectionMove { SectionId = id, TenantId = ours, AppId = vardApp, AnswerId = id });
        db.SaveChanges();

        (await new KnowledgeMerge(new TestDbContextFactory(connection), Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeMerge>.Instance).ForecastAsync()).Should().BeEmpty();
    }

    // ───────────────────────────── Deletion and the wipe ─────────────────────────────

    [Fact]
    public async Task Deleting_an_application_takes_its_knowledge_and_leaves_the_rest()
    {
        KbAnswer runbook = await Answer("Access", KbVisibility.Operations, app: vardApp);
        KbAnswer help = await Answer("Help", KbVisibility.Public);
        await kb.SetRelatedAsync(ours, help.Id, [runbook.Id]);
        runbook.BodyHtml = "<p>changed</p>";
        await kb.SaveAnswerAsync(ours, runbook, "nils");
        db.KnowledgeSectionMoves.Add(new KnowledgeSectionMove { SectionId = Guid.NewGuid(), TenantId = ours, AppId = vardApp, AnswerId = runbook.Id });
        db.SaveChanges();

        TenantService tenants = new(new TestDbContextFactory(connection), null!, new EntKube.Web.Modules.Api.CatalogApi(new TestDbContextFactory(connection)));
        (await tenants.DeleteAppAsync(ours, vardApp)).Should().BeTrue();

        db.ChangeTracker.Clear();
        db.KbAnswers.Select(a => a.Title).Should().Equal("Help");
        db.KbAnswerLinks.Should().BeEmpty();
        db.KbAnswerRevisions.Should().BeEmpty();
        db.KnowledgeSectionMoves.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_customer_with_application_knowledge_succeeds()
    {
        await Answer("Access", KbVisibility.Operations, app: vardApp);

        TenantService tenants = new(new TestDbContextFactory(connection), null!, new EntKube.Web.Modules.Api.CatalogApi(new TestDbContextFactory(connection)));
        (await tenants.DeleteCustomerAsync(ours, vard)).Should().BeTrue();

        db.ChangeTracker.Clear();
        db.KbAnswers.Should().BeEmpty();
        db.Customers.Any(c => c.Id == vard).Should().BeFalse();
    }

    /// <summary>An app's answers hold their app by a Restrict key, so the purge must take them first.</summary>
    [Fact]
    public async Task Purging_a_tenant_with_application_knowledge_succeeds_and_leaves_the_other_tenant_alone()
    {
        KbAnswer runbook = await Answer("Access", KbVisibility.Operations, app: vardApp);
        runbook.BodyHtml = "<p>changed</p>";
        await kb.SaveAnswerAsync(ours, runbook, "nils");
        Guid theirCategory = await Category(theirs);
        await kb.SaveAnswerAsync(theirs, new KbAnswer
        {
            Title = "Theirs", CategoryId = theirCategory, Visibility = KbVisibility.Operations, AppId = theirApp, Language = "sv",
        }, "eve");

        TenantService tenants = new(new TestDbContextFactory(connection), null!, new EntKube.Web.Modules.Api.CatalogApi(new TestDbContextFactory(connection)));
        (await tenants.DeleteTenantAsync(ours)).Should().BeTrue();

        db.ChangeTracker.Clear();
        db.KbAnswers.Select(a => a.Title).Should().Equal("Theirs");
        db.KbAnswerRevisions.Should().BeEmpty();
    }

    /// <summary>
    /// A wipe truncated three roots and trusted CASCADE to reach the rest — but the helpdesk tables
    /// keep their tenant as a plain column, so nothing led to them and a restore collided.
    /// </summary>
    /// <summary>
    /// …but only what the restore brings back. The telemetry segment catalogue indexes sealed logs
    /// and traces in S3 and is not in the bundle: wiping it would leave every sealed segment
    /// unreachable, with nothing restored in its place. Metric rollups likewise.
    /// </summary>
    [Fact]
    public void The_wipe_spares_what_the_bundle_does_not_restore()
    {
        List<string> wiped = BackupService.TablesToWipeBeyondTheRoots(db.Model);

        wiped.Should().Contain(["KbCategories", "KnowledgeSectionMoves", "NotificationPreferences", "IssueTrackers"]);
        wiped.Should().NotContain(["TelemetrySegments", "MetricRollups"]);
    }

    [Fact]
    public void The_wipe_truncates_every_table_the_roots_cannot_reach()
    {
        List<string> missed = BackupService.TablesTheRootsMiss(db.Model);

        // Plain-column tables: nothing leads to them from a root. (Not only the helpdesk's — the
        // telemetry, CA-trust and dashboard tables were never wiped either.)
        missed.Should().Contain(["KbCategories", "KnowledgeSectionMoves", "NotificationPreferences", "IssueTrackers",
            "OutOfOffices", "CalendarFeeds", "Dashboards", "TelemetryAlertRules"]);
        // Reached from a root: Answers through their application (and their category, truncated above).
        missed.Should().NotContain(["Tenants", "Tickets", "Apps", "Customers", "KbAnswers", "KbAnswerRevisions"],
            "CASCADE from the roots reaches those");
    }
}
