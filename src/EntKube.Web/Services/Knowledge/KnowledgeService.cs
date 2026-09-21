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

/// <summary>One thing wrong with what we know about an application.</summary>
/// <param name="Severity">How serious.</param>
/// <param name="Title">What is wrong, in a few words.</param>
/// <param name="Detail">Why it matters, with the clause that says so.</param>
/// <param name="Clause">The section of the agreement behind it.</param>
public readonly record struct KnowledgeGap(
    KnowledgeGapSeverity Severity,
    string Title,
    string Detail,
    string Clause);

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

    public async Task<List<KnowledgeSection>> GetSectionsAsync(
        Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.KnowledgeSections.AsNoTracking()
            .Where(s => s.AppId == appId)
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Title)
            .ToListAsync(ct);
    }

    public async Task<KnowledgeSection?> GetSectionAsync(Guid sectionId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.KnowledgeSections
            .Include(s => s.Revisions.OrderByDescending(r => r.SavedAt))
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sectionId, ct);
    }

    /// <summary>
    /// Saves a section, keeping what it said before.
    ///
    /// <para>The previous body becomes a revision rather than being overwritten. A
    /// runbook that changed and cannot say when or why is not evidence of anything at
    /// the next review — and §19 hands this material over at off-boarding, where its
    /// history is part of what is handed over.</para>
    /// </summary>
    public async Task<KnowledgeSection> SaveSectionAsync(
        Guid tenantId,
        Guid appId,
        Guid? sectionId,
        KnowledgeSectionKind kind,
        string title,
        string body,
        string? author,
        string? changeSummary,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        KnowledgeSection? section = sectionId is Guid id
            ? await db.KnowledgeSections.FirstOrDefaultAsync(s => s.Id == id, ct)
            : null;

        if (section is null)
        {
            section = new KnowledgeSection
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                AppId = appId,
                Kind = kind,
                Title = title,
                Body = body,
                UpdatedBy = author,
                ReviewedAt = DateTime.UtcNow,
                ReviewedBy = author,
            };

            db.KnowledgeSections.Add(section);
            await db.SaveChangesAsync(ct);

            return section;
        }

        if (section.Body != body)
        {
            db.KnowledgeRevisions.Add(new KnowledgeRevision
            {
                Id = Guid.NewGuid(),
                SectionId = section.Id,
                Body = section.Body,
                SavedAt = DateTime.UtcNow,
                SavedBy = author,
                Summary = changeSummary,
            });
        }

        section.Kind = kind;
        section.Title = title;
        section.Body = body;
        section.UpdatedAt = DateTime.UtcNow;
        section.UpdatedBy = author;

        await db.SaveChangesAsync(ct);
        return section;
    }

    /// <summary>
    /// Records that someone has confirmed a section is still true. Separate from editing it
    /// on purpose: fixing a typo is not a review, and the fee is paid for the confirmation.
    /// </summary>
    public async Task MarkReviewedAsync(
        Guid sectionId, string? reviewer, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        KnowledgeSection? section = await db.KnowledgeSections
            .FirstOrDefaultAsync(s => s.Id == sectionId, ct);

        if (section is not null)
        {
            section.ReviewedAt = at;
            section.ReviewedBy = reviewer;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteSectionAsync(Guid sectionId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        KnowledgeSection? section = await db.KnowledgeSections.FindAsync([sectionId], ct);
        if (section is not null)
        {
            db.KnowledgeSections.Remove(section);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<AppKnowledgeProfile?> GetProfileAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.AppKnowledgeProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AppId == appId, ct);
    }

    public async Task<AppKnowledgeProfile> SaveProfileAsync(
        AppKnowledgeProfile edited, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        AppKnowledgeProfile? existing = await db.AppKnowledgeProfiles
            .FirstOrDefaultAsync(p => p.AppId == edited.AppId, ct);

        if (existing is null)
        {
            edited.Id = edited.Id == Guid.Empty ? Guid.NewGuid() : edited.Id;
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
        Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.AppServiceDependencies.AsNoTracking()
            .Where(d => d.AppId == appId)
            .OrderByDescending(d => d.CriticalPath)
            .ThenBy(d => d.Name)
            .ToListAsync(ct);
    }

    public async Task<AppServiceDependency> SaveDependencyAsync(
        AppServiceDependency dependency, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        dependency.UpdatedAt = DateTime.UtcNow;

        if (dependency.Id == Guid.Empty)
        {
            dependency.Id = Guid.NewGuid();
            db.AppServiceDependencies.Add(dependency);
        }
        else
        {
            db.AppServiceDependencies.Update(dependency);
        }

        await db.SaveChangesAsync(ct);
        return dependency;
    }

    public async Task DeleteDependencyAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        AppServiceDependency? dependency = await db.AppServiceDependencies.FindAsync([id], ct);
        if (dependency is not null)
        {
            db.AppServiceDependencies.Remove(dependency);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<List<EndOfLifeNotice>> GetEndOfLifeAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.EndOfLifeNotices.AsNoTracking()
            .Where(n => n.AppId == appId)
            .OrderBy(n => n.UpgradedAt != null)
            .ThenBy(n => n.EndOfLifeOn)
            .ToListAsync(ct);
    }

    public async Task<EndOfLifeNotice> SaveEndOfLifeAsync(
        EndOfLifeNotice notice, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        notice.UpdatedAt = DateTime.UtcNow;

        if (notice.Id == Guid.Empty)
        {
            notice.Id = Guid.NewGuid();
            db.EndOfLifeNotices.Add(notice);
        }
        else
        {
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
    /// What is missing or has lapsed in what we know about an application.
    ///
    /// <para>These are the things the agreement assumes are true and nobody checks until
    /// they are needed — usually at 03:00, or at a quarterly review where the
    /// knowledge fee is being questioned.</para>
    /// </summary>
    public async Task<List<KnowledgeGap>> GetGapsAsync(
        Guid appId, DateTime asOf, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<KnowledgeGap> gaps = [];

        List<KnowledgeSection> sections = await db.KnowledgeSections.AsNoTracking()
            .Where(s => s.AppId == appId)
            .ToListAsync(ct);

        foreach (KnowledgeSectionKind expected in ExpectedSections)
        {
            if (!sections.Any(s => s.Kind == expected))
            {
                gaps.Add(new KnowledgeGap(
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

        foreach (KnowledgeSection section in sections)
        {
            DateTime due = (section.ReviewedAt ?? section.CreatedAt).AddDays(section.ReviewIntervalDays);

            if (due <= asOf)
            {
                gaps.Add(new KnowledgeGap(
                    KnowledgeGapSeverity.Warning,
                    $"{section.Title} has not been reviewed since "
                    + $"{(section.ReviewedAt ?? section.CreatedAt):yyyy-MM-dd}",
                    "The knowledge fee is charged for keeping this current. A section nobody has "
                    + "confirmed in months is the first thing to be questioned at a quarterly review.",
                    "§10.2, §16.2"));
            }
        }

        // §23: a support window wider than the dependencies can be reached in.
        ResolvedServiceLevel? level = await contracts.ResolveServiceLevelAsync(appId, asOf, ct);

        if (level?.Window is SupportWindow window and > SupportWindow.S1)
        {
            List<AppServiceDependency> officeHours = await db.AppServiceDependencies.AsNoTracking()
                .Where(d => d.AppId == appId && d.OfficeHoursOnly && d.CriticalPath)
                .ToListAsync(ct);

            foreach (AppServiceDependency dependency in officeHours)
            {
                gaps.Add(new KnowledgeGap(
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
            .Where(n => n.AppId == appId && n.UpgradedAt == null)
            .ToListAsync(ct);

        foreach (EndOfLifeNotice notice in notices)
        {
            if (ExemptionApplies(notice, asOf))
            {
                gaps.Add(new KnowledgeGap(
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
                    KnowledgeGapSeverity.Warning,
                    $"{notice.Component} is out of support and no notice has been given",
                    "§14.7's protection starts from written notice with a proposed upgrade. Until "
                    + "that is sent, the clock has not started and the exemption is not available.",
                    "§14.7"));
            }
        }

        if (await db.AppKnowledgeProfiles.AsNoTracking().AllAsync(p => p.AppId != appId, ct))
        {
            gaps.Add(new KnowledgeGap(
                KnowledgeGapSeverity.Warning,
                "No classification recorded",
                "Whether this holds personal or patient data decides how access to it is handled "
                + "under §24, and §23 makes the regulatory assessment the customer's to state.",
                "§23, §24"));
        }

        return [.. gaps.OrderBy(g => g.Severity)];
    }
}
