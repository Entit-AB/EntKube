using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Bilaga A, B and C as data: what was agreed for an application, what the portfolio chose,
/// and what the grundavgift of §10 comes to.
///
/// <para>The properties these defend, in order of how expensive they are to get wrong: the
/// terms in force on a past date do not change when someone edits them today; the
/// fönsteravgift is charged once for the portfolio and priced on the most extensive window
/// anyone bought; and an application nobody classified is reported as unpriced rather than
/// charged at zero.</para>
/// </summary>
public class ContractModelTests : IDisposable
{
    private static readonly DateTime January = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime June = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ContractService contracts;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();

    public ContractModelTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.PriceLists.Add(StandardPriceList.Create(tenantId, January));
        db.SaveChanges();

        contracts = new ContractService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddApp(string name)
    {
        Guid appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = name });
        db.SaveChanges();
        return appId;
    }

    private ApplicationContract AddContract(
        Guid appId,
        ManagementLevel level,
        SupportWindow? window,
        DateTime? onboardedAt = null,
        Guid? parentAppId = null,
        DateTime? effectiveFrom = null)
    {
        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppId = appId,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = onboardedAt ?? January,
            ParentAppId = parentAppId,
        };

        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = level,
            SupportWindow = window,
            EffectiveFrom = effectiveFrom ?? onboardedAt ?? January,
            Reason = "On-boarding classification",
        });

        db.ApplicationContracts.Add(contract);
        db.SaveChanges();
        return contract;
    }

    // ---- §4.1, §16.2: the classification is a dated series -----------------------------

    /// <summary>
    /// A reclassification in June must not rewrite what March was charged on. This is the
    /// whole reason level and window are rows rather than columns.
    /// </summary>
    [Fact]
    public async Task A_later_reclassification_does_not_change_the_past()
    {
        Guid appId = AddApp("Journal");
        ApplicationContract contract = AddContract(appId, ManagementLevel.Standard, SupportWindow.S1);

        db.ApplicationServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Complex,
            SupportWindow = SupportWindow.S3,
            EffectiveFrom = June,
            Reason = "§4.1 — technical state differs from on-boarding",
        });
        await db.SaveChangesAsync();

        ResolvedServiceLevel? march = await contracts.ResolveServiceLevelAsync(appId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        ResolvedServiceLevel? july = await contracts.ResolveServiceLevelAsync(appId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        march!.Value.Level.Should().Be(ManagementLevel.Standard);
        march.Value.Window.Should().Be(SupportWindow.S1);
        july!.Value.Level.Should().Be(ManagementLevel.Complex);
        july.Value.Window.Should().Be(SupportWindow.S3);
    }

    [Fact]
    public async Task A_classification_that_has_not_taken_effect_yet_does_not_apply()
    {
        Guid appId = AddApp("Journal");
        AddContract(appId, ManagementLevel.Standard, SupportWindow.S1, effectiveFrom: June);

        ResolvedServiceLevel? inMarch = await contracts.ResolveServiceLevelAsync(
            appId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        inMarch.Should().BeNull();
    }

    // ---- §10.2.1: instances ------------------------------------------------------------

    /// <summary>
    /// An instance with no window of its own takes its moderapplikation's, which is what
    /// §10.2.1 makes the default.
    /// </summary>
    [Fact]
    public async Task An_instance_inherits_its_parents_support_window()
    {
        Guid parentId = AddApp("Vårdportal");
        AddContract(parentId, ManagementLevel.Complex, SupportWindow.S3);

        Guid instanceId = AddApp("Vårdportal — Region Syd");
        AddContract(instanceId, ManagementLevel.Instance, window: null, parentAppId: parentId);

        ResolvedServiceLevel? resolved = await contracts.ResolveServiceLevelAsync(instanceId, June);

        resolved!.Value.Window.Should().Be(SupportWindow.S3);
        resolved.Value.WindowInherited.Should().BeTrue();
        resolved.Value.Level.Should().Be(ManagementLevel.Instance);
    }

    [Fact]
    public async Task An_instance_that_states_its_own_window_keeps_it()
    {
        Guid parentId = AddApp("Vårdportal");
        AddContract(parentId, ManagementLevel.Complex, SupportWindow.S1);

        Guid instanceId = AddApp("Vårdportal — Region Nord");
        AddContract(instanceId, ManagementLevel.Instance, SupportWindow.S4, parentAppId: parentId);

        ResolvedServiceLevel? resolved = await contracts.ResolveServiceLevelAsync(instanceId, June);

        resolved!.Value.Window.Should().Be(SupportWindow.S4);
        resolved.Value.WindowInherited.Should().BeFalse();
    }

    /// <summary>
    /// §10.2.1 drops the kännedomsavgift from the twenty-first instance of one
    /// moderapplikation. The ordinal is by on-boarding date, so it does not move month to
    /// month.
    /// </summary>
    [Fact]
    public async Task The_twenty_first_instance_is_charged_the_reduced_fee()
    {
        Guid parentId = AddApp("Vårdportal");
        AddContract(parentId, ManagementLevel.Complex, SupportWindow.S1);

        for (int i = 1; i <= 22; i++)
        {
            Guid instanceId = AddApp($"Vårdportal — kund {i:00}");
            AddContract(
                instanceId, ManagementLevel.Instance, window: null, parentAppId: parentId,
                onboardedAt: January.AddDays(i));
        }

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.Unpriced.Should().BeEmpty();
        fee.Applications.Count(a => a.PriceKey == nameof(ManagementLevel.Instance)).Should().Be(20);
        fee.Applications.Count(a => a.PriceKey == ContractPricing.ReducedInstanceKey).Should().Be(2);

        // 9 000 for the parent, 20 × 500 and 2 × 350 for the instances.
        fee.KnowledgeFees.Should().Be(9_000m + (20 * 500m) + (2 * 350m));
    }

    // ---- §10.1: the fönsteravgift ------------------------------------------------------

    /// <summary>
    /// One fönsteravgift for the whole portfolio, priced on the most extensive window any
    /// application bought — not one per application, and not the commonest window.
    /// </summary>
    [Fact]
    public async Task The_window_fee_is_charged_once_on_the_most_extensive_window()
    {
        AddContract(AddApp("Enkel sajt"), ManagementLevel.Simple, SupportWindow.S1);
        AddContract(AddApp("Bokning"), ManagementLevel.Standard, SupportWindow.S1);
        AddContract(AddApp("Journal"), ManagementLevel.Complex, SupportWindow.S3);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.Window.Should().Be(SupportWindow.S3);
        fee.WindowFee.Should().Be(55_000m);
        fee.KnowledgeFees.Should().Be(1_500m + 4_000m + 9_000m);
        fee.Total.Should().Be(55_000m + 14_500m);
    }

    [Fact]
    public void The_most_extensive_window_of_nothing_is_nothing() =>
        ContractPricing.MostExtensiveWindow([]).Should().BeNull();

    // ---- §19: applications entering and leaving förvaltning ----------------------------

    [Fact]
    public async Task An_application_not_yet_onboarded_is_not_charged()
    {
        AddContract(AddApp("Journal"), ManagementLevel.Complex, SupportWindow.S1, onboardedAt: June);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        fee.Applications.Should().BeEmpty();
        fee.Total.Should().Be(0m);
    }

    [Fact]
    public async Task An_application_taken_out_of_management_stops_being_charged()
    {
        Guid appId = AddApp("Journal");
        ApplicationContract contract = AddContract(appId, ManagementLevel.Complex, SupportWindow.S1);
        contract.ManagementEndedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        BaseFeeBreakdown march = await contracts.CalculateBaseFeeAsync(
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        BaseFeeBreakdown june = await contracts.CalculateBaseFeeAsync(customerId, June);

        march.Applications.Should().ContainSingle();
        june.Applications.Should().BeEmpty();
    }

    // ---- Gaps are reported, never discounted -------------------------------------------

    /// <summary>
    /// §10.2 quotes Behöver undersökas after a technical review, so the standard list has no
    /// amount for it. An application sitting at that level has to surface as a gap: charging
    /// it at zero would hide an unbilled application indefinitely.
    /// </summary>
    [Fact]
    public async Task An_unclassified_application_is_reported_rather_than_charged_at_zero()
    {
        AddContract(AddApp("Nytt system"), ManagementLevel.NeedsInvestigation, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.Applications.Should().BeEmpty();
        fee.Unpriced.Should().ContainSingle().Which.Should().Contain("Nytt system");
        fee.Total.Should().Be(0m);
    }

    [Fact]
    public async Task An_application_with_no_window_anywhere_is_reported()
    {
        AddContract(AddApp("Föräldralös instans"), ManagementLevel.Instance, window: null);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.Unpriced.Should().ContainSingle().Which.Should().Contain("support window");
    }

    // ---- Bilaga B and C ----------------------------------------------------------------

    [Fact]
    public async Task The_portfolio_agreement_in_force_is_the_latest_one_that_has_started()
    {
        db.PortfolioAgreements.AddRange(
            new PortfolioAgreement
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                EffectiveFrom = January, PricingModel = PricingModel.TimeAndMaterials,
            },
            new PortfolioAgreement
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                EffectiveFrom = June, PricingModel = PricingModel.HourBank,
                HourBankHoursPerMonth = 20m,
            });
        await db.SaveChangesAsync();

        PortfolioAgreement? march = await contracts.GetPortfolioAgreementAsync(
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        PortfolioAgreement? july = await contracts.GetPortfolioAgreementAsync(
            customerId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        march!.PricingModel.Should().Be(PricingModel.TimeAndMaterials);
        july!.PricingModel.Should().Be(PricingModel.HourBank);
        july.HourBankHoursPerMonth.Should().Be(20m);
    }

    /// <summary>
    /// §19 indexes prices every January, and §13 moves every category with the ordinary
    /// rate. A statement covering a month before the new list must price at the old one.
    /// </summary>
    [Fact]
    public async Task A_new_price_list_does_not_reprice_earlier_months()
    {
        DateTime nextYear = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PriceList indexed = StandardPriceList.Create(tenantId, nextYear);
        indexed.Entries.Single(e => e.Kind == PriceKind.WindowFee && e.Key == nameof(SupportWindow.S1))
            .Amount = 6_120m;
        db.PriceLists.Add(indexed);
        await db.SaveChangesAsync();

        AddContract(AddApp("Bokning"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown before = await contracts.CalculateBaseFeeAsync(customerId, June);
        BaseFeeBreakdown after = await contracts.CalculateBaseFeeAsync(customerId, nextYear);

        before.WindowFee.Should().Be(6_000m);
        after.WindowFee.Should().Be(6_120m);
    }

    /// <summary>A price list negotiated for a customer overrides the tenant's standard one.</summary>
    [Fact]
    public async Task A_customer_price_list_beats_the_standard_one()
    {
        PriceList negotiated = StandardPriceList.Create(tenantId, January, customerId);
        negotiated.Entries.Single(e => e.Kind == PriceKind.KnowledgeFee && e.Key == nameof(ManagementLevel.Complex))
            .Amount = 7_500m;
        db.PriceLists.Add(negotiated);
        await db.SaveChangesAsync();

        AddContract(AddApp("Journal"), ManagementLevel.Complex, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.KnowledgeFees.Should().Be(7_500m);
    }

    [Fact]
    public void The_standard_price_list_carries_every_amount_in_Bilaga_C()
    {
        PriceList list = StandardPriceList.Create(tenantId, January);

        list.Entries.Should().HaveCount(22);
        ContractService.Lookup(list, PriceKind.WindowFee, nameof(SupportWindow.S4)).Should().Be(95_000m);
        ContractService.Lookup(list, PriceKind.KnowledgeFee, nameof(ManagementLevel.Instance)).Should().Be(500m);
        ContractService.Lookup(list, PriceKind.KnowledgeFee, ContractPricing.ReducedInstanceKey).Should().Be(350m);
        ContractService.Lookup(list, PriceKind.HourlyRate, nameof(SupportTimeCategory.Night)).Should().Be(2_300m);
        ContractService.Lookup(list, PriceKind.HourlyRate, StandardPriceList.DevelopmentRateKey).Should().Be(1_200m);
        ContractService.Lookup(list, PriceKind.OneOffFee, StandardPriceList.OnboardingKey(ManagementLevel.Complex))
            .Should().Be(34_500m);
        ContractService.Lookup(list, PriceKind.OneOffFee, StandardPriceList.NewInstanceKey).Should().Be(2_500m);

        // Behöver undersökas is quoted after review (§10.2), so it deliberately has no amount.
        ContractService.Lookup(list, PriceKind.KnowledgeFee, nameof(ManagementLevel.NeedsInvestigation))
            .Should().BeNull();
    }

    /// <summary>
    /// The timbank tiers carry their hours numerically as well as in the key, so a tier can
    /// be compared and totalled without parsing strings.
    /// </summary>
    [Fact]
    public void Hour_bank_tiers_carry_their_hours()
    {
        PriceList list = StandardPriceList.Create(tenantId, January);

        PriceListEntry twenty = list.Entries.Single(e => e.Kind == PriceKind.HourBankTier && e.Key == "20");

        twenty.Hours.Should().Be(20m);
        twenty.Amount.Should().Be(21_800m);
    }

    // ---- Contacts ------------------------------------------------------------------------

    /// <summary>
    /// §14.5 wants a named person at each escalation level on both sides, and §23 a contact
    /// with a mandate plus a deputy. Before this the customer was a name and nothing else.
    /// </summary>
    [Fact]
    public async Task Both_sides_of_the_escalation_chain_can_be_recorded()
    {
        db.ContractContacts.AddRange(
            new ContractContact
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                Party = ContractParty.Customer, Role = ContractContactRole.TechnicalContact,
                Name = "Kundens tekniska kontakt", Email = "tech@example.org", Phone = "+46 70 000 00 00",
            },
            new ContractContact
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                Party = ContractParty.Customer, Role = ContractContactRole.EscalationLevel3,
                Name = "Kundens IT-chef",
            },
            new ContractContact
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                Party = ContractParty.Supplier, Role = ContractContactRole.EscalationLevel3,
                Name = "Leverantörens VD",
            });
        await db.SaveChangesAsync();

        List<ContractContact> levelThree = await db.ContractContacts
            .Where(c => c.CustomerId == customerId && c.Role == ContractContactRole.EscalationLevel3)
            .ToListAsync();

        levelThree.Should().HaveCount(2);
        levelThree.Select(c => c.Party).Should().BeEquivalentTo(
            [ContractParty.Customer, ContractParty.Supplier]);
    }

    [Fact]
    public async Task An_application_can_only_have_one_contract()
    {
        Guid appId = AddApp("Journal");
        AddContract(appId, ManagementLevel.Standard, SupportWindow.S1);

        db.ApplicationContracts.Add(new ApplicationContract
        {
            Id = Guid.NewGuid(), TenantId = tenantId, AppId = appId,
            Origin = ContractOrigin.SupplierDeveloped,
        });

        Func<Task> act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();

    }
}
