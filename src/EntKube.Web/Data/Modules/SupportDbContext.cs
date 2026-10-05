using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Support module's view of the database — tickets, support mail, contracts, time, knowledge and on-call.
///
/// <para>Exposes only the 25 tables Support owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class SupportDbContext(DbContextOptions<SupportDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<TicketPause> TicketPauses => Set<TicketPause>();
    public DbSet<TicketAffectedApp> TicketAffectedApps => Set<TicketAffectedApp>();
    public DbSet<TicketBridgeConnection> TicketBridgeConnections => Set<TicketBridgeConnection>();
    public DbSet<ExternalTicketLink> ExternalTicketLinks => Set<ExternalTicketLink>();
    public DbSet<SupportMailbox> SupportMailboxes => Set<SupportMailbox>();
    public DbSet<SupportDuty> SupportDuties => Set<SupportDuty>();
    public DbSet<CustomerSupportAddress> CustomerSupportAddresses => Set<CustomerSupportAddress>();
    public DbSet<InboundMailMessage> InboundMailMessages => Set<InboundMailMessage>();
    public DbSet<MailTriageRule> MailTriageRules => Set<MailTriageRule>();
    public DbSet<MailSuggestion> MailSuggestions => Set<MailSuggestion>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<WorkAuthorisation> WorkAuthorisations => Set<WorkAuthorisation>();
    public DbSet<ApplicationContract> ApplicationContracts => Set<ApplicationContract>();
    public DbSet<ApplicationServiceLevel> ApplicationServiceLevels => Set<ApplicationServiceLevel>();
    public DbSet<ContractContact> ContractContacts => Set<ContractContact>();
    public DbSet<PortfolioAgreement> PortfolioAgreements => Set<PortfolioAgreement>();
    public DbSet<SlaTarget> SlaTargets => Set<SlaTarget>();
    public DbSet<Subconsultant> Subconsultants => Set<Subconsultant>();
    public DbSet<OnCallSchedule> OnCallSchedules => Set<OnCallSchedule>();
    public DbSet<OnCallShift> OnCallShifts => Set<OnCallShift>();
    public DbSet<KnowledgeSection> KnowledgeSections => Set<KnowledgeSection>();
    public DbSet<KnowledgeRevision> KnowledgeRevisions => Set<KnowledgeRevision>();
    public DbSet<AppKnowledgeProfile> AppKnowledgeProfiles => Set<AppKnowledgeProfile>();

    // ---- Hubs owned by other modules -------------------------------------------------
    // The measurement in docs/decomposition.md §4.0 found 70 of 164 cross-module foreign
    // keys point at Tenant or Customer, and another 40 at App or KubernetesCluster. Making
    // those four readable here is what stops the boundary being merely annoying. They are
    // IQueryable rather than DbSet on purpose: readable, not writable, and not trackable.

    /// <summary>Tenant, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Tenant> Tenants => Set<Tenant>().AsNoTracking();

    /// <summary>Customer, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Customer> Customers => Set<Customer>().AsNoTracking();

    /// <summary>App, read-only: this module may look one up, never write it.</summary>
    public IQueryable<App> Apps => Set<App>().AsNoTracking();

    /// <summary>KubernetesCluster, read-only: this module may look one up, never write it.</summary>
    public IQueryable<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>().AsNoTracking();
}
