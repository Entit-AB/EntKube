using EntKube.Web.Data;

namespace EntKube.Web.Services.Contracts;

/// <summary>What one application contributes to the monthly grundavgift.</summary>
/// <param name="AppId">The application charged for.</param>
/// <param name="AppName">Its name as it stood when the fee was computed.</param>
/// <param name="Level">The förvaltningsnivå in force on the date.</param>
/// <param name="Window">The support window in force, after an instance inherits its parent's.</param>
/// <param name="PriceKey">The Bilaga C key the fee was read from — the audit trail for the amount.</param>
/// <param name="KnowledgeFee">The kännedomsavgift for the month.</param>
public readonly record struct ApplicationFee(
    Guid AppId,
    string AppName,
    ManagementLevel Level,
    SupportWindow Window,
    string PriceKey,
    decimal KnowledgeFee);

/// <summary>
/// The grundavgift of §10 for one month: one fönsteravgift for the portfolio plus one
/// kännedomsavgift per application.
/// </summary>
/// <param name="Window">The most extensive window in the portfolio, which §10.1 prices on.</param>
/// <param name="WindowFee">The fönsteravgift for that window.</param>
/// <param name="Applications">Each application's kännedomsavgift.</param>
/// <param name="Unpriced">
/// Applications that could not be priced — a level of Behöver undersökas, or a price list
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

    /// <summary>The whole grundavgift — what §10 says is charged every month regardless of work done.</summary>
    public decimal Total => WindowFee + KnowledgeFees;
}

/// <summary>
/// The arithmetic of the grundavgift, kept pure so it can be checked without a database.
///
/// <para>This decides what a customer is charged every month whether or not anything
/// happened, which makes it the most-read number in the agreement. The rules it encodes are
/// small but easy to get subtly wrong: the fönsteravgift is charged once per portfolio and
/// priced on the <em>most extensive</em> window anyone bought (§10.1), an instance inherits
/// its moderapplikation's window unless told otherwise (§10.2.1), and from the twenty-first
/// instance of the same parent the kännedomsavgift drops to a lower rate.</para>
/// </summary>
public static class ContractPricing
{
    /// <summary>
    /// The Bilaga C key for the reduced kännedomsavgift that §10.2.1 applies from the
    /// twenty-first instance of one moderapplikation.
    /// </summary>
    public const string ReducedInstanceKey = "InstanceReduced";

    /// <summary>
    /// The ordinal at which an instance starts costing the reduced rate. §10.2.1: "från och
    /// med den tjugoförsta (21:a) instansen".
    /// </summary>
    public const int ReducedInstanceFrom = 21;

    /// <summary>
    /// The window the fönsteravgift is priced on: the most extensive one bought for any
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
    /// The Bilaga C key an application's kännedomsavgift is read from.
    /// <paramref name="instanceOrdinal"/> is this instance's position among the instances of
    /// its moderapplikation, counted from one; it is ignored at every other level.
    /// </summary>
    public static string KnowledgeFeeKey(ManagementLevel level, int instanceOrdinal = 0) =>
        level == ManagementLevel.Instance && instanceOrdinal >= ReducedInstanceFrom
            ? ReducedInstanceKey
            : level.ToString();

    /// <summary>
    /// Orders a moderapplikation's instances so their ordinals are stable. Oldest first by
    /// when it came under management, then by id: the twenty-first instance has to stay the
    /// twenty-first next month, or the reduced rate would move between customers' rows for
    /// no reason anyone could explain.
    /// </summary>
    public static IReadOnlyList<T> InOrdinalOrder<T>(
        IEnumerable<T> instances, Func<T, DateTime?> onboardedAt, Func<T, Guid> id) =>
        [.. instances.OrderBy(i => onboardedAt(i) ?? DateTime.MaxValue).ThenBy(id)];
}
