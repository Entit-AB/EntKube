using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>The signal a dashboard panel visualizes.</summary>
public enum DashboardPanelType
{
    /// <summary>A native metric time series (line chart).</summary>
    MetricSeries,
    /// <summary>A service's RED (requests / error-rate / p95) summary + request-volume strip.</summary>
    ServiceRed,
    /// <summary>Log volume over time (histogram), optionally namespace/text-scoped.</summary>
    LogVolume
}

/// <summary>One panel in a dashboard. Serialized into <see cref="Dashboard.PanelsJson"/>.</summary>
public sealed class DashboardPanel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DashboardPanelType Type { get; set; }
    public string? Metric { get; set; }      // MetricSeries
    public string? Service { get; set; }     // MetricSeries scope / ServiceRed
    public string? Namespace { get; set; }   // LogVolume
    public string? MatchText { get; set; }   // LogVolume
    /// <summary>Bootstrap grid width (columns of 12); 6 = half, 12 = full.</summary>
    public int Width { get; set; } = 6;
}

/// <summary>CRUD for tenant-scoped composable dashboards; (de)serializes the panel list.</summary>
public class DashboardService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<List<Dashboard>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Dashboards.Where(d => d.TenantId == tenantId).OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<Dashboard?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Dashboards.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
    }

    public async Task<Dashboard> CreateAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        Dashboard d = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = name };
        db.Dashboards.Add(d);
        await db.SaveChangesAsync(ct);
        return d;
    }

    public async Task RenameAsync(Guid tenantId, Guid id, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        Dashboard? d = await db.Dashboards.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (d is null) return;
        d.Name = name; d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SavePanelsAsync(Guid tenantId, Guid id, List<DashboardPanel> panels, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        Dashboard? d = await db.Dashboards.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (d is null) return;
        d.PanelsJson = JsonSerializer.Serialize(panels);
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.Dashboards.Where(d => d.Id == id && d.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }

    public static List<DashboardPanel> ParsePanels(Dashboard d)
    {
        try { return JsonSerializer.Deserialize<List<DashboardPanel>>(d.PanelsJson, JsonOpts) ?? []; }
        catch { return []; }
    }
}
