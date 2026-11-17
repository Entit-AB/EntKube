using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 1 of helpdesk parity (docs/helpdesk.md): tags, links, merge, split, reminders,
/// automatic close, followers, overviews and bulk changes — and above all the places where
/// these meet the agreement's clocks, because that is where a Zammad habit would quietly
/// rewrite a §14.6 record.
/// </summary>
public class TicketWorkTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026, inside an office-hours window.</summary>
    private static DateTime Tue(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketWorkService work;
    private readonly TicketBulkService bulk;
    private readonly TicketFollowUp followUp;
    private readonly SupportMailService mail;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid otherCustomer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();
    private readonly Guid app = Guid.NewGuid();
    private readonly Guid theirApp = Guid.NewGuid();

    private const string Reporter = "karin@kund.example";
    private const string Colleague = "user-anna";

    public TicketWorkTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Nordvik Drift", Slug = "nordvik" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = otherCustomer, TenantId = ours, Name = "Skolbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.Apps.Add(new App { Id = app, CustomerId = customer, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = theirApp, CustomerId = theirCustomer, Name = "Remissen" });

        Guid role = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = role, TenantId = ours, Name = "Admin" });
        db.Users.Add(new ApplicationUser { Id = Colleague, UserName = "anna", Email = "anna@entit.example" });
        db.TenantMemberships.Add(new TenantMembership { UserId = Colleague, TenantId = ours, RoleId = role });

        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        TicketNotifier notifier = SilentTicketNotifier.For(factory);
        SupportDutyService duty = new(factory);

        tickets = new TicketService(factory, contracts, notifier);
        work = new TicketWorkService(factory, tickets, notifier, duty);
        bulk = new TicketBulkService(tickets, work);
        followUp = new TicketFollowUp(factory, notifier, duty, NullLogger<TicketFollowUp>.Instance);
        mail = new SupportMailService(
            factory, new RuleBasedMailAnalyst(TestTicketReference.Instance),
            new MailTriageRuleService(factory), tickets,
            new TimeService(factory, contracts), TestTicketReference.Instance, duty);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Reported(string title = "Remisser kommer inte fram", DateTime? at = null,
        Guid? customerId = null, TicketPriority priority = TicketPriority.P2) =>
        tickets.CreateAsync(
            ours, customerId ?? customer, customerId is null ? app : null, title, "Inget syns i listan.",
            TicketChannel.Email, priority, at ?? Tue(10), "Karin", Reporter);

    private Ticket Fresh(Guid id)
    {
        db.ChangeTracker.Clear();
        return db.Tickets.Include(t => t.Pauses).Include(t => t.Events).Single(t => t.Id == id);
    }

    // ───────────────────────────── Tags ─────────────────────────────

    [Theory]
    [InlineData("  VPN  ", "vpn")]
    [InlineData("Slow   Login", "slow login")]
    public void A_tag_is_stored_in_one_spelling(string raw, string stored) =>
        TicketWorkService.NormaliseTag(raw).Should().Be(stored);

    [Theory]
    [InlineData("")]
    [InlineData("a,b")]
    [InlineData("an extremely long tag that nobody would ever type into a box like this one")]
    public void Some_text_cannot_be_a_tag(string raw) =>
        TicketWorkService.NormaliseTag(raw).Should().BeNull();

    [Fact]
    public async Task Adding_a_tag_twice_leaves_one_and_the_vocabulary_counts_tickets()
    {
        Ticket a = await Reported();
        Ticket b = await Reported("Annat");

        await work.AddTagAsync(ours, a.Id, "VPN", "nils", Tue(11));
        await work.AddTagAsync(ours, a.Id, "vpn", "nils", Tue(11));
        await work.AddTagAsync(ours, b.Id, "vpn", "nils", Tue(11));
        await work.AddTagAsync(ours, b.Id, "login", "nils", Tue(11));

        (await work.GetTagsAsync(ours, a.Id)).Should().Equal("vpn");
        (await work.GetTagVocabularyAsync(ours)).Should().Equal(
            new TicketTagUse("vpn", 2), new TicketTagUse("login", 1));
    }

    [Fact]
    public async Task Another_tenant_cannot_tag_our_ticket_or_read_our_vocabulary()
    {
        Ticket a = await Reported();
        await work.AddTagAsync(ours, a.Id, "vpn", "nils", Tue(11));

        (await work.AddTagAsync(theirs, a.Id, "intruder", "x", Tue(11))).Should().BeNull();
        (await work.GetTagVocabularyAsync(theirs)).Should().BeEmpty();
        (await work.GetTagsAsync(ours, a.Id)).Should().Equal("vpn");
    }

    // ───────────────────────────── Links ─────────────────────────────

    [Fact]
    public async Task A_link_reads_correctly_from_both_ends()
    {
        Ticket parent = await Reported("Huvudärende");
        Ticket child = await Reported("Deluppgift");

        await work.LinkAsync(ours, child.Id, parent.Number, TicketLinkRole.Parent, "nils", Tue(11));

        (await work.GetLinksAsync(ours, child.Id)).Should().ContainSingle(l =>
            l.Role == TicketLinkRole.Parent && l.OtherNumber == parent.Number);
        (await work.GetLinksAsync(ours, parent.Id)).Should().ContainSingle(l =>
            l.Role == TicketLinkRole.Child && l.OtherNumber == child.Number);
    }

    [Fact]
    public async Task Two_tickets_cannot_each_be_the_others_parent()
    {
        Ticket a = await Reported("A");
        Ticket b = await Reported("B");

        await work.LinkAsync(ours, a.Id, b.Number, TicketLinkRole.Parent, "nils", Tue(11));

        Func<Task> contradiction = () =>
            work.LinkAsync(ours, a.Id, b.Number, TicketLinkRole.Child, "nils", Tue(11));

        await contradiction.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*parent and child the other way round*");
    }

    [Fact]
    public async Task Another_tenants_ticket_number_is_no_such_ticket()
    {
        Ticket ourTicket = await Reported();
        Ticket theirTicket = await tickets.CreateAsync(
            theirs, theirCustomer, theirApp, "Deras", "", TicketChannel.Email, null, Tue(10));

        Func<Task> link = () =>
            work.LinkAsync(ours, ourTicket.Id, theirTicket.Number, TicketLinkRole.Related, "nils", Tue(11));

        await link.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*no ticket #{theirTicket.Number} here*");
    }

    [Fact]
    public async Task Moving_a_ticket_to_another_tenant_drops_its_links()
    {
        Ticket a = await Reported("A");
        Ticket b = await Reported("B");
        await work.LinkAsync(ours, a.Id, b.Number, TicketLinkRole.Related, "nils", Tue(11));

        await tickets.MoveAsync(ours, a.Id, theirCustomer, theirApp, "nils", "Theirs.", Tue(12));

        (await work.GetLinksAsync(ours, b.Id)).Should().BeEmpty(
            "a link left behind would show the other tenant's ticket title on ours");
    }

    // ───────────────────────────── Merge ─────────────────────────────

    [Fact]
    public async Task A_merge_folds_the_duplicate_in_and_stops_its_clocks()
    {
        Ticket original = await Reported("Original", Tue(9));
        Ticket duplicate = await Reported("Duplicate", Tue(10));

        TicketWorkService.MergeResult? result = await work.MergeAsync(
            ours, duplicate.Id, original.Number, excludeFromSla: false, null, "nils", Tue(10, 30));

        Ticket after = Fresh(duplicate.Id);
        after.Status.Should().Be(TicketStatus.Merged);
        after.MergedIntoTicketId.Should().Be(original.Id);
        after.ResolvedAt.Should().Be(Tue(10, 30));

        // Unanswered until the merge, and the merge is the assessment: the response clock is
        // met there, not left running into a breach nobody will now prevent.
        result!.Value.RecordedAsResponse.Should().BeTrue();
        after.FirstResponseAt.Should().Be(Tue(10, 30));
        TicketService.StatusOf(after, Tue(10, 30).AddDays(14)).Response.Breached.Should().BeFalse();

        (await work.GetLinksAsync(ours, duplicate.Id)).Should().ContainSingle(l =>
            l.Role == TicketLinkRole.Parent && l.OtherNumber == original.Number);
    }

    /// <summary>
    /// ⭐ The rule docs/helpdesk.md exists to keep. A P2 must be answered within its response
    /// time; merging it away afterwards must not make the miss disappear.
    /// </summary>
    [Fact]
    public async Task A_merge_never_erases_a_missed_response()
    {
        Ticket original = await Reported("Original", Tue(9));
        Ticket duplicate = await Reported("Duplicate", Tue(10), priority: TicketPriority.P2);

        DateTime aWeekLater = Tue(10).AddDays(7);

        TicketWorkService.MergeResult? result = await work.MergeAsync(
            ours, duplicate.Id, original.Number, excludeFromSla: false, null, "nils", aWeekLater);

        result!.Value.ResponseWasLate.Should().BeTrue();

        Ticket after = Fresh(duplicate.Id);
        TicketSlaStatus status = TicketService.StatusOf(after, aWeekLater);
        status.Response.Breached.Should().BeTrue();
        status.IsPenaltyDeviation.Should().BeTrue("excluding it is a separate act, not a side effect of merging");
        after.ExcludedFromSla.Should().BeFalse();
    }

    [Fact]
    public async Task Excluding_the_duplicate_is_asked_for_and_reasoned_not_assumed()
    {
        Ticket original = await Reported("Original", Tue(9));
        Ticket duplicate = await Reported("Duplicate", Tue(10));

        Func<Task> withoutReason = () => work.MergeAsync(
            ours, duplicate.Id, original.Number, excludeFromSla: true, "  ", "nils", Tue(11));
        await withoutReason.Should().ThrowAsync<ArgumentException>().WithMessage("*needs a reason*");

        await work.MergeAsync(ours, duplicate.Id, original.Number, true, "Same outage as the original.", "nils", Tue(11));

        Ticket after = Fresh(duplicate.Id);
        after.ExcludedFromSla.Should().BeTrue();
        after.SlaExclusionReason.Should().Be("Same outage as the original.");
        after.Events.Should().Contain(e => e.Actor == "nils" && e.Detail.Contains("Excluded from SLA"));
    }

    [Fact]
    public async Task The_duplicates_history_is_not_rewritten_but_is_shown_on_the_target()
    {
        Ticket original = await Reported("Original", Tue(9));
        Ticket duplicate = await Reported("Duplicate", Tue(10));
        await tickets.AddEventAsync(ours, duplicate.Id, TicketEventKind.Note, "Customer called too.", "nils", Tue(10, 5));

        List<Guid> before = [.. db.TicketEvents.Where(e => e.TicketId == duplicate.Id).Select(e => e.Id)];

        await work.MergeAsync(ours, duplicate.Id, original.Number, false, null, "nils", Tue(11));

        db.TicketEvents.Where(e => e.TicketId == duplicate.Id).Select(e => e.Id).ToList()
            .Should().Contain(before, "§14.6 makes the duplicate's history its record; nothing moves off it");

        (await work.GetMergedHistoryAsync(ours, original.Id)).Should().Contain(m =>
            m.FromNumber == duplicate.Number && m.Event.Detail == "Customer called too.");
    }

    [Fact]
    public async Task A_merge_across_customers_is_refused()
    {
        Ticket ours1 = await Reported("Vårdbolaget's");
        Ticket other = await Reported("Skolbolaget's", customerId: otherCustomer);

        Func<Task> merge = () => work.MergeAsync(ours, ours1.Id, other.Number, false, null, "nils", Tue(11));

        await merge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another customer*");
        Fresh(ours1.Id).Status.Should().NotBe(TicketStatus.Merged);
    }

    [Fact]
    public async Task A_merge_into_a_merged_or_closed_ticket_is_refused()
    {
        Ticket a = await Reported("A");
        Ticket b = await Reported("B");
        Ticket c = await Reported("C");

        await work.MergeAsync(ours, b.Id, a.Number, false, null, "nils", Tue(11));
        Func<Task> intoMerged = () => work.MergeAsync(ours, c.Id, b.Number, false, null, "nils", Tue(11));
        await intoMerged.Should().ThrowAsync<InvalidOperationException>().WithMessage("*itself merged*");

        await tickets.CloseAsync(ours, a.Id, "nils", Tue(12));
        Func<Task> intoClosed = () => work.MergeAsync(ours, c.Id, a.Number, false, null, "nils", Tue(12));
        await intoClosed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Reopen it first*");
    }

    [Fact]
    public async Task A_merged_ticket_cannot_be_reopened_and_leaves_the_queue()
    {
        Ticket original = await Reported("Original");
        Ticket duplicate = await Reported("Duplicate");
        await work.MergeAsync(ours, duplicate.Id, original.Number, false, null, "nils", Tue(11));

        Func<Task> reopen = () => tickets.ReopenAsync(ours, duplicate.Id, "?", "nils", Tue(12));
        await reopen.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Reopen that ticket instead*");

        (await tickets.GetTenantQueueAsync(ours, Tue(12))).Select(s => s.Ticket.Id)
            .Should().Contain(original.Id).And.NotContain(duplicate.Id);
    }

    /// <summary>
    /// The reporter was sent the duplicate's number and goes on quoting it. Their reply
    /// belongs on the ticket the matter now lives on — not in a third, new ticket.
    /// </summary>
    [Fact]
    public async Task A_reply_quoting_a_merged_duplicate_is_filed_on_the_ticket_it_went_into()
    {
        Ticket original = await Reported("Original", Tue(9));
        Ticket duplicate = await Reported("Duplicate", Tue(10));
        await work.MergeAsync(ours, duplicate.Id, original.Number, false, null, "nils", Tue(11));

        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(), TenantId = ours, CustomerId = customer, Domain = "kund.example",
        });
        await db.SaveChangesAsync();

        InboundMailMessage? reply = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = ours,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = Reporter,
            Subject = $"Re: [#{duplicate.Number}] Duplicate",
            Body = "Det fungerar fortfarande inte.",
            SentAt = Tue(12),
        });

        MailSuggestion append = reply!.Suggestions.Should().ContainSingle(s =>
            s.Kind == MailSuggestionKind.AppendToTicket).Subject;
        append.TicketId.Should().Be(original.Id);

        await mail.AcceptAsync(ours, append.Id, "nils", Tue(13));

        db.TicketEvents.Where(e => e.TicketId == original.Id).Select(e => e.Detail).ToList()
            .Should().Contain(d => d.Contains("Det fungerar fortfarande inte."));
    }

    // ───────────────────────────── Split ─────────────────────────────

    /// <summary>
    /// The new ticket is reported when the customer raised it — the entry's time — so the
    /// split cannot make us punctual by having been slow to notice.
    /// </summary>
    [Fact]
    public async Task A_split_opens_a_linked_ticket_reported_when_the_entry_was_written()
    {
        Ticket source = await Reported("Two problems", Tue(9));
        await tickets.AddEventAsync(ours, source.Id, TicketEventKind.Note, "Also: printing fails.", "Karin", Tue(9, 30));
        Guid entry = db.TicketEvents.Single(e => e.Detail == "Also: printing fails.").Id;

        Ticket? created = await work.SplitAsync(ours, source.Id, entry, "Printing fails", "nils", Tue(14));

        created!.ReportedAt.Should().Be(Tue(9, 30));
        created.Description.Should().Be("Also: printing fails.");
        created.CustomerId.Should().Be(customer);
        (await work.GetLinksAsync(ours, created.Id)).Should().ContainSingle(l =>
            l.Role == TicketLinkRole.Parent && l.OtherNumber == source.Number);
    }

    // ───────────────────────────── Reminders and automatic close ─────────────────────────────

    [Fact]
    public async Task A_reminder_fires_once_however_many_passes_see_it()
    {
        Ticket t = await Reported();
        await work.SetReminderAsync(ours, t.Id, Tue(12), "Call Karin back", "nils", Tue(11));

        FollowUpPass first = await followUp.RunAsync(ours, Tue(12, 1));
        FollowUpPass second = await followUp.RunAsync(ours, Tue(12, 2));

        first.RemindersSent.Should().Be(1);
        second.RemindersSent.Should().Be(0);
        Fresh(t.Id).Events.Count(e => e.Kind == TicketEventKind.Reminder && e.Detail.StartsWith("Reminder due"))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_reminder_never_pauses_a_clock()
    {
        Ticket t = await Reported(priority: TicketPriority.P2);
        await work.SetReminderAsync(ours, t.Id, Tue(16), null, "nils", Tue(10, 5));

        Ticket after = Fresh(t.Id);
        after.Status.Should().Be(TicketStatus.New);
        after.Pauses.Should().BeEmpty();
        TicketService.StatusOf(after, Tue(15)).Response.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task Only_a_resolved_ticket_closes_itself_and_a_reopen_cancels_it()
    {
        Ticket t = await Reported();
        Func<Task> notResolved = () => work.SetAutoCloseAsync(ours, t.Id, Tue(15), "nils", Tue(11));
        await notResolved.Should().ThrowAsync<InvalidOperationException>();

        await tickets.ResolveAsync(ours, t.Id, "Restarted the service.", "nils", Tue(11));
        await work.SetAutoCloseAsync(ours, t.Id, Tue(15), "nils", Tue(11));
        await tickets.ReopenAsync(ours, t.Id, "Still broken.", "Karin", Tue(14));

        (await followUp.RunAsync(ours, Tue(16))).Closed.Should().Be(0);
        Fresh(t.Id).Status.Should().Be(TicketStatus.InProgress);
    }

    [Fact]
    public async Task A_resolved_ticket_closes_itself_once_when_its_time_comes()
    {
        Ticket t = await Reported();
        await tickets.ResolveAsync(ours, t.Id, "Restarted the service.", "nils", Tue(11));
        await work.SetAutoCloseAsync(ours, t.Id, Tue(15), "nils", Tue(11));

        (await followUp.RunAsync(ours, Tue(14))).Closed.Should().Be(0);
        (await followUp.RunAsync(ours, Tue(15, 1))).Closed.Should().Be(1);
        (await followUp.RunAsync(ours, Tue(15, 2))).Closed.Should().Be(0);

        Ticket after = Fresh(t.Id);
        after.Status.Should().Be(TicketStatus.Closed);
        after.Events.Should().ContainSingle(e => e.Kind == TicketEventKind.Closed && e.CustomerVisible);
    }

    // ───────────────────────────── Followers ─────────────────────────────

    [Fact]
    public async Task Only_a_member_of_the_tenant_can_follow_its_tickets()
    {
        Ticket t = await Reported();

        Func<Task> stranger = () => work.SubscribeAsync(
            ours, t.Id, "user-stranger", "Mallory", "m@evil.example", TicketSubscriptionReason.Subscribed, "x", Tue(11));
        await stranger.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a member*");

        (await work.SubscribeAsync(ours, t.Id, Colleague, "anna", "anna@entit.example",
            TicketSubscriptionReason.Subscribed, "anna", Tue(11))).Should().BeTrue();
        (await work.GetFollowersAsync(ours, t.Id)).Should().ContainSingle(f => f.UserId == Colleague);
    }

    [Fact]
    public async Task A_followers_digest_cursor_moves_once_and_only_after_the_ticket_settles()
    {
        Ticket t = await Reported();
        await work.SubscribeAsync(ours, t.Id, Colleague, "anna", "anna@entit.example",
            TicketSubscriptionReason.Subscribed, "anna", DateTime.UtcNow);

        await tickets.AddEventAsync(ours, t.Id, TicketEventKind.Note, "Logs attached.", "nils", DateTime.UtcNow);
        DateTime cursorBefore = db.TicketSubscriptions.AsNoTracking().Single().NotifiedThrough;

        // Busy a moment ago: nothing yet.
        await followUp.RunAsync(ours, DateTime.UtcNow);
        db.TicketSubscriptions.AsNoTracking().Single().NotifiedThrough.Should().Be(cursorBefore);

        // Quiet for longer than the settle time: the cursor moves past the note, once.
        DateTime later = DateTime.UtcNow + TicketFollowUp.DigestSettle + TimeSpan.FromSeconds(1);
        await followUp.RunAsync(ours, later);
        DateTime moved = db.TicketSubscriptions.AsNoTracking().Single().NotifiedThrough;
        moved.Should().BeAfter(cursorBefore);

        await followUp.RunAsync(ours, later);
        db.TicketSubscriptions.AsNoTracking().Single().NotifiedThrough.Should().Be(moved);
    }

    [Fact]
    public async Task Somebody_who_left_the_tenant_stops_following()
    {
        Ticket t = await Reported();
        await work.SubscribeAsync(ours, t.Id, Colleague, "anna", "anna@entit.example",
            TicketSubscriptionReason.Subscribed, "anna", Tue(11));

        db.TenantMemberships.RemoveRange(db.TenantMemberships);
        await db.SaveChangesAsync();

        await followUp.RunAsync(ours, DateTime.UtcNow);

        (await work.GetFollowersAsync(ours, t.Id)).Should().BeEmpty();
    }

    [Fact]
    public void A_digest_says_what_happened_oldest_first_and_marks_internal_entries()
    {
        Ticket t = new() { Number = 42, Title = "VPN", Status = TicketStatus.InProgress };

        string body = TicketFollowUp.Digest(t,
        [
            new TicketEvent { At = Tue(10), Actor = "nils", Kind = TicketEventKind.Note, Detail = "Looking.", CustomerVisible = false },
            new TicketEvent { At = Tue(11), Actor = "Karin", Kind = TicketEventKind.Note, Detail = "Thanks.", CustomerVisible = true },
        ]);

        body.Should().Contain("#42").And.Contain("Note (internal)");
        body.IndexOf("Looking.", StringComparison.Ordinal).Should().BeLessThan(body.IndexOf("Thanks.", StringComparison.Ordinal));
    }

    // ───────────────────────────── Overviews ─────────────────────────────

    [Fact]
    public async Task Overviews_split_the_queue_the_way_their_names_say()
    {
        Ticket mine = await Reported("Mine");
        Ticket nobodys = await Reported("Nobody's");
        await tickets.AssignAsync(ours, mine.Id, "anna", "anna", Tue(10, 1), Colleague);
        await work.SetReminderAsync(ours, mine.Id, Tue(11), null, "anna", Tue(10, 2));

        Dictionary<TicketOverview, List<TicketSlaStatus>> all =
            await new TicketOverviews(tickets, new TestDbContextFactory(connection))
                .AllAsync(ours, new OverviewViewer(Colleague, "anna"), Tue(12));

        all[TicketOverview.Mine].Select(s => s.Ticket.Id).Should().Equal(mine.Id);
        all[TicketOverview.Unassigned].Select(s => s.Ticket.Id).Should().Equal(nobodys.Id);
        all[TicketOverview.MyReminders].Select(s => s.Ticket.Id).Should().Equal(mine.Id);
        all[TicketOverview.Open].Should().HaveCount(2);
    }

    [Fact]
    public async Task A_missed_response_is_escalated_and_a_resolved_ticket_is_not()
    {
        Ticket late = await Reported("Late", Tue(9), priority: TicketPriority.P2);
        Ticket resolved = await Reported("Resolved", Tue(9), priority: TicketPriority.P2);
        await tickets.ResolveAsync(ours, resolved.Id, "Fixed.", "nils", Tue(9, 10));

        DateTime nextDay = Tue(9).AddDays(1);
        List<TicketSlaStatus> queue = await tickets.GetTenantQueueAsync(ours, nextDay);

        TicketOverviews.IsEscalated(queue.Single(s => s.Ticket.Id == late.Id), nextDay).Should().BeTrue();
        TicketOverviews.IsEscalated(queue.Single(s => s.Ticket.Id == resolved.Id), nextDay).Should().BeFalse();
    }

    // ───────────────────────────── Bulk ─────────────────────────────

    [Fact]
    public async Task Bulk_close_closes_the_resolved_and_says_why_it_skipped_the_rest()
    {
        Ticket done = await Reported("Done");
        Ticket open = await Reported("Open");
        await tickets.ResolveAsync(ours, done.Id, "Fixed.", "nils", Tue(11));

        List<BulkOutcome> outcomes = await bulk.ApplyAsync(
            ours, [done.Id, open.Id], new BulkChange.Close(), "nils", Tue(12));

        outcomes.Single(o => o.TicketId == done.Id).Skipped.Should().BeNull();
        outcomes.Single(o => o.TicketId == open.Id).Skipped.Should().Be("Not resolved, so not closed.");
        Fresh(open.Id).Status.Should().Be(TicketStatus.New);
    }

    [Fact]
    public async Task Bulk_priority_needs_the_written_reason_and_records_it_on_each()
    {
        Ticket a = await Reported("A", priority: TicketPriority.P3);
        Ticket b = await Reported("B", priority: TicketPriority.P3);

        Func<Task> noReason = () => bulk.ApplyAsync(
            ours, [a.Id, b.Id], new BulkChange.Prioritise(TicketPriority.P2, " "), "nils", Tue(11));
        await noReason.Should().ThrowAsync<ArgumentException>();

        await bulk.ApplyAsync(ours, [a.Id, b.Id],
            new BulkChange.Prioritise(TicketPriority.P2, "Same outage affects both."), "nils", Tue(11));

        Fresh(a.Id).PriorityReason.Should().Be("Same outage affects both.");
        Fresh(b.Id).Priority.Should().Be(TicketPriority.P2);
    }

    [Fact]
    public async Task Bulk_changes_only_reach_this_tenants_tickets()
    {
        Ticket theirTicket = await tickets.CreateAsync(
            theirs, theirCustomer, theirApp, "Deras", "", TicketChannel.Email, null, Tue(10));

        List<BulkOutcome> outcomes = await bulk.ApplyAsync(
            ours, [theirTicket.Id], new BulkChange.AddTag("ours-now"), "nils", Tue(11));

        outcomes.Single().Skipped.Should().Be("No such ticket here.");
        db.TicketTags.AsNoTracking().Should().BeEmpty();
    }
}
