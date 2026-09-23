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

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
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

        ResolvedServiceLevel? resolved = await contracts.ResolveServiceLevelAsync(instanceId, June);

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

        ResolvedServiceLevel? resolved = await contracts.ResolveServiceLevelAsync(instanceId, June);

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

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

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

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

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
    /// §10.2 quotes Needs investigation after a technical review, so the standard list has no
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
        AddContract(AddApp("Orphaned instance"), ManagementLevel.Instance, window: null);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

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

        await contracts.RecordServiceLevelAsync(
            contract.Id, ManagementLevel.Complex, SupportWindow.S3, June,
            "§16.2 quarterly review", "nils");

        List<ApplicationServiceLevel> history = await contracts.ListServiceLevelsAsync(contract.Id);

        history.Should().HaveCount(2);
        history[0].EffectiveFrom.Should().Be(June);
        history[0].RecordedBy.Should().Be("nils");
        history[1].Level.Should().Be(ManagementLevel.Standard);
    }

    [Fact]
    public async Task Saving_a_contract_twice_updates_rather_than_duplicates()
    {
        Guid appId = AddApp("Journal");

        await contracts.SaveContractAsync(new ApplicationContract
        {
            TenantId = tenantId, AppId = appId, Origin = ContractOrigin.ExternallyDeveloped,
            DevelopedBy = "Previous supplier", OnboardedAt = January,
        });

        await contracts.SaveContractAsync(new ApplicationContract
        {
            TenantId = tenantId, AppId = appId, Origin = ContractOrigin.SupplierDeveloped,
            OnboardedAt = January, GuaranteeEndsAt = June,
        });

        ApplicationContract? saved = await contracts.GetContractAsync(appId);

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
        ContractContact saved = await contracts.SaveContactAsync(new ContractContact
        {
            TenantId = tenantId, CustomerId = customerId,
            Party = ContractParty.Customer, Role = ContractContactRole.Deputy,
            Name = "Deputy", Phone = "+46 70 111 11 11",
        });

        (await contracts.ListContactsAsync(customerId)).Should().ContainSingle();

        await contracts.DeleteContactAsync(saved.Id);

        (await contracts.ListContactsAsync(customerId)).Should().BeEmpty();
    }

    // ---- Deleting and correcting -----------------------------------------------------------

    /// <summary>
    /// A row entered by mistake has to be removable. Deleting the one in force changes
    /// which terms a past month resolves to, which is why the UI says so before it happens.
    /// </summary>
    [Fact]
    public async Task An_Annex_B_can_be_deleted_and_the_earlier_one_takes_over()
    {
        PortfolioAgreement first = await contracts.RecordPortfolioAgreementAsync(new PortfolioAgreement
        {
            TenantId = tenantId, CustomerId = customerId, EffectiveFrom = January,
            PricingModel = PricingModel.TimeAndMaterials,
        });

        PortfolioAgreement mistake = await contracts.RecordPortfolioAgreementAsync(new PortfolioAgreement
        {
            TenantId = tenantId, CustomerId = customerId, EffectiveFrom = June,
            PricingModel = PricingModel.HourBank, HourBankHoursPerMonth = 20m,
        });

        await contracts.DeletePortfolioAgreementAsync(mistake.Id);

        PortfolioAgreement? inForce = await contracts.GetPortfolioAgreementAsync(
            customerId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        inForce!.Id.Should().Be(first.Id);
        (await contracts.ListPortfolioAgreementsAsync(customerId)).Should().ContainSingle();
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

        BaseFeeBreakdown after = await contracts.CalculateBaseFeeAsync(customerId, nextYear);
        after.WindowFee.Should().Be(6_600m);

        await contracts.DeletePriceListAsync(indexed!.Id);

        BaseFeeBreakdown reverted = await contracts.CalculateBaseFeeAsync(customerId, nextYear);
        reverted.WindowFee.Should().Be(6_000m);
    }

    [Fact]
    public async Task Deleting_a_price_list_takes_its_amounts_with_it()
    {
        PriceList extra = StandardPriceList.Create(tenantId, June);
        await contracts.AddPriceListAsync(extra);

        await contracts.DeletePriceListAsync(extra.Id);

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

        await contracts.SavePriceListEntryAsync(
            list.Id, PriceKind.WindowFee, nameof(SupportWindow.S1), 7_500m);

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.WindowFee.Should().Be(7_500m);
    }

    [Fact]
    public async Task An_amount_the_list_did_not_carry_can_be_added()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();

        // §10.2 quotes Needs investigation after a review, so the standard list has no
        // amount for it — which is exactly the case where one gets typed in later.
        await contracts.SavePriceListEntryAsync(
            list.Id, PriceKind.KnowledgeFee, nameof(ManagementLevel.NeedsInvestigation), 2_750m);

        AddContract(AddApp("New system"), ManagementLevel.NeedsInvestigation, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

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

        await contracts.DeletePriceListEntryAsync(standard.Id);

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown fee = await contracts.CalculateBaseFeeAsync(customerId, June);

        fee.KnowledgeFees.Should().Be(0m);
        fee.Unpriced.Should().ContainSingle().Which.Should().Contain("Booking");
    }

    [Fact]
    public async Task Editing_an_amount_twice_updates_rather_than_duplicates()
    {
        PriceList list = (await contracts.ListPriceListsAsync(tenantId, null)).Single();

        await contracts.SavePriceListEntryAsync(list.Id, PriceKind.WindowFee, nameof(SupportWindow.S1), 7_000m);
        await contracts.SavePriceListEntryAsync(list.Id, PriceKind.WindowFee, nameof(SupportWindow.S1), 8_000m);

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

        await contracts.UpdatePriceListAsync(list.Id, moved, "Corrected — signed in April.");

        AddContract(AddApp("Booking"), ManagementLevel.Standard, SupportWindow.S1);

        BaseFeeBreakdown march = await contracts.CalculateBaseFeeAsync(
            customerId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        BaseFeeBreakdown may = await contracts.CalculateBaseFeeAsync(
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
}
