using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Annex A, B and C as data: what was agreed for an application, what the portfolio chose,
/// and what the base fee of §10 comes to.
///
/// <para>The properties these defend, in order of how expensive they are to get wrong: the
/// terms in force on a past date do not change when someone edits them today; the
/// window fee is charged once for the portfolio and priced on the most extensive window
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

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
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

        ResolvedServiceLevel? march = await contracts.ResolveServiceLevelAsync(tenantId, appId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        ResolvedServiceLevel? july = await contracts.ResolveServiceLevelAsync(tenantId, appId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

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

        ResolvedServiceLevel? inMarch = await contracts.ResolveServiceLevelAsync(tenantId, 
            appId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        inMarch.Should().BeNull();
    }

    // ---- §10.2.1: instances ------------------------------------------------------------

    /// <summary>
    /// An instance with no window of its own takes its parent application's, which is what
    /// §10.2.1 makes the default.
    /// </summary>
    [Fact]
    public async Task An_instance_inherits_its_parents_support_window()
    {
        Guid parentId = AddApp("Care portal");
        AddContract(parentId, ManagementLevel.Complex, SupportWindow.S3);

        Guid instanceId = AddApp("Care portal — South");
        AddContract(instanceId, ManagementLevel.Instance, window: null, parentAppId: parentId);

        ResolvedServiceLevel? resolved = await contracts.ResolveServiceLevelAsync(tenantId, instanceId, June);

        resolved!.Value.Window.Should().Be(SupportWindow.S3);
        resolved.Value.WindowInherited.Should().BeTrue();
        resolved.Value.Level.Should().Be(ManagementLevel.Instance);
    }

    [Fact]
    public async Task An_instance_that_states_its_own_window_keeps_it()
    {
        Guid parentId = AddApp("Care portal");
        AddContract(parentId, ManagementLevel.Complex, SupportWindow.S1);

        Guid instanceId = AddApp("Care portal — North");
        AddContract(instanceId, ManagementLevel.Instance, SupportWindow.S4, parentAppId: parentId);

        ResolvedServiceLevel? resolved = await contracts.ResolveServiceLevelAsync(tenantId, instanceId, June);

        resolved!.Value.Window.Should().Be(SupportWindow.S4);
        resolved.Value.WindowInherited.Should().BeFalse();
    }

    /// <summary>
    /// §10.2.1 drops the knowledge fee from the twenty-first instance of one
    /// parent application. The ordinal is by on-boarding date, so it does not move month to
    /// month.
    /// </summary>
    [Fact]
    public async Task The_twenty_first_instance_is_charged_the_reduced_fee()
    {
        Guid parentId = AddApp("Care portal");
        AddContract(parentId, ManagementLevel.Complex, SupportWindow.S1);

        for (int i = 1; i <= 22; i++)
        {
            Guid instanceId = AddApp($"Care portal — customer {i:00}");
            AddContract(
                instanceId, ManagementLevel.Instance, window: null, parentAppId: parentId,
                onboardedAt: January.AddDays(i));
        }

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.Unpriced.Should().BeEmpty();
        fee.Applications.Count(a => a.PriceKey == nameof(ManagementLevel.Instance)).Should().Be(20);
        fee.Applications.Count(a => a.PriceKey == ContractPricing.ReducedInstanceKey).Should().Be(2);

        // 9 000 for the parent, 20 × 500 and 2 × 350 for the instances.
        fee.KnowledgeFees.Should().Be(9_000m + (20 * 500m) + (2 * 350m));
    }

    // ---- §10.1: the window fee ------------------------------------------------------

    /// <summary>
    /// One window fee for the whole portfolio, priced on the most extensive window any
    /// application bought — not one per application, and not the commonest window.
    /// </summary>
    [Fact]
    public async Task The_window_fee_is_charged_once_on_the_most_extensive_window()
    {
        AddContract(AddApp("Enkel sajt"), ManagementLevel.Simple, SupportWindow.S1);
        AddContract(AddApp("Bokning"), ManagementLevel.Standard, SupportWindow.S1);
        AddContract(AddApp("Journal"), ManagementLevel.Complex, SupportWindow.S3);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.Window.Should().Be(SupportWindow.S3);
        fee.WindowFee.Should().Be(55_000m);
        fee.KnowledgeFees.Should().Be(1_500m + 4_000m + 9_000m);
        fee.Total.Should().Be(55_000m + 14_500m);
    }

    [Fact]
    public void The_most_extensive_window_of_nothing_is_nothing() =>
        ContractPricing.MostExtensiveWindow([]).Should().BeNull();

    // ---- §19: applications entering and leaving management ----------------------------

    [Fact]
    public async Task An_application_not_yet_onboarded_is_not_charged()
    {
        AddContract(AddApp("Journal"), ManagementLevel.Complex, SupportWindow.S1, onboardedAt: June);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, 
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

        BaseFeeBreakdown march = await contracts.CalculateBaseFeeAsync(tenantId, 
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        BaseFeeBreakdown june = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        march.Applications.Should().ContainSingle();
        june.Applications.Should().BeEmpty();
    }

    // ---- Gaps are reported, never discounted -------------------------------------------

    /// <summary>
    /// §10.2 quotes Needs investigation after a technical review, so the standard list has no
    /// amount for it. An application sitting at that level has to surface as a gap: charging
    /// it at zero would hide an unbilled application indefinitely.
    /// </summary>
    [Fact]
    public async Task An_unclassified_application_is_reported_rather_than_charged_at_zero()
    {
        AddContract(AddApp("Nytt system"), ManagementLevel.NeedsInvestigation, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.Applications.Should().BeEmpty();
        fee.Unpriced.Should().ContainSingle().Which.Should().Contain("Nytt system");
        fee.Total.Should().Be(0m);
    }

    [Fact]
    public async Task An_application_with_no_window_anywhere_is_reported()
    {
        AddContract(AddApp("Orphaned instance"), ManagementLevel.Instance, window: null);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.Unpriced.Should().ContainSingle().Which.Should().Contain("support window");
    }

    // ---- Annex B and C ----------------------------------------------------------------

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

        PortfolioAgreement? march = await contracts.GetPortfolioAgreementAsync(tenantId, 
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        PortfolioAgreement? july = await contracts.GetPortfolioAgreementAsync(tenantId, 
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

        BaseFeeBreakdown before = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);
        BaseFeeBreakdown after = await contracts.CalculateBaseFeeAsync(tenantId, customerId, nextYear);

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

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

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

        // Needs investigation is quoted after review (§10.2), so it deliberately has no amount.
        ContractService.Lookup(list, PriceKind.KnowledgeFee, nameof(ManagementLevel.NeedsInvestigation))
            .Should().BeNull();
    }

    /// <summary>
    /// The hour bank tiers carry their hours numerically as well as in the key, so a tier can
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
                Name = "Customer technical contact", Email = "tech@example.org", Phone = "+46 70 000 00 00",
            },
            new ContractContact
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                Party = ContractParty.Customer, Role = ContractContactRole.EscalationLevel3,
                Name = "Customer IT manager",
            },
            new ContractContact
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
                Party = ContractParty.Supplier, Role = ContractContactRole.EscalationLevel3,
                Name = "Our managing director",
            });
        await db.SaveChangesAsync();

        List<ContractContact> levelThree = await db.ContractContacts
            .Where(c => c.CustomerId == customerId && c.Role == ContractContactRole.EscalationLevel3)
            .ToListAsync();

        levelThree.Should().HaveCount(2);
        levelThree.Select(c => c.Party).Should().BeEquivalentTo(
            [ContractParty.Customer, ContractParty.Supplier]);
    }

    // ---- Writes --------------------------------------------------------------------------

    /// <summary>
    /// Recording a change never edits the entry in force — it adds one. The old terms have
    /// to stay readable, because an invoice raised under them may still be disputed.
    /// </summary>
    [Fact]
    public async Task Recording_a_classification_adds_to_the_history_rather_than_replacing_it()
    {
        Guid appId = AddApp("Journal");
        ApplicationContract contract = AddContract(appId, ManagementLevel.Standard, SupportWindow.S1);

        await contracts.RecordServiceLevelAsync(tenantId, 
            contract.Id, ManagementLevel.Complex, SupportWindow.S3, June,
            "§16.2 quarterly review", "nils");

        List<ApplicationServiceLevel> history = await contracts.ListServiceLevelsAsync(tenantId, contract.Id);

        history.Should().HaveCount(2);
        history[0].EffectiveFrom.Should().Be(June);
        history[0].RecordedBy.Should().Be("nils");
        history[1].Level.Should().Be(ManagementLevel.Standard);
    }

    [Fact]
    public async Task Saving_a_contract_twice_updates_rather_than_duplicates()
    {
        Guid appId = AddApp("Journal");

        await contracts.SaveContractAsync(tenantId, new ApplicationContract
        {
            TenantId = tenantId, AppId = appId, Origin = ContractOrigin.ExternallyDeveloped,
            DevelopedBy = "Previous supplier", OnboardedAt = January,
        });

        await contracts.SaveContractAsync(tenantId, new ApplicationContract
        {
            TenantId = tenantId, AppId = appId, Origin = ContractOrigin.SupplierDeveloped,
            OnboardedAt = January, GuaranteeEndsAt = June,
        });

        ApplicationContract? saved = await contracts.GetContractAsync(tenantId, appId);

        saved!.Origin.Should().Be(ContractOrigin.SupplierDeveloped);
        saved.GuaranteeEndsAt.Should().Be(June);
        saved.DevelopedBy.Should().BeNull();
        db.ApplicationContracts.Count(c => c.AppId == appId).Should().Be(1);
    }

    /// <summary>
    /// §19 raises prices every January by SCB's AKI "dock med minst 2 %". A lower figure is
    /// lifted to the floor rather than applied — the agreement does not allow the smaller
    /// increase, and silently honouring it would undercharge for a year.
    /// </summary>
    [Theory]
    [InlineData(0, 6_120)]      // Below the floor: 2% applies.
    [InlineData(1.5, 6_120)]    // Still below the floor.
    [InlineData(3.4, 6_204)]    // Above it: the real figure applies.
    public async Task Indexation_never_goes_below_the_two_percent_floor(double percent, int expected)
    {
        DateTime nextYear = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        PriceList? indexed = await contracts.IndexPriceListAsync(
            tenantId, null, nextYear, (decimal)percent, "nils");

        ContractService.Lookup(indexed, PriceKind.WindowFee, nameof(SupportWindow.S1))
            .Should().Be(expected);
    }

    [Fact]
    public async Task Indexing_leaves_the_earlier_list_untouched()
    {
        DateTime nextYear = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await contracts.IndexPriceListAsync(tenantId, null, nextYear, 5m, "nils");

        PriceList? original = await contracts.GetPriceListAsync(tenantId, null, June);

        ContractService.Lookup(original, PriceKind.WindowFee, nameof(SupportWindow.S4))
            .Should().Be(95_000m);
        (await contracts.ListPriceListsAsync(tenantId, null)).Should().HaveCount(2);
    }

    /// <summary>
    /// Every amount moves together. §13 says that when the ordinary rate is adjusted, every
    /// time category and every hour bank price moves with it from the same date — so an
    /// indexation that touched only some rows would break the relationship between them.
    /// </summary>
    [Fact]
    public async Task Indexation_moves_every_amount_in_the_list()
    {
        DateTime nextYear = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        PriceList? indexed = await contracts.IndexPriceListAsync(tenantId, null, nextYear, 10m, null);
        PriceList? original = await contracts.GetPriceListAsync(tenantId, null, June);

        indexed!.Entries.Should().HaveCount(original!.Entries.Count);

        foreach (PriceListEntry before in original.Entries)
        {
            PriceListEntry after = indexed.Entries.Single(e => e.Kind == before.Kind && e.Key == before.Key);
            after.Amount.Should().Be(Math.Round(before.Amount * 1.10m, 0, MidpointRounding.AwayFromZero));
            after.Hours.Should().Be(before.Hours);
        }
    }

    [Fact]
    public async Task Indexing_with_no_list_to_index_from_returns_nothing()
    {
        Guid emptyTenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = emptyTenant, Name = "Other", Slug = "other" });
        await db.SaveChangesAsync();

        PriceList? indexed = await contracts.IndexPriceListAsync(
            emptyTenant, null, June, 2m, null);

        indexed.Should().BeNull();
    }

    [Fact]
    public async Task A_contact_can_be_saved_then_removed()
    {
        ContractContact saved = await contracts.SaveContactAsync(tenantId, new ContractContact
        {
            TenantId = tenantId, CustomerId = customerId,
            Party = ContractParty.Customer, Role = ContractContactRole.Deputy,
            Name = "Deputy", Phone = "+46 70 111 11 11",
        });

        (await contracts.ListContactsAsync(tenantId, customerId)).Should().ContainSingle();

        await contracts.DeleteContactAsync(tenantId, saved.Id);

        (await contracts.ListContactsAsync(tenantId, customerId)).Should().BeEmpty();
    }

    // ---- Deleting and correcting -----------------------------------------------------------

    /// <summary>
    /// A row entered by mistake has to be removable. Deleting the one in force changes
    /// which terms a past month resolves to, which is why the UI says so before it happens.
    /// </summary>
    [Fact]
    public async Task An_Annex_B_can_be_deleted_and_the_earlier_one_takes_over()
    {
        PortfolioAgreement first = await contracts.RecordPortfolioAgreementAsync(tenantId, new PortfolioAgreement
        {
            TenantId = tenantId, CustomerId = customerId, EffectiveFrom = January,
            PricingModel = PricingModel.TimeAndMaterials,
        });

        PortfolioAgreement mistake = await contracts.RecordPortfolioAgreementAsync(tenantId, new PortfolioAgreement
        {
            TenantId = tenantId, CustomerId = customerId, EffectiveFrom = June,
            PricingModel = PricingModel.HourBank, HourBankHoursPerMonth = 20m,
        });

        await contracts.DeletePortfolioAgreementAsync(tenantId, mistake.Id);

        PortfolioAgreement? inForce = await contracts.GetPortfolioAgreementAsync(tenantId, 
            customerId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        inForce!.Id.Should().Be(first.Id);
        (await contracts.ListPortfolioAgreementsAsync(tenantId, customerId)).Should().ContainSingle();
    }

    /// <summary>
    /// Deleting a price list reprices every month that used it — the earlier list takes
    /// over, and with it the earlier amounts.
    /// </summary>
    [Fact]
    public async Task Deleting_a_price_list_reprices_from_the_one_before_it()
    {
        DateTime nextYear = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PriceList? indexed = await contracts.IndexPriceListAsync(tenantId, null, nextYear, 10m, "nils");

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown after = await contracts.CalculateBaseFeeAsync(tenantId, customerId, nextYear);
        after.WindowFee.Should().Be(6_600m);

        await contracts.DeletePriceListAsync(tenantId, indexed!.Id);

        BaseFeeBreakdown reverted = await contracts.CalculateBaseFeeAsync(tenantId, customerId, nextYear);
        reverted.WindowFee.Should().Be(6_000m);
    }

    [Fact]
    public async Task Deleting_a_price_list_takes_its_amounts_with_it()
    {
        PriceList extra = StandardPriceList.Create(tenantId, June);
        await contracts.AddPriceListAsync(tenantId, extra);

        await contracts.DeletePriceListAsync(tenantId, extra.Id);

        db.PriceListEntries.Count(e => e.PriceListId == extra.Id).Should().Be(0);
    }

    /// <summary>
    /// The standard list is a template; what governs a customer is what they signed. Until
    /// an amount could be edited, there was no way to record the difference.
    /// </summary>
    [Fact]
    public async Task An_amount_can_be_corrected_to_what_was_signed()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();

        await contracts.SavePriceListEntryAsync(tenantId, 
            list.Id, PriceKind.WindowFee, nameof(SupportWindow.S1), 7_500m);

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.WindowFee.Should().Be(7_500m);
    }

    [Fact]
    public async Task An_amount_the_list_did_not_carry_can_be_added()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();

        // §10.2 quotes Needs investigation after a review, so the standard list has no
        // amount for it — which is exactly the case where one gets typed in later.
        await contracts.SavePriceListEntryAsync(tenantId, 
            list.Id, PriceKind.KnowledgeFee, nameof(ManagementLevel.NeedsInvestigation), 2_750m);

        AddContract(AddApp("New system"), ManagementLevel.NeedsInvestigation, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.Unpriced.Should().BeEmpty();
        fee.KnowledgeFees.Should().Be(2_750m);
    }

    /// <summary>
    /// Removing an amount makes whatever used it report as unpriced rather than as free —
    /// the same principle the gap reporting rests on everywhere else.
    /// </summary>
    [Fact]
    public async Task Removing_an_amount_makes_what_used_it_unpriced()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();
        PriceListEntry standard = list.Entries
            .Single(e => e.Kind == PriceKind.KnowledgeFee && e.Key == nameof(ManagementLevel.Standard));

        await contracts.DeletePriceListEntryAsync(tenantId, standard.Id);

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(tenantId, customerId, June);

        fee.KnowledgeFees.Should().Be(0m);
        fee.Unpriced.Should().ContainSingle().Which.Should().Contain("Booking");
    }

    [Fact]
    public async Task Editing_an_amount_twice_updates_rather_than_duplicates()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();

        await contracts.SavePriceListEntryAsync(tenantId, list.Id, PriceKind.WindowFee, nameof(SupportWindow.S1), 7_000m);
        await contracts.SavePriceListEntryAsync(tenantId, list.Id, PriceKind.WindowFee, nameof(SupportWindow.S1), 8_000m);

        db.PriceListEntries
            .Count(e => e.PriceListId == list.Id && e.Kind == PriceKind.WindowFee
                        && e.Key == nameof(SupportWindow.S1))
            .Should().Be(1);

        ContractService.Lookup(
            await contracts.GetPriceListAsync(tenantId, null, June),
            PriceKind.WindowFee, nameof(SupportWindow.S1))
            .Should().Be(8_000m);
    }

    [Fact]
    public async Task A_price_list_can_be_redated()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();
        DateTime moved = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        await contracts.UpdatePriceListAsync(tenantId, list.Id, moved, "Corrected — signed in April.");

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown march = await contracts.CalculateBaseFeeAsync(tenantId, 
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        BaseFeeBreakdown may = await contracts.CalculateBaseFeeAsync(tenantId, 
            customerId, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        // Before it took effect there is no list at all, so nothing prices.
        march.WindowFee.Should().Be(0m);
        may.WindowFee.Should().Be(6_000m);
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

    // ──────── Tenancy ────────

    /// <summary>A second tenant with a customer, an app and a contract of its own.</summary>
    private (Guid Tenant, Guid Customer, Guid App, Guid Contract, Guid PriceList) SeedStranger()
    {
        Guid tenant = Guid.NewGuid(), customer = Guid.NewGuid(), app = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenant, Name = "Stranger", Slug = $"stranger-{tenant:N}" });
        db.Customers.Add(new Customer { Id = customer, TenantId = tenant, Name = "Elsewhere AB" });
        db.Apps.Add(new App { Id = app, CustomerId = customer, Name = "theirs" });

        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            AppId = app,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = January,
            MonthlyWorkCapHours = 10m,
            Notes = "theirs",
        };
        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Standard,
            SupportWindow = SupportWindow.S3,
            EffectiveFrom = January,
            Reason = "signed",
        });
        db.ApplicationContracts.Add(contract);

        PriceList theirPrices = StandardPriceList.Create(tenant, January);
        db.PriceLists.Add(theirPrices);
        db.SaveChanges();

        return (tenant, customer, app, contract.Id, theirPrices.Id);
    }

    /// <summary>
    /// A contract is the support agreement — classification, support window, monthly work cap,
    /// onboarding fee. Every read of one was keyed by an app, a customer or a contract id alone.
    ///
    /// <para>⚠️ <c>ResolveServiceLevelAsync</c> was the worst of them: it loaded <b>every contract
    /// in the installation</b> into a dictionary and then looked one app up in it, so the answer
    /// was scoped by nothing at all and the read grew with every tenant on the box.</para>
    /// </summary>
    [Fact]
    public async Task Contract_reads_are_confined_to_their_tenant()
    {
        var them = SeedStranger();

        (await contracts.GetContractAsync(tenantId, them.App)).Should().BeNull(
            "their contract exists, so this is a refusal");
        (await contracts.GetContractAsync(them.Tenant, them.App)).Should().NotBeNull(
            "the owner still sees it");

        (await contracts.ResolveServiceLevelAsync(tenantId, them.App, June)).Should().BeNull();
        (await contracts.ResolveServiceLevelAsync(them.Tenant, them.App, June)).Should().NotBeNull();

        (await contracts.ListServiceLevelsAsync(tenantId, them.Contract)).Should().BeEmpty(
            "ApplicationServiceLevel has no TenantId — this is the entry → contract traversal");
        (await contracts.ListServiceLevelsAsync(them.Tenant, them.Contract)).Should().ContainSingle();

        // The base fee is the invoice line. It already filtered by customer.TenantId — read off
        // the customer row it had just fetched by id alone, which compares that row to itself.
        BaseFeeBreakdown refused = await contracts.CalculateBaseFeeAsync(tenantId, them.Customer, June);
        refused.Applications.Should().BeEmpty("a customer we cannot see has no fees to report");
        refused.WindowFee.Should().Be(0m);

        BaseFeeBreakdown theirs = await contracts.CalculateBaseFeeAsync(them.Tenant, them.Customer, June);
        theirs.Applications.Should().ContainSingle("the owner's own figures still compute");
    }

    /// <summary>
    /// The writes that take a whole entity from the caller. <c>edited.TenantId</c> is whatever was
    /// posted, so it cannot be the authority for anything — the app and the claimed tenant are
    /// both checked instead.
    /// </summary>
    [Fact]
    public async Task A_contract_cannot_be_written_against_another_tenants_application()
    {
        var them = SeedStranger();

        Func<Task> overwrite = () => contracts.SaveContractAsync(tenantId, new ApplicationContract
        {
            TenantId = tenantId,
            AppId = them.App,
            Origin = ContractOrigin.SupplierDeveloped,
            OnboardedAt = June,
            Notes = "hijacked",
        });
        await overwrite.Should().ThrowAsync<InvalidOperationException>();

        // ⚠️ And claiming their tenant in the posted object must not help either.
        Func<Task> claimTheirs = () => contracts.SaveContractAsync(tenantId, new ApplicationContract
        {
            TenantId = them.Tenant,
            AppId = them.App,
            Origin = ContractOrigin.SupplierDeveloped,
            OnboardedAt = June,
            Notes = "hijacked",
        });
        await claimTheirs.Should().ThrowAsync<InvalidOperationException>();

        ApplicationContract survived = (await contracts.GetContractAsync(them.Tenant, them.App))!;
        survived.Notes.Should().Be("theirs");
        survived.MonthlyWorkCapHours.Should().Be(10m);

        // A classification sets the fee and the window, so an insert must not land either.
        Func<Task> reclassify = () => contracts.RecordServiceLevelAsync(
            tenantId, them.Contract, ManagementLevel.Simple, SupportWindow.S1, June,
            "downgraded", null);
        await reclassify.Should().ThrowAsync<InvalidOperationException>();

        (await contracts.ListServiceLevelsAsync(them.Tenant, them.Contract)).Should().ContainSingle();
    }

    /// <summary>
    /// ⚠️ The sharpest write in the file, and its own doc comment says why: an edit reprices every
    /// month that used the list, <b>including ones already reported and invoiced</b>, and §28 gives
    /// the customer thirty days to dispute a report. With a bare list id that was one guess away.
    /// </summary>
    [Fact]
    public async Task Another_tenants_price_list_cannot_be_edited_or_deleted()
    {
        var them = SeedStranger();

        decimal before = ContractService.Lookup(
            (await contracts.ListPriceListsAsync(them.Tenant, null))[0],
            PriceKind.WindowFee, nameof(SupportWindow.S4))!.Value;

        Func<Task> reprice = () => contracts.SavePriceListEntryAsync(
            tenantId, them.PriceList, PriceKind.WindowFee, nameof(SupportWindow.S4), 1m);
        await reprice.Should().ThrowAsync<InvalidOperationException>();

        // The list's own details decide which months resolve to it, so moving it is the same act.
        await contracts.UpdatePriceListAsync(tenantId, them.PriceList, June, "moved");
        await contracts.DeletePriceListAsync(tenantId, them.PriceList);

        PriceList intact = (await contracts.ListPriceListsAsync(them.Tenant, null)).Should()
            .ContainSingle("their list must still be there").Subject;
        intact.EffectiveFrom.Should().Be(January, "and still take effect when it did");
        ContractService.Lookup(intact, PriceKind.WindowFee, nameof(SupportWindow.S4))
            .Should().Be(before, "with the amount they agreed");

        // The entry-level delete travels entry → list, since PriceListEntry has no TenantId.
        PriceListEntry entry = intact.Entries.First(e => e.Kind == PriceKind.WindowFee);
        await contracts.DeletePriceListEntryAsync(tenantId, entry.Id);

        PriceList stillPriced = (await contracts.ListPriceListsAsync(them.Tenant, null))[0];
        stillPriced.Entries.Should().Contain(e => e.Id == entry.Id,
            "anything priced from a deleted line reports as unpriced, not as free");

        // And the owner can still do all three.
        await contracts.SavePriceListEntryAsync(
            them.Tenant, them.PriceList, PriceKind.WindowFee, nameof(SupportWindow.S4), 1m);
        ContractService.Lookup(
                (await contracts.ListPriceListsAsync(them.Tenant, null))[0],
                PriceKind.WindowFee, nameof(SupportWindow.S4))
            .Should().Be(1m);
    }

    /// <summary>
    /// The portfolio side: Annex B terms, contacts, mail domains and support addresses. The adds
    /// already took a tenant — these are the reads and the deletes that did not.
    /// </summary>
    [Fact]
    public async Task Portfolio_rows_are_confined_to_their_tenant()
    {
        var them = SeedStranger();

        PortfolioAgreement agreement = await contracts.RecordPortfolioAgreementAsync(
            them.Tenant, new PortfolioAgreement
            {
                TenantId = them.Tenant,
                CustomerId = them.Customer,
                EffectiveFrom = January,
                PricingModel = PricingModel.HourBank,
                HourBankHoursPerMonth = 10m,
            });

        ContractContact contact = await contracts.SaveContactAsync(them.Tenant, new ContractContact
        {
            TenantId = them.Tenant,
            CustomerId = them.Customer,
            Party = ContractParty.Customer,
            Role = ContractContactRole.TechnicalContact,
            Name = "Their Person",
        });

        (await contracts.ListPortfolioAgreementsAsync(tenantId, them.Customer)).Should().BeEmpty();
        (await contracts.GetPortfolioAgreementAsync(tenantId, them.Customer, June)).Should().BeNull();
        (await contracts.ListContactsAsync(tenantId, them.Customer)).Should().BeEmpty();

        // Writing against their customer must be refused outright.
        Func<Task> plantAgreement = () => contracts.RecordPortfolioAgreementAsync(
            tenantId, new PortfolioAgreement
            {
                TenantId = tenantId, CustomerId = them.Customer,
                EffectiveFrom = June, PricingModel = PricingModel.HourBank,
                HourBankHoursPerMonth = 99m,
            });
        Func<Task> plantContact = () => contracts.SaveContactAsync(tenantId, new ContractContact
        {
            TenantId = tenantId, CustomerId = them.Customer,
            Party = ContractParty.Customer, Role = ContractContactRole.TechnicalContact, Name = "Us",
        });
        // ⚠️ And moving one of THEIR rows into our tenant — SaveContactAsync used to call
        // Update() on the caller's entity, which writes TenantId and CustomerId along with
        // everything else.
        Func<Task> stealContact = () => contracts.SaveContactAsync(tenantId, new ContractContact
        {
            Id = contact.Id, TenantId = tenantId, CustomerId = customerId,
            Party = ContractParty.Customer, Role = ContractContactRole.Deputy, Name = "Their Person",
        });

        await plantAgreement.Should().ThrowAsync<InvalidOperationException>();
        await plantContact.Should().ThrowAsync<InvalidOperationException>();
        await stealContact.Should().ThrowAsync<InvalidOperationException>();

        // The deletes refuse silently, so the surviving rows are the evidence.
        await contracts.DeletePortfolioAgreementAsync(tenantId, agreement.Id);
        await contracts.DeleteContactAsync(tenantId, contact.Id);

        (await contracts.ListPortfolioAgreementsAsync(them.Tenant, them.Customer)).Should().ContainSingle();
        (await contracts.ListContactsAsync(them.Tenant, them.Customer)).Should().ContainSingle()
            .Which.Name.Should().Be("Their Person");

        // And their own tenant can delete them.
        await contracts.DeletePortfolioAgreementAsync(them.Tenant, agreement.Id);
        await contracts.DeleteContactAsync(them.Tenant, contact.Id);
        (await contracts.ListPortfolioAgreementsAsync(them.Tenant, them.Customer)).Should().BeEmpty();
        (await contracts.ListContactsAsync(them.Tenant, them.Customer)).Should().BeEmpty();
    }
}
