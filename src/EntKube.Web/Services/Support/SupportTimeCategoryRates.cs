using EntKube.Web.Data;

namespace EntKube.Web.Services.Support;

/// <summary>
/// The pricing consequences of a time category, kept beside the category so the two cannot
/// drift apart. The percentages are the surcharge on the ordinary hourly rate (Bilaga C,
/// C.4); the factor is how many hours one worked hour draws from a Modell A timbank (§13).
/// </summary>
public static class SupportTimeCategoryRates
{
    /// <summary>Surcharge on the ordinary hourly rate, as a percentage.</summary>
    public static int SurchargePercent(this SupportTimeCategory category) => category switch
    {
        SupportTimeCategory.Ordinary => 0,
        SupportTimeCategory.EveningMorning => 25,
        SupportTimeCategory.WeekendOrRedDay => 50,
        SupportTimeCategory.Night => 100,
        SupportTimeCategory.Callout => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>
    /// Hours drawn from the timbank per hour worked (§13). One hour of weekend work costs
    /// the bank 1.5 hours.
    /// </summary>
    public static decimal BankFactor(this SupportTimeCategory category) => category switch
    {
        SupportTimeCategory.Ordinary => 1.0m,
        SupportTimeCategory.EveningMorning => 1.25m,
        SupportTimeCategory.WeekendOrRedDay => 1.5m,
        SupportTimeCategory.Night => 2.0m,
        SupportTimeCategory.Callout => 2.0m,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>
    /// The floor on a single occasion, in hours. §13 bills an utryckning at a minimum of
    /// two hours however short it was; every other category bills per started hour.
    /// </summary>
    public static decimal MinimumBillableHours(this SupportTimeCategory category) =>
        category == SupportTimeCategory.Callout ? 2m : 1m;
}
