using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Knowledge;

/// <summary>How serious a gap in what we know about an application is.</summary>
public enum KnowledgeGapSeverity
{
    /// <summary>Something the agreement depends on is missing or has lapsed.</summary>
    Critical = 0,

    /// <summary>Worth fixing before it becomes the first one.</summary>
    Warning = 1,

    /// <summary>Worth knowing.</summary>
    Info = 2,
}

/// <summary>
/// The things that can be wrong with an application's knowledge. Each is one check, so a
/// completeness figure and the list of gaps are two views of the same answer rather than
/// two calculations that can drift apart.
/// </summary>
public enum KnowledgeCheck
{
    /// <summary>An architecture narrative exists.</summary>
    Architecture = 0,

    /// <summary>A runbook exists — §5.2 and §19 both name it as a deliverable.</summary>
    Runbook = 1,

    /// <summary>Every section has been confirmed inside its review interval (§10.2).</summary>
    Freshness = 2,

    /// <summary>No critical dependency is reachable for fewer hours than the window sold (§23).</summary>
    DependencyHours = 3,

    /// <summary>No component is out of support with its notice period run out (§14.7).</summary>
    EndOfLife = 4,

    /// <summary>The data and regulatory classification is recorded (§23, §24).</summary>
    Classification = 5,
}

/// <summary>One thing wrong with what we know about an application.</summary>
/// <param name="Check">Which check it failed.</param>
/// <param name="Severity">How serious.</param>
/// <param name="Title">What is wrong, in a few words.</param>
/// <param name="Detail">Why it matters, with the clause that says so.</param>
/// <param name="Clause">The section of the agreement behind it.</param>
public readonly record struct KnowledgeGap(
    KnowledgeCheck Check,
    KnowledgeGapSeverity Severity,
    string Title,
    string Detail,
    string Clause);

/// <summary>
/// How much of what the agreement assumes about an application is actually written down.
/// </summary>
/// <param name="Gaps">Everything wrong, worst first.</param>
public readonly record struct KnowledgeHealth(IReadOnlyList<KnowledgeGap> Gaps)
{
    /// <summary>Every check there is. A check nothing can fail counts as passed.</summary>
    public static int TotalChecks => Enum.GetValues<KnowledgeCheck>().Length;

    public int FailedChecks => Gaps.Select(g => g.Check).Distinct().Count();

    public int PassedChecks => TotalChecks - FailedChecks;

    public int Percent => (int)Math.Round(100.0 * PassedChecks / TotalChecks);

    public int Critical => Gaps.Count(g => g.Severity == KnowledgeGapSeverity.Critical);

    public int Warnings => Gaps.Count(g => g.Severity == KnowledgeGapSeverity.Warning);
}

/// <summary>
/// What we know about an application, and what we do not.
///
/// <para>§10.2 charges a knowledge fee per application for keeping its monitoring,
/// alarms, certificates and runbook current and the support team insatt in it. That
/// fee is defensible at a quarterly review only if someone can show the knowledge exists
/// and is current — so this both holds it and reports where it has lapsed.</para>
/// </summary>
public class KnowledgeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ContractService contracts)
{
    /// <summary>
    /// The sections §10.2 and §19 both assume exist. A runbook is named twice in the
    /// agreement; an architecture narrative is what makes the rest readable.
    /// </summary>
    public static readonly KnowledgeSectionKind[] ExpectedSections =
        [KnowledgeSectionKind.Architecture, KnowledgeSectionKind.Runbook];

    /// <summary>A section's name as both the application's panel and the knowledge base show it.</summary>
    public static string SectionName(KnowledgeSectionKind kind) => kind switch
    {
        KnowledgeSectionKind.Architecture => "Architecture",
        KnowledgeSectionKind.Runbook => "Runbook",
        KnowledgeSectionKind.Decisions => "Decisions",
        KnowledgeSectionKind.KnownDefects => "Known defects",
        KnowledgeSectionKind.Access => "Access",
        KnowledgeSectionKind.Recovery => "Backup and recovery",
        _ => "Notes",
    };

    /// <summary>How long a section stays "current" after it was last confirmed (§10.2).</summary>
    public const int ReviewIntervalDays = 90;

    /// <summary>
    /// Whether an application is this tenant's.
    ///
    /// <para>Every row here is keyed by an application. A read or an update has a row to
    /// compare a tenant against; a <i>create</i> does not yet, so the application is the only
    /// thing there is to check — and it is what the caller named.</para>
    /// </summary>
    private static Task<bool> AppIsTenantsAsync(
        ApplicationDbContext db, Guid tenantId, Guid appId, CancellationToken ct) =>
        db.Apps.AnyAsync(a => a.Id == appId && a.Customer.TenantId == tenantId, ct);

    /// <summary>
    /// An application's old runbook sections that have not moved into the knowledge base yet — no
    /// ledger row. Normally none: the mover runs at startup. Shown read-only, never edited here,
    /// because an edit to the old row is exactly what the mover would then have to reconcile.
    /// </summary>
    public async Task<List<KnowledgeSection>> UnmovedSectionsAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.KnowledgeSections.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.AppId == appId && !db.KnowledgeSectionMoves.Any(m => m.SectionId == s.Id))
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Title)
            .ToListAsync(ct);
    }

    // The old section editing (Get/Save/MarkReviewed/DeleteSection) went with the knowledge merge:
    // a section is now written, confirmed and deleted as a knowledge-base answer
    // (KnowledgeBaseService), and the old rows are only read — by the mover, the meter and the
    // read-only "not moved yet" list above — until release C drops them.

    public async Task<AppKnowledgeProfile?> GetProfileAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.AppKnowledgeProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AppId == appId && p.TenantId == tenantId, ct);
    }

    /// <param name="tenantId">
    /// Whose classification. Not read off <paramref name="edited"/>: the entity is the
    /// caller's to fill in, so a TenantId on it is a claim rather than an authorization — and
    /// it was the AppId on it that decided which row got written.
    /// </param>
    public async Task<AppKnowledgeProfile> SaveProfileAsync(
        Guid tenantId, AppKnowledgeProfile edited, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // The application rather than the row, for once. There is one profile per application
        // — the AppId index is unique — so the application is what says whose row this is,
        // and asking here covers the create as well as the edit.
        if (!await AppIsTenantsAsync(db, tenantId, edited.AppId, ct))
        {
            throw new InvalidOperationException(
                "Application not found, or it is not this tenant's.");
        }

        AppKnowledgeProfile? existing = await db.AppKnowledgeProfiles
            .FirstOrDefaultAsync(p => p.AppId == edited.AppId, ct);

        if (existing is null)
        {
            edited.Id = edited.Id == Guid.Empty ? Guid.NewGuid() : edited.Id;
            edited.TenantId = tenantId;
            edited.UpdatedAt = DateTime.UtcNow;
            db.AppKnowledgeProfiles.Add(edited);
            await db.SaveChangesAsync(ct);
            return edited;
        }

        existing.DataClassification = edited.DataClassification;
        existing.HandlesPatientData = edited.HandlesPatientData;
        existing.RegulatoryScope = edited.RegulatoryScope;
        existing.BackupResponsibility = edited.BackupResponsibility;
        existing.BusinessPurpose = edited.BusinessPurpose;
        existing.ImpactWhenDown = edited.ImpactWhenDown;
        existing.Notes = edited.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = edited.UpdatedBy;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<List<AppServiceDependency>> GetDependenciesAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.AppServiceDependencies.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.AppId == appId)
            .OrderByDescending(d => d.CriticalPath)
            .ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose dependency. A detached <c>Update</c> writes every column of whichever row the
    /// entity's Id names, so without this the Id alone decided what was rewritten — and the
    /// TenantId and AppId on the entity decided whose list it appeared in afterwards.
    /// </param>
    public async Task<AppServiceDependency> SaveDependencyAsync(
        Guid tenantId, AppServiceDependency dependency, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        dependency.UpdatedAt = DateTime.UtcNow;

        if (dependency.Id == Guid.Empty)
        {
            if (!await AppIsTenantsAsync(db, tenantId, dependency.AppId, ct))
            {
                throw new InvalidOperationException(
                    "Application not found, or it is not this tenant's.");
            }

            dependency.Id = Guid.NewGuid();
            dependency.TenantId = tenantId;
            db.AppServiceDependencies.Add(dependency);
        }
        else
        {
            AppServiceDependency stored = await db.AppServiceDependencies.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == dependency.Id && d.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException(
                    "Dependency not found, or it is not this tenant's.");

            // Where the row lives is the stored row's to say, not the entity's.
            dependency.TenantId = stored.TenantId;
            dependency.AppId = stored.AppId;
            dependency.CreatedAt = stored.CreatedAt;

            db.AppServiceDependencies.Update(dependency);
        }

        await db.SaveChangesAsync(ct);
        return dependency;
    }

    public async Task DeleteDependencyAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        AppServiceDependency? dependency = await db.AppServiceDependencies
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (dependency is not null)
        {
            db.AppServiceDependencies.Remove(dependency);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<List<EndOfLifeNotice>> GetEndOfLifeAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.EndOfLifeNotices.AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.AppId == appId)
            .OrderBy(n => n.UpgradedAt != null)
            .ThenBy(n => n.EndOfLifeOn)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose notice. This row <i>is</i> §14.7's evidence — the date written notice was given,
    /// and whether an upgrade was ordered — so being able to rewrite one by Id alone was
    /// being able to rewrite when somebody else's exemption started, or move the row out of
    /// their application altogether.
    /// </param>
    public async Task<EndOfLifeNotice> SaveEndOfLifeAsync(
        Guid tenantId, EndOfLifeNotice notice, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        notice.UpdatedAt = DateTime.UtcNow;

        if (notice.Id == Guid.Empty)
        {
            if (!await AppIsTenantsAsync(db, tenantId, notice.AppId, ct))
            {
                throw new InvalidOperationException(
                    "Application not found, or it is not this tenant's.");
            }

            notice.Id = Guid.NewGuid();
            notice.TenantId = tenantId;
            db.EndOfLifeNotices.Add(notice);
        }
        else
        {
            EndOfLifeNotice stored = await db.EndOfLifeNotices.AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == notice.Id && n.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException(
                    "Notice not found, or it is not this tenant's.");

            // Where the row lives is the stored row's to say, not the entity's.
            notice.TenantId = stored.TenantId;
            notice.AppId = stored.AppId;
            notice.CreatedAt = stored.CreatedAt;

            db.EndOfLifeNotices.Update(notice);
        }

        await db.SaveChangesAsync(ct);
        return notice;
    }

    /// <summary>
    /// Whether §14.7's exemption has come into force for a component: written notice was
    /// given more than three months ago, no upgrade was ordered, and it has not been done.
    /// </summary>
    public static bool ExemptionApplies(EndOfLifeNotice notice, DateTime asOf) =>
        notice.NoticeGivenAt is not null
        && notice.UpgradeOrderedAt is null
        && notice.UpgradedAt is null
        && notice.NoticeGivenAt.Value.AddMonths(3) <= asOf;

    /// <summary>
    /// How complete an application's knowledge is, from the same gaps the list shows —
    /// so the figure at the top and the items underneath it always agree.
    /// </summary>
    public async Task<KnowledgeHealth> GetHealthAsync(
        Guid tenantId, Guid appId, DateTime asOf, CancellationToken ct = default) =>
        new(await GetGapsAsync(tenantId, appId, asOf, ct));

    /// <summary>
    /// What is missing or has lapsed in what we know about an application.
    ///
    /// <para>These are the things the agreement assumes are true and nobody checks until
    /// they are needed — usually at 03:00, or at a quarterly review where the
    /// knowledge fee is being questioned.</para>
    /// </summary>
    /// <param name="tenantId">
    /// Whose application. The gaps are derived from its contract, so this has to ask for the
    /// contract as somebody — and the panel that shows them has always had a tenant.
    /// </param>
    public async Task<List<KnowledgeGap>> GetGapsAsync(
        Guid tenantId, Guid appId, DateTime asOf, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<KnowledgeGap> gaps = [];

        // Since the knowledge merge (phase 11) an application's sections are its knowledge-base
        // answers — archived ones do not count. A section not moved yet still counts from the old
        // store, so it is never reported as "not written" while it waits to move.
        List<(KnowledgeSectionKind Kind, string Title, DateTime? ReviewedAt, DateTime CreatedAt, int Interval)> sections =
        [
            .. (await db.KbAnswers.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.AppId == appId && a.Section != null && a.Visibility != KbVisibility.Archived)
                .Select(a => new { Kind = a.Section!.Value, a.Title, a.ReviewedAt, a.CreatedAt })
                .ToListAsync(ct)).Select(a => (a.Kind, a.Title, a.ReviewedAt, a.CreatedAt, ReviewIntervalDays)),
            .. (await db.KnowledgeSections.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.AppId == appId && !db.KnowledgeSectionMoves.Any(m => m.SectionId == s.Id))
                .Select(s => new { s.Kind, s.Title, s.ReviewedAt, s.CreatedAt, s.ReviewIntervalDays })
                .ToListAsync(ct)).Select(s => (s.Kind, s.Title, s.ReviewedAt, s.CreatedAt, s.ReviewIntervalDays)),
        ];

        foreach (KnowledgeSectionKind expected in ExpectedSections)
        {
            if (!sections.Any(s => s.Kind == expected))
            {
                gaps.Add(new KnowledgeGap(
                    expected == KnowledgeSectionKind.Runbook
                        ? KnowledgeCheck.Runbook
                        : KnowledgeCheck.Architecture,
                    expected == KnowledgeSectionKind.Runbook
                        ? KnowledgeGapSeverity.Critical
                        : KnowledgeGapSeverity.Warning,
                    $"No {expected} written",
                    expected == KnowledgeSectionKind.Runbook
                        ? "A runbook is a deliverable of on-boarding and of off-boarding, and it "
                          + "is what a jourhavande who has never seen this application reads first."
                        : "Without it, everything else here is harder to read than it needs to be.",
                    expected == KnowledgeSectionKind.Runbook ? "§5.2, §19" : "§10.2"));
            }
        }

        foreach (var section in sections)
        {
            DateTime due = (section.ReviewedAt ?? section.CreatedAt).AddDays(section.Interval);

            if (due <= asOf)
            {
                gaps.Add(new KnowledgeGap(
                    KnowledgeCheck.Freshness,
                    KnowledgeGapSeverity.Warning,
                    $"{section.Title} has not been reviewed since "
                    + $"{(section.ReviewedAt ?? section.CreatedAt):yyyy-MM-dd}",
                    "The knowledge fee is charged for keeping this current. A section nobody has "
                    + "confirmed in months is the first thing to be questioned at a quarterly review.",
                    "§10.2, §16.2"));
            }
        }

        // §23: a support window wider than the dependencies can be reached in.
        ResolvedServiceLevel? level = await contracts.ResolveServiceLevelAsync(tenantId, appId, asOf, ct);

        if (level?.Window is SupportWindow window and > SupportWindow.S1)
        {
            List<AppServiceDependency> officeHours = await db.AppServiceDependencies.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.AppId == appId
                    && d.OfficeHoursOnly && d.CriticalPath)
                .ToListAsync(ct);

            foreach (AppServiceDependency dependency in officeHours)
            {
                gaps.Add(new KnowledgeGap(
                    KnowledgeCheck.DependencyHours,
                    KnowledgeGapSeverity.Critical,
                    $"{dependency.Name} is office-hours only, but this application is on {window}",
                    "§23 says that where the customer chooses a wider window than their own "
                    + "dependencies can be reached in, no resolution time can be guaranteed — and §14.4 "
                    + "pauses the clock while we wait for them. Worth saying out loud before an "
                    + "incident, not after.",
                    "§23, §14.4"));
            }
        }

        // §14.7: components whose notice period has run out.
        List<EndOfLifeNotice> notices = await db.EndOfLifeNotices.AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.AppId == appId && n.UpgradedAt == null)
            .ToListAsync(ct);

        foreach (EndOfLifeNotice notice in notices)
        {
            if (ExemptionApplies(notice, asOf))
            {
                gaps.Add(new KnowledgeGap(
                    KnowledgeCheck.EndOfLife,
                    KnowledgeGapSeverity.Critical,
                    $"{notice.Component} is out of support and the notice period has run out",
                    "Three months have passed since written notice and no upgrade has been ordered. "
                    + "§14.7 lets us exempt this application from the SLA times and from liability "
                    + "for security faults in this component until it is done — the base fee is "
                    + "unaffected.",
                    "§14.7"));
            }
            else if (notice.NoticeGivenAt is null && notice.EndOfLifeOn <= asOf)
            {
                gaps.Add(new KnowledgeGap(
                    KnowledgeCheck.EndOfLife,
                    KnowledgeGapSeverity.Warning,
                    $"{notice.Component} is out of support and no notice has been given",
                    "§14.7's protection starts from written notice with a proposed upgrade. Until "
                    + "that is sent, the clock has not started and the exemption is not available.",
                    "§14.7"));
            }
        }

        if (!await db.AppKnowledgeProfiles.AsNoTracking()
                .AnyAsync(p => p.AppId == appId && p.TenantId == tenantId, ct))
        {
            gaps.Add(new KnowledgeGap(
                KnowledgeCheck.Classification,
                KnowledgeGapSeverity.Warning,
                "No classification recorded",
                "Whether this holds personal or patient data decides how access to it is handled "
                + "under §24, and §23 makes the regulatory assessment the customer's to state.",
                "§23, §24"));
        }

        return [.. gaps.OrderBy(g => g.Severity)];
    }
}
