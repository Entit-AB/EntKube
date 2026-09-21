using System.Collections.Concurrent;

namespace EntKube.Web.Services.Support;

/// <summary>
/// The Swedish calendar the management agreement is written against: which dates are public
/// holidays and which are working days — Monday to Friday, holidays excluded.
///
/// <para><b>Why this is data and not a constant.</b> Half the public holidays move with Easter,
/// and three of them — Midsummer Eve, Christmas Eve and New Year's Eve — are not public
/// holidays in law at all. They are closed here because §9 of the agreement says so, which
/// makes the set a contractual choice rather than a fact about Sweden. Another customer
/// could reasonably be sold a different one, so the two halves are kept separable:
/// <see cref="PublicHolidays"/> is the law, <see cref="ContractEves"/> is the clause.</para>
///
/// <para><b>Pure and clock-free.</b> Everything here is a function of a date. Nothing
/// reads the system time or a database, because this arithmetic decides when an SLA
/// breach happened and what an hour is billed at, and it has to be checkable without
/// standing anything up.</para>
/// </summary>
public static class SwedishHolidays
{
    /// <summary>
    /// Public holidays are looked up per instant while SLA clocks tick, so the per-year set is
    /// computed once. Bounded by the number of distinct years a process is asked about.
    /// </summary>
    private static readonly ConcurrentDictionary<int, HashSet<DateOnly>> RedDayCache = new();

    /// <summary>
    /// Easter Sunday in the Gregorian calendar (the anonymous Gregorian algorithm).
    /// Five of the thirteen public holidays are defined relative to it.
    /// </summary>
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// The thirteen days that are public holidays in Swedish law.
    ///
    /// <para>Plain Sundays are also public holidays in law, and are deliberately left
    /// out: every support window in §9 already treats Sunday by the day of the week, and
    /// folding Sundays in here would make "working day" ambiguous.</para>
    /// </summary>
    public static IReadOnlySet<DateOnly> PublicHolidays(int year)
    {
        DateOnly easter = EasterSunday(year);

        return new HashSet<DateOnly>
        {
            new(year, 1, 1),                    // New Year's Day (nyårsdagen)
            new(year, 1, 6),                    // Epiphany (trettondedag jul)
            easter.AddDays(-2),                 // Good Friday (långfredagen)
            easter,                             // Easter Sunday (påskdagen)
            easter.AddDays(1),                  // Easter Monday (annandag påsk)
            new(year, 5, 1),                    // May Day (första maj)
            easter.AddDays(39),                 // Ascension Day (Kristi himmelsfärdsdag)
            easter.AddDays(49),                 // Whit Sunday (pingstdagen)
            new(year, 6, 6),                    // National Day (nationaldagen)
            MidsummerDay(year),                 // Midsummer Day (midsommardagen)
            AllSaintsDay(year),                 // All Saints' Day (alla helgons dag)
            new(year, 12, 25),                  // Christmas Day (juldagen)
            new(year, 12, 26),                  // Boxing Day (annandag jul)
        };
    }

    /// <summary>
    /// The three eves §9 adds to the public holidays: Midsummer Eve, Christmas Eve and New
    /// Year's Eve. Working days in law, closed days under this agreement.
    /// </summary>
    public static IReadOnlySet<DateOnly> ContractEves(int year) =>
        new HashSet<DateOnly>
        {
            MidsummerDay(year).AddDays(-1),     // Midsummer Eve (midsommarafton)
            new(year, 12, 24),                  // Christmas Eve (julafton)
            new(year, 12, 31),                  // New Year's Eve (nyårsafton)
        };

    /// <summary>
    /// Midsummer Day (midsommardagen): the Saturday that falls between 20 and 26 June inclusive.
    /// </summary>
    public static DateOnly MidsummerDay(int year) => FirstSaturdayOnOrAfter(new DateOnly(year, 6, 20));

    /// <summary>
    /// All Saints' Day (alla helgons dag): the Saturday that falls between 31 October and 6 November inclusive.
    /// </summary>
    public static DateOnly AllSaintsDay(int year) => FirstSaturdayOnOrAfter(new DateOnly(year, 10, 31));

    /// <summary>
    /// Every closed day in a year under this agreement — the public holidays plus the three
    /// eves named in §9.
    /// </summary>
    public static IReadOnlySet<DateOnly> RedDays(int year) => RedDaysInternal(year);

    /// <summary>
    /// Whether a date is a public holiday as §9 defines it. A Saturday or Sunday that is not also
    /// a holiday is <em>not</em> a public holiday by this test — it is a weekend, which the support
    /// windows and time categories handle by the day of the week. Callers asking "is this a
    /// closed day" want <see cref="IsWorkingDay"/>.
    /// </summary>
    public static bool IsRedDay(DateOnly date) => RedDaysInternal(date.Year).Contains(date);

    /// <summary>
    /// A working day as the agreement defines it: Monday to Friday, public holidays excluded.
    /// This is the unit
    /// behind the P3 and P4 targets, the five working days for a P1 incident report (§14.6),
    /// the ten for customer test acceptance (§23) and the ten for approving a subconsultant
    /// (§18).
    /// </summary>
    public static bool IsWorkingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsRedDay(date);

    /// <summary>
    /// The date <paramref name="count"/> working days after <paramref name="from"/>.
    /// Counting starts the day after <paramref name="from"/>, so "within five working days"
    /// from a Friday lands on the following Friday, and a <paramref name="count"/> of zero
    /// returns <paramref name="from"/> unchanged.
    /// </summary>
    public static DateOnly AddWorkingDays(DateOnly from, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        DateOnly date = from;
        int remaining = count;

        while (remaining > 0)
        {
            date = date.AddDays(1);
            if (IsWorkingDay(date))
            {
                remaining--;
            }
        }

        return date;
    }

    /// <summary>
    /// How many working days lie in (<paramref name="from"/>, <paramref name="to"/>] —
    /// the inverse of <see cref="AddWorkingDays"/>, for reporting how late something was.
    /// </summary>
    public static int WorkingDaysBetween(DateOnly from, DateOnly to)
    {
        if (to <= from)
        {
            return 0;
        }

        int count = 0;
        for (DateOnly date = from.AddDays(1); date <= to; date = date.AddDays(1))
        {
            if (IsWorkingDay(date))
            {
                count++;
            }
        }

        return count;
    }

    private static HashSet<DateOnly> RedDaysInternal(int year) =>
        RedDayCache.GetOrAdd(year, static y =>
        {
            HashSet<DateOnly> days = [.. PublicHolidays(y)];
            days.UnionWith(ContractEves(y));
            return days;
        });

    private static DateOnly FirstSaturdayOnOrAfter(DateOnly date)
    {
        int offset = ((int)DayOfWeek.Saturday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset);
    }
}
