using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Support"/> module —
/// Tickets, support mail, contracts, time, knowledge and on-call.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class SupportModel
{
    internal static void Configure(ModelBuilder builder)
    {
        // Table names, stated rather than inferred.
        //
        // EF derives a table name from the DbSet property on whichever context declares it,
        // so "AlertIncident" became "AlertIncidents" only because ApplicationDbContext spells
        // that property in the plural. The per-module contexts do not declare each other's
        // sets, which means that without this block each of them maps the same entity to a
        // different table — and a query crossing a module boundary would hit a table that is
        // not there. Naming them here makes the schema a property of the model instead of a
        // property of C# property naming.
        builder.Entity<AppKnowledgeProfile>().ToTable("AppKnowledgeProfiles");
        builder.Entity<ApplicationContract>().ToTable("ApplicationContracts");
        builder.Entity<ApplicationServiceLevel>().ToTable("ApplicationServiceLevels");
        builder.Entity<ContractContact>().ToTable("ContractContacts");
        builder.Entity<CustomerSupportAddress>().ToTable("CustomerSupportAddresses");
        builder.Entity<ExternalTicketLink>().ToTable("ExternalTicketLinks");
        builder.Entity<InboundMailMessage>().ToTable("InboundMailMessages");
        builder.Entity<KnowledgeRevision>().ToTable("KnowledgeRevisions");
        builder.Entity<KnowledgeSection>().ToTable("KnowledgeSections");
        builder.Entity<MailSuggestion>().ToTable("MailSuggestions");
        builder.Entity<MailTriageRule>().ToTable("MailTriageRules");
        builder.Entity<OnCallSchedule>().ToTable("OnCallSchedules");
        builder.Entity<OnCallShift>().ToTable("OnCallShifts");
        builder.Entity<PortfolioAgreement>().ToTable("PortfolioAgreements");
        builder.Entity<SlaTarget>().ToTable("SlaTargets");
        builder.Entity<Subconsultant>().ToTable("Subconsultants");
        builder.Entity<SupportDuty>().ToTable("SupportDuties");
        builder.Entity<SupportMailbox>().ToTable("SupportMailboxes");
        builder.Entity<Ticket>().ToTable("Tickets");
        builder.Entity<TicketAffectedApp>().ToTable("TicketAffectedApps");
        builder.Entity<TicketBridgeConnection>().ToTable("TicketBridgeConnections");
        builder.Entity<TicketEvent>().ToTable("TicketEvents");
        builder.Entity<TicketPause>().ToTable("TicketPauses");
        builder.Entity<TimeEntry>().ToTable("TimeEntries");
        builder.Entity<WorkAuthorisation>().ToTable("WorkAuthorisations");

        // ---- The management agreement: Annex A, B and C as data -------------------------

        builder.Entity<ApplicationContract>(entity =>
        {
            entity.HasKey(c => c.Id);

            // One set of terms per application. Annex A is signed per application, and two
            // live contracts for the same one would make "what was agreed" unanswerable.
            entity.HasIndex(c => c.AppId).IsUnique();
            entity.HasIndex(c => c.TenantId);

            // Instances are found by their parent application often enough to index: the
            // reduced fee from the twenty-first instance and §14.3's collapsing of one
            // incident across many instances both count them.
            entity.HasIndex(c => c.ParentAppId);

            entity.Property(c => c.MonthlyWorkCapHours).HasPrecision(18, 2);
            entity.Property(c => c.OnboardingFee).HasPrecision(18, 2);

            entity.HasOne(c => c.Tenant)
                  .WithMany()
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            // A plain FK plus the unique index above, deliberately not a required one-to-one.
            // EF treats a second dependent on a required 1:1 as replacing the first and
            // silently marks the old row deleted; for a signed Annex A that is the wrong
            // failure mode. As a normal reference, a duplicate hits the unique index and
            // fails loudly instead.
            entity.HasOne(c => c.App)
                  .WithMany()
                  .HasForeignKey(c => c.AppId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not cascade: removing a parent application from EntKube must not
            // silently delete the terms of every instance that was built on it. §10.2.1
            // makes the instances depend on it, so the dependency has to be dealt with
            // deliberately rather than by a delete rule.
            entity.HasOne(c => c.ParentApp)
                  .WithMany()
                  .HasForeignKey(c => c.ParentAppId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ApplicationServiceLevel>(entity =>
        {
            entity.HasKey(l => l.Id);

            // Resolving the level in force on a date reads exactly this shape.
            entity.HasIndex(l => new { l.ApplicationContractId, l.EffectiveFrom });

            entity.HasOne(l => l.Contract)
                  .WithMany(c => c.ServiceLevels)
                  .HasForeignKey(l => l.ApplicationContractId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortfolioAgreement>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.CustomerId, a.EffectiveFrom });

            entity.Property(a => a.HourBankHoursPerMonth).HasPrecision(18, 2);

            entity.HasOne(a => a.Tenant)
                  .WithMany()
                  .HasForeignKey(a => a.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Customer)
                  .WithMany()
                  .HasForeignKey(a => a.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ContractContact>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.CustomerId, c.Party, c.Role });
            entity.HasIndex(c => c.AppId);

            entity.HasOne(c => c.Tenant)
                  .WithMany()
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Customer)
                  .WithMany()
                  .HasForeignKey(c => c.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);

            // An application-specific contact outlives the application only as a row to be
            // tidied up; cascading would be fine, but Restrict keeps the delete explicit and
            // matches how the contract itself treats a parent application.
            entity.HasOne(c => c.App)
                  .WithMany()
                  .HasForeignKey(c => c.AppId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- Ticketing (§14) ------------------------------------------------------------

        builder.Entity<Ticket>(entity =>
        {
            entity.HasKey(t => t.Id);

            // The human reference people quote, and the only identifying part of the
            // Message-Id we stamp on our own mail — so it is what a reply threads on and
            // what the receipt tells the customer to keep in the subject.
            //
            // The index is per tenant; the numbers are handed out across the installation
            // (TicketService.NextNumberAsync). Those are not in conflict — the wider
            // allocation satisfies the narrower index — and the reason for it is that a
            // ticket can be moved to another tenant. A number that meant a different ticket
            // on the other side could not be kept, and renumbering would strand every reply
            // already quoting the old one. Tenants therefore see gaps in their sequence,
            // which costs nothing.
            entity.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();

            entity.Property(t => t.AssigneeUserId).HasMaxLength(450);

            // The round-robin reads this: the most recently assigned ticket in a tenant is
            // where the rotation carries on from.
            entity.HasIndex(t => new { t.TenantId, t.AssigneeUserId });

            // The queue: open tickets for a customer, worst first. Also the shape the
            // monthly report reads for a period.
            entity.HasIndex(t => new { t.CustomerId, t.Status, t.Priority });
            entity.HasIndex(t => new { t.TenantId, t.ReportedAt });
            entity.HasIndex(t => t.AppId);

            entity.HasOne(t => t.Tenant)
                  .WithMany()
                  .HasForeignKey(t => t.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(t => t.Customer)
                  .WithMany()
                  .HasForeignKey(t => t.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Restrict: removing an application from EntKube must not delete the history of
            // what went wrong with it. §14.6 makes these timestamps the record between the
            // parties, and a record that disappears with its subject is not a record.
            entity.HasOne(t => t.App)
                  .WithMany()
                  .HasForeignKey(t => t.AppId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TicketEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TicketId, e.At });

            entity.HasOne(e => e.Ticket)
                  .WithMany(t => t.Events)
                  .HasForeignKey(e => e.TicketId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TicketPause>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.TicketId, p.StartedAt });

            entity.HasOne(p => p.Ticket)
                  .WithMany(t => t.Pauses)
                  .HasForeignKey(p => p.TicketId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TicketAffectedApp>(entity =>
        {
            entity.HasKey(a => new { a.TicketId, a.AppId });

            entity.HasOne(a => a.Ticket)
                  .WithMany(t => t.AffectedApps)
                  .HasForeignKey(a => a.TicketId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.App)
                  .WithMany()
                  .HasForeignKey(a => a.AppId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- Worked time (§11, §13) -------------------------------------------------------

        builder.Entity<TimeEntry>(entity =>
        {
            entity.HasKey(e => e.Id);

            // The shape both reports read: a customer's entries over a period.
            entity.HasIndex(e => new { e.CustomerId, e.StartedAt });
            entity.HasIndex(e => e.TicketId);
            entity.HasIndex(e => e.AppId);

            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Customer)
                  .WithMany()
                  .HasForeignKey(e => e.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Restrict on both: removing an application or a ticket must not quietly delete
            // the record of hours that were worked and may already have been invoiced.
            entity.HasOne(e => e.App)
                  .WithMany()
                  .HasForeignKey(e => e.AppId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Ticket)
                  .WithMany()
                  .HasForeignKey(e => e.TicketId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Authorisation)
                  .WithMany(a => a.Entries)
                  .HasForeignKey(e => e.AuthorisationId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<WorkAuthorisation>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.CustomerId, a.Month });

            entity.Property(a => a.Hours).HasPrecision(18, 2);

            entity.HasOne(a => a.Tenant)
                  .WithMany()
                  .HasForeignKey(a => a.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Customer)
                  .WithMany()
                  .HasForeignKey(a => a.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Ticket)
                  .WithMany()
                  .HasForeignKey(a => a.TicketId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.App)
                  .WithMany()
                  .HasForeignKey(a => a.AppId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- Knowledge: what we know about an application (§10.2) ---------------------------

        builder.Entity<KnowledgeSection>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.AppId, s.Kind });

            entity.HasOne(s => s.Tenant)
                  .WithMany()
                  .HasForeignKey(s => s.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade here, unlike tickets and time: this is documentation about the
            // application, not a record of what was done to it or billed for it. When the
            // application goes, §19 has already handed the runbook over.
            entity.HasOne(s => s.App)
                  .WithMany()
                  .HasForeignKey(s => s.AppId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<KnowledgeRevision>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.SectionId, r.SavedAt });

            entity.HasOne(r => r.Section)
                  .WithMany(s => s.Revisions)
                  .HasForeignKey(r => r.SectionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AppKnowledgeProfile>(entity =>
        {
            entity.HasKey(p => p.Id);

            // One classification per application; two would make "does this hold patient
            // data" unanswerable, which §24 needs answered.
            entity.HasIndex(p => p.AppId).IsUnique();

            entity.HasOne(p => p.Tenant)
                  .WithMany()
                  .HasForeignKey(p => p.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.App)
                  .WithMany()
                  .HasForeignKey(p => p.AppId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- The support mailbox (§14.3) ---------------------------------------------------

        builder.Entity<InboundMailMessage>(entity =>
        {
            entity.HasKey(m => m.Id);

            // A mailbox poll hands over the same message repeatedly; this is what makes
            // ingestion idempotent rather than duplicating tickets.
            entity.HasIndex(m => new { m.TenantId, m.MessageId }).IsUnique();
            entity.Property(m => m.ToAddresses).HasMaxLength(2000);
            entity.HasIndex(m => new { m.TenantId, m.State });

            entity.HasOne(m => m.Tenant)
                  .WithMany()
                  .HasForeignKey(m => m.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Customer)
                  .WithMany()
                  .HasForeignKey(m => m.CustomerId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Restrict: the message is the evidence of when a request actually arrived,
            // which §14.6 makes the record between the parties.
            entity.HasOne(m => m.Ticket)
                  .WithMany()
                  .HasForeignKey(m => m.TicketId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MailSuggestion>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.MessageId, s.Kind });

            entity.HasOne(s => s.Message)
                  .WithMany(m => m.Suggestions)
                  .HasForeignKey(s => s.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MailTriageRule>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.TenantId, r.Signal, r.SortOrder });

            // One phrase per signal per tenant. Two rows for the same words would make the
            // proposal depend on which one the ordering happened to reach first.
            entity.HasIndex(r => new { r.TenantId, r.Signal, r.Phrase }).IsUnique();

            entity.Property(r => r.Phrase).HasMaxLength(200);
            entity.Property(r => r.Criterion).HasMaxLength(300);

            entity.HasOne(r => r.Tenant)
                  .WithMany()
                  .HasForeignKey(r => r.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CustomerSupportAddress>(entity =>
        {
            entity.HasKey(a => a.Id);

            // One customer per address. Two claiming the same one would make routing depend
            // on which row came back first, and the same message would land in different
            // queues on different days.
            entity.HasIndex(a => new { a.TenantId, a.Address }).IsUnique();
            entity.HasIndex(a => a.CustomerId);

            entity.Property(a => a.Address).HasMaxLength(320);
            entity.Property(a => a.Notes).HasMaxLength(500);
            entity.Property(a => a.AddedBy).HasMaxLength(256);

            entity.HasOne(a => a.Tenant)
                  .WithMany()
                  .HasForeignKey(a => a.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Customer)
                  .WithMany()
                  .HasForeignKey(a => a.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- The inbound ticket bridge -----------------------------------------------------

        builder.Entity<TicketBridgeConnection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.CustomerId });

            entity.Property(c => c.Instance).HasMaxLength(256);
            entity.Property(c => c.PriorityMap).HasMaxLength(2000);
            entity.Property(c => c.LastError).HasMaxLength(2000);

            entity.HasOne(c => c.Tenant)
                  .WithMany()
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Customer)
                  .WithMany()
                  .HasForeignKey(c => c.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Restrict: an application still receiving tickets from a customer's own system
            // is not one to delete out from under the connection quietly.
            entity.HasOne(c => c.DefaultApp)
                  .WithMany()
                  .HasForeignKey(c => c.DefaultAppId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ExternalTicketLink>(entity =>
        {
            entity.HasKey(l => l.Id);

            // What actually stops a resent incident becoming a second ticket. A sending
            // system retries, and delivers the same thing again on every field change; a
            // check in code would be a race between two deliveries, and this is not.
            entity.HasIndex(l => new { l.TenantId, l.System, l.Instance, l.ExternalId })
                  .IsUnique();

            entity.HasIndex(l => l.TicketId);
            entity.HasIndex(l => new { l.TenantId, l.ExternalKey });

            entity.Property(l => l.Instance).HasMaxLength(256);
            entity.Property(l => l.ExternalId).HasMaxLength(256);
            entity.Property(l => l.ExternalKey).HasMaxLength(256);
            entity.Property(l => l.Url).HasMaxLength(1000);

            entity.HasOne(l => l.Tenant)
                  .WithMany()
                  .HasForeignKey(l => l.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade from the ticket: the link says what this ticket mirrors, and means
            // nothing without it.
            entity.HasOne(l => l.Ticket)
                  .WithMany()
                  .HasForeignKey(l => l.TicketId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Restrict from the connection: deleting a connection must not quietly erase
            // the record of where a customer's tickets came from. Disable it instead.
            entity.HasOne(l => l.Connection)
                  .WithMany()
                  .HasForeignKey(l => l.ConnectionId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SupportMailbox>(entity =>
        {
            entity.HasKey(m => m.Id);

            // One support mailbox per tenant. Two would race each other for the same
            // messages and produce the duplicate tickets the message-id check exists to
            // prevent — only it would not, because both would win their own insert.
            entity.HasIndex(m => m.TenantId).IsUnique();

            entity.Property(m => m.Host).HasMaxLength(256);
            entity.Property(m => m.Username).HasMaxLength(256);
            entity.Property(m => m.OAuthClientId).HasMaxLength(256);
            entity.Property(m => m.OAuthTokenEndpoint).HasMaxLength(512);
            entity.Property(m => m.OAuthScopes).HasMaxLength(256);
            entity.Property(m => m.Address).HasMaxLength(256);
            entity.Property(m => m.Folder).HasMaxLength(256);
            entity.Property(m => m.MoveToFolder).HasMaxLength(256);
            entity.Property(m => m.LastError).HasMaxLength(2000);

            entity.HasOne(m => m.Tenant)
                  .WithMany()
                  .HasForeignKey(m => m.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SlaTarget>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.TenantId, s.CustomerId, s.AppId }).IsUnique().HasFilter(null);

            entity.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Customer)
                .WithMany()
                .HasForeignKey(s => s.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.App)
                .WithMany()
                .HasForeignKey(s => s.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OnCallSchedule — named rotation schedule owned by a tenant.

        builder.Entity<OnCallSchedule>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Description).HasMaxLength(500);

            entity.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(s => s.Shifts)
                .WithOne(sh => sh.Schedule)
                .HasForeignKey(sh => sh.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // OnCallShift — a single time-boxed assignment within a schedule.

        builder.Entity<OnCallShift>(entity =>
        {
            entity.HasKey(sh => sh.Id);
            entity.HasIndex(sh => sh.ScheduleId);
            entity.HasIndex(sh => sh.StartsAt);
            entity.Property(sh => sh.AssigneeName).HasMaxLength(256).IsRequired();
            entity.Property(sh => sh.AssigneeEmail).HasMaxLength(256);
            entity.Property(sh => sh.Notes).HasMaxLength(1000);
            entity.Property(sh => sh.AssigneePhone).HasMaxLength(64);
            entity.Property(sh => sh.AssigneeTeamsHandle).HasMaxLength(256);
            entity.Property(sh => sh.HandoverNotes).HasMaxLength(2000);

            // Restrict: removing somebody from the §18 register must not erase the record of
            // the shifts they covered, which is part of what that register is for.
            entity.HasOne(sh => sh.Subconsultant)
                  .WithMany()
                  .HasForeignKey(sh => sh.SubconsultantId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Subconsultant>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.IsActive });
            entity.HasIndex(c => c.CustomerId);

            entity.HasOne(c => c.Tenant)
                  .WithMany()
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Customer)
                  .WithMany()
                  .HasForeignKey(c => c.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<InboundMailMessage>(entity =>
        {
            entity.Property(m => m.FailedRecipient).HasMaxLength(320);
        });

        builder.Entity<SupportDuty>(entity =>
        {
            // One row per person per tenant: the roster is a set of people, and two rows
            // for one of them would let the rota disagree with itself about whether they
            // are taking work.
            entity.HasIndex(d => new { d.TenantId, d.UserId }).IsUnique();

            entity.Property(d => d.UserId).HasMaxLength(450).IsRequired();
            entity.Property(d => d.Note).HasMaxLength(500);
            entity.Property(d => d.UpdatedBy).HasMaxLength(256);

            entity.HasOne(d => d.Tenant)
                .WithMany()
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
