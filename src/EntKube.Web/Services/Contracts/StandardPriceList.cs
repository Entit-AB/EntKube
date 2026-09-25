using EntKube.Web.Data;

namespace EntKube.Web.Services.Contracts;

/// <summary>
/// Annex C's amounts as a starting point for a new price list.
///
/// <para>These are the figures in the standard price annex, in SEK excluding VAT. They are
/// a <em>template</em>, not a source of truth: the list that governs a customer is the one
/// stored against them and signed, and §19 will index these upward every January. Nothing
/// reads this at runtime — it exists so creating a price list does not mean typing
/// twenty-three amounts, and so the shape of the keys has one definition.</para>
/// </summary>
public static class StandardPriceList
{
    /// <summary>
    /// A new price list carrying the standard amounts, effective from the given date.
    /// </summary>
    public static PriceList Create(Guid tenantId, DateTime effectiveFrom, Guid? customerId = null)
    {
        PriceList list = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            EffectiveFrom = effectiveFrom,
            Currency = "SEK",
            Notes = "Standard price list (Bilaga C).",
        };

        int order = 0;

        void Add(PriceKind kind, string key, decimal amount, decimal? hours = null) =>
            list.Entries.Add(new PriceListEntry
            {
                Id = Guid.NewGuid(),
                PriceListId = list.Id,
                Kind = kind,
                Key = key,
                Amount = amount,
                Hours = hours,
                SortOrder = order++,
            });

        // C.1 — window fee per support window, charged once for the whole portfolio.
        Add(PriceKind.WindowFee, nameof(SupportWindow.S1), 6_000m);
        Add(PriceKind.WindowFee, nameof(SupportWindow.S2), 30_000m);
        Add(PriceKind.WindowFee, nameof(SupportWindow.S3), 55_000m);
        Add(PriceKind.WindowFee, nameof(SupportWindow.S4), 95_000m);

        // C.2 — knowledge fee per application. Needs investigation has no amount: §10.2
        // quotes it after a technical review, which is why it is absent rather than zero.
        Add(PriceKind.KnowledgeFee, nameof(ManagementLevel.Simple), 1_500m);
        Add(PriceKind.KnowledgeFee, nameof(ManagementLevel.Standard), 4_000m);
        Add(PriceKind.KnowledgeFee, nameof(ManagementLevel.Complex), 9_000m);
        Add(PriceKind.KnowledgeFee, nameof(ManagementLevel.Instance), 500m);
        Add(PriceKind.KnowledgeFee, ContractPricing.ReducedInstanceKey, 350m);

        // C.3 — hour bank tiers. The amount is the monthly price for the whole tier.
        Add(PriceKind.HourBankTier, "10", 11_150m, 10m);
        Add(PriceKind.HourBankTier, "20", 21_800m, 20m);
        Add(PriceKind.HourBankTier, "40", 42_400m, 40m);

        // C.4 — hourly rates. The surcharges in §13 are applied to the ordinary rate, and
        // the results are stored rather than recomputed: the annex states them as amounts,
        // and a rounding difference between the two would be a dispute.
        Add(PriceKind.HourlyRate, nameof(SupportTimeCategory.Ordinary), 1_150m);
        Add(PriceKind.HourlyRate, nameof(SupportTimeCategory.EveningMorning), 1_440m);
        Add(PriceKind.HourlyRate, nameof(SupportTimeCategory.WeekendOrRedDay), 1_725m);
        Add(PriceKind.HourlyRate, nameof(SupportTimeCategory.Night), 2_300m);
        Add(PriceKind.HourlyRate, nameof(SupportTimeCategory.Callout), 2_300m);
        Add(PriceKind.HourlyRate, DevelopmentRateKey, 1_200m);

        // C.5 — one-off fees.
        Add(PriceKind.OneOffFee, OnboardingKey(ManagementLevel.Simple), 9_000m);
        Add(PriceKind.OneOffFee, OnboardingKey(ManagementLevel.Standard), 18_500m);
        Add(PriceKind.OneOffFee, OnboardingKey(ManagementLevel.Complex), 34_500m);
        Add(PriceKind.OneOffFee, NewInstanceKey, 2_500m);

        return list;
    }

    /// <summary>
    /// The rate for development assignments under §15, including UX, architecture and DevOps
    /// within the assignment. A separate rate from ordinary management, so a separate key.
    /// </summary>
    public const string DevelopmentRateKey = "Development";

    /// <summary>Deploying a further instance of a parent application (§10.2.1, C.5).</summary>
    public const string NewInstanceKey = "NewInstance";

    /// <summary>The one-off fee key for on-boarding an application at a given level (§5.1).</summary>
    public static string OnboardingKey(ManagementLevel level) => $"Onboarding{level}";
}
