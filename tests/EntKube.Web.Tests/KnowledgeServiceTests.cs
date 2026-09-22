using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Knowledge;
using EntKube.Web.Services.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// What we know about an application — the half of §10.2 that is not monitoring.
///
/// <para>The knowledge fee is charged for keeping this current, so the properties worth
/// defending are that an edit does not destroy what was there before, that a review is a
/// distinct act from an edit, and that the gaps the agreement cares about are reported
/// before somebody needs them at three in the morning.</para>
/// </summary>
public class KnowledgeServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KnowledgeService knowledge;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public KnowledgeServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Records" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        knowledge = new KnowledgeService(factory, new ContractService(factory));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void GiveSupportWindow(SupportWindow window)
    {
        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppId = appId,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Complex,
            SupportWindow = window,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Reason = "On-boarding",
        });

        db.ApplicationContracts.Add(contract);
        db.SaveChanges();
    }

    private Task<KnowledgeSection> Write(
        KnowledgeSectionKind kind, string title, string body = "Text.") =>
        knowledge.SaveSectionAsync(tenantId, appId, null, kind, title, body, "nils", null);

    // ---- Sections and their history ----------------------------------------------------

    /// <summary>
    /// An edit keeps what the section said before. §19 hands this material over at
    /// off-boarding, and a runbook that cannot say when it changed is not evidence of
    /// anything at a quarterly review.
    /// </summary>
    [Fact]
    public async Task Editing_a_section_keeps_the_previous_version()
    {
        KnowledgeSection section = await Write(KnowledgeSectionKind.Runbook, "Runbook", "First.");

        await knowledge.SaveSectionAsync(
            tenantId, appId, section.Id, KnowledgeSectionKind.Runbook, "Runbook", "Second.",
            "nils", "Corrected the restore order.");

        KnowledgeSection? loaded = await knowledge.GetSectionAsync(section.Id);

        loaded!.Body.Should().Be("Second.");
        loaded.Revisions.Should().ContainSingle();
        loaded.Revisions[0].Body.Should().Be("First.");
        loaded.Revisions[0].Summary.Should().Be("Corrected the restore order.");
    }

    [Fact]
    public async Task Saving_the_same_text_does_not_add_a_revision()
    {
        KnowledgeSection section = await Write(KnowledgeSectionKind.Runbook, "Runbook", "Same.");

        await knowledge.SaveSectionAsync(
            tenantId, appId, section.Id, KnowledgeSectionKind.Runbook, "Runbook", "Same.",
            "nils", null);

        KnowledgeSection? loaded = await knowledge.GetSectionAsync(section.Id);

        loaded!.Revisions.Should().BeEmpty();
    }

    /// <summary>
    /// Confirming a section is still true is a separate act from editing it. Fixing a typo
    /// is not a review, and the fee is paid for the confirmation.
    /// </summary>
    [Fact]
    public async Task A_review_is_recorded_separately_from_an_edit()
    {
        KnowledgeSection section = await Write(KnowledgeSectionKind.Architecture, "Architecture");

        await knowledge.MarkReviewedAsync(section.Id, "anna", Now);

        KnowledgeSection? loaded = await knowledge.GetSectionAsync(section.Id);

        loaded!.ReviewedAt.Should().Be(Now);
        loaded.ReviewedBy.Should().Be("anna");
        loaded.Revisions.Should().BeEmpty();
    }

    // ---- The gaps the agreement cares about ---------------------------------------------

    /// <summary>
    /// A runbook is a named deliverable of on-boarding fas 4 and of off-boarding under
    /// §19. Its absence is the most expensive thing on this list at three in the morning.
    /// </summary>
    [Fact]
    public async Task A_missing_runbook_is_critical()
    {
        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().Contain(g =>
            g.Severity == KnowledgeGapSeverity.Critical && g.Title.Contains("Runbook"));
    }

    [Fact]
    public async Task Writing_the_expected_sections_clears_those_gaps()
    {
        await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await Write(KnowledgeSectionKind.Architecture, "Architecture");

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("No Runbook"));
        gaps.Should().NotContain(g => g.Title.Contains("No Architecture"));
    }

    /// <summary>
    /// §10.2 charges for keeping this current. A section nobody has confirmed for a quarter
    /// is the first thing questioned when the fee is.
    /// </summary>
    [Fact]
    public async Task A_section_nobody_has_reviewed_for_a_quarter_is_stale()
    {
        KnowledgeSection section = await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await knowledge.MarkReviewedAsync(section.Id, "nils", Now.AddDays(-120));

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().Contain(g => g.Title.Contains("has not been reviewed"));
    }

    [Fact]
    public async Task A_recently_reviewed_section_is_not_stale()
    {
        KnowledgeSection section = await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await knowledge.MarkReviewedAsync(section.Id, "nils", Now.AddDays(-10));

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("has not been reviewed"));
    }

    /// <summary>
    /// §23: choosing a window wider than your own dependencies can be reached in means no
    /// guaranteed resolution time. Worth saying before an incident rather than after one.
    /// </summary>
    [Fact]
    public async Task An_office_hours_dependency_under_a_wide_window_is_critical()
    {
        GiveSupportWindow(SupportWindow.S4);

        await knowledge.SaveDependencyAsync(new AppServiceDependency
        {
            TenantId = tenantId,
            AppId = appId,
            Name = "Regional identity provider",
            Kind = DependencyKind.Infrastructure,
            OfficeHoursOnly = true,
            CriticalPath = true,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().Contain(g =>
            g.Severity == KnowledgeGapSeverity.Critical && g.Title.Contains("office-hours only"));
    }

    [Fact]
    public async Task The_same_dependency_under_S1_is_not_a_problem()
    {
        GiveSupportWindow(SupportWindow.S1);

        await knowledge.SaveDependencyAsync(new AppServiceDependency
        {
            TenantId = tenantId,
            AppId = appId,
            Name = "Regional identity provider",
            Kind = DependencyKind.Infrastructure,
            OfficeHoursOnly = true,
            CriticalPath = true,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("office-hours only"));
    }

    [Fact]
    public async Task A_dependency_off_the_critical_path_does_not_warn()
    {
        GiveSupportWindow(SupportWindow.S4);

        await knowledge.SaveDependencyAsync(new AppServiceDependency
        {
            TenantId = tenantId,
            AppId = appId,
            Name = "Reporting tool",
            Kind = DependencyKind.Integration,
            OfficeHoursOnly = true,
            CriticalPath = false,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("office-hours only"));
    }

    // ---- End of life (§14.7) ----------------------------------------------------------------

    /// <summary>
    /// §14.7's exemption starts three months after written notice, if no upgrade was
    /// ordered. Before that it is not available and afterwards it is.
    /// </summary>
    [Theory]
    [InlineData(-100, true)]
    [InlineData(-80, false)]
    public void The_exemption_starts_three_months_after_the_notice(int noticeDaysAgo, bool applies)
    {
        EndOfLifeNotice notice = new()
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            NoticeGivenAt = Now.AddDays(noticeDaysAgo),
        };

        KnowledgeService.ExemptionApplies(notice, Now).Should().Be(applies);
    }

    [Fact]
    public void Ordering_the_upgrade_stops_the_exemption_arising()
    {
        EndOfLifeNotice notice = new()
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            NoticeGivenAt = Now.AddDays(-200),
            UpgradeOrderedAt = Now.AddDays(-30),
        };

        KnowledgeService.ExemptionApplies(notice, Now).Should().BeFalse();
    }

    [Fact]
    public void Without_a_notice_there_is_no_exemption_however_old_the_component()
    {
        EndOfLifeNotice notice = new()
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            EndOfLifeOn = Now.AddYears(-2),
        };

        KnowledgeService.ExemptionApplies(notice, Now).Should().BeFalse();
    }

    [Fact]
    public async Task An_unnotified_end_of_life_component_is_reported_as_a_gap()
    {
        await knowledge.SaveEndOfLifeAsync(new EndOfLifeNotice
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            EndOfLifeOn = Now.AddMonths(-3),
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().Contain(g => g.Title.Contains("no notice has been given"));
    }

    [Fact]
    public async Task An_expired_notice_period_is_reported_as_critical()
    {
        await knowledge.SaveEndOfLifeAsync(new EndOfLifeNotice
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            NoticeGivenAt = Now.AddMonths(-4),
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().Contain(g =>
            g.Severity == KnowledgeGapSeverity.Critical && g.Clause == "§14.7");
    }

    [Fact]
    public async Task An_upgraded_component_is_not_reported()
    {
        await knowledge.SaveEndOfLifeAsync(new EndOfLifeNotice
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            NoticeGivenAt = Now.AddMonths(-6),
            UpgradedAt = Now.AddDays(-5),
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().NotContain(g => g.Clause == "§14.7");
    }

    // ---- Classification -------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_classification_is_reported()
    {
        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().Contain(g => g.Title.Contains("No classification"));
    }

    [Fact]
    public async Task Recording_the_classification_clears_it()
    {
        await knowledge.SaveProfileAsync(new AppKnowledgeProfile
        {
            TenantId = tenantId,
            AppId = appId,
            DataClassification = DataClassification.SensitivePersonal,
            HandlesPatientData = true,
            RegulatoryScope = "Patientdatalagen",
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("No classification"));
    }

    [Fact]
    public async Task Saving_a_profile_twice_updates_rather_than_duplicates()
    {
        await knowledge.SaveProfileAsync(new AppKnowledgeProfile
        {
            TenantId = tenantId, AppId = appId, DataClassification = DataClassification.Personal,
        });

        await knowledge.SaveProfileAsync(new AppKnowledgeProfile
        {
            TenantId = tenantId, AppId = appId,
            DataClassification = DataClassification.SensitivePersonal, HandlesPatientData = true,
        });

        AppKnowledgeProfile? profile = await knowledge.GetProfileAsync(appId);

        profile!.DataClassification.Should().Be(DataClassification.SensitivePersonal);
        profile.HandlesPatientData.Should().BeTrue();
        db.AppKnowledgeProfiles.Count(p => p.AppId == appId).Should().Be(1);
    }

    // ---- Health ------------------------------------------------------------------------

    /// <summary>
    /// The figure at the top of the panel and the list underneath it come from the same
    /// gaps, so they cannot say different things.
    /// </summary>
    [Fact]
    public async Task Health_counts_the_same_checks_the_gaps_report()
    {
        KnowledgeHealth health = await knowledge.GetHealthAsync(appId, Now);

        health.FailedChecks.Should().Be(health.Gaps.Select(g => g.Check).Distinct().Count());
        health.PassedChecks.Should().Be(KnowledgeHealth.TotalChecks - health.FailedChecks);
    }

    /// <summary>
    /// Several stale sections are one failed check, not several — otherwise a well-kept
    /// application with three sections would score worse than an empty one with none.
    /// </summary>
    [Fact]
    public async Task Many_gaps_of_one_kind_are_one_failed_check()
    {
        KnowledgeSection runbook = await Write(KnowledgeSectionKind.Runbook, "Runbook");
        KnowledgeSection architecture = await Write(KnowledgeSectionKind.Architecture, "Architecture");
        await knowledge.MarkReviewedAsync(runbook.Id, "nils", Now.AddDays(-200));
        await knowledge.MarkReviewedAsync(architecture.Id, "nils", Now.AddDays(-200));

        KnowledgeHealth health = await knowledge.GetHealthAsync(appId, Now);

        health.Gaps.Count(g => g.Check == KnowledgeCheck.Freshness).Should().Be(2);
        health.FailedChecks.Should().Be(2, "staleness and the missing classification");
    }

    [Fact]
    public async Task A_fully_kept_application_passes_everything()
    {
        await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await Write(KnowledgeSectionKind.Architecture, "Architecture");
        await knowledge.SaveProfileAsync(new AppKnowledgeProfile
        {
            TenantId = tenantId, AppId = appId,
            DataClassification = DataClassification.SensitivePersonal, HandlesPatientData = true,
        });

        KnowledgeHealth health = await knowledge.GetHealthAsync(appId, Now);

        health.Gaps.Should().BeEmpty();
        health.Percent.Should().Be(100);
    }

    [Fact]
    public async Task An_empty_application_does_not_score_zero_for_checks_nothing_can_fail()
    {
        KnowledgeHealth health = await knowledge.GetHealthAsync(appId, Now);

        // Nothing written, no classification — but no dependency or end-of-life problem
        // either, because there is nothing to have a problem with.
        health.FailedChecks.Should().Be(3);
        health.Percent.Should().Be(50);
    }

    [Fact]
    public async Task Gaps_are_ordered_worst_first()
    {
        GiveSupportWindow(SupportWindow.S4);
        await knowledge.SaveDependencyAsync(new AppServiceDependency
        {
            TenantId = tenantId, AppId = appId, Name = "IdP",
            Kind = DependencyKind.Infrastructure, OfficeHoursOnly = true,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(appId, Now);

        gaps.Should().HaveCountGreaterThan(1);
        gaps.Should().BeInAscendingOrder(g => g.Severity);
        gaps[0].Severity.Should().Be(KnowledgeGapSeverity.Critical);
    }
}
