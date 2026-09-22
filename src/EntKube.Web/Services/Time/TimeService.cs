using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Time;

/// <summary>How much of a month's hour bank has gone, and what is left (§11).</summary>
/// <param name="Month">First instant of the month, in UTC.</param>
/// <param name="EntitledHours">What Annex B bought for the month. Null under Model B.</param>
/// <param name="DrawnHours">Hours taken out, after the §13 factors.</param>
/// <param name="OverspillHours">Hours worked beyond the bank, billed per §13.</param>
/// <param name="ByCategory">The drawdown split by time category.</param>
public readonly record struct TimebankStatement(
    DateTime Month,
    decimal? EntitledHours,
    decimal DrawnHours,
    decimal OverspillHours,
    IReadOnlyDictionary<SupportTimeCategory, decimal> ByCategory)
{
    /// <summary>What is left of the month's bank, floored at zero.</summary>
    public decimal RemainingHours =>
        EntitledHours is null ? 0m : Math.Max(0m, EntitledHours.Value - DrawnHours);

    /// <summary>
    /// Hours that will expire unused if nothing more is booked. §11.1: they cannot be
    /// saved, transferred, offset or refunded, so this is what the month is about to lose.
    /// </summary>
    public decimal ExpiringHours => RemainingHours;

    public bool IsExhausted => EntitledHours is not null && DrawnHours >= EntitledHours.Value;
}

/// <summary>One line of the §20 specification: per application, ticket, day and category.</summary>
/// <param name="Day">The day the work was done, in Swedish local time.</param>
/// <param name="AppId">The application, when the work was on one.</param>
/// <param name="AppName">Its name as it stands now, for reading.</param>
/// <param name="TicketId">The ticket, when the work belongs to one.</param>
/// <param name="TicketNumber">Its human reference.</param>
/// <param name="Kind">What kind of work it was.</param>
/// <param name="Category">The §13 time category.</param>
/// <param name="BilledHours">Hours billed after the pass rounding.</param>
/// <param name="BankHours">Hours drawn from a Model A hour bank.</param>
/// <param name="Description">What was done.</param>
public readonly record struct BillingLine(
    DateOnly Day,
    Guid? AppId,
    string? AppName,
    Guid? TicketId,
    int? TicketNumber,
    WorkKind Kind,
    SupportTimeCategory Category,
    decimal BilledHours,
    decimal BankHours,
    string Description);

/// <summary>Hours performed and billable but not covered by a hour bank (§12, and Model A overspill).</summary>
/// <param name="From">Start of the period.</param>
/// <param name="To">End of the period, exclusive.</param>
/// <param name="Lines">The specification §20 requires, one line per application/ticket/day/category.</param>
/// <param name="UnauthorisedHours">
/// Hours worked beyond the bank or above a §12 cap with no recorded approval and no P1/P2
/// exemption. Reported rather than hidden: these are the hours an invoice will be argued
/// about.
/// </param>
public readonly record struct CommittedHoursStatement(
    DateTime From,
    DateTime To,
    IReadOnlyList<BillingLine> Lines,
    decimal UnauthorisedHours)
{
    public decimal TotalHours => Lines.Sum(l => l.BilledHours);

    public IEnumerable<IGrouping<SupportTimeCategory, BillingLine>> ByCategory =>
        Lines.GroupBy(l => l.Category);
}

/// <summary>
/// Records worked time and reports it two ways: how much of the hour bank is gone, and how
/// many hours have been committed.
///
/// <para><b>It does not invoice.</b> That happens in another system, so what this owes that
/// system is the numbers at the granularity §20 requires — per application, ticket, day and
/// time category — and nothing beyond it.</para>
/// </summary>
public class TimeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ContractService contracts)
{
    /// <summary>Records a stretch of work.</summary>
    public async Task<TimeEntry> LogAsync(
        Guid tenantId,
        Guid customerId,
        Guid? appId,
        Guid? ticketId,
        DateTime startedAt,
        DateTime endedAt,
        WorkKind kind,
        string description,
        string? performedBy,
        CancellationToken ct = default)
    {
        if (endedAt <= startedAt)
        {
            throw new ArgumentException("A time entry has to end after it started.", nameof(endedAt));
        }

        TimeEntry entry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            AppId = appId,
            TicketId = ticketId,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Kind = kind,
            Description = description,
            PerformedBy = performedBy,
        };

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        db.TimeEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return entry;
    }

    public async Task DeleteAsync(Guid entryId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TimeEntry? entry = await db.TimeEntries.FindAsync([entryId], ct);
        if (entry is not null)
        {
            db.TimeEntries.Remove(entry);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Records the customer's approval for work beyond the bank or above a cap.</summary>
    public async Task<WorkAuthorisation> AuthoriseAsync(
        WorkAuthorisation authorisation, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        authorisation.Id = authorisation.Id == Guid.Empty ? Guid.NewGuid() : authorisation.Id;
        authorisation.ApprovedAt = authorisation.ApprovedAt == default
            ? DateTime.UtcNow
            : authorisation.ApprovedAt;

        db.WorkAuthorisations.Add(authorisation);
        await db.SaveChangesAsync(ct);

        return authorisation;
    }

    /// <summary>
    /// How much of a month's hour bank has gone (§11). The balance is derived from the
    /// month's entries rather than stored, because §11.1 makes the bank non-rolling: every
    /// month starts again at the agreed number, and a counter would drift.
    /// </summary>
    public async Task<TimebankStatement> GetTimebankAsync(
        Guid customerId, DateTime month, CancellationToken ct = default)
    {
        (DateTime from, DateTime to) = MonthBounds(month);

        PortfolioAgreement? agreement = await contracts.GetPortfolioAgreementAsync(customerId, from, ct);

        decimal? entitled = agreement?.PricingModel == PricingModel.HourBank
            ? agreement.HourBankHoursPerMonth
            : null;

        IReadOnlyList<WorkPass> passes = await PassesAsync(customerId, from, to, ct);

        Dictionary<SupportTimeCategory, decimal> byCategory = [];
        decimal drawn = 0m;

        foreach (WorkPass pass in passes.Where(p => WorkPassCalculator.DrawsOnTimebank(p.Kind)))
        {
            foreach (WorkPassPart part in pass.Parts)
            {
                byCategory[part.Category] = byCategory.GetValueOrDefault(part.Category) + part.BankHours;
                drawn += part.BankHours;
            }
        }

        decimal overspill = entitled is null ? 0m : Math.Max(0m, drawn - entitled.Value);

        return new TimebankStatement(from, entitled, drawn, overspill, byCategory);
    }

    /// <summary>
    /// The hours committed in a period, at the granularity §20 requires so the customer can
    /// validate them.
    /// </summary>
    public async Task<CommittedHoursStatement> GetCommittedHoursAsync(
        Guid customerId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Dictionary<Guid, string> appNames = await db.Apps.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        Dictionary<Guid, int> ticketNumbers = await db.Tickets.AsNoTracking()
            .Where(t => t.CustomerId == customerId)
            .ToDictionaryAsync(t => t.Id, t => t.Number, ct);

        List<TimeEntry> entries = await EntriesAsync(db, customerId, from, to, ct);
        IReadOnlyList<WorkPass> passes = await PassesAsync(customerId, from, to, ct);

        // The description of the first entry in a pass stands for the pass; §20 wants the
        // activities to be validatable, not every keystroke.
        Dictionary<(Guid?, DateOnly, WorkKind), string> descriptions = [];

        foreach (TimeEntry entry in entries.OrderBy(e => e.StartedAt))
        {
            var key = (entry.TicketId ?? entry.AppId, LocalDay(entry.StartedAt), entry.Kind);
            descriptions.TryAdd(key, entry.Description);
        }

        List<BillingLine> lines = [];

        foreach (WorkPass pass in passes)
        {
            DateOnly day = LocalDay(pass.StartedAt);
            descriptions.TryGetValue((pass.TicketId ?? pass.AppId, day, pass.Kind), out string? description);

            foreach (WorkPassPart part in pass.Parts)
            {
                lines.Add(new BillingLine(
                    day,
                    pass.AppId,
                    pass.AppId is Guid appId ? appNames.GetValueOrDefault(appId) : null,
                    pass.TicketId,
                    pass.TicketId is Guid ticketId && ticketNumbers.TryGetValue(ticketId, out int n) ? n : null,
                    pass.Kind,
                    part.Category,
                    part.BilledHours,
                    WorkPassCalculator.DrawsOnTimebank(pass.Kind) ? part.BankHours : 0m,
                    description ?? ""));
            }
        }

        decimal unauthorised = await UnauthorisedHoursAsync(db, customerId, from, passes, entries, ct);

        return new CommittedHoursStatement(from, to, lines, unauthorised);
    }

    /// <summary>The work passes in a period, with the §13 rounding applied.</summary>
    public async Task<IReadOnlyList<WorkPass>> PassesAsync(
        Guid customerId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<TimeEntry> entries = await EntriesAsync(db, customerId, from, to, ct);

        HashSet<Guid> calloutTickets = [.. await db.Tickets.AsNoTracking()
            .Where(t => t.CustomerId == customerId && t.IsCallout)
            .Select(t => t.Id)
            .ToListAsync(ct)];

        return WorkPassCalculator.BuildPasses(entries, calloutTickets);
    }

    private static Task<List<TimeEntry>> EntriesAsync(
        ApplicationDbContext db, Guid customerId, DateTime from, DateTime to, CancellationToken ct) =>
        db.TimeEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId && e.StartedAt >= from && e.StartedAt < to)
            .OrderBy(e => e.StartedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Hours worked past the hour bank with nothing authorising them.
    ///
    /// <para>§11.1 excepts P1 and P2, which are worked without delay and billed without
    /// separate approval, so their hours are never counted here. What is left is the work
    /// somebody should have got a yes for — worth surfacing before the invoice does it.</para>
    /// </summary>
    private async Task<decimal> UnauthorisedHoursAsync(
        ApplicationDbContext db,
        Guid customerId,
        DateTime month,
        IReadOnlyList<WorkPass> passes,
        List<TimeEntry> entries,
        CancellationToken ct)
    {
        PortfolioAgreement? agreement = await contracts.GetPortfolioAgreementAsync(customerId, month, ct);

        if (agreement?.PricingModel != PricingModel.HourBank || agreement.HourBankHoursPerMonth is null)
        {
            return 0m;
        }

        (DateTime from, DateTime to) = MonthBounds(month);

        HashSet<Guid> authorisedTickets = [.. await db.WorkAuthorisations.AsNoTracking()
            .Where(a => a.CustomerId == customerId && a.Month >= from && a.Month < to && a.TicketId != null)
            .Select(a => a.TicketId!.Value)
            .ToListAsync(ct)];

        bool blanket = await db.WorkAuthorisations.AsNoTracking()
            .AnyAsync(a => a.CustomerId == customerId && a.Month >= from && a.Month < to && a.TicketId == null, ct);

        if (blanket)
        {
            return 0m;
        }

        HashSet<Guid> urgentTickets = [.. await db.Tickets.AsNoTracking()
            .Where(t => t.CustomerId == customerId
                        && (t.Priority == TicketPriority.P1 || t.Priority == TicketPriority.P2))
            .Select(t => t.Id)
            .ToListAsync(ct)];

        HashSet<Guid> authorisedEntries = [.. entries
            .Where(e => e.AuthorisationId is not null)
            .Select(e => e.Id)];

        decimal budget = agreement.HourBankHoursPerMonth.Value;
        decimal drawn = 0m;
        decimal unauthorised = 0m;

        foreach (WorkPass pass in passes
                     .Where(p => WorkPassCalculator.DrawsOnTimebank(p.Kind))
                     .OrderBy(p => p.StartedAt))
        {
            decimal before = drawn;
            drawn += pass.BankHours;

            decimal beyond = Math.Max(0m, drawn - Math.Max(before, budget));
            if (beyond <= 0m)
            {
                continue;
            }

            bool excused = pass.TicketId is Guid id
                && (urgentTickets.Contains(id) || authorisedTickets.Contains(id));

            bool entryAuthorised = entries.Any(e =>
                e.TicketId == pass.TicketId && authorisedEntries.Contains(e.Id));

            if (!excused && !entryAuthorised)
            {
                unauthorised += beyond;
            }
        }

        return unauthorised;
    }

    /// <summary>
    /// The month containing an instant, as Swedish calendar months — §11.1 counts the bank
    /// per calendar month, and a UTC month boundary is an hour or two out of step with that.
    /// </summary>
    public static (DateTime From, DateTime To) MonthBounds(DateTime anyInstantInMonth)
    {
        // Unspecified means UTC here, as it does everywhere a DateTime comes back from the
        // database; see BusinessCalendar.ToLocal for why that matters.
        DateTime utc = anyInstantInMonth.Kind == DateTimeKind.Local
            ? anyInstantInMonth.ToUniversalTime()
            : DateTime.SpecifyKind(anyInstantInMonth, DateTimeKind.Utc);

        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, Support.BusinessCalendar.SwedishTime);

        DateTime firstLocal = new(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        DateTime nextLocal = firstLocal.AddMonths(1);

        return (
            TimeZoneInfo.ConvertTimeToUtc(firstLocal, Support.BusinessCalendar.SwedishTime),
            TimeZoneInfo.ConvertTimeToUtc(nextLocal, Support.BusinessCalendar.SwedishTime));
    }

    /// <summary>
    /// The Swedish calendar day an instant falls on. Through the calendar's own converter
    /// rather than a second copy of the same three lines — a copy is a second place for
    /// the Kind to be read wrongly, which is a fault this codebase has already paid for.
    /// </summary>
    private static DateOnly LocalDay(DateTime instant) =>
        DateOnly.FromDateTime(Support.BusinessCalendar.ToLocal(instant));
}
