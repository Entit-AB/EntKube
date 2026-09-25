using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Turning an alert into a ticket, which §14.3 requires us to do for anything our own
/// monitoring finds — registered by us, timed at the alarm.
///
/// <para>The judgements this defends: only applications under an Annex A get tickets, the
/// priority the machine picks is a proposal and not a confirmation, and an alert that stops
/// firing does not resolve anything, because §14.4 resolves a ticket when the service is
/// restored or the customer accepts a workaround.</para>
/// </summary>
public class AlertTicketBridgeTests : IDisposable
{
    private static readonly DateTime Alarm = new(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly AlertTicketBridge bridge;
    private readonly TicketService tickets;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid managedAppId = Guid.NewGuid();
    private readonly Guid unmanagedAppId = Guid.NewGuid();

    public AlertTicketBridgeTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-1", ApiServerUrl = "https://k8s.example.com",
        });

        db.Apps.AddRange(
            new App { Id = managedAppId, CustomerId = customerId, Name = "Records" },
            new App { Id = unmanagedAppId, CustomerId = customerId, Name = "Unknown" });

        db.AppDeployments.AddRange(
            Deployment(managedAppId, "journal-prod"),
            Deployment(unmanagedAppId, "okand-prod"));

        // Only the first application is under an Annex A.
        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AppId = managedAppId,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(), ApplicationContractId = contract.Id,
            Level = ManagementLevel.Complex, SupportWindow = SupportWindow.S4,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Reason = "On-boarding",
        });
        db.ApplicationContracts.Add(contract);

        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        tickets = new TicketService(factory, contracts, SilentTicketNotifier.For(factory));
        bridge = new AlertTicketBridge(
            factory, contracts, tickets, NullLogger<AlertTicketBridge>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private AppDeployment Deployment(Guid appId, string ns) => new()
    {
        Id = Guid.NewGuid(),
        AppId = appId,
        Name = ns,
        EnvironmentId = environmentId,
        ClusterId = clusterId,
        Namespace = ns,
    };

    private AlertIncident Alert(string ns, string severity = "critical", string name = "TargetDown")
    {
        AlertIncident incident = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Fingerprint = Guid.NewGuid().ToString("N"),
            AlertName = name,
            Severity = severity,
            Summary = "The service is not responding",
            Description = "Probe has failed for five minutes.",
            LabelsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["namespace"] = ns }),
            StartsAt = Alarm,
        };

        db.AlertIncidents.Add(incident);
        db.SaveChanges();
        return incident;
    }

    // ---- What becomes a ticket -------------------------------------------------------------

    /// <summary>
    /// §14.3: registered by us, with the time of the alarm as the reporting time — not the
    /// time the sweep happened to run.
    /// </summary>
    [Fact]
    public async Task An_alert_on_a_managed_application_opens_a_ticket_timed_at_the_alarm()
    {
        AlertIncident incident = Alert("journal-prod");

        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        opened.Should().ContainSingle();
        opened[0].AppId.Should().Be(managedAppId);
        opened[0].CustomerId.Should().Be(customerId);
        opened[0].Channel.Should().Be(TicketChannel.Monitoring);
        opened[0].ReportedAt.Should().Be(Alarm);
        opened[0].AlertIncidentId.Should().Be(incident.Id);
        opened[0].SupportWindow.Should().Be(SupportWindow.S4);
    }

    /// <summary>
    /// An application with no Annex A has no agreed response time, no price and nobody who
    /// agreed to be woken for it. The alert stays an alert.
    /// </summary>
    [Fact]
    public async Task An_alert_on_an_application_with_no_contract_opens_nothing()
    {
        AlertIncident incident = Alert("okand-prod");

        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        opened.Should().BeEmpty();
    }

    [Fact]
    public async Task An_alert_that_matches_no_deployment_opens_nothing()
    {
        AlertIncident incident = Alert("kube-system");

        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        opened.Should().BeEmpty();
    }

    [Fact]
    public async Task An_alert_with_no_namespace_label_opens_nothing()
    {
        AlertIncident incident = Alert("journal-prod");
        incident.LabelsJson = "{}";
        await db.SaveChangesAsync();

        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        opened.Should().BeEmpty();
    }

    /// <summary>
    /// The alert sweep runs every couple of minutes and hands over the same firing incident
    /// each time. One alert is one ticket.
    /// </summary>
    [Fact]
    public async Task The_same_alert_does_not_open_a_second_ticket()
    {
        AlertIncident incident = Alert("journal-prod");

        await bridge.OpenForIncidentsAsync(tenantId, [incident]);
        List<Ticket> again = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        again.Should().BeEmpty();
        db.Tickets.Count().Should().Be(1);
    }

    /// <summary>One bad alert must not cost the rest of the batch.</summary>
    [Fact]
    public async Task A_batch_survives_one_unmappable_alert()
    {
        AlertIncident broken = Alert("journal-prod");
        broken.LabelsJson = "{ not json";
        await db.SaveChangesAsync();

        AlertIncident good = Alert("journal-prod", name: "HighErrorRate");

        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [broken, good]);

        opened.Should().ContainSingle();
        opened[0].AlertIncidentId.Should().Be(good.Id);
    }

    // ---- Priority is proposed, not decided -----------------------------------------------

    /// <summary>
    /// §14.3 makes confirming a priority a written act with reasons. A severity label does
    /// not know whether a workaround exists or whether patient safety is at stake, so what
    /// the bridge picks is a proposal awaiting the first assessment.
    /// </summary>
    [Fact]
    public async Task The_priority_is_left_unconfirmed()
    {
        AlertIncident incident = Alert("journal-prod");

        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        opened[0].Priority.Should().Be(TicketPriority.P1);
        opened[0].ProposedPriority.Should().Be(TicketPriority.P1);
        opened[0].PriorityConfirmedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("critical", TicketPriority.P1)]
    [InlineData("CRITICAL", TicketPriority.P1)]
    [InlineData("page", TicketPriority.P1)]
    [InlineData("warning", TicketPriority.P3)]
    [InlineData("info", TicketPriority.P4)]
    [InlineData("", TicketPriority.P4)]
    public void Severity_maps_to_a_proposed_priority(string severity, TicketPriority expected) =>
        AlertTicketBridge.ProposedPriority(severity).Should().Be(expected);

    // ---- Resolution ------------------------------------------------------------------------

    /// <summary>
    /// §14.4 resolves a P1 or P2 when the service is restored or the customer accepts a
    /// workaround. An alert falling silent is evidence of neither — a probe can recover
    /// while the underlying fault is still there — so the ticket stays open with a note.
    /// </summary>
    [Fact]
    public async Task An_alert_that_stops_firing_notes_the_ticket_but_does_not_resolve_it()
    {
        AlertIncident incident = Alert("journal-prod");
        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);

        await bridge.NoteIncidentResolvedAsync([incident], Alarm.AddHours(1));

        Ticket? ticket = await tickets.GetAsync(opened[0].Id);

        ticket!.Status.Should().NotBe(TicketStatus.Resolved);
        ticket.ResolvedAt.Should().BeNull();
        ticket.Events.Should().Contain(e =>
            e.Kind == TicketEventKind.Note && e.Detail.Contains("stopped firing"));
    }

    [Fact]
    public async Task A_closed_ticket_is_not_annotated_again()
    {
        AlertIncident incident = Alert("journal-prod");
        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, [incident]);
        await tickets.CloseAsync(opened[0].Id, "nils", Alarm.AddMinutes(30));

        await bridge.NoteIncidentResolvedAsync([incident], Alarm.AddHours(1));

        Ticket? ticket = await tickets.GetAsync(opened[0].Id);

        ticket!.Events.Should().NotContain(e => e.Detail.Contains("stopped firing"));
    }

    [Fact]
    public async Task Nothing_happens_for_an_empty_batch()
    {
        List<Ticket> opened = await bridge.OpenForIncidentsAsync(tenantId, []);

        opened.Should().BeEmpty();
        db.Tickets.Should().BeEmpty();
    }
}
