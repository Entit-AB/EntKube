using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Services.Support;

namespace EntKube.Web.Services;

/// <summary>A stretch of a support window that no shift covers.</summary>
/// <param name="From">Start of the uncovered stretch.</param>
/// <param name="To">End of it.</param>
public readonly record struct CoverageGap(DateTime From, DateTime To)
{
    public TimeSpan Duration => To - From;
}

/// <summary>Where a subconsultant stands against §18's approval process.</summary>
public enum SubconsultantApproval
{
    /// <summary>Not yet notified to the customer, so not usable.</summary>
    NotNotified = 0,

    /// <summary>Notified, and the ten working days have not run out.</summary>
    AwaitingResponse = 1,

    /// <summary>Approved — either explicitly, or by the customer not objecting in time.</summary>
    Approved = 2,

    /// <summary>The customer objected. §18 says approval may not be unreasonably withheld.</summary>
    Objected = 3,
}

public class OnCallService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// The working days §18 gives the customer to object to a subconsultant before the
    /// notification counts as accepted.
    /// </summary>
    public const int SubconsultantObjectionWorkingDays = 10;

    /// <summary>
    /// Whoever is on call at a given instant, across every enabled schedule in the tenant.
    /// More than one is normal: a rotation per window, or a handover overlap.
    /// </summary>
    public async Task<List<OnCallShift>> WhoIsOnCallAsync(
        Guid tenantId, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.OnCallShifts
            .Include(sh => sh.Schedule)
            .Include(sh => sh.Subconsultant)
            .AsNoTracking()
            .Where(sh => sh.Schedule.TenantId == tenantId
                         && sh.Schedule.IsEnabled
                         && sh.StartsAt <= at
                         && sh.EndsAt > at)
            .OrderBy(sh => sh.Schedule.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stretches of a schedule's support window that nobody is rostered for.
    ///
    /// <para>§10.1.1 sells S3 and S4 as a staffed on-call rotation, and the window fee for S4
    /// is 95 000 kr a month for availability rather than output. Discovering a hole in the
    /// roster when an incident lands in it is the expensive way to find out.</para>
    ///
    /// <para>A schedule with no <see cref="OnCallSchedule.Covers"/> has nothing to be
    /// measured against and reports no gaps.</para>
    /// </summary>
    public async Task<List<CoverageGap>> GetCoverageGapsAsync(
        Guid scheduleId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OnCallSchedule? schedule = await db.OnCallSchedules
            .Include(s => s.Shifts)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

        if (schedule?.Covers is not SupportWindow window)
        {
            return [];
        }

        List<(DateTime From, DateTime To)> shifts = [.. schedule.Shifts
            .Where(sh => sh.EndsAt > from && sh.StartsAt < to)
            .Select(sh => (sh.StartsAt, sh.EndsAt))
            .OrderBy(sh => sh.StartsAt)];

        List<CoverageGap> gaps = [];

        foreach ((DateTime openFrom, DateTime openTo) in
                 BusinessCalendar.OpenSpansBetween(from, to, window))
        {
            DateTime cursor = openFrom;

            foreach ((DateTime shiftFrom, DateTime shiftTo) in shifts)
            {
                if (shiftTo <= cursor)
                {
                    continue;
                }

                if (shiftFrom >= openTo)
                {
                    break;
                }

                if (shiftFrom > cursor)
                {
                    gaps.Add(new CoverageGap(cursor, shiftFrom < openTo ? shiftFrom : openTo));
                }

                if (shiftTo > cursor)
                {
                    cursor = shiftTo;
                }

                if (cursor >= openTo)
                {
                    break;
                }
            }

            if (cursor < openTo)
            {
                gaps.Add(new CoverageGap(cursor, openTo));
            }
        }

        return gaps;
    }

    // ---- The §18 register ----------------------------------------------------------------

    public async Task<List<Subconsultant>> GetSubconsultantsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Subconsultants.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<Subconsultant> SaveSubconsultantAsync(
        Subconsultant subconsultant, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        subconsultant.UpdatedAt = DateTime.UtcNow;

        if (subconsultant.Id == Guid.Empty)
        {
            subconsultant.Id = Guid.NewGuid();
            db.Subconsultants.Add(subconsultant);
        }
        else
        {
            db.Subconsultants.Update(subconsultant);
        }

        await db.SaveChangesAsync(ct);
        return subconsultant;
    }

    /// <summary>
    /// When §18's objection period runs out — ten working days after the customer was
    /// notified. Null when no notification has been sent.
    /// </summary>
    public static DateTime? ObjectionDeadline(Subconsultant subconsultant) =>
        subconsultant.NotifiedAt is DateTime notified
            ? BusinessCalendar.WorkingDaysDeadline(notified, SubconsultantObjectionWorkingDays)
            : null;

    /// <summary>
    /// Where a subconsultant stands under §18. Approval is tacit: once ten working days
    /// have passed without an objection, they count as approved.
    /// </summary>
    public static SubconsultantApproval ApprovalStatus(Subconsultant subconsultant, DateTime asOf)
    {
        if (subconsultant.ObjectedAt is not null)
        {
            return SubconsultantApproval.Objected;
        }

        if (subconsultant.ApprovedAt is not null)
        {
            return SubconsultantApproval.Approved;
        }

        if (subconsultant.NotifiedAt is null)
        {
            return SubconsultantApproval.NotNotified;
        }

        return ObjectionDeadline(subconsultant) <= asOf
            ? SubconsultantApproval.Approved
            : SubconsultantApproval.AwaitingResponse;
    }

    /// <summary>
    /// Whether §18 and §24 both allow this person into the customer's environments: bound
    /// by confidentiality, bound by data-processing terms, and approved.
    /// </summary>
    public static bool MayAccessCustomerEnvironments(Subconsultant subconsultant, DateTime asOf) =>
        subconsultant.IsActive
        && subconsultant.ConfidentialitySignedAt is not null
        && subconsultant.DataProcessingBoundAt is not null
        && ApprovalStatus(subconsultant, asOf) == SubconsultantApproval.Approved;

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

    /// <summary>
    /// Puts somebody on the roster.
    /// </summary>
    /// <param name="assigneePhone">
    /// How to actually reach them. The roster carried a name and an address for a long
    /// time, while the entity's own comment observed that at three in the morning an
    /// address is not a way to reach a person — which was true, and there was no field on
    /// the form to do anything about it.
    /// </param>
    /// <param name="assigneeTeamsHandle">§14.3 makes Teams a contact channel for P1 and P2.</param>
    /// <param name="subconsultantId">
    /// Whoever is covering, when it is not our own staff. §18 requires them to be in the
    /// register, bound by §17's terms and approved before they go near the customer's
    /// environments — <see cref="MayAccessCustomerEnvironments"/> is that test, and this
    /// refuses rather than quietly rostering somebody who fails it.
    /// </param>
    public async Task AddShiftAsync(
        Guid scheduleId, string assigneeName, string? assigneeEmail,
        DateTime startsAt, DateTime endsAt, string? notes,
        string? assigneePhone = null,
        string? assigneeTeamsHandle = null,
        OnCallAffiliation affiliation = OnCallAffiliation.Employee,
        Guid? subconsultantId = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (subconsultantId is Guid id)
        {
            Subconsultant? person = await db.Subconsultants
                .FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new InvalidOperationException("That subconsultant is not registered.");

            if (!MayAccessCustomerEnvironments(person, startsAt))
            {
                throw new InvalidOperationException(
                    $"§18 does not yet allow {person.Name} into the customer's environments. "
                    + "They must be bound by confidentiality and data-processing terms and "
                    + "approved by the customer before taking a shift.");
            }
        }

        db.OnCallShifts.Add(new OnCallShift
        {
            ScheduleId = scheduleId,
            AssigneeName = assigneeName,
            AssigneeEmail = assigneeEmail,
            AssigneePhone = assigneePhone,
            AssigneeTeamsHandle = assigneeTeamsHandle,
            Affiliation = affiliation,
            SubconsultantId = subconsultantId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Notes = notes
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// What is running hot as a shift ends, written by whoever is handing over.
    ///
    /// <para>The field and the place it is displayed have existed from the start; nothing
    /// could write to it. A handover note is the one thing the next person actually needs
    /// and the one thing a rota cannot infer — which incident is still live, what was tried
    /// at two in the morning, who has already been rung.</para>
    /// </summary>
    public async Task RecordHandoverAsync(
        Guid shiftId, string? notes, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OnCallShift? shift = await db.OnCallShifts.FirstOrDefaultAsync(sh => sh.Id == shiftId, ct);

        if (shift is null)
        {
            return;
        }

        shift.HandoverNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
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
