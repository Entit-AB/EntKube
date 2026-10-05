using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Support;

/// <summary>
/// One person on the support roster, as a screen or a rota needs them.
/// </summary>
/// <param name="UserId">The account.</param>
/// <param name="Name">What to call them — their display name, or their address.</param>
/// <param name="Email">Where a ticket assigned to them is sent.</param>
/// <param name="Enrolled">Whether they are on the roster at all.</param>
/// <param name="IsActive">Whether they are taking work right now.</param>
/// <param name="Note">Why they are not, when they are not.</param>
public readonly record struct SupportTechnician(
    string UserId,
    string Name,
    string? Email,
    bool Enrolled,
    bool IsActive,
    string? Note)
{
    /// <summary>
    /// Whether a ticket can actually be handed to them. An enrolled, active technician with
    /// no address is on the roster and cannot be written to, and assigning to them would be
    /// the silent-nothing-happened failure this subsystem is most prone to.
    /// </summary>
    public bool CanTakeWork => Enrolled && IsActive && !string.IsNullOrWhiteSpace(Email);
}

/// <summary>
/// Who is taking support work, and whose turn it is.
///
/// <para><b>The roster is the tenant's members, not a register of its own.</b> A separate
/// table of technicians would be a second list of people to keep in step with the first,
/// and it would hold addresses nobody verified. Membership already carries a real account
/// with a real address, so the only thing added on top is whether that person takes support
/// work — <see cref="SupportDuty"/>.</para>
///
/// <para><b>The rotation has no counter.</b> It is "the next active technician after
/// whoever holds the most recently assigned ticket", read from the tickets each time. A
/// stored cursor would be a second source of truth about something the tickets already say,
/// and it would drift the first time somebody assigned a ticket by hand — which is a thing
/// people do constantly and which should plainly count as that person's turn.</para>
/// </summary>
public class SupportDutyService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// The name that goes on an assignment nobody made by hand. §14.6 makes the history the
    /// record between the parties, so a rotation's turn says so in the same field a person's
    /// name would have gone in.
    /// </summary>
    public const string RoundRobinActor = "EntKube support rota";

    /// <summary>
    /// Everybody with access to this tenant, and what the roster says about them. Members
    /// who were never enrolled are included so that putting somebody on the roster is a
    /// matter of finding them in a list rather than knowing they exist.
    /// </summary>
    public async Task<List<SupportTechnician>> GetRosterAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var members = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Join(
                db.Users.AsNoTracking(),
                m => m.UserId,
                u => u.Id,
                (m, u) => new { u.Id, u.Email, u.UserName })
            .ToListAsync(ct);

        Dictionary<string, SupportDuty> duties = await db.SupportDuties.AsNoTracking()
            .Where(d => d.TenantId == tenantId)
            .ToDictionaryAsync(d => d.UserId, ct);

        return [.. members
            .Select(m =>
            {
                duties.TryGetValue(m.Id, out SupportDuty? duty);

                return new SupportTechnician(
                    m.Id,
                    m.UserName ?? m.Email ?? m.Id,
                    m.Email,
                    Enrolled: duty is not null,
                    IsActive: duty?.IsActive ?? false,
                    Note: duty?.Note);
            })
            .OrderByDescending(t => t.Enrolled)
            .ThenByDescending(t => t.IsActive)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Those a ticket can be handed to, in the stable order the rotation walks.
    ///
    /// <para>Ordered by account id rather than by name, because the rotation has to survive
    /// somebody changing their display name — which would otherwise move them in the ring
    /// and skip or repeat whoever they passed.</para>
    /// </summary>
    public async Task<List<SupportTechnician>> GetActiveAsync(
        Guid tenantId, CancellationToken ct = default) =>
        [.. (await GetRosterAsync(tenantId, ct))
            .Where(t => t.CanTakeWork)
            .OrderBy(t => t.UserId, StringComparer.Ordinal)];

    /// <summary>
    /// Whose turn it is, or null when nobody is taking work.
    ///
    /// <para>Null is an ordinary answer and not an error: a tenant that has enrolled nobody,
    /// or whose whole team is away, leaves tickets unassigned exactly as they were before
    /// any of this existed. What must not happen is a ticket assigned to somebody who is
    /// not there, because the mail about it then goes to a mailbox nobody is reading.</para>
    /// </summary>
    /// <param name="excludeUserId">
    /// Somebody to pass over — used when reassigning away from the current holder, so that
    /// "give it to somebody else" cannot hand it straight back.
    /// </param>
    public async Task<SupportTechnician?> NextAsync(
        Guid tenantId, string? excludeUserId = null, CancellationToken ct = default)
    {
        List<SupportTechnician> active = await GetActiveAsync(tenantId, ct);

        if (excludeUserId is not null && active.Count > 1)
        {
            active = [.. active.Where(t => t.UserId != excludeUserId)];
        }

        if (active.Count == 0)
        {
            return null;
        }

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Who holds the most recently assigned ticket. UpdatedAt rather than CreatedAt: a
        // reassignment is the latest thing that happened to the rota, and the rotation
        // should carry on from there.
        string? last = await db.Tickets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.AssigneeUserId != null)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => t.AssigneeUserId)
            .FirstOrDefaultAsync(ct);

        int previous = last is null
            ? -1
            : active.FindIndex(t => t.UserId == last);

        // Not found covers two ordinary cases: the last holder has since gone inactive, or
        // they were excluded just above. Either way the ring starts from the beginning,
        // which is the one answer that cannot skip somebody twice running.
        return active[(previous + 1) % active.Count];
    }

    /// <summary>
    /// Puts somebody on the roster, or changes whether they are taking work.
    ///
    /// <para>Enrolling and activating are the same call because they are the same intent
    /// said twice; standing down is this with <paramref name="isActive"/> false and a note,
    /// which keeps them on the roster where a colleague can see where they went.</para>
    /// </summary>
    public async Task SetDutyAsync(
        Guid tenantId, string userId, bool isActive, string? note, string actor,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        bool isMember = await db.TenantMemberships.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);

        if (!isMember)
        {
            // The roster cannot name somebody who cannot see the tickets on it. Refused
            // rather than ignored: this is reached from a screen that only offers members,
            // so getting here means something is wrong rather than merely unusual.
            throw new InvalidOperationException(
                "Only a member of this tenant can be put on its support roster.");
        }

        SupportDuty? duty = await db.SupportDuties
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.UserId == userId, ct);

        if (duty is null)
        {
            duty = new SupportDuty
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
            };

            db.SupportDuties.Add(duty);
        }

        duty.IsActive = isActive;
        duty.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        duty.UpdatedAt = DateTime.UtcNow;
        duty.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes somebody off the roster entirely.</summary>
    public async Task RemoveAsync(Guid tenantId, string userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SupportDuty? duty = await db.SupportDuties
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.UserId == userId, ct);

        if (duty is not null)
        {
            db.SupportDuties.Remove(duty);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The tenant member whose address this is, or null when it is nobody's.
    ///
    /// <para>What decides that an arriving mail is one of our own people answering rather
    /// than a customer writing in. Membership rather than the roster: a colleague who is
    /// standing down this week is still ours, and a reply they send should still reach the
    /// customer.</para>
    /// </summary>
    public async Task<SupportTechnician?> FindByAddressAsync(
        Guid tenantId, string? address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        string wanted = address.Trim().ToLowerInvariant();

        return (await GetRosterAsync(tenantId, ct))
            .Cast<SupportTechnician?>()
            .FirstOrDefault(t =>
                t!.Value.Email != null
                && t.Value.Email.Trim().ToLowerInvariant() == wanted);
    }
}
