using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

public class OnCallService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<List<OnCallSchedule>> GetSchedulesAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.OnCallSchedules
            .Include(s => s.Shifts.OrderBy(sh => sh.StartsAt))
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<OnCallSchedule?> GetScheduleAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.OnCallSchedules
            .Include(s => s.Shifts.OrderBy(sh => sh.StartsAt))
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<Guid> CreateScheduleAsync(Guid tenantId, string name, string? description, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        OnCallSchedule schedule = new()
        {
            TenantId = tenantId,
            Name = name,
            Description = description
        };
        db.OnCallSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        return schedule.Id;
    }

    public async Task UpdateScheduleAsync(Guid id, string name, string? description, bool isEnabled, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        OnCallSchedule? schedule = await db.OnCallSchedules.FindAsync([id], ct);
        if (schedule is null) return;

        schedule.Name = name;
        schedule.Description = description;
        schedule.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        OnCallSchedule? schedule = await db.OnCallSchedules.FindAsync([id], ct);
        if (schedule is not null) db.OnCallSchedules.Remove(schedule);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddShiftAsync(Guid scheduleId, string assigneeName, string? assigneeEmail,
        DateTime startsAt, DateTime endsAt, string? notes, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.OnCallShifts.Add(new OnCallShift
        {
            ScheduleId = scheduleId,
            AssigneeName = assigneeName,
            AssigneeEmail = assigneeEmail,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Notes = notes
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteShiftAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        OnCallShift? shift = await db.OnCallShifts.FindAsync([id], ct);
        if (shift is not null) db.OnCallShifts.Remove(shift);
        await db.SaveChangesAsync(ct);
    }

    public async Task<OnCallShift?> GetCurrentOnCallAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;
        return await db.OnCallShifts
            .Include(sh => sh.Schedule)
            .Where(sh => sh.Schedule.TenantId == tenantId
                      && sh.Schedule.IsEnabled
                      && sh.StartsAt <= now
                      && sh.EndsAt >= now)
            .OrderBy(sh => sh.Schedule.Name)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<OnCallShift>> GetUpcomingShiftsAsync(Guid tenantId, int days = 7, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;
        DateTime horizon = now.AddDays(days);
        return await db.OnCallShifts
            .Include(sh => sh.Schedule)
            .Where(sh => sh.Schedule.TenantId == tenantId
                      && sh.Schedule.IsEnabled
                      && sh.StartsAt <= horizon
                      && sh.EndsAt >= now)
            .OrderBy(sh => sh.StartsAt)
            .Take(10)
            .ToListAsync(ct);
    }
}
