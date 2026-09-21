using EntKube.Web.Data;

namespace EntKube.Web.Services.Contracts;

/// <summary>What one application contributes to the monthly base fee.</summary>
/// <param name="AppId">The application charged for.</param>
/// <param name="AppName">Its name as it stood when the fee was computed.</param>
/// <param name="Level">The service level in force on the date.</param>
/// <param name="Window">The support window in force, after an instance inherits its parent's.</param>
/// <param name="PriceKey">The Annex C key the fee was read from — the audit trail for the amount.</param>
/// <param name="KnowledgeFee">The knowledge fee for the month.</param>
public readonly record struct ApplicationFee(
    Guid AppId,
    string AppName,
    ManagementLevel Level,
    SupportWindow Window,
    string PriceKey,
    decimal KnowledgeFee);

/// <summary>
/// The base fee of §10 for one month: one window fee for the portfolio plus one
/// knowledge fee per application.
/// </summary>
/// <param name="Window">The most extensive window in the portfolio, which §10.1 prices on.</param>
/// <param name="WindowFee">The window fee for that window.</param>
/// <param name="Applications">Each application's knowledge fee.</param>
/// <param name="Unpriced">
/// Applications that could not be priced — a level of Needs investigation, or a price list
/// with no entry for the level. Reported rather than silently charged at zero, on the same
/// principle as the cost ledger's coverage rows: an hour nobody measured is not a free hour.
/// </param>
public readonly record struct BaseFeeBreakdown(
    SupportWindow? Window,
    decimal WindowFee,
    IReadOnlyList<ApplicationFee> Applications,
    IReadOnlyList<string> Unpriced)
{
    public decimal KnowledgeFees => Applications.Sum(a => a.KnowledgeFee);

    /// <summary>The whole base fee — what §10 says is charged every month regardless of work done.</summary>
    public decimal Total => WindowFee + KnowledgeFees;
}

/// <summary>
/// The arithmetic of the base fee, kept pure so it can be checked without a database.
///
/// <para>This decides what a customer is charged every month whether or not anything
/// happened, which makes it the most-read number in the agreement. The rules it encodes are
/// small but easy to get subtly wrong: the window fee is charged once per portfolio and
/// priced on the <em>most extensive</em> window anyone bought (§10.1), an instance inherits
/// its parent application's window unless told otherwise (§10.2.1), and from the twenty-first
/// instance of the same parent the knowledge fee drops to a lower rate.</para>
/// </summary>
public static class ContractPricing
{
    /// <summary>
    /// The Annex C key for the reduced knowledge fee that §10.2.1 applies from the
    /// twenty-first instance of one parent application.
    /// </summary>
    public const string ReducedInstanceKey = "InstanceReduced";

    /// <summary>
    /// The ordinal at which an instance starts costing the reduced rate. §10.2.1: "från och
    /// med den tjugoförsta (21:a) instansen".
    /// </summary>
    public const int ReducedInstanceFrom = 21;

    /// <summary>
    /// The window the window fee is priced on: the most extensive one bought for any
    /// application in the portfolio (§10.1). Null when the portfolio is empty.
    /// </summary>
    public static SupportWindow? MostExtensiveWindow(IEnumerable<SupportWindow> windows)
    {
        SupportWindow? widest = null;

        foreach (SupportWindow window in windows)
        {
            if (widest is null || window > widest)
            {
                widest = window;
            }
        }

        return widest;
    }

    /// <summary>
    /// The Annex C key an application's knowledge fee is read from.
    /// <paramref name="instanceOrdinal"/> is this instance's position among the instances of
    /// its parent application, counted from one; it is ignored at every other level.
    /// </summary>
    public static string KnowledgeFeeKey(ManagementLevel level, int instanceOrdinal = 0) =>
        level == ManagementLevel.Instance && instanceOrdinal >= ReducedInstanceFrom
            ? ReducedInstanceKey
            : level.ToString();

    /// <summary>
    /// Orders a parent application's instances so their ordinals are stable. Oldest first by
    /// when it came under management, then by id: the twenty-first instance has to stay the
    /// twenty-first next month, or the reduced rate would move between customers' rows for
    /// no reason anyone could explain.
    /// </summary>
    public static IReadOnlyList<T> InOrdinalOrder<T>(
        IEnumerable<T> instances, Func<T, DateTime?> onboardedAt, Func<T, Guid> id) =>
        [.. instances.OrderBy(i => onboardedAt(i) ?? DateTime.MaxValue).ThenBy(id)];
}
