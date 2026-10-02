using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Who is taking support work, and whose turn it is.
///
/// <para>The property worth defending is that a ticket is never handed to somebody who is
/// not there. An unassigned ticket waits in a queue, which is what every ticket did before
/// the rota existed; a ticket assigned to a mailbox nobody is reading looks handled and is
/// not, which is strictly worse than the state this replaced.</para>
/// </summary>
public class SupportDutyServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;
    private readonly SupportDutyService duty;
    private readonly TicketService tickets;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid roleId = Guid.NewGuid();
    private readonly Guid otherRoleId = Guid.NewGuid();

    public SupportDutyServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Annat", Slug = "annat" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.TenantRoles.AddRange(
            new TenantRole { Id = roleId, TenantId = tenantId, Name = "Support" },
            new TenantRole { Id = otherRoleId, TenantId = otherTenantId, Name = "Support" });
        db.SaveChanges();

        factory = new TestDbContextFactory(connection);
        ContractService contracts = new(factory);
        duty = new SupportDutyService(factory);
        tickets = new TicketService(factory, contracts, SilentTicketNotifier.For(factory));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A member of the tenant, with an address, who could be enrolled.</summary>
    private string Member(string name, Guid? inTenant = null, string? email = null)
    {
        string id = $"user-{name}";

        if (!db.Users.Any(u => u.Id == id))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = name,
                Email = email ?? $"{name}@entit.example",
            });
        }

        Guid tenant = inTenant ?? tenantId;

        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = id,
            TenantId = tenant,
            RoleId = tenant == tenantId ? roleId : otherRoleId,
        });

        db.SaveChanges();
        return id;
    }

    private Task Enrol(string userId, bool active = true, string? note = null) =>
        duty.SetDutyAsync(tenantId, userId, active, note, "nils");

    // ---- The roster ---------------------------------------------------------------------------

    /// <summary>
    /// Everybody with access is listed, enrolled or not — otherwise putting somebody on the
    /// rota means knowing their account id, and the screen cannot offer them.
    /// </summary>
    [Fact]
    public async Task Every_member_is_listed_whether_or_not_they_are_on_the_rota()
    {
        Member("anna");
        string bo = Member("bo");
        await Enrol(bo);

        List<SupportTechnician> roster = await duty.GetRosterAsync(tenantId);

        roster.Should().HaveCount(2);
        roster.Should().Contain(t => t.Name == "anna" && !t.Enrolled);
        roster.Should().Contain(t => t.Name == "bo" && t.Enrolled && t.IsActive);
    }

    /// <summary>
    /// <b>Enrolment is deliberate.</b> Not every member is a technician — somebody only
    /// looks at the invoices, somebody else was given access for one migration. Round-robin
    /// over everybody with access would hand a P1 to whoever that is.
    /// </summary>
    [Fact]
    public async Task A_member_who_was_never_enrolled_is_not_in_the_rotation()
    {
        Member("anna");

        (await duty.GetActiveAsync(tenantId)).Should().BeEmpty();
        (await duty.NextAsync(tenantId)).Should().BeNull();
    }

    /// <summary>
    /// Support duty is per tenant: the same person can carry a portfolio for one and have
    /// nothing to do with another.
    /// </summary>
    [Fact]
    public async Task A_roster_is_one_tenants_own()
    {
        string anna = Member("anna");
        await Enrol(anna);
        Member("cecilia", inTenant: otherTenantId);

        (await duty.GetRosterAsync(tenantId)).Should().ContainSingle();
        (await duty.GetRosterAsync(otherTenantId)).Should().ContainSingle()
            .Which.Name.Should().Be("cecilia");
    }

    /// <summary>
    /// Somebody who cannot see the ticket cannot be on the rota for it. Refused rather than
    /// ignored — the screen only offers members, so getting here means something is wrong.
    /// </summary>
    [Fact]
    public async Task Somebody_outside_the_tenant_cannot_be_put_on_its_rota()
    {
        string cecilia = Member("cecilia", inTenant: otherTenantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => duty.SetDutyAsync(tenantId, cecilia, true, null, "nils"));
    }

    // ---- Standing down -----------------------------------------------------------------------

    /// <summary>
    /// <b>The thing that was asked for.</b> Somebody has to be able to be free of work, and
    /// standing down must not take them off the roster — a colleague should read "parental
    /// leave until March" rather than find a name silently missing.
    /// </summary>
    [Fact]
    public async Task Standing_down_leaves_them_on_the_roster_with_the_reason()
    {
        string anna = Member("anna");
        await Enrol(anna);
        await Enrol(anna, active: false, note: "Parental leave until March");

        SupportTechnician listed = (await duty.GetRosterAsync(tenantId)).Single();

        listed.Enrolled.Should().BeTrue();
        listed.IsActive.Should().BeFalse();
        listed.Note.Should().Be("Parental leave until March");

        (await duty.NextAsync(tenantId)).Should().BeNull("they are not taking work");
    }

    /// <summary>
    /// Enrolled, active, and with nowhere to send the mail. The one combination that looks
    /// fine on the roster and cannot work, so the rotation passes over it rather than
    /// assigning a ticket to a mailbox that does not exist.
    /// </summary>
    [Fact]
    public async Task Somebody_with_no_address_is_passed_over()
    {
        string ghost = Member("ghost", email: "");
        await Enrol(ghost);

        (await duty.GetRosterAsync(tenantId)).Single().CanTakeWork.Should().BeFalse();
        (await duty.NextAsync(tenantId)).Should().BeNull();
    }

    [Fact]
    public async Task Removing_somebody_takes_them_off_the_rota_but_not_the_tenant()
    {
        string anna = Member("anna");
        await Enrol(anna);
        await duty.RemoveAsync(tenantId, anna);

        SupportTechnician listed = (await duty.GetRosterAsync(tenantId)).Single();

        listed.Enrolled.Should().BeFalse();
        listed.Name.Should().Be("anna");
    }

    // ---- The rotation ------------------------------------------------------------------------

    /// <summary>
    /// Round robin: each new ticket goes to the next one along, and the ring comes back
    /// round rather than piling up on whoever sorts first.
    /// </summary>
    [Fact]
    public async Task Each_ticket_goes_to_the_next_one_along()
    {
        foreach (string name in new[] { "anna", "bo", "cecilia" })
        {
            await Enrol(Member(name));
        }

        List<string> order = [];

        for (int i = 0; i < 6; i++)
        {
            SupportTechnician? next = await duty.NextAsync(tenantId);
            next.Should().NotBeNull();

            order.Add(next!.Value.Name);
            await Assigned(next.Value);
        }

        // Three people, six tickets, nobody twice before everybody once.
        order.Take(3).Should().OnlyHaveUniqueItems();
        order.Skip(3).Take(3).Should().OnlyHaveUniqueItems();
        order.Skip(3).Take(3).Should().BeEquivalentTo(order.Take(3));
    }

    /// <summary>
    /// <b>No counter to drift.</b> The rotation reads the tickets, so a ticket assigned by
    /// hand plainly counts as that person's turn and the next one goes to whoever follows
    /// them — rather than to whoever a stored cursor happened to be pointing at.
    /// </summary>
    [Fact]
    public async Task An_assignment_made_by_hand_counts_as_that_persons_turn()
    {
        string anna = Member("anna");
        string bo = Member("bo");
        await Enrol(anna);
        await Enrol(bo);

        List<SupportTechnician> active = await duty.GetActiveAsync(tenantId);

        // Hand the ticket to the second of them, out of turn.
        await Assigned(active[1]);

        (await duty.NextAsync(tenantId))!.Value.UserId.Should().Be(active[0].UserId);
    }

    /// <summary>
    /// The last holder has since stood down. The ring starts again from the beginning, which
    /// is the one answer that cannot skip somebody twice running.
    /// </summary>
    [Fact]
    public async Task A_rotation_whose_last_holder_left_starts_again()
    {
        string anna = Member("anna");
        string bo = Member("bo");
        await Enrol(anna);
        await Enrol(bo);

        List<SupportTechnician> active = await duty.GetActiveAsync(tenantId);
        await Assigned(active[0]);

        await Enrol(active[0].UserId, active: false, note: "Away");

        (await duty.NextAsync(tenantId))!.Value.UserId.Should().Be(active[1].UserId);
    }

    /// <summary>
    /// Handing a ticket on should not be able to hand it straight back to the person giving
    /// it up — unless they are the only one taking work, when there is nowhere else for it
    /// to go and pretending otherwise would lose it.
    /// </summary>
    [Fact]
    public async Task Somebody_can_be_passed_over_on_purpose()
    {
        string anna = Member("anna");
        string bo = Member("bo");
        await Enrol(anna);
        await Enrol(bo);

        (await duty.NextAsync(tenantId, excludeUserId: anna))!.Value.UserId.Should().Be(bo);

        await duty.RemoveAsync(tenantId, bo);

        (await duty.NextAsync(tenantId, excludeUserId: anna))!.Value.UserId
            .Should().Be(anna, "there is nobody else, and the ticket still has to go somewhere");
    }

    /// <summary>A ticket held by whoever's turn it just was, so the ring moves on.</summary>
    private async Task Assigned(SupportTechnician technician)
    {
        Ticket ticket = await tickets.CreateAsync(
            tenantId, customerId, null, "Fel", "Beskrivning.", TicketChannel.Email,
            TicketPriority.P3, DateTime.UtcNow);

        await tickets.AssignAsync(
            ticket.Id, technician.Name, SupportDutyService.RoundRobinActor,
            DateTime.UtcNow.AddSeconds(1), technician.UserId);
    }
}
