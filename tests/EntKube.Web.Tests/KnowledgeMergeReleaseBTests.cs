using System.Data.Common;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Knowledge;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Release B of the knowledge merge (docs/helpdesk.md, phase 11): moving each application's runbook
/// sections into the knowledge base — keeping everything anyone wrote, once, never touching the old
/// tables, and carrying across what an older EntKube changed after the move without overwriting.
/// </summary>
public class KnowledgeMergeReleaseBTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KnowledgeMerge merge;
    private readonly KnowledgeService knowledge;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid vard = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();
    private readonly Guid app = Guid.NewGuid();
    private readonly Guid theirApp = Guid.NewGuid();

    private static readonly DateTime Created = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Edited = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Reviewed = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    public KnowledgeMergeReleaseBTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = vard, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.Apps.Add(new App { Id = app, CustomerId = vard, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = theirApp, CustomerId = theirCustomer, Name = "Remissen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        merge = new KnowledgeMerge(factory, NullLogger<KnowledgeMerge>.Instance);
        knowledge = new KnowledgeService(factory, new ContractService(factory));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private KnowledgeSection Section(
        string body = "Restart with `systemctl restart app`.", KnowledgeSectionKind kind = KnowledgeSectionKind.Runbook,
        Guid? onApp = null, Guid? tenant = null, string title = "Runbook", int revisions = 0)
    {
        KnowledgeSection s = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant ?? ours, AppId = onApp ?? app, Kind = kind, Title = title, Body = body,
            CreatedAt = Created, UpdatedAt = Edited, UpdatedBy = "anna", ReviewedAt = Reviewed, ReviewedBy = "nils",
        };

        for (int i = 0; i < revisions; i++)
        {
            s.Revisions.Add(new KnowledgeRevision
            {
                Id = Guid.NewGuid(), SectionId = s.Id, Body = $"Version {i}", SavedAt = Created.AddDays(i), SavedBy = "anna", Summary = $"edit {i}",
            });
        }

        db.KnowledgeSections.Add(s);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return s;
    }

    [Fact]
    public async Task Moving_keeps_every_body_revision_review_stamp_author_and_date()
    {
        KnowledgeSection s = Section(revisions: 2);
        List<Guid> oldRevisionIds = [.. db.KnowledgeRevisions.Select(r => r.Id)];

        await merge.MoveAsync();

        KbAnswer a = db.KbAnswers.AsNoTracking().Single();
        a.Should().BeEquivalentTo(new
        {
            s.Id, AppId = (Guid?)app, Section = (KnowledgeSectionKind?)KnowledgeSectionKind.Runbook, CustomerId = (Guid?)vard,
            Visibility = KbVisibility.Operations, CreatedAt = Created, UpdatedAt = Edited, UpdatedBy = "anna",
            ReviewedAt = (DateTime?)Reviewed, ReviewedBy = "nils", Title = "Runbook",
        });
        a.BodyHtml.Should().Be("<p>Restart with <code>systemctl restart app</code>.</p>");

        List<KbAnswerRevision> history = [.. db.KbAnswerRevisions.AsNoTracking().OrderBy(r => r.SavedAt)];
        history.Select(r => r.Id).Take(2).Should().Equal(oldRevisionIds.OrderBy(id => db.KnowledgeRevisions.Single(r => r.Id == id).SavedAt));
        history.Should().OnlyContain(r => r.BodyFormat == KbBodyFormat.Markdown);
        history.Last().Body.Should().Be(s.Body, "the markdown as it stood is the newest earlier version");
        history.Last().SavedBy.Should().Be(KnowledgeMerge.MoverName);
    }

    [Fact]
    public async Task The_move_never_writes_to_the_old_tables()
    {
        KnowledgeSection s = Section(revisions: 1);
        string before = db.KnowledgeSections.AsNoTracking().Single().Body;

        await merge.MoveAsync();

        db.KnowledgeSections.AsNoTracking().Single().Should().BeEquivalentTo(new { s.Id, Body = before, UpdatedAt = Edited });
        db.KnowledgeRevisions.Count().Should().Be(1);
    }

    [Fact]
    public async Task Moving_twice_moves_once()
    {
        Section();

        await merge.MoveAsync();
        List<KnowledgeMerge.MoveReport> second = await merge.MoveAsync();

        db.KbAnswers.Count().Should().Be(1);
        db.KbAnswerRevisions.Count().Should().Be(1);
        second.Should().BeEmpty("nothing was left to do");
    }

    [Fact]
    public async Task An_answer_deleted_after_the_move_is_not_brought_back()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();
        await new KnowledgeBaseService(new TestDbContextFactory(connection)).DeleteAnswerAsync(ours, s.Id);

        await merge.MoveAsync();

        db.KbAnswers.Should().BeEmpty();
    }

    /// <summary>Only the ledger says "moved": a failed insert must not be counted, and is retried.</summary>
    [Fact]
    public async Task A_failed_move_is_not_counted_and_the_section_still_counts_for_the_meter()
    {
        KnowledgeSection s = Section(kind: KnowledgeSectionKind.Runbook);
        // Something already holds the answer's key, without a ledger row: a genuine failure.
        Guid category = KnowledgeBaseService.AppCategoryId(ours);
        db.KbCategories.Add(new KbCategory { Id = category, TenantId = ours, Name = "Applications" });
        db.KbAnswers.Add(new KbAnswer { Id = s.Id, TenantId = ours, CategoryId = category, Title = "squatter", Visibility = KbVisibility.Archived });
        db.SaveChanges();

        KnowledgeMerge.MoveReport report = (await merge.MoveAsync()).Single();

        report.Failed.Should().Be(1);
        report.LeftToMove.Should().Be(1);
        db.KnowledgeSectionMoves.Should().BeEmpty();
        (await knowledge.GetGapsAsync(ours, app, Reviewed.AddDays(1))).Should().NotContain(g => g.Check == KnowledgeCheck.Runbook,
            "a section waiting to move is still written");
    }

    [Fact]
    public async Task A_section_edited_on_an_older_build_after_the_move_is_appended_as_history_not_applied()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();
        string current = db.KbAnswers.AsNoTracking().Single().BodyHtml;

        // An older EntKube, rolled back to, edits the legacy section the old way.
        Guid olderBuildVersion = Guid.NewGuid();
        db.KnowledgeRevisions.Add(new KnowledgeRevision { Id = olderBuildVersion, SectionId = s.Id, Body = s.Body, SavedAt = Edited.AddDays(10), SavedBy = "eve" });
        // Saved here: the ExecuteUpdate below does not flush tracked changes, and without this the
        // older build's version never existed — the test passed without it for that reason.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.KnowledgeSections.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Body, "Changed on the old build").SetProperty(x => x.UpdatedAt, Edited.AddDays(10)));

        KnowledgeMerge.MoveReport report = (await merge.MoveAsync()).Single();

        report.Diverged.Should().Be(1);
        db.KbAnswers.AsNoTracking().Single().BodyHtml.Should().Be(current, "nothing current is overwritten");
        db.KbAnswerRevisions.AsNoTracking().Should().Contain(r => r.Body == "Changed on the old build" && r.Summary!.Contains("older EntKube"));
        db.KbAnswerRevisions.AsNoTracking().Should().Contain(r => r.Id == olderBuildVersion && r.SavedBy == "eve",
            "the version the older build kept is in the answer's history too, under its own id");
        db.KnowledgeSectionMoves.AsNoTracking().Single().Verdict.Should().Be(MoveVerdict.Diverged);

        (await merge.MoveAsync()).Should().BeEmpty("the change was carried across once");
    }

    [Fact]
    public async Task A_review_made_on_an_older_build_after_the_move_is_carried_across()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();

        await db.KnowledgeSections.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.ReviewedAt, Reviewed.AddDays(30)).SetProperty(x => x.ReviewedBy, "anna"));

        await merge.MoveAsync();

        KbAnswer a = db.KbAnswers.AsNoTracking().Single();
        a.ReviewedAt.Should().Be(Reviewed.AddDays(30));
        a.ReviewedBy.Should().Be("anna");
    }

    [Fact]
    public async Task A_section_edited_after_its_answer_was_deleted_comes_back_archived()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();
        await new KnowledgeBaseService(new TestDbContextFactory(connection)).DeleteAnswerAsync(ours, s.Id);
        await db.KnowledgeSections.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Body, "Edited later").SetProperty(x => x.UpdatedAt, Edited.AddDays(5)));

        await merge.MoveAsync();

        KbAnswer back = db.KbAnswers.AsNoTracking().Single();
        back.Visibility.Should().Be(KbVisibility.Archived, "the deletion still holds everywhere it is shown");
        back.AppId.Should().Be(app);
    }

    [Fact]
    public async Task A_section_whose_application_is_another_tenants_is_kept_as_an_archived_tenant_wide_answer_of_its_own_tenant()
    {
        Section(onApp: theirApp);

        await merge.MoveAsync();

        KbAnswer a = db.KbAnswers.AsNoTracking().Single();
        a.TenantId.Should().Be(ours);
        a.AppId.Should().BeNull();
        a.Visibility.Should().Be(KbVisibility.Archived);
        db.KnowledgeSectionMoves.AsNoTracking().Single().Verdict.Should().Be(MoveVerdict.Detached);
    }

    [Fact]
    public async Task A_long_title_is_kept_whole_in_the_body()
    {
        string title = new('T', 350);
        Section(title: title);

        await merge.MoveAsync();

        KbAnswer a = db.KbAnswers.AsNoTracking().Single();
        a.Title.Should().HaveLength(300).And.EndWith("…");
        a.BodyHtml.Should().StartWith($"<p>{title}</p>");
    }

    [Fact]
    public async Task Each_tenants_sections_move_into_that_tenant()
    {
        Section();
        Section(tenant: theirs, onApp: theirApp);

        await merge.MoveAsync();

        db.KbAnswers.AsNoTracking().Select(a => new { a.TenantId, a.AppId }).ToList()
            .Should().BeEquivalentTo([new { TenantId = ours, AppId = (Guid?)app }, new { TenantId = theirs, AppId = (Guid?)theirApp }]);
    }

    // ───────────────────────────── The health meter ─────────────────────────────

    [Fact]
    public async Task The_health_meter_reads_the_same_before_and_after_the_move()
    {
        Section(kind: KnowledgeSectionKind.Runbook);
        Section(kind: KnowledgeSectionKind.Access, title: "Access");
        DateTime asOf = Reviewed.AddDays(KnowledgeService.ReviewIntervalDays + 1);

        List<KnowledgeGap> before = await knowledge.GetGapsAsync(ours, app, asOf);
        await merge.MoveAsync();
        List<KnowledgeGap> after = await knowledge.GetGapsAsync(ours, app, asOf);

        after.Should().BeEquivalentTo(before);
        after.Should().Contain(g => g.Check == KnowledgeCheck.Architecture, "missing before, missing after");
        after.Count(g => g.Check == KnowledgeCheck.Freshness).Should().Be(2, "both sections are stale");
    }

    [Fact]
    public async Task An_archived_answer_does_not_count_as_written()
    {
        KnowledgeSection s = Section(kind: KnowledgeSectionKind.Runbook);
        await merge.MoveAsync();
        await db.KbAnswers.Where(a => a.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Visibility, KbVisibility.Archived));

        (await knowledge.GetGapsAsync(ours, app, Reviewed.AddDays(1))).Should().Contain(g => g.Check == KnowledgeCheck.Runbook);
    }

    /// <summary>
    /// An older build could rename a section, or file it under another kind, without touching
    /// the body — and then wrote no revision of its own. That is still something somebody did:
    /// the version kept carries the title (Restore applies it) and names the section.
    /// </summary>
    [Fact]
    public async Task A_title_changed_on_an_older_build_after_the_move_can_be_restored()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();

        await db.KnowledgeSections.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Title, "Runbook – DR failover via site B")
            .SetProperty(x => x.Kind, KnowledgeSectionKind.Recovery)
            .SetProperty(x => x.UpdatedAt, Edited.AddDays(10)));

        (await merge.MoveAsync()).Single().Diverged.Should().Be(1);

        KbAnswerRevision kept = db.KbAnswerRevisions.AsNoTracking().Single(r => r.Summary!.Contains("older EntKube"));
        kept.Title.Should().Be("Runbook – DR failover via site B");
        kept.Summary.Should().Contain("Backup and recovery");
        db.KbAnswers.AsNoTracking().Single().Title.Should().Be("Runbook", "nothing current is overwritten");

        KnowledgeBaseService kb = new(new TestDbContextFactory(connection));
        (await kb.RestoreAsync(ours, s.Id, kept.Id, "nils"))!.Title.Should().Be("Runbook – DR failover via site B");
    }

    /// <summary>An answer brought back archived keeps the review the older build recorded, as a move does.</summary>
    [Fact]
    public async Task An_answer_brought_back_keeps_the_review_made_on_the_older_build()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();
        await new KnowledgeBaseService(new TestDbContextFactory(connection)).DeleteAnswerAsync(ours, s.Id);

        await db.KnowledgeSections.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.ReviewedAt, Reviewed.AddDays(30)).SetProperty(x => x.ReviewedBy, "anna"));

        await merge.MoveAsync();

        KbAnswer back = db.KbAnswers.AsNoTracking().Single();
        back.Visibility.Should().Be(KbVisibility.Archived);
        back.ReviewedAt.Should().Be(Reviewed.AddDays(30));
        back.ReviewedBy.Should().Be("anna");
    }

    /// <summary>
    /// <b>One section that cannot be carried across stops nothing else.</b> It used to throw out
    /// of the whole mover, so every later section waited — on every start, because the failure
    /// repeats. Now it is counted as failed, the tenant's other sections still converge in the
    /// same run, and it is retried next start. Both sections are one tenant's, so the per-tenant
    /// boundary cannot stand in for the per-section one.
    /// </summary>
    [Fact]
    public async Task One_change_that_cannot_be_carried_across_stops_nothing_else()
    {
        KnowledgeSection first = Section();
        KnowledgeSection second = Section(kind: KnowledgeSectionKind.Architecture, title: "Architecture");
        await merge.MoveAsync();

        foreach (Guid id in new[] { first.Id, second.Id })
        {
            await db.KnowledgeSections.Where(x => x.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Body, "Changed on the old build").SetProperty(x => x.UpdatedAt, Edited.AddDays(10)));
        }

        FailFirstCarry failing = new();
        KnowledgeMerge racer = new(new RacingSupportFactory(connection, failing), NullLogger<KnowledgeMerge>.Instance);

        List<KnowledgeMerge.MoveReport> reports = await racer.MoveAsync();

        failing.Fired.Should().BeTrue("one carry must actually have failed, or this proves nothing");
        reports.Sum(r => r.Failed).Should().Be(1);
        reports.Single().Diverged.Should().Be(1, "the tenant's other change was still carried across, in the same run");

        (await merge.MoveAsync()).Sum(r => r.Diverged).Should().Be(1, "the failed one is retried, its claim having rolled back");
        db.KbAnswerRevisions.AsNoTracking().Count(r => r.Body == "Changed on the old build").Should().Be(2);
    }

    /// <summary>
    /// <b>One tenant that stops part-way stops no other tenant.</b> Anything but a key conflict
    /// thrown while moving a section used to leave the mover altogether; now that tenant is
    /// reported as failed, with nothing counted as left, and every other tenant still moves.
    /// </summary>
    [Fact]
    public async Task A_tenant_that_stops_part_way_stops_no_other_tenant()
    {
        Section();
        Section(onApp: theirApp, tenant: theirs);

        ThrowOnFirstMove failing = new();
        KnowledgeMerge racer = new(new RacingSupportFactory(connection, failing), NullLogger<KnowledgeMerge>.Instance);

        List<KnowledgeMerge.MoveReport> reports = await racer.MoveAsync();

        failing.Fired.Should().BeTrue();
        reports.Should().HaveCount(2);
        reports.Should().ContainSingle(r => r.Failed == 1 && r.LeftToMove == -1);
        reports.Should().ContainSingle(r => r.Moved == 1 && r.Failed == 0);
        db.KbAnswers.AsNoTracking().Count().Should().Be(1);
    }

    /// <summary>Throws something that is not a key conflict, once, on the first move's save.</summary>
    private sealed class ThrowOnFirstMove : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool moving = eventData.Context?.ChangeTracker.Entries<KnowledgeSectionMove>().Any(e => e.State == EntityState.Added) == true;

            if (!Fired && moving)
            {
                Fired = true;
                throw new InvalidOperationException("Simulated: not a key conflict.");
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// A title is cut to fit, never between the halves of a surrogate pair: Npgsql refuses a lone
    /// surrogate, and that section would then fail to move on every start.
    /// </summary>
    [Fact]
    public async Task A_long_title_is_never_cut_through_a_character()
    {
        Section(title: new string('a', 298) + "😀" + " and the rest of a very long title");

        await merge.MoveAsync();

        string title = db.KbAnswers.AsNoTracking().Single().Title;
        title.Should().Be(new string('a', 298) + "…");
        KnowledgeMerge.ClipTitle(new string('a', 297) + "😀" + "tail").Should().Be(new string('a', 297) + "😀…", "a whole pair that fits is kept");
    }

    /// <summary>Throws, once, on the first save that carries an older build's change across.</summary>
    private sealed class FailFirstCarry : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool carrying = eventData.Context?.ChangeTracker.Entries<KbAnswerRevision>()
                .Any(e => e.State == EntityState.Added && e.Entity.Summary != null && e.Entity.Summary.Contains("older EntKube")) == true;

            if (!Fired && carrying)
            {
                Fired = true;
                throw new DbUpdateException("Simulated: this one cannot be written.");
            }

            return ValueTask.FromResult(result);
        }
    }

    // ---- Two instances at once -----------------------------------------------------------

    /// <summary>
    /// <b>Two instances move the same section.</b> The second one has read the section and is
    /// about to save when the first finishes it. Its save then fails on the keys, and only the
    /// ledger can say whether that was a lost race or a real failure. A lost race is not a
    /// failure: nothing is logged as an error, nothing is counted as failed, and the section is
    /// moved exactly once.
    /// </summary>
    [Fact]
    public async Task An_instance_that_loses_the_race_for_a_section_says_already_moved_not_failed()
    {
        Section(revisions: 2);

        SaveRace race = new() { Competitor = () => merge.MoveAsync() };
        RecordingLogger<KnowledgeMerge> log = new();
        KnowledgeMerge racer = new(new RacingSupportFactory(connection, race), log);

        List<KnowledgeMerge.MoveReport> reports = await racer.MoveAsync();

        // ⚠️ First, and not optional: had the interceptor never matched, no race was staged and
        // everything below would pass for the wrong reason.
        race.Fired.Should().BeTrue("the competitor must have run inside the racer's save");

        reports.Should().BeEmpty("the racer lost the section to the competitor; nothing failed and nothing is left");
        log.Errors.Should().BeEmpty("a lost race is not logged as a failure");
        db.KbAnswers.AsNoTracking().Count().Should().Be(1);
        db.KbAnswerRevisions.AsNoTracking().Count().Should().Be(3, "two old versions and the markdown as it stood, once");
        db.KnowledgeSectionMoves.AsNoTracking().Count().Should().Be(1);
    }

    /// <summary>
    /// <b>Two instances carry the same older-build edit across.</b> Both saw the section differ
    /// from the ledger; the first carries it across while the second is about to. The second's
    /// compare-and-swap on the ledger then matches nothing, so the edit is appended once and
    /// counted once.
    /// </summary>
    [Fact]
    public async Task Two_instances_carry_an_older_build_edit_across_once()
    {
        KnowledgeSection s = Section();
        await merge.MoveAsync();

        await db.KnowledgeSections.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Body, "Changed on the old build").SetProperty(x => x.UpdatedAt, Edited.AddDays(10)));

        List<KnowledgeMerge.MoveReport> competitor = [];
        TransactionRace race = new() { Competitor = async () => competitor = await merge.MoveAsync() };
        KnowledgeMerge racer = new(new RacingSupportFactory(connection, race), NullLogger<KnowledgeMerge>.Instance);

        List<KnowledgeMerge.MoveReport> reports = await racer.MoveAsync();

        race.Fired.Should().BeTrue("the competitor must have run between the racer's read and its claim");

        competitor.Single().Diverged.Should().Be(1, "the competitor carried the edit across");
        reports.Should().BeEmpty("the racer's claim matched nothing, so it carried nothing");
        db.KbAnswerRevisions.AsNoTracking().Count(r => r.Body == "Changed on the old build").Should().Be(1, "appended once");
    }

    /// <summary>
    /// Runs a competitor once, inside the racer's save of a move, before anything is written:
    /// the racer has read the section and decided, and no transaction is open yet. Disarmed
    /// before it runs, so the competitor — on an ordinary factory anyway — cannot recurse.
    /// </summary>
    private sealed class SaveRace : SaveChangesInterceptor
    {
        public Func<Task>? Competitor { get; set; }

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool moving = eventData.Context?.ChangeTracker.Entries<KnowledgeSectionMove>().Any(e => e.State == EntityState.Added) == true;

            if (Competitor is not null && moving)
            {
                Func<Task> competitor = Competitor;
                Competitor = null;
                Fired = true;
                await competitor();
            }

            return result;
        }
    }

    /// <summary>
    /// Runs a competitor once, as the racer is about to open the transaction that claims an
    /// older-build edit: it has read the ledger and the section, and holds no lock. A
    /// SaveChanges hook cannot do this — the claim is an <c>ExecuteUpdate</c> — and a command
    /// hook on the claim would fire inside the racer's open transaction, where the competitor
    /// could not start its own on the shared connection.
    /// </summary>
    private sealed class TransactionRace : DbTransactionInterceptor
    {
        public Func<Task>? Competitor { get; set; }

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Competitor is not null)
            {
                Func<Task> competitor = Competitor;
                Competitor = null;
                Fired = true;
                await competitor();
            }

            return result;
        }
    }

    /// <summary>The same single connection every other context here uses, plus one interceptor.</summary>
    private sealed class RacingSupportFactory(SqliteConnection connection, IInterceptor interceptor) : IDbContextFactory<SupportDbContext>
    {
        public SupportDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<SupportDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
