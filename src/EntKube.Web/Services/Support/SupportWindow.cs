namespace EntKube.Web.Services.Support;

/// <summary>
/// The four support windows of §9 — the hours during which the supplier is staffed and
/// during which the SLA clocks of §14.4 run. Chosen per application in Bilaga A; the most
/// extensive one in the portfolio sets the fönsteravgift (§10.1).
/// </summary>
public enum SupportWindow
{
    /// <summary>Kontorstid — helgfria vardagar 08:00–17:00. No jour.</summary>
    S1 = 1,

    /// <summary>Utökad kontorstid — helgfria vardagar 05:00–22:00.</summary>
    S2 = 2,

    /// <summary>Utökad kontorstid inkl. helger och röda dagar — alla dagar 05:00–22:00.</summary>
    S3 = 3,

    /// <summary>Dygnet runt — 24/7/365.</summary>
    S4 = 4,
}

/// <summary>
/// The §13 time categories. Which one applies is decided by <em>when the work was done</em>,
/// not by which support window the application bought — §13 is explicit about that, and it
/// is the mistake most likely to be made when reading the two tables side by side.
/// </summary>
public enum SupportTimeCategory
{
    /// <summary>Ordinarie — helgfria vardagar 08:00–17:00.</summary>
    Ordinary = 0,

    /// <summary>Kväll/morgon — helgfria vardagar 05:00–08:00 and 17:00–22:00.</summary>
    EveningMorning = 1,

    /// <summary>Helg och röd dag — 05:00–22:00.</summary>
    WeekendOrRedDay = 2,

    /// <summary>Natt — 22:00–05:00, every day of the week.</summary>
    Night = 3,

    /// <summary>
    /// Utryckning — work on a P1 incident outside the application's chosen support window.
    /// Not derivable from the clock alone: it depends on which window was bought and on the
    /// ticket's priority, so callers state it rather than the calendar inferring it.
    /// </summary>
    Callout = 4,
}

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
