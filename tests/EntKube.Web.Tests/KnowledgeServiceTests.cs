using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Knowledge;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
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
///
/// <para>Since the knowledge merge the written sections are the application's
/// knowledge-base answers, so those are written through <see cref="KnowledgeBaseService"/>
/// here; what is asked of them — history, review, gaps, tenancy — has not changed.</para>
/// </summary>
public class KnowledgeServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KnowledgeService knowledge;
    private readonly KnowledgeBaseService kb;

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

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Records" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        knowledge = new KnowledgeService(factory, new ContractService(factory));
        kb = new KnowledgeBaseService(factory);
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

    /// <summary>Writes a section of this application: an agents-only answer of the knowledge base.</summary>
    private async Task<KbAnswer> Write(
        KnowledgeSectionKind kind, string title, string body = "Text.") =>
        await kb.SaveAnswerAsync(tenantId, new KbAnswer
        {
            Title = title, BodyHtml = $"<p>{body}</p>", AppId = appId, Section = kind,
            Visibility = KbVisibility.Operations, Language = "sv", CategoryId = await kb.EnsureAppCategoryAsync(tenantId),
        }, "nils");

    /// <summary>The same section with a new text, as the editor would send it.</summary>
    private static KbAnswer Edited(KbAnswer section, string body) => new()
    {
        Id = section.Id, Title = section.Title, AppId = section.AppId, Section = section.Section,
        Visibility = section.Visibility, Language = section.Language, CategoryId = section.CategoryId,
        BodyHtml = $"<p>{body}</p>",
    };

    // ---- Sections and their history ----------------------------------------------------

    /// <summary>
    /// An edit keeps what the section said before. §19 hands this material over at
    /// off-boarding, and a runbook that cannot say when it changed is not evidence of
    /// anything at a quarterly review.
    /// </summary>
    [Fact]
    public async Task Editing_a_section_keeps_the_previous_version()
    {
        KbAnswer section = await Write(KnowledgeSectionKind.Runbook, "Runbook", "First.");

        await kb.SaveAnswerAsync(tenantId, Edited(section, "Second."), "nils", default, "Corrected the restore order.");

        KbAnswer? loaded = await kb.GetAsync(tenantId, section.Id);
        List<KbAnswerRevision> revisions = await kb.RevisionsAsync(tenantId, section.Id);

        loaded!.BodyHtml.Should().Be("<p>Second.</p>");
        revisions.Should().ContainSingle();
        revisions[0].Body.Should().Be("<p>First.</p>");
        revisions[0].Summary.Should().Be("Corrected the restore order.");
    }

    [Fact]
    public async Task Saving_the_same_text_does_not_add_a_revision()
    {
        KbAnswer section = await Write(KnowledgeSectionKind.Runbook, "Runbook", "Same.");

        await kb.SaveAnswerAsync(tenantId, Edited(section, "Same."), "nils");

        (await kb.RevisionsAsync(tenantId, section.Id)).Should().BeEmpty();
    }

    /// <summary>
    /// Confirming a section is still true is a separate act from editing it. Fixing a typo
    /// is not a review, and the fee is paid for the confirmation.
    /// </summary>
    [Fact]
    public async Task A_review_is_recorded_separately_from_an_edit()
    {
        KbAnswer section = await Write(KnowledgeSectionKind.Architecture, "Architecture");

        await kb.MarkReviewedAsync(tenantId, section.Id, "anna", Now);

        KbAnswer? loaded = await kb.GetAsync(tenantId, section.Id);

        loaded!.ReviewedAt.Should().Be(Now);
        loaded.ReviewedBy.Should().Be("anna");
        loaded.UpdatedAt.Should().Be(section.UpdatedAt, "a review changes no words");
        (await kb.RevisionsAsync(tenantId, section.Id)).Should().BeEmpty();
    }

    // ---- The gaps the agreement cares about ---------------------------------------------

    /// <summary>
    /// A runbook is a named deliverable of on-boarding fas 4 and of off-boarding under
    /// §19. Its absence is the most expensive thing on this list at three in the morning.
    /// </summary>
    [Fact]
    public async Task A_missing_runbook_is_critical()
    {
        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().Contain(g =>
            g.Severity == KnowledgeGapSeverity.Critical && g.Title.Contains("Runbook"));
    }

    [Fact]
    public async Task Writing_the_expected_sections_clears_those_gaps()
    {
        await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await Write(KnowledgeSectionKind.Architecture, "Architecture");

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

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
        KbAnswer section = await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await kb.MarkReviewedAsync(tenantId, section.Id, "nils", Now.AddDays(-120));

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().Contain(g => g.Title.Contains("has not been reviewed"));
    }

    [Fact]
    public async Task A_recently_reviewed_section_is_not_stale()
    {
        KbAnswer section = await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await kb.MarkReviewedAsync(tenantId, section.Id, "nils", Now.AddDays(-10));

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

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

        await knowledge.SaveDependencyAsync(tenantId, new AppServiceDependency
        {
            TenantId = tenantId,
            AppId = appId,
            Name = "Regional identity provider",
            Kind = DependencyKind.Infrastructure,
            OfficeHoursOnly = true,
            CriticalPath = true,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().Contain(g =>
            g.Severity == KnowledgeGapSeverity.Critical && g.Title.Contains("office-hours only"));
    }

    [Fact]
    public async Task The_same_dependency_under_S1_is_not_a_problem()
    {
        GiveSupportWindow(SupportWindow.S1);

        await knowledge.SaveDependencyAsync(tenantId, new AppServiceDependency
        {
            TenantId = tenantId,
            AppId = appId,
            Name = "Regional identity provider",
            Kind = DependencyKind.Infrastructure,
            OfficeHoursOnly = true,
            CriticalPath = true,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("office-hours only"));
    }

    [Fact]
    public async Task A_dependency_off_the_critical_path_does_not_warn()
    {
        GiveSupportWindow(SupportWindow.S4);

        await knowledge.SaveDependencyAsync(tenantId, new AppServiceDependency
        {
            TenantId = tenantId,
            AppId = appId,
            Name = "Reporting tool",
            Kind = DependencyKind.Integration,
            OfficeHoursOnly = true,
            CriticalPath = false,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

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
        await knowledge.SaveEndOfLifeAsync(tenantId, new EndOfLifeNotice
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            EndOfLifeOn = Now.AddMonths(-3),
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().Contain(g => g.Title.Contains("no notice has been given"));
    }

    [Fact]
    public async Task An_expired_notice_period_is_reported_as_critical()
    {
        await knowledge.SaveEndOfLifeAsync(tenantId, new EndOfLifeNotice
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            NoticeGivenAt = Now.AddMonths(-4),
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().Contain(g =>
            g.Severity == KnowledgeGapSeverity.Critical && g.Clause == "§14.7");
    }

    [Fact]
    public async Task An_upgraded_component_is_not_reported()
    {
        await knowledge.SaveEndOfLifeAsync(tenantId, new EndOfLifeNotice
        {
            TenantId = tenantId,
            AppId = appId,
            Component = ".NET 6",
            NoticeGivenAt = Now.AddMonths(-6),
            UpgradedAt = Now.AddDays(-5),
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().NotContain(g => g.Clause == "§14.7");
    }

    // ---- Classification -------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_classification_is_reported()
    {
        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().Contain(g => g.Title.Contains("No classification"));
    }

    [Fact]
    public async Task Recording_the_classification_clears_it()
    {
        await knowledge.SaveProfileAsync(tenantId, new AppKnowledgeProfile
        {
            TenantId = tenantId,
            AppId = appId,
            DataClassification = DataClassification.SensitivePersonal,
            HandlesPatientData = true,
            RegulatoryScope = "Patientdatalagen",
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().NotContain(g => g.Title.Contains("No classification"));
    }

    [Fact]
    public async Task Saving_a_profile_twice_updates_rather_than_duplicates()
    {
        await knowledge.SaveProfileAsync(tenantId, new AppKnowledgeProfile
        {
            TenantId = tenantId, AppId = appId, DataClassification = DataClassification.Personal,
        });

        await knowledge.SaveProfileAsync(tenantId, new AppKnowledgeProfile
        {
            TenantId = tenantId, AppId = appId,
            DataClassification = DataClassification.SensitivePersonal, HandlesPatientData = true,
        });

        AppKnowledgeProfile? profile = await knowledge.GetProfileAsync(tenantId, appId);

        profile!.DataClassification.Should().Be(DataClassification.SensitivePersonal);
        profile.HandlesPatientData.Should().BeTrue();
        db.AppKnowledgeProfiles.Count(p => p.AppId == appId).Should().Be(1);
    }

    // ---- Health ------------------------------------------------------------------------

    /// <summary>
    /// Which checks a bare application fails — by name, not by count.
    ///
    /// <para>⚠️ <b>BOTH ASSERTIONS HERE USED TO BE TAUTOLOGIES</b>, and the test could not fail for
    /// any input. It read
    /// <c>FailedChecks.Should().Be(Gaps.Select(g =&gt; g.Check).Distinct().Count())</c> against a
    /// <c>FailedChecks</c> that <em>is</em> <c>Gaps.Select(g =&gt; g.Check).Distinct().Count()</c>,
    /// and <c>PassedChecks.Should().Be(TotalChecks - FailedChecks)</c> against a
    /// <c>PassedChecks</c> that <em>is</em> <c>TotalChecks - FailedChecks</c> — each the
    /// implementation restated, over the same object.</para>
    ///
    /// <para>Its stated purpose cannot be tested, which is why it ended up that way: the headline
    /// figure and the list underneath are both derived from the one <c>Gaps</c> field, so they
    /// cannot disagree by construction. There is nothing there to protect. The one mutation that
    /// could make them disagree — dropping the <c>Distinct()</c> — is already killed by
    /// <see cref="Many_gaps_of_one_kind_are_one_failed_check"/>, and this fixture's counts are
    /// already pinned with literals by
    /// <see cref="An_empty_application_does_not_score_zero_for_checks_nothing_can_fail"/>.</para>
    ///
    /// <para>So rather than delete it, it now asserts the thing neither sibling does: <b>which</b>
    /// three checks a bare application fails. A gap that reported the wrong
    /// <see cref="KnowledgeCheck"/> would keep the count at three and pass both of them.</para>
    /// </summary>
    [Fact]
    public async Task A_bare_application_fails_exactly_the_three_checks_it_can_fail()
    {
        KnowledgeHealth health = await knowledge.GetHealthAsync(tenantId, appId, Now);

        health.Gaps.Select(g => g.Check).Distinct().Should().BeEquivalentTo(
            new[] { KnowledgeCheck.Architecture, KnowledgeCheck.Runbook, KnowledgeCheck.Classification },
            "nothing is written and no classification is recorded, while Freshness, "
            + "DependencyHours and EndOfLife have nothing to be wrong about — a gap mislabelled "
            + "with another check keeps the count at three and is invisible to a count assertion");
    }

    /// <summary>
    /// Several stale sections are one failed check, not several — otherwise a well-kept
    /// application with three sections would score worse than an empty one with none.
    /// </summary>
    [Fact]
    public async Task Many_gaps_of_one_kind_are_one_failed_check()
    {
        KbAnswer runbook = await Write(KnowledgeSectionKind.Runbook, "Runbook");
        KbAnswer architecture = await Write(KnowledgeSectionKind.Architecture, "Architecture");
        await kb.MarkReviewedAsync(tenantId, runbook.Id, "nils", Now.AddDays(-200));
        await kb.MarkReviewedAsync(tenantId, architecture.Id, "nils", Now.AddDays(-200));

        KnowledgeHealth health = await knowledge.GetHealthAsync(tenantId, appId, Now);

        health.Gaps.Count(g => g.Check == KnowledgeCheck.Freshness).Should().Be(2);
        health.FailedChecks.Should().Be(2, "staleness and the missing classification");
    }

    [Fact]
    public async Task A_fully_kept_application_passes_everything()
    {
        await Write(KnowledgeSectionKind.Runbook, "Runbook");
        await Write(KnowledgeSectionKind.Architecture, "Architecture");
        await knowledge.SaveProfileAsync(tenantId, new AppKnowledgeProfile
        {
            TenantId = tenantId, AppId = appId,
            DataClassification = DataClassification.SensitivePersonal, HandlesPatientData = true,
        });

        KnowledgeHealth health = await knowledge.GetHealthAsync(tenantId, appId, Now);

        health.Gaps.Should().BeEmpty();
        health.Percent.Should().Be(100);
    }

    [Fact]
    public async Task An_empty_application_does_not_score_zero_for_checks_nothing_can_fail()
    {
        KnowledgeHealth health = await knowledge.GetHealthAsync(tenantId, appId, Now);

        // Nothing written, no classification — but no dependency or end-of-life problem
        // either, because there is nothing to have a problem with.
        health.FailedChecks.Should().Be(3);
        health.Percent.Should().Be(50);
    }

    [Fact]
    public async Task Gaps_are_ordered_worst_first()
    {
        GiveSupportWindow(SupportWindow.S4);
        await knowledge.SaveDependencyAsync(tenantId, new AppServiceDependency
        {
            TenantId = tenantId, AppId = appId, Name = "IdP",
            Kind = DependencyKind.Infrastructure, OfficeHoursOnly = true,
        });

        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, appId, Now);

        gaps.Should().HaveCountGreaterThan(1);
        gaps.Should().BeInAscendingOrder(g => g.Severity);
        gaps[0].Severity.Should().Be(KnowledgeGapSeverity.Critical);
    }
    // ---- Somebody else's application ---------------------------------------------------

    /// <summary>
    /// A second tenant with their own application, and one of everything written about it.
    /// Returned ids are the only thing a caller would need to have held to reach them.
    /// </summary>
    private (Guid Tenant, Guid App, Guid Section, Guid Dependency, Guid Notice) Stranger()
    {
        Guid strangerTenant = Guid.NewGuid();
        Guid strangerCustomer = Guid.NewGuid();
        Guid strangerApp = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = strangerTenant, Name = "Annan AB", Slug = $"annan-{strangerTenant:N}",
        });
        db.Customers.Add(new Customer
        {
            Id = strangerCustomer, TenantId = strangerTenant, Name = "Annan AB",
        });
        db.Apps.Add(new App { Id = strangerApp, CustomerId = strangerCustomer, Name = "Journal" });

        // Their runbook, in the knowledge base as every moved section is — and a second one
        // still waiting in the old store, so both places are tried.
        KbCategory category = new() { Id = Guid.NewGuid(), TenantId = strangerTenant, Name = "Applications" };
        KbAnswer section = new()
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            CategoryId = category.Id,
            AppId = strangerApp,
            CustomerId = strangerCustomer,
            Section = KnowledgeSectionKind.Runbook,
            Visibility = KbVisibility.Operations,
            Title = "Runbook",
            BodyHtml = "<p>Restore from the 02:00 snapshot, then start the API.</p>",
        };
        KnowledgeSection waiting = new()
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            AppId = strangerApp,
            Kind = KnowledgeSectionKind.Architecture,
            Title = "Journal architecture",
            Body = "Two API pods behind the gateway.",
            // Long unreviewed: were the legacy half of the meter to leak across tenants, this
            // would satisfy our Architecture check AND show up as a stale gap quoting its title.
            CreatedAt = Now.AddDays(-200),
        };

        AppServiceDependency dependency = new()
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            AppId = strangerApp,
            Name = "Region Skåne IdP",
            Kind = DependencyKind.Infrastructure,
        };

        EndOfLifeNotice notice = new()
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            AppId = strangerApp,
            Component = "PostgreSQL 11",
            NoticeGivenAt = Now.AddMonths(-6),
        };

        db.KbCategories.Add(category);
        db.KbAnswers.Add(section);
        db.KnowledgeSections.Add(waiting);
        db.AppServiceDependencies.Add(dependency);
        db.EndOfLifeNotices.Add(notice);
        db.AppKnowledgeProfiles.Add(new AppKnowledgeProfile
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            AppId = strangerApp,
            DataClassification = DataClassification.SensitivePersonal,
            HandlesPatientData = true,
        });
        db.SaveChanges();

        return (strangerTenant, strangerApp, section.Id, dependency.Id, notice.Id);
    }

    /// <summary>
    /// Nothing another tenant has written about their application is readable by naming its
    /// id — not the sections, not the dependencies, not the end-of-life notices, not the
    /// classification, and not the gap list, whose text quotes section titles and component
    /// names back at whoever asks.
    /// </summary>
    [Fact]
    public async Task Another_tenants_knowledge_is_not_readable()
    {
        (Guid strangerTenant, Guid strangerApp, Guid strangerSection, Guid _, Guid _) = Stranger();

        // The owner can see it, so the assertions below are about the tenant and not about
        // an empty database.
        (await kb.AnswersForAppAsync(tenantId, appId)).Should().BeEmpty();
        await Write(KnowledgeSectionKind.Runbook, "Ours");
        (await kb.AnswersForAppAsync(tenantId, appId)).Should().ContainSingle();
        (await knowledge.UnmovedSectionsAsync(strangerTenant, strangerApp))
            .Should().ContainSingle("their own view has the waiting section, so the empty answers below are about the tenant");

        (await kb.AnswersForAppAsync(tenantId, strangerApp)).Should().BeEmpty();
        (await knowledge.UnmovedSectionsAsync(tenantId, strangerApp)).Should().BeEmpty();
        (await kb.GetAsync(tenantId, strangerSection)).Should().BeNull();
        (await knowledge.GetDependenciesAsync(tenantId, strangerApp)).Should().BeEmpty();
        (await knowledge.GetEndOfLifeAsync(tenantId, strangerApp)).Should().BeEmpty();
        (await knowledge.GetProfileAsync(tenantId, strangerApp)).Should().BeNull();

        // Asked about their application we answer as if nothing had been written about it,
        // which is what we are entitled to know: their runbook does not satisfy the check,
        // their classification does not either, and their out-of-support PostgreSQL is not
        // quoted back at us.
        List<KnowledgeGap> gaps = await knowledge.GetGapsAsync(tenantId, strangerApp, Now);

        gaps.Should().Contain(g => g.Check == KnowledgeCheck.Runbook);
        gaps.Should().Contain(g => g.Check == KnowledgeCheck.Architecture, "their waiting legacy section does not satisfy our check");
        gaps.Should().NotContain(g => g.Check == KnowledgeCheck.Freshness, "nothing of ours is written, so nothing can be stale for us");
        gaps.Should().Contain(g => g.Check == KnowledgeCheck.Classification);
        gaps.Should().NotContain(g => g.Check == KnowledgeCheck.EndOfLife);
        gaps.Should().AllSatisfy(gap =>
        {
            gap.Title.Should().NotContain("PostgreSQL 11");
            gap.Title.Should().NotContain("Region Skåne IdP");
            gap.Title.Should().NotContain("Journal architecture");
        });
    }

    /// <summary>
    /// <b>Saving a section by an id that is not ours does not reach their row.</b> The old
    /// body would have gone into a revision and the runbook been replaced in place — under
    /// their application, with their history showing the edit as theirs.
    /// </summary>
    [Fact]
    public async Task Another_tenants_section_cannot_be_rewritten()
    {
        (Guid _, Guid _, Guid strangerSection, Guid _, Guid _) = Stranger();
        Guid ourCategory = await kb.EnsureAppCategoryAsync(tenantId);

        Func<Task> rewrite = () => kb.SaveAnswerAsync(tenantId, new KbAnswer
        {
            Id = strangerSection, Title = "Runbook", BodyHtml = "<p>Delete the volume, then page nobody.</p>",
            AppId = appId, Section = KnowledgeSectionKind.Runbook, Visibility = KbVisibility.Operations,
            Language = "sv", CategoryId = ourCategory,
        }, "nils", default, "Improved");

        await rewrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("This answer no longer exists*");

        KbAnswer theirs = await db.KbAnswers.AsNoTracking().FirstAsync(a => a.Id == strangerSection);

        theirs.BodyHtml.Should().StartWith("<p>Restore from the 02:00 snapshot");
        theirs.UpdatedBy.Should().BeNull();
        (await db.KbAnswerRevisions.AsNoTracking().AnyAsync(r => r.AnswerId == strangerSection)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_section_cannot_be_reviewed_or_deleted()
    {
        (Guid _, Guid _, Guid strangerSection, Guid _, Guid _) = Stranger();

        (await kb.MarkReviewedAsync(tenantId, strangerSection, "nils", Now)).Should().BeFalse();
        (await kb.DeleteAnswerAsync(tenantId, strangerSection)).Should().BeFalse();

        KbAnswer theirs = await db.KbAnswers.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == strangerSection)
            ?? throw new InvalidOperationException("The section was deleted.");

        theirs.ReviewedBy.Should().NotBe("nils");
    }

    /// <summary>
    /// <b>A dependency and an end-of-life notice were updated whole, detached, by Id.</b> So
    /// the Id alone said which row was rewritten and the entity said where it ended up — the
    /// row left the stranger's list and arrived in ours, carrying its history.
    /// </summary>
    [Fact]
    public async Task Another_tenants_dependency_cannot_be_rewritten_or_taken()
    {
        (Guid _, Guid _, Guid _, Guid strangerDependency, Guid _) = Stranger();

        Func<Task> take = () => knowledge.SaveDependencyAsync(tenantId, new AppServiceDependency
        {
            Id = strangerDependency,
            TenantId = tenantId,
            AppId = appId,
            Name = "Ours now",
            Kind = DependencyKind.Infrastructure,
        });

        await take.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Dependency not found, or it is not this tenant's.");

        AppServiceDependency theirs = await db.AppServiceDependencies.AsNoTracking()
            .FirstAsync(d => d.Id == strangerDependency);

        theirs.Name.Should().Be("Region Skåne IdP");
        theirs.AppId.Should().NotBe(appId);
        theirs.TenantId.Should().NotBe(tenantId);

        await knowledge.DeleteDependencyAsync(tenantId, strangerDependency);
        (await db.AppServiceDependencies.AsNoTracking()
            .AnyAsync(d => d.Id == strangerDependency)).Should().BeTrue();
    }

    /// <summary>
    /// §14.7's exemption runs from the date on this row, which is why being able to rewrite
    /// one by Id was being able to rewrite when somebody else's exemption started.
    /// </summary>
    [Fact]
    public async Task Another_tenants_end_of_life_notice_cannot_be_rewritten()
    {
        (Guid _, Guid _, Guid _, Guid _, Guid strangerNotice) = Stranger();

        Func<Task> rewrite = () => knowledge.SaveEndOfLifeAsync(tenantId, new EndOfLifeNotice
        {
            Id = strangerNotice,
            TenantId = tenantId,
            AppId = appId,
            Component = "PostgreSQL 11",
            NoticeGivenAt = null,
            UpgradedAt = Now,
        });

        await rewrite.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Notice not found, or it is not this tenant's.");

        EndOfLifeNotice theirs = await db.EndOfLifeNotices.AsNoTracking()
            .FirstAsync(n => n.Id == strangerNotice);

        theirs.NoticeGivenAt.Should().Be(Now.AddMonths(-6));
        theirs.UpgradedAt.Should().BeNull();
        theirs.AppId.Should().NotBe(appId);
    }

    /// <summary>
    /// The classification is the one row here keyed only by its application — AppId is
    /// unique — so naming somebody else's application was enough to overwrite theirs, and
    /// §24 limits access to patient systems to the ones that say they are one.
    /// </summary>
    [Fact]
    public async Task Another_tenants_classification_cannot_be_overwritten()
    {
        (Guid _, Guid strangerApp, Guid _, Guid _, Guid _) = Stranger();

        Func<Task> overwrite = () => knowledge.SaveProfileAsync(tenantId, new AppKnowledgeProfile
        {
            TenantId = tenantId,
            AppId = strangerApp,
            DataClassification = DataClassification.None,
            HandlesPatientData = false,
        });

        await overwrite.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application not found, or it is not this tenant's.");

        AppKnowledgeProfile theirs = await db.AppKnowledgeProfiles.AsNoTracking()
            .FirstAsync(p => p.AppId == strangerApp);

        theirs.HandlesPatientData.Should().BeTrue();
        theirs.DataClassification.Should().Be(DataClassification.SensitivePersonal);
    }

    /// <summary>
    /// A created row has no row to compare a tenant against, so the application is what is
    /// checked — otherwise a section, dependency or notice could be filed against somebody
    /// else's application and the tenant stamped on it would be ours.
    /// </summary>
    [Fact]
    public async Task Nothing_can_be_written_against_another_tenants_application()
    {
        (Guid _, Guid strangerApp, Guid _, Guid _, Guid _) = Stranger();

        Guid ourCategory = await kb.EnsureAppCategoryAsync(tenantId);

        Func<Task> section = () => kb.SaveAnswerAsync(tenantId, new KbAnswer
        {
            Title = "Ours", BodyHtml = "<p>Text.</p>", AppId = strangerApp, Section = KnowledgeSectionKind.Runbook,
            Visibility = KbVisibility.Operations, Language = "sv", CategoryId = ourCategory,
        }, "nils");

        Func<Task> dependency = () => knowledge.SaveDependencyAsync(
            tenantId, new AppServiceDependency
            {
                TenantId = tenantId, AppId = strangerApp, Name = "Ours",
                Kind = DependencyKind.Infrastructure,
            });

        Func<Task> notice = () => knowledge.SaveEndOfLifeAsync(
            tenantId, new EndOfLifeNotice
            {
                TenantId = tenantId, AppId = strangerApp, Component = "Ours",
            });

        await section.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application not found, or it is not this tenant's.");
        await dependency.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application not found, or it is not this tenant's.");
        await notice.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application not found, or it is not this tenant's.");

        (await db.KbAnswers.AsNoTracking().CountAsync(a => a.AppId == strangerApp)).Should().Be(1, "only their own runbook");
    }
    /// <summary>
    /// <b>The other direction: planting a row in somebody else's application.</b>
    ///
    /// <para>The row predicate answers "may I write this row", and once it says yes the
    /// detached <c>Update</c> writes every column — TenantId and AppId among them. So an
    /// authorized edit of our <i>own</i> row could still move it into another tenant's
    /// application. That is the inverse of reaching across for theirs, and no test that only
    /// tries to reach across can see it.</para>
    ///
    /// <para>For an end-of-life notice that is forging §14.7 evidence: a notice planted in
    /// their application, dated six months ago with no upgrade ordered, makes
    /// <see cref="KnowledgeService.ExemptionApplies"/> true — and §14.7 reads that as licence
    /// to exempt the application from the SLA times and from liability for security faults in
    /// that component. The row is the only thing that says the notice was given.</para>
    /// </summary>
    [Fact]
    public async Task Our_own_rows_cannot_be_moved_into_another_tenants_application()
    {
        (Guid strangerTenant, Guid strangerApp, Guid _, Guid _, Guid _) = Stranger();

        AppServiceDependency ours = await knowledge.SaveDependencyAsync(
            tenantId, new AppServiceDependency
            {
                TenantId = tenantId, AppId = appId, Name = "Planted dependency",
                Kind = DependencyKind.Infrastructure,
            });

        EndOfLifeNotice notice = await knowledge.SaveEndOfLifeAsync(
            tenantId, new EndOfLifeNotice
            {
                TenantId = tenantId, AppId = appId, Component = "Planted runtime",
            });

        // Authorized edits of our own rows — that name their tenant and their application.
        ours.TenantId = strangerTenant;
        ours.AppId = strangerApp;
        ours.Notes = "Edited";
        await knowledge.SaveDependencyAsync(tenantId, ours);

        notice.TenantId = strangerTenant;
        notice.AppId = strangerApp;
        notice.NoticeGivenAt = Now.AddMonths(-6);
        await knowledge.SaveEndOfLifeAsync(tenantId, notice);

        AppServiceDependency storedDependency = await db.AppServiceDependencies.AsNoTracking()
            .FirstAsync(d => d.Id == ours.Id);
        EndOfLifeNotice storedNotice = await db.EndOfLifeNotices.AsNoTracking()
            .FirstAsync(n => n.Id == notice.Id);

        // The edit landed; where the row lives did not move.
        storedDependency.Notes.Should().Be("Edited");
        storedDependency.TenantId.Should().Be(tenantId);
        storedDependency.AppId.Should().Be(appId);
        storedNotice.NoticeGivenAt.Should().Be(Now.AddMonths(-6));
        storedNotice.TenantId.Should().Be(tenantId);
        storedNotice.AppId.Should().Be(appId);

        // So nothing of ours shows up in their application, and in particular their §14.7
        // position is still the one their own notice gives them.
        (await knowledge.GetDependenciesAsync(strangerTenant, strangerApp))
            .Should().NotContain(d => d.Name == "Planted dependency");
        (await knowledge.GetEndOfLifeAsync(strangerTenant, strangerApp))
            .Should().NotContain(n => n.Component == "Planted runtime");
        (await knowledge.GetGapsAsync(strangerTenant, strangerApp, Now))
            .Should().NotContain(g => g.Title.Contains("Planted runtime"));
    }
}
