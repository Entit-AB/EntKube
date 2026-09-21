using System.Collections.Concurrent;

namespace EntKube.Web.Services.Support;

/// <summary>
/// The Swedish calendar the förvaltningsavtal is written against: which dates are röda
/// dagar and which are working days ("helgfri måndag–fredag enligt svensk kalender").
///
/// <para><b>Why this is data and not a constant.</b> Half the red days move with Easter,
/// and three of them — midsommarafton, julafton and nyårsafton — are not allmänna
/// helgdagar in law at all. They are red here because §9 of the agreement says so, which
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
    /// Red days are looked up per instant while SLA clocks tick, so the per-year set is
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
    /// The allmänna helgdagar — the thirteen days that are public holidays in Swedish law.
    ///
    /// <para>Plain Sundays are also allmänna helgdagar in law, and are deliberately left
    /// out: every support window in §9 already treats Sunday by the day of the week, and
    /// folding Sundays in here would make "helgfri vardag" ambiguous.</para>
    /// </summary>
    public static IReadOnlySet<DateOnly> PublicHolidays(int year)
    {
        DateOnly easter = EasterSunday(year);

        return new HashSet<DateOnly>
        {
            new(year, 1, 1),                    // Nyårsdagen
            new(year, 1, 6),                    // Trettondedag jul
            easter.AddDays(-2),                 // Långfredagen
            easter,                             // Påskdagen
            easter.AddDays(1),                  // Annandag påsk
            new(year, 5, 1),                    // Första maj
            easter.AddDays(39),                 // Kristi himmelsfärdsdag
            easter.AddDays(49),                 // Pingstdagen
            new(year, 6, 6),                    // Nationaldagen
            MidsummerDay(year),                 // Midsommardagen
            AllSaintsDay(year),                 // Alla helgons dag
            new(year, 12, 25),                  // Juldagen
            new(year, 12, 26),                  // Annandag jul
        };
    }

    /// <summary>
    /// The three eves §9 adds to the red days: midsommarafton, julafton and nyårsafton.
    /// Working days in law, closed days under this agreement.
    /// </summary>
    public static IReadOnlySet<DateOnly> ContractEves(int year) =>
        new HashSet<DateOnly>
        {
            MidsummerDay(year).AddDays(-1),     // Midsommarafton
            new(year, 12, 24),                  // Julafton
            new(year, 12, 31),                  // Nyårsafton
        };

    /// <summary>
    /// Midsommardagen: the Saturday that falls between 20 and 26 June inclusive.
    /// </summary>
    public static DateOnly MidsummerDay(int year) => FirstSaturdayOnOrAfter(new DateOnly(year, 6, 20));

    /// <summary>
    /// Alla helgons dag: the Saturday that falls between 31 October and 6 November inclusive.
    /// </summary>
    public static DateOnly AllSaintsDay(int year) => FirstSaturdayOnOrAfter(new DateOnly(year, 10, 31));

    /// <summary>
    /// Every red day in a year under this agreement — the public holidays plus the three
    /// eves named in §9.
    /// </summary>
    public static IReadOnlySet<DateOnly> RedDays(int year) => RedDaysInternal(year);

    /// <summary>
    /// Whether a date is a röd dag as §9 defines it. A Saturday or Sunday that is not also
    /// a holiday is <em>not</em> a red day by this test — it is a weekend, which the support
    /// windows and time categories handle by the day of the week. Callers asking "is this a
    /// closed day" want <see cref="IsWorkingDay"/>.
    /// </summary>
    public static bool IsRedDay(DateOnly date) => RedDaysInternal(date.Year).Contains(date);

    /// <summary>
    /// An arbetsdag as the agreement defines it: a helgfri måndag–fredag. This is the unit
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
