using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ContractKind = EntKube.Contracts.Fleet.MaintenanceKind;
using EntityKind = EntKube.Web.Data.MaintenanceKind;

namespace EntKube.Web.Tests;

/// <summary>
/// The two declarations of "did this window owe notice", held together.
///
/// <para><c>MaintenanceWindowSummary</c> grew a <see cref="ContractKind"/> so the monthly report
/// could reach maintenance through <c>IFleetApi</c> instead of the all-seeing context. That makes
/// two enums for one fact, and this column is stored as an <b>int</b> — so inserting a member into
/// the middle of one and not the other would silently relabel every stored row. A planned window
/// reported as an emergency owes no notice, and §16's short-notice list would quietly empty
/// itself: the report would look clean because the question had changed, not the service.</para>
///
/// <para>The mapping in <c>FleetApi</c> is an explicit switch rather than a cast for the same
/// reason, so a new member stops compiling instead of being mapped to whatever shares its
/// ordinal.</para>
/// </summary>
public class ApiMaintenanceKindTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;
    private readonly FleetApi fleet;

    private readonly Guid tenantId = Guid.NewGuid();

    public ApiMaintenanceKindTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);
        fleet = new FleetApi(factory);

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();
    }

    [Fact]
    public void The_two_enums_declare_the_same_members_in_the_same_order()
    {
        Enum.GetNames<ContractKind>().Should().Equal(Enum.GetNames<EntityKind>(),
            "one fact, two declarations — and the stored column is an int, so a member inserted "
            + "into one and not the other relabels every row that is already in the database");

        Enum.GetValues<ContractKind>().Select(v => (int)v)
            .Should().Equal(Enum.GetValues<EntityKind>().Select(v => (int)v),
                "the names agreeing is not enough while the storage is ordinal");
    }

    [Theory]
    [InlineData(EntityKind.Planned, ContractKind.Planned)]
    [InlineData(EntityKind.Emergency, ContractKind.Emergency)]
    public async Task A_window_crosses_the_contract_with_its_kind_intact(
        EntityKind stored, ContractKind expected)
    {
        Guid id = Guid.NewGuid();
        DateTime starts = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = id,
            TenantId = tenantId,
            Title = "Patch the control plane",
            CreatedBy = "nils",
            Kind = stored,
            StartsAt = starts,
            EndsAt = starts.AddHours(2),
            CreatedAt = starts.AddDays(-10),
        });
        await db.SaveChangesAsync();

        var windows = await fleet.GetMaintenanceWindowsOverlappingAsync(
            tenantId, starts.AddDays(-1), starts.AddDays(1));

        windows.Should().HaveCount(1);
        windows[0].Kind.Should().Be(expected);
        windows[0].Title.Should().Be("Patch the control plane");
        windows[0].CreatedBy.Should().Be("nils");
    }

    /// <summary>
    /// The announcement is resolved at the boundary: the entity's <c>AnnouncedAt</c> when someone
    /// recorded one, else <c>CreatedAt</c> — the only announcement the system can vouch for. The
    /// contract carries one non-nullable field so no consumer can read <c>CreatedAt</c> and call
    /// it the announcement when a real one exists.
    /// </summary>
    [Fact]
    public async Task AnnouncedAt_falls_back_to_CreatedAt_and_is_preferred_when_recorded()
    {
        DateTime starts = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);
        DateTime created = starts.AddDays(-2);
        DateTime announced = starts.AddDays(-9);

        db.MaintenanceWindows.AddRange(
            new MaintenanceWindow
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Title = "No announcement recorded",
                CreatedBy = "nils", StartsAt = starts, EndsAt = starts.AddHours(1),
                CreatedAt = created,
            },
            new MaintenanceWindow
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Title = "Announced earlier",
                CreatedBy = "nils", StartsAt = starts, EndsAt = starts.AddHours(1),
                CreatedAt = created, AnnouncedAt = announced,
            });
        await db.SaveChangesAsync();

        var windows = await fleet.GetMaintenanceWindowsOverlappingAsync(
            tenantId, starts.AddDays(-1), starts.AddDays(1));

        windows.Single(w => w.Title == "No announcement recorded")
            .AnnouncedAt.Should().Be(created);
        windows.Single(w => w.Title == "Announced earlier")
            .AnnouncedAt.Should().Be(announced);
    }

    /// <summary>
    /// The contract overload of <see cref="MaintenanceNotice.Assess(EntKube.Contracts.Fleet.MaintenanceWindowSummary, int)"/>
    /// must agree with the entity one it sits beside. Two routes to one assessment is how a
    /// report and a page come to disagree about whether notice was owed.
    /// </summary>
    [Fact]
    public void Assess_agrees_whether_the_window_came_from_the_database_or_the_contract()
    {
        DateTime starts = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);
        MaintenanceWindow entity = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Title = "Patch", CreatedBy = "nils",
            StartsAt = starts, EndsAt = starts.AddHours(1), CreatedAt = starts.AddDays(-2),
        };

        EntKube.Contracts.Fleet.MaintenanceWindowSummary summary = new(
            entity.Id, null, entity.StartsAt, entity.EndsAt, entity.Title,
            entity.AnnouncedAt ?? entity.CreatedAt, entity.CreatedBy, ContractKind.Planned);

        MaintenanceNotice.Assess(summary).Should().Be(MaintenanceNotice.Assess(entity));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
