using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace EntKube.Web.Tests;

/// <summary>
/// Attachments (docs/helpdesk.md, phase 2c, decision 4): kept in the tenant's bucket, recorded
/// even when they could not be, never reachable across tenants, never served as something a
/// browser would run.
/// </summary>
public class TicketAttachmentTests : IDisposable
{
    /// <summary>
    /// An object store in memory, which can be told to be unavailable or to fail.
    ///
    /// <para>⚠️ Deliberately NOT tenant-aware on read. The first version keyed objects by tenant
    /// and refused a cross-tenant read itself — which hid the service's own tenant predicate: a
    /// mutation deleting it passed the whole suite. The real store does refuse too (it resolves
    /// the link through the asking tenant), but a guard this test is about has to be the one
    /// that fails.</para>
    /// </summary>
    private sealed class MemoryStore : IAttachmentObjectStore
    {
        public string? Unavailable { get; set; }
        public bool FailUploads { get; set; }
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Dictionary<string, Guid> WrittenBy { get; } = [];
        public Guid LinkId { get; } = Guid.NewGuid();

        public Task<string?> WhyUnavailableAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(Unavailable);

        public async Task<Guid> PutAsync(Guid tenantId, string key, Stream content, string contentType, CancellationToken ct = default)
        {
            if (FailUploads) throw new IOException("bucket unreachable");
            using MemoryStream copy = new();
            await content.CopyToAsync(copy, ct);
            Objects[key] = copy.ToArray();
            WrittenBy[key] = tenantId;
            return LinkId;
        }

        public Task<Stream?> OpenAsync(Guid tenantId, Guid storageLinkId, string key, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out byte[]? b) ? new MemoryStream(b) : null);
    }

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly MemoryStore store = new();
    private readonly TicketAttachmentService attachments;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid otherCustomer = Guid.NewGuid();

    public TicketAttachmentTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = otherCustomer, TenantId = ours, Name = "Skolbolaget" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        tickets = new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory));
        attachments = new TicketAttachmentService(factory, store);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise() => tickets.CreateAsync(
        ours, customer, null, "Login fails", "", TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow.AddHours(-1));

    private static IncomingAttachment File(string name, string type, int bytes) =>
        new(name, type, () => new MemoryStream(new byte[bytes]));

    private Guid FiledMail(Guid ticketId, bool deliveryReport = false)
    {
        InboundMailMessage m = new()
        {
            Id = Guid.NewGuid(), TenantId = ours, MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "karin@kund.example", Subject = "Screenshot", TicketId = ticketId,
            IsDeliveryReport = deliveryReport,
        };
        db.InboundMailMessages.Add(m);
        db.SaveChanges();
        return m.Id;
    }

    [Fact]
    public async Task Without_storage_a_mails_files_are_recorded_as_not_kept_and_say_why()
    {
        store.Unavailable = "No attachment storage is configured.";
        Ticket t = await Raise();
        Guid mail = FiledMail(t.Id);

        int kept = await attachments.KeepFromMailAsync(ours, mail, [File("screenshot.png", "image/png", 100)]);

        kept.Should().Be(0);
        AttachmentView row = (await attachments.ForTicketAsync(ours, t.Id)).Single();
        row.Stored.Should().BeFalse();
        row.Problem.Should().Contain("No attachment storage is configured");
        store.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_storage_an_agent_is_refused_rather_than_told_it_worked()
    {
        store.Unavailable = "No attachment storage is configured.";
        Ticket t = await Raise();

        Func<Task> add = () => attachments.AddToTicketAsync(ours, t.Id, null, [File("a.txt", "text/plain", 10)], false, "nils");

        await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No attachment storage*");
        db.TicketAttachments.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public async Task A_mails_file_is_kept_under_a_tenant_key_that_carries_nothing_the_sender_chose()
    {
        Ticket t = await Raise();
        Guid mail = FiledMail(t.Id);

        await attachments.KeepFromMailAsync(ours, mail, [File("../../etc/passwd invoice.pdf", "application/pdf", 2048)]);

        AttachmentView row = (await attachments.ForTicketAsync(ours, t.Id)).Single();
        row.Stored.Should().BeTrue();
        row.FileName.Should().Be("passwd invoice.pdf");
        row.CustomerVisible.Should().BeTrue();

        string key = store.Objects.Keys.Single();
        store.WrittenBy[key].Should().Be(ours);
        key.Should().StartWith($"support-attachments/{ours:N}/").And.NotContain("passwd").And.NotContain("invoice");
    }

    [Fact]
    public async Task A_file_over_the_limit_is_recorded_not_kept_and_the_rest_of_the_mail_is()
    {
        Ticket t = await Raise();
        Guid mail = FiledMail(t.Id);

        await attachments.KeepFromMailAsync(ours, mail,
        [
            File("huge.iso", "application/octet-stream", (int)TicketAttachmentService.MaxFileBytes + 1),
            File("small.txt", "text/plain", 10),
        ]);

        List<AttachmentView> rows = await attachments.ForTicketAsync(ours, t.Id);
        rows.Single(r => r.FileName == "huge.iso").Problem.Should().Contain("limit");
        rows.Single(r => r.FileName == "small.txt").Stored.Should().BeTrue();
    }

    [Fact]
    public async Task A_bucket_that_fails_does_not_stop_mail_intake_and_the_failure_is_recorded()
    {
        store.FailUploads = true;
        Ticket t = await Raise();
        Guid mail = FiledMail(t.Id);

        Func<Task> keep = () => attachments.KeepFromMailAsync(ours, mail, [File("a.pdf", "application/pdf", 10)]);

        await keep.Should().NotThrowAsync();
        (await attachments.ForTicketAsync(ours, t.Id)).Single().Problem.Should().Contain("upload failed");
    }

    [Fact]
    public async Task Another_tenant_cannot_open_our_attachment()
    {
        Ticket t = await Raise();
        List<AttachmentView>? added = await attachments.AddToTicketAsync(ours, t.Id, null, [File("a.txt", "text/plain", 10)], false, "nils");
        Guid id = added!.Single().Id;

        (await attachments.OpenAsync(theirs, id)).Should().BeNull();
        (await attachments.OpenAsync(ours, id)).Should().NotBeNull();
        (await attachments.AddToTicketAsync(theirs, t.Id, null, [File("b.txt", "text/plain", 10)], false, "x")).Should().BeNull();
    }

    [Fact]
    public async Task The_duplicates_mail_attachments_are_shown_on_the_ticket_it_was_merged_into()
    {
        Ticket original = await Raise();
        Ticket duplicate = await Raise();
        Guid mail = FiledMail(duplicate.Id);
        await attachments.KeepFromMailAsync(ours, mail, [File("log.txt", "text/plain", 10)]);

        TestDbContextFactory factory = new(connection);
        await new TicketWorkService(factory, tickets, SilentTicketNotifier.For(factory), new SupportDutyService(factory))
            .MergeAsync(ours, duplicate.Id, original.Number, false, null, "nils", DateTime.UtcNow);

        (await attachments.ForTicketAsync(ours, original.Id)).Should().ContainSingle(a => a.FileName == "log.txt");
    }

    [Theory]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("image/svg+xml", "application/octet-stream")]
    [InlineData("application/javascript", "application/octet-stream")]
    [InlineData("application/pdf", "application/pdf")]
    [InlineData("IMAGE/PNG; name=x.png", "image/png")]
    public void Only_types_a_browser_cannot_run_are_served_as_themselves(string stored, string served) =>
        TicketAttachmentService.ServedContentType(stored).Should().Be(served);

    [Fact]
    public void The_files_attached_to_a_mail_are_read_including_a_forwarded_mail()
    {
        MimeMessage forwarded = new() { Subject = "Original report" };
        forwarded.Body = new TextPart("plain") { Text = "hello" };

        BodyBuilder body = new() { TextBody = "See attached." };
        body.Attachments.Add("report.pdf", Encoding.UTF8.GetBytes("%PDF-1.7"), new ContentType("application", "pdf"));
        body.Attachments.Add(new MessagePart { Message = forwarded, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) });

        MimeMessage message = new() { Subject = "Two things", Body = body.ToMessageBody() };

        IReadOnlyList<IncomingAttachment> files = MailAttachments.From(message);

        files.Select(f => f.FileName).Should().Equal("report.pdf", "Original report.eml");
        new StreamReader(files[0].Open()).ReadToEnd().Should().Be("%PDF-1.7");
    }

    // ───────────────────────────── The customer portal (phase 7) ─────────────────────────────

    private async Task<Guid> MailFile(Guid ticketId, string name, bool deliveryReport = false)
    {
        Guid mail = FiledMail(ticketId, deliveryReport);
        await attachments.KeepFromMailAsync(ours, mail, [File(name, "image/png", 10)]);
        db.ChangeTracker.Clear();
        return db.TicketAttachments.Single(a => a.InboundMailMessageId == mail).Id;
    }

    [Fact]
    public async Task The_portal_shows_what_the_customer_sent_and_what_we_shared_but_not_internal_files()
    {
        Ticket t = await Raise();
        await MailFile(t.Id, "screenshot.png");
        await attachments.AddToTicketAsync(ours, t.Id, null, [File("fix.pdf", "application/pdf", 10)], customerVisible: true, "nils");
        await attachments.AddToTicketAsync(ours, t.Id, null, [File("internal-log.txt", "text/plain", 10)], customerVisible: false, "nils");

        List<AttachmentView> seen = await attachments.ForCustomerTicketAsync(ours, customer, t.Id);

        seen.Select(a => a.FileName).Should().BeEquivalentTo(["screenshot.png", "fix.pdf"]);
    }

    [Fact]
    public async Task Another_customer_neither_sees_nor_opens_a_customers_files()
    {
        Ticket t = await Raise();
        Guid shot = await MailFile(t.Id, "screenshot.png");

        (await attachments.ForCustomerTicketAsync(ours, otherCustomer, t.Id)).Should().BeEmpty();
        (await attachments.OpenForCustomerAsync(ours, otherCustomer, shot)).Should().BeNull();
        (await attachments.OpenForCustomerAsync(theirs, customer, shot)).Should().BeNull();

        (await attachments.OpenForCustomerAsync(ours, customer, shot)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_internal_file_cannot_be_opened_from_the_portal_by_its_id()
    {
        Ticket t = await Raise();
        List<AttachmentView> added = (await attachments.AddToTicketAsync(
            ours, t.Id, null, [File("internal-log.txt", "text/plain", 10)], customerVisible: false, "nils"))!;

        (await attachments.OpenForCustomerAsync(ours, customer, added.Single().Id)).Should().BeNull();
    }

    /// <summary>
    /// Agents see a merged duplicate's files on the ticket it was merged into. If that duplicate
    /// was another customer's, its files must not follow it into this customer's portal.
    /// </summary>
    [Fact]
    public async Task A_duplicate_merged_in_from_another_customer_brings_its_files_to_agents_only()
    {
        Ticket mine = await Raise();
        Ticket sameCustomerDuplicate = await Raise();
        Ticket theirDuplicate = await tickets.CreateAsync(
            ours, otherCustomer, null, "Login fails for us too", "", TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow.AddHours(-1));
        Guid ours1 = await MailFile(sameCustomerDuplicate.Id, "ours.png");
        Guid theirs1 = await MailFile(theirDuplicate.Id, "theirs.png");

        await db.Tickets.Where(x => x.Id == sameCustomerDuplicate.Id || x.Id == theirDuplicate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.MergedIntoTicketId, mine.Id));

        (await attachments.ForTicketAsync(ours, mine.Id)).Select(a => a.FileName).Should().BeEquivalentTo(["ours.png", "theirs.png"]);
        (await attachments.ForCustomerTicketAsync(ours, customer, mine.Id)).Select(a => a.FileName).Should().Equal("ours.png");
        (await attachments.OpenForCustomerAsync(ours, customer, theirs1)).Should().BeNull();
        (await attachments.OpenForCustomerAsync(ours, customer, ours1)).Should().NotBeNull();
    }

    /// <summary>A bounce is filed as an internal note; what it carries is our own mail, returned.</summary>
    [Fact]
    public async Task A_delivery_reports_files_never_reach_the_portal()
    {
        Ticket t = await Raise();
        Guid returned = await MailFile(t.Id, "original-message.eml", deliveryReport: true);

        (await attachments.ForCustomerTicketAsync(ours, customer, t.Id)).Should().BeEmpty();
        (await attachments.OpenForCustomerAsync(ours, customer, returned)).Should().BeNull();
    }

    [Fact]
    public async Task The_portal_finds_the_customers_tenant_from_the_customer()
    {
        (await attachments.TenantOfCustomerAsync(customer)).Should().Be(ours);
        (await attachments.TenantOfCustomerAsync(Guid.NewGuid())).Should().BeNull();
    }
}
