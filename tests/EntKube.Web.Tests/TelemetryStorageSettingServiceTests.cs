using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The global, admin-editable telemetry object-storage setting (which StorageLink backs sealed segments):
/// verifies it persists a single row and that the in-memory cache is invalidated on save so the telemetry
/// blob store picks up an admin change without a restart.
/// </summary>
public sealed class TelemetryStorageSettingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TelemetryStorageSettingService _service;

    public TelemetryStorageSettingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _service = new TelemetryStorageSettingService(new TestDbContextFactory(_connection));
    }

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    [Fact]
    public async Task Get_WhenUnset_ReturnsNull()
        => (await _service.GetStorageLinkIdAsync(_tenantA)).Should().BeNull();

    [Fact]
    public async Task Set_ThenGet_IsPerTenant_AndInvalidatesCache()
    {
        Guid linkA = Guid.NewGuid();

        await _service.GetStorageLinkIdAsync(_tenantA);                // prime the cache (null)
        await _service.SetStorageLinkIdAsync(_tenantA, linkA, "admin-1"); // must invalidate it
        (await _service.GetStorageLinkIdAsync(_tenantA)).Should().Be(linkA);

        // Per-tenant: tenant B is unaffected by tenant A's setting.
        (await _service.GetStorageLinkIdAsync(_tenantB)).Should().BeNull();

        // One row per tenant.
        (await _context.TelemetryStorageSettings.CountAsync()).Should().Be(1);

        // Clearing tenant A goes back to null; the row is updated in place.
        await _service.SetStorageLinkIdAsync(_tenantA, null, "admin-1");
        (await _service.GetStorageLinkIdAsync(_tenantA)).Should().BeNull();
        (await _context.TelemetryStorageSettings.CountAsync()).Should().Be(1);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
