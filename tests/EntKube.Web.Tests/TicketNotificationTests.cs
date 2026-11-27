using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 4 (docs/helpdesk.md): the bell, out of office, presence and the calendar feed — who
/// hears what, exactly once, and nothing across a tenant boundary.
/// </summary>
public class TicketNotificationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketWorkService work;
    private readonly SupportDutyService duty;
    private readonly OutOfOfficeService away;
    private readonly TicketNotificationService bell;
    private readonly RecordingMailer mailer = new();

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    private const string Anna = "user-anna", Nils = "user-nils", Stranger = "user-stranger";

    public TicketNotificationTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });

        Guid role = Guid.NewGuid(), theirRole = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = role, TenantId = ours, Name = "Admin", PermissionsJson = TenantRoleService.EncodePermissions(new Dictionary<TenantFeature, AccessLevel> { [TenantFeature.SupportTickets] = AccessLevel.Edit }) });
        db.TenantRoles.Add(new TenantRole { Id = theirRole, TenantId = theirs, Name = "Admin", PermissionsJson = TenantRoleService.EncodePermissions(new Dictionary<TenantFeature, AccessLevel> { [TenantFeature.SupportTickets] = AccessLevel.Edit }) });
        foreach ((string id, string name) in new[] { (Anna, "anna"), (Nils, "nils"), (Stranger, "mallory") })
        {
            db.Users.Add(new ApplicationUser { Id = id, UserName = name, Email = $"{name}@entit.example" });
        }

        db.TenantMemberships.Add(new TenantMembership { UserId = Anna, TenantId = ours, RoleId = role });
        db.TenantMemberships.Add(new TenantMembership { UserId = Nils, TenantId = ours, RoleId = role });
        db.TenantMemberships.Add(new TenantMembership { UserId = Stranger, TenantId = theirs, RoleId = theirRole });
        db.SupportDuties.Add(new SupportDuty { Id = Guid.NewGuid(), TenantId = ours, UserId = Anna, IsActive = true });
        db.SupportDuties.Add(new SupportDuty { Id = Guid.NewGuid(), TenantId = ours, UserId = Nils, IsActive = true });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        TicketNotifier notifier = SilentTicketNotifier.For(factory);
        duty = new SupportDutyService(factory);
        tickets = new TicketService(factory, new ContractService(factory), notifier);
        work = new TicketWorkService(factory, tickets, notifier, duty);
        away = new OutOfOfficeService(factory, duty);
        bell = new TicketNotificationService(factory, duty, away, mailer);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(Guid? tenant = null) => tickets.CreateAsync(
        tenant ?? ours, tenant == theirs ? theirCustomer : customer, null, "Login fails", "", TicketChannel.Email,
        TicketPriority.P3, DateTime.UtcNow.AddMinutes(-1), "Karin", "karin@kund.example");

    /// <summary>The sweep's first pass starts the cursor; this is the pass after it.</summary>
    private async Task<int> PassAsync(Guid? tenant = null) => await bell.GenerateAsync(tenant ?? ours, DateTime.UtcNow.AddSeconds(1));

    private List<UserNotification> For(string userId) =>
        [.. db.UserNotifications.AsNoTracking().Where(n => n.UserId == userId).OrderBy(n => n.CreatedAt)];

    [Fact]
    public async Task A_new_or_restored_server_does_not_announce_the_past()
    {
        await Raise();

        (await bell.GenerateAsync(ours, DateTime.UtcNow)).Should().Be(0, "the first pass only starts the cursor");
        (await PassAsync()).Should().Be(0);
        db.UserNotifications.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_unassigned_ticket_reaches_everyone_who_hears_about_unassigned_ones()
    {
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        Ticket t = await Raise();

        await PassAsync();

        For(Anna).Should().ContainSingle(n => n.Kind == NotificationKind.NewTicket && n.TicketNumber == t.Number);
        For(Nils).Should().ContainSingle(n => n.Kind == NotificationKind.NewTicket);
        For(Stranger).Should().BeEmpty("another tenant's member hears nothing of ours");
    }

    /// <summary>
    /// A member whose role cannot see tickets — a developer, say — is told nothing about them:
    /// a ticket's title is the ticket's, and the bell would otherwise hand it to every member.
    /// </summary>
    [Fact]
    public async Task A_member_whose_role_cannot_see_tickets_hears_nothing_of_them()
    {
        Guid developer = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = developer, TenantId = ours, Name = "Developer",
            PermissionsJson = TenantRoleService.EncodePermissions(new Dictionary<TenantFeature, AccessLevel> { [TenantFeature.Clusters] = AccessLevel.Manage }) });
        db.Users.Add(new ApplicationUser { Id = "user-carl", UserName = "carl", Email = "carl@entit.example" });
        db.TenantMemberships.Add(new TenantMembership { UserId = "user-carl", TenantId = ours, RoleId = developer });
        await db.SaveChangesAsync();

        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        Ticket t = await Raise();

        await PassAsync();

        For(Anna).Should().Contain(n => n.TicketNumber == t.Number, "the support members still hear about it");
        For("user-carl").Should().BeEmpty();
    }

    [Fact]
    public async Task Three_updates_are_one_line_and_nobody_is_told_about_their_own()
    {
        Ticket t = await Raise();
        await tickets.AssignAsync(ours, t.Id, "anna", "anna", DateTime.UtcNow, Anna);

        // Nils follows the ticket, so its updates WOULD reach him — the only thing keeping these
        // off his list is that he wrote them. (Without this the test passed with that rule deleted.)
        await work.SubscribeAsync(ours, t.Id, Nils, "nils", "nils@entit.example", TicketSubscriptionReason.Subscribed, "nils", DateTime.UtcNow);

        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await PassAsync();
        db.UserNotifications.RemoveRange(db.UserNotifications);
        await db.SaveChangesAsync();

        await tickets.AddEventAsync(ours, t.Id, TicketEventKind.Note, "One", "nils", DateTime.UtcNow);
        await tickets.AddEventAsync(ours, t.Id, TicketEventKind.Note, "Two", "nils", DateTime.UtcNow);
        await tickets.AddEventAsync(ours, t.Id, TicketEventKind.Note, "Three", "nils", DateTime.UtcNow);
        await PassAsync();

        For(Anna).Should().ContainSingle().Which.Text.Should().Contain("3 updates, last by nils");
        For(Nils).Should().BeEmpty("nils wrote them");
    }

    [Fact]
    public async Task Being_assigned_a_ticket_is_its_own_notification()
    {
        Ticket t = await Raise();
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await PassAsync();
        db.UserNotifications.RemoveRange(db.UserNotifications);
        await db.SaveChangesAsync();

        await tickets.AssignAsync(ours, t.Id, "anna", "nils", DateTime.UtcNow, Anna);
        await PassAsync();

        For(Anna).Should().ContainSingle(n => n.Kind == NotificationKind.Assigned && n.Text.Contains("Assigned to anna"));
    }

    [Fact]
    public async Task Turning_a_kind_off_means_hearing_nothing_of_it()
    {
        NotificationPreference quiet = NotificationPreferences.Defaults(Anna, ours);
        quiet.NewTicket = NotifyScope.None;
        await bell.SavePreferencesAsync(quiet);

        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await Raise();
        await PassAsync();

        For(Anna).Should().BeEmpty();
        For(Nils).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Each_event_is_announced_once_however_many_passes_see_it()
    {
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await Raise();

        await PassAsync();
        await PassAsync();

        For(Anna).Should().ContainSingle();
    }

    [Fact]
    public async Task A_substitute_hears_about_the_absent_colleagues_tickets()
    {
        NotificationPreference nilsQuiet = NotificationPreferences.Defaults(Nils, ours);
        nilsQuiet.Update = NotifyScope.Mine;
        await bell.SavePreferencesAsync(nilsQuiet);

        Ticket t = await Raise();
        await tickets.AssignAsync(ours, t.Id, "anna", "anna", DateTime.UtcNow, Anna);
        DateOnly today = DateOnly.FromDateTime(BusinessCalendar.ToLocal(DateTime.UtcNow));
        await away.SetAsync(ours, Anna, today, today.AddDays(3), Nils, "Holiday");

        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await tickets.AddEventAsync(ours, t.Id, TicketEventKind.Note, "Customer called", "karin", DateTime.UtcNow);
        await PassAsync();

        For(Nils).Should().ContainSingle(n => n.TicketId == t.Id);
    }

    [Fact]
    public async Task The_rota_passes_over_somebody_who_is_away()
    {
        DateOnly today = DateOnly.FromDateTime(BusinessCalendar.ToLocal(DateTime.UtcNow));
        await away.SetAsync(ours, Anna, today, today, null, null);

        for (int i = 0; i < 3; i++)
        {
            (await duty.NextAsync(ours))!.Value.UserId.Should().Be(Nils);
        }
    }

    [Fact]
    public async Task A_substitute_must_be_another_member_of_the_tenant()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        Func<Task> stranger = () => away.SetAsync(ours, Anna, today, today, Stranger, null);
        await stranger.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> self = () => away.SetAsync(ours, Anna, today, today, Anna, null);
        await self.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> backwards = () => away.SetAsync(ours, Anna, today, today.AddDays(-1), null, null);
        await backwards.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_escalation_is_announced_once_per_deadline()
    {
        Ticket t = await tickets.CreateAsync(ours, customer, null, "Down", "", TicketChannel.Email,
            TicketPriority.P1, DateTime.UtcNow.AddDays(-2), "Karin", "karin@kund.example");
        DateTime now = DateTime.UtcNow;

        List<TicketSlaStatus> queue = await tickets.GetTenantQueueAsync(ours, now);
        (await bell.EscalationsAsync(ours, queue, now)).Should().BeGreaterThan(0);
        (await bell.EscalationsAsync(ours, queue, now)).Should().Be(0, "the claim row already exists");

        For(Anna).Should().ContainSingle(n => n.Kind == NotificationKind.Escalation && n.TicketId == t.Id);
    }

    // ───────────────────────────── By mail (phase 9) ─────────────────────────────

    private async Task MailAsync(string userId, NotifyByMail mail, Action<NotificationPreference>? adjust = null)
    {
        NotificationPreference p = NotificationPreferences.Defaults(userId, ours);
        p.Mail = mail;
        adjust?.Invoke(p);
        await bell.SavePreferencesAsync(p);
    }

    [Fact]
    public async Task Only_the_kinds_a_person_chose_are_mailed_and_the_bell_rings_regardless()
    {
        await MailAsync(Anna, NotifyByMail.NewTicket);
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        Ticket t = await Raise();

        await PassAsync();

        mailer.Sent.Should().ContainSingle().Which.Should().Match<RecordingMailer.Mail>(m =>
            m.To == "anna@entit.example" && m.Subject.Contains($"#{t.Number}") && m.TicketId == t.Id);
        For(Nils).Should().ContainSingle("Nils's bell rings; he chose no mail");
    }

    [Fact]
    public async Task A_mail_goes_once_however_many_passes_see_the_event()
    {
        await MailAsync(Anna, NotifyByMail.NewTicket);
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await Raise();

        await PassAsync();
        await PassAsync();

        mailer.Sent.Should().ContainSingle();
    }

    /// <summary>Mail follows the bell: where the scope says nothing, choosing mail adds nothing.</summary>
    [Fact]
    public async Task Mail_never_goes_where_the_bell_would_not_ring()
    {
        await MailAsync(Anna, NotifyByMail.NewTicket, p => p.NewTicket = NotifyScope.None);
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await Raise();

        await PassAsync();

        mailer.Sent.Should().BeEmpty();
    }

    /// <summary>A follower already gets the digest; an update must not reach them twice by mail.</summary>
    [Fact]
    public async Task An_update_to_a_followed_ticket_is_left_to_the_digest_but_ones_own_ticket_is_mailed()
    {
        Ticket followed = await Raise();
        Ticket mine = await Raise();
        await work.SubscribeAsync(ours, followed.Id, Anna, "anna", "anna@entit.example", TicketSubscriptionReason.Subscribed, "anna", DateTime.UtcNow);
        await tickets.AssignAsync(ours, mine.Id, "anna", "anna", DateTime.UtcNow, Anna);
        await MailAsync(Anna, NotifyByMail.Update);

        // The cursor starts after the assignment, so only the two updates below are news.
        await bell.GenerateAsync(ours, DateTime.UtcNow);
        await tickets.AddEventAsync(ours, followed.Id, TicketEventKind.Note, "On the followed one", "nils", DateTime.UtcNow);
        await tickets.AddEventAsync(ours, mine.Id, TicketEventKind.Note, "On hers", "nils", DateTime.UtcNow);
        await PassAsync();

        For(Anna).Select(n => n.TicketId).Should().BeEquivalentTo(new Guid?[] { followed.Id, mine.Id }, "the bell rings for both");
        mailer.Sent.Select(m => m.TicketId).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task An_escalation_is_mailed_once_per_deadline()
    {
        await MailAsync(Anna, NotifyByMail.Escalation);
        await tickets.CreateAsync(ours, customer, null, "Down", "", TicketChannel.Email,
            TicketPriority.P1, DateTime.UtcNow.AddDays(-2), "Karin", "karin@kund.example");
        DateTime now = DateTime.UtcNow;
        List<TicketSlaStatus> queue = await tickets.GetTenantQueueAsync(ours, now);

        await bell.EscalationsAsync(ours, queue, now);
        await bell.EscalationsAsync(ours, queue, now);

        mailer.Sent.Where(m => m.To == "anna@entit.example").Should().ContainSingle();
    }

    [Fact]
    public async Task Somebody_with_no_address_is_not_mailed_and_still_hears_the_bell()
    {
        await db.Users.Where(u => u.Id == Anna).ExecuteUpdateAsync(u => u.SetProperty(x => x.Email, (string?)null));
        await MailAsync(Anna, NotifyByMail.NewTicket);
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await Raise();

        await PassAsync();

        mailer.Sent.Should().BeEmpty();
        For(Anna).Should().ContainSingle();
    }

    [Fact]
    public async Task The_mail_choice_is_saved_with_the_rest_of_the_matrix()
    {
        await MailAsync(Anna, NotifyByMail.Assigned | NotifyByMail.Escalation);

        (await bell.GetPreferencesAsync(ours, Anna)).Mail.Should().Be(NotifyByMail.Assigned | NotifyByMail.Escalation);
        (await bell.GetPreferencesAsync(ours, Nils)).Mail.Should().Be(NotifyByMail.None, "nobody is mailed until they choose it");
    }

    /// <summary>Records what would be mailed, and to which ticket.</summary>
    private sealed class RecordingMailer : INotificationMailer
    {
        public sealed record Mail(string To, string Subject, string Body, Guid TicketId);

        public List<Mail> Sent { get; } = [];

        public Task<bool> SendAsync(Ticket ticket, string toEmail, string subject, string body, CancellationToken ct)
        {
            Sent.Add(new Mail(toEmail, subject, body, ticket.Id));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Marking_read_touches_only_ones_own()
    {
        await bell.GenerateAsync(ours, DateTime.UtcNow.AddSeconds(-5));
        await Raise();
        await PassAsync();

        Guid annas = For(Anna).Single().Id;
        await bell.MarkSeenAsync(Nils, annas);
        (await bell.UnreadCountAsync(Anna)).Should().Be(1);

        await bell.MarkSeenAsync(Anna, null);
        (await bell.UnreadCountAsync(Anna)).Should().Be(0);
        (await bell.UnreadCountAsync(Nils)).Should().Be(1);
    }

    // ───────────────────────────── Presence ─────────────────────────────

    [Fact]
    public async Task Colleagues_on_the_ticket_are_seen_until_they_go_quiet()
    {
        TicketPresenceService presence = new(new TestDbContextFactory(connection));
        Ticket t = await Raise();

        await presence.HeartbeatAsync(ours, t.Id, Anna, "anna", editing: true);
        await presence.HeartbeatAsync(theirs, t.Id, Stranger, "mallory", editing: false);

        (await presence.OthersAsync(ours, t.Id, Nils, DateTime.UtcNow)).Should().Equal(("anna", true));
        (await presence.OthersAsync(ours, t.Id, Anna, DateTime.UtcNow)).Should().BeEmpty("you are not 'also here' to yourself");
        (await presence.OthersAsync(ours, t.Id, Nils, DateTime.UtcNow + TicketPresenceService.Window + TimeSpan.FromSeconds(1)))
            .Should().BeEmpty();
    }

    // ───────────────────────────── Calendar ─────────────────────────────

    [Fact]
    public async Task The_calendar_feed_holds_my_deadlines_and_only_its_current_address_works()
    {
        CalendarFeedService calendar = new(new TestDbContextFactory(connection), tickets);
        Ticket t = await Raise();
        await tickets.AssignAsync(ours, t.Id, "anna", "anna", DateTime.UtcNow, Anna);
        await work.SetReminderAsync(ours, t.Id, DateTime.UtcNow.AddDays(1), "Call; back, later", "anna", DateTime.UtcNow);

        string first = await calendar.ResetAsync(ours, Anna);
        string ics = (await calendar.FeedAsync(first, DateTime.UtcNow))!;

        ics.Should().StartWith("BEGIN:VCALENDAR").And.Contain($"SUMMARY:Reminder: #{t.Number} Login fails")
            .And.Contain("DESCRIPTION:Call\\; back\\, later");

        string second = await calendar.ResetAsync(ours, Anna);
        (await calendar.FeedAsync(first, DateTime.UtcNow)).Should().BeNull("resetting retires the old address");
        (await calendar.FeedAsync(second, DateTime.UtcNow)).Should().NotBeNull();

        await calendar.RevokeAsync(ours, Anna);
        (await calendar.FeedAsync(second, DateTime.UtcNow)).Should().BeNull();
        db.CalendarFeeds.AsNoTracking().Should().NotContain(f => f.TokenHash == second, "only a hash is kept");
    }

    /// <summary>
    /// A calendar app presents nothing but the address, and goes on polling it long after its owner
    /// stopped working support. The feed asks, each time, whether its owner may still see tickets
    /// in that tenant — and answers a stranger's 404 when they may not.
    /// </summary>
    [Fact]
    public async Task The_calendar_feed_stops_when_its_owner_may_no_longer_see_tickets()
    {
        CalendarFeedService calendar = new(new TestDbContextFactory(connection), tickets);
        Ticket t = await Raise();
        await tickets.AssignAsync(ours, t.Id, "anna", "anna", DateTime.UtcNow, Anna);
        string token = await calendar.ResetAsync(ours, Anna);

        (await calendar.FeedAsync(token, DateTime.UtcNow, (tenant, user, _) => Task.FromResult(tenant == ours && user == Anna)))
            .Should().NotBeNull("while she may see tickets the feed is hers");
        (await calendar.FeedAsync(token, DateTime.UtcNow, (_, _, _) => Task.FromResult(false)))
            .Should().BeNull("once she may not, the address answers like one that never existed");
    }
}
