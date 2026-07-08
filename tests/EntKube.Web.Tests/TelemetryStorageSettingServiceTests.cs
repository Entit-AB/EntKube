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

    [Fact]
    public async Task Get_WhenUnset_ReturnsNull()
        => (await _service.GetStorageLinkIdAsync()).Should().BeNull();

    [Fact]
    public async Task Set_ThenGet_PersistsAndInvalidatesCache()
    {
        Guid link = Guid.NewGuid();

        await _service.GetStorageLinkIdAsync();               // prime the cache (null)
        await _service.SetStorageLinkIdAsync(link, "admin-1"); // must invalidate it
        (await _service.GetStorageLinkIdAsync()).Should().Be(link);

        // Persisted as a single row.
        (await _context.TelemetryStorageSettings.CountAsync()).Should().Be(1);

        // Clearing goes back to null (fallback storage).
        await _service.SetStorageLinkIdAsync(null, "admin-1");
        (await _service.GetStorageLinkIdAsync()).Should().BeNull();
        (await _context.TelemetryStorageSettings.CountAsync()).Should().Be(1); // still one row, updated in place
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
