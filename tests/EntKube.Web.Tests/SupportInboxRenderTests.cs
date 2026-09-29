using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The support inbox as it is actually rendered, rather than the services underneath it.
///
/// <para><b>Why this class exists.</b> Every other test here calls a service directly, and
/// that left a whole category invisible: the actor's name came from a component parameter,
/// nothing passed one, and every accept, reject and resolution on this branch was recorded
/// as "unattributed" while the services — tested in isolation, handed a name — all
/// behaved perfectly. Nothing rendered a component, so nothing noticed for the length of
/// the branch.</para>
///
/// <para>These tests are therefore about the wiring and not the logic: who ends up on the
/// record, and whether the queue offers a way out of the states it can reach.</para>
/// </summary>
public class SupportInboxRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportMailService mail;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public SupportInboxRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        TicketService tickets = new(factory, contracts, SilentTicketNotifier.For(factory));
        mail = new SupportMailService(
            factory, new RuleBasedMailAnalyst(), new MailTriageRuleService(factory), tickets,
            new TimeService(factory, contracts));

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton(contracts);
        Services.AddSingleton(tickets);
        Services.AddSingleton(mail);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils"));
        Services.AddScoped<CurrentActor>();

        // Both screens now offer somewhere else to put the work — the inbox can place a
        // message with another tenant's customer, the detail can move a ticket there — and
        // both ask this who the person is allowed to reach before offering anything.
        Services.AddSingleton(new UserAccessService(factory));
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A provider that reports one signed-in person, as the circuit would.</summary>
    private sealed class SignedInAs(string name) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))));
    }

    /// <summary>The tenant's mailbox, set to answer a new report as it arrives.</summary>
    private void AnswerOnArrival()
    {
        db.SupportMailboxes.Add(new SupportMailbox
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Host = "mail.entit.se",
            Username = "support@entit.se",
            Address = "support@entit.se",
            IsEnabled = true,
            AcknowledgeOnArrival = true,
        });

        db.SaveChanges();
    }

    /// <summary>Somebody named in the agreement, so their mail is placed without a caveat.</summary>
    private async Task KnownContact()
    {
        db.ContractContacts.Add(new ContractContact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Party = ContractParty.Customer,
            Role = ContractContactRole.TechnicalContact,
            Name = "Karin",
            Email = "karin@entit.example",
        });

        await db.SaveChangesAsync();
    }

    private Task<InboundMailMessage?> Receive(string subject, string body, string from) =>
        mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = from,
            Subject = subject,
            Body = body,
            SentAt = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc),
        });

    /// <summary>
    /// The inbox is rendered exactly as the tenant tree renders it — with a TenantId and
    /// nothing else. That is the whole point: the previous wiring left the actor's name to
    /// a parameter this call site does not pass.
    /// </summary>
    private IRenderedComponent<SupportInbox> RenderInbox() =>
        Render<SupportInbox>(p => p.Add(c => c.TenantId, tenantId));

    // ---- Attribution ------------------------------------------------------------------------

    /// <summary>
    /// <b>The regression this class was written for.</b> Accepting a suggestion from the
    /// inbox, mounted the way the application mounts it, records the signed-in person —
    /// not "unattributed". §14.3 makes this a written act and §14.6 attaches money to it,
    /// so a ticket that cannot say who opened it is not worth much.
    /// </summary>
    [Fact]
    public async Task Accepting_from_the_inbox_records_the_signed_in_person()
    {
        db.ContractContacts.Add(new ContractContact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Party = ContractParty.Customer,
            Role = ContractContactRole.TechnicalContact,
            Name = "Karin",
            Email = "karin@entit.example",
        });
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte", "Ingen kommer in.", "karin@entit.example");

        IRenderedComponent<SupportInbox> inbox = RenderInbox();

        // The Accept button of the "open a ticket" proposal.
        IElement accept = inbox.FindAll("button")
            .First(b => b.TextContent.Trim().StartsWith("Accept", StringComparison.Ordinal));

        await inbox.InvokeAsync(() => accept.Click());

        db.ChangeTracker.Clear();

        MailSuggestion decided = db.Set<MailSuggestion>()
            .First(s => s.MessageId == message!.Id && s.Kind == MailSuggestionKind.OpenTicket);

        decided.DecidedBy.Should().Be("nils");
        decided.DecidedBy.Should().NotBe(CurrentActor.Unattributed);
    }

    // ---- The way out of a dead end ------------------------------------------------------------

    // ---- What the mailbox did before anyone looked --------------------------------------------

    /// <summary>
    /// A message the mailbox answered by itself has to say so on the card. The operator
    /// picking the thread up is otherwise reading an ordinary queue entry with no sign that
    /// the customer has already been written to — and the one thing on this screen they
    /// could send twice is a receipt.
    /// </summary>
    [Fact]
    public async Task A_message_answered_on_arrival_says_so_and_shows_the_number()
    {
        AnswerOnArrival();
        await KnownContact();

        await Receive("Journalportalen svarar inte", "Ingen kommer in.", "karin@entit.example");

        // Handled messages are out of the default view, which is itself the point — this is
        // the queue with "show handled" on.
        IRenderedComponent<SupportInbox> inbox = RenderInbox();
        await inbox.InvokeAsync(() => inbox.Find("#handled").Change(true));

        db.ChangeTracker.Clear();
        Ticket ticket = db.Tickets.Single();

        inbox.Markup.Should().Contain("Answered as it arrived");
        inbox.Markup.Should().Contain($"[#{ticket.Number}]");

        // And that the priority is still nobody's decision.
        inbox.Markup.Should().Contain("unconfirmed");
    }

    /// <summary>
    /// The drafted acknowledgement was written before the mailbox sent one of its own.
    /// Offering it as something to send would acknowledge the same person twice, with two
    /// different wordings.
    /// </summary>
    [Fact]
    public async Task The_draft_receipt_is_not_offered_once_one_has_been_sent()
    {
        AnswerOnArrival();
        await KnownContact();

        await Receive("Fel", "Det fungerar inte.", "karin@entit.example");

        IRenderedComponent<SupportInbox> inbox = RenderInbox();
        await inbox.InvokeAsync(() => inbox.Find("#handled").Change(true));

        // Fragments that survive the line breaks Razor keeps from the template.
        inbox.Markup.Should().Contain("already been acknowledged");
        inbox.Markup.Should().Contain("kept as what would have been said");
        inbox.Markup.Should().NotContain("Nothing is sent from here");
    }

    /// <summary>
    /// A report the mailbox declined to answer because it came from a program looks, on the
    /// card, exactly like one it simply has not got to. The badge is the difference.
    /// </summary>
    [Fact]
    public async Task Mail_from_a_program_is_marked_as_such_in_the_queue()
    {
        AnswerOnArrival();
        await KnownContact();

        await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "karin@entit.example",
            Subject = "Automatiskt svar: Journalportalen svarar inte",
            Body = "Jag är tillbaka på måndag.",
            SentAt = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc),
            IsMachineGenerated = true,
        });

        IRenderedComponent<SupportInbox> inbox = RenderInbox();

        inbox.Markup.Should().Contain("sent by a program");
        db.Tickets.Should().BeEmpty("a receipt to a program can be answered by the program");
    }

    /// <summary>
    /// An unrecognised sender has to be placeable from the queue. The suggestion has always
    /// said so in words; for most of this branch there was no control to do it with, which
    /// is the same failure as the missing name — a screen asserting something the wiring
    /// did not provide.
    /// </summary>
    [Fact]
    public async Task An_unplaced_message_offers_a_way_to_say_who_it_is_from()
    {
        await Receive("Fel", "Beskrivning.", "stranger@elsewhere.example");

        IRenderedComponent<SupportInbox> inbox = RenderInbox();

        inbox.Markup.Should().Contain("Nobody recognised this sender");
        inbox.FindAll("select").Should().NotBeEmpty("there has to be a customer to choose");
        inbox.Markup.Should().Contain("Entit AB", "the tenant's customers are offered");
    }

    /// <summary>
    /// A message that was placed needs no such prompt — and showing it anyway would train
    /// people to ignore it on the ones that do.
    /// </summary>
    [Fact]
    public async Task A_placed_message_is_not_asked_about()
    {
        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Domain = "entit.example",
        });
        await db.SaveChangesAsync();

        await Receive("Fel", "Beskrivning.", "anyone@entit.example");

        RenderInbox().Markup.Should().NotContain("Nobody recognised this sender");
    }

    /// <summary>
    /// Placing a message by hand from the queue attributes that to the signed-in person
    /// too, and analyses it again — the assignment path had its own way of reading the
    /// user before, which is how the duplication that exposed all of this was noticed.
    /// </summary>
    [Fact]
    public async Task Placing_a_message_by_hand_attributes_it_and_reads_it_again()
    {
        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte", "Ingen kommer in.", "stranger@elsewhere.example");

        message!.Suggestions.Should().NotContain(s => s.AppId == appId);

        await mail.AssignCustomerAsync(message.Id, customerId, "nils");

        db.ChangeTracker.Clear();

        db.Set<MailSuggestion>().Where(s => s.MessageId == message.Id)
            .Should().Contain(s => s.AppId == appId);

        RenderInbox().Markup.Should().NotContain("Nobody recognised this sender");
    }

    // ---- The empty case ---------------------------------------------------------------------

    [Fact]
    public void An_empty_queue_says_so_rather_than_rendering_nothing() =>
        RenderInbox().Markup.Should().Contain("Nothing waiting");
}
