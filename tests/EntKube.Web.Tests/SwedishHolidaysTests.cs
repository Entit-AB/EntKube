using EntKube.Web.Services.Support;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The Swedish calendar the management agreement is written against.
///
/// <para>Every commercial clause in the agreement counts in either public holidays or
/// working days, so an error here propagates straight into SLA compliance and into what an
/// hour is billed at. The cases below are named after what they defend rather than after
/// the method they call.</para>
/// </summary>
public class SwedishHolidaysTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    public void Easter_is_computed_correctly(int year, int month, int day) =>
        SwedishHolidays.EasterSunday(year).Should().Be(new DateOnly(year, month, day));

    [Fact]
    public void The_five_moveable_holidays_follow_Easter()
    {
        IReadOnlySet<DateOnly> holidays = SwedishHolidays.PublicHolidays(2026);

        holidays.Should().Contain(new DateOnly(2026, 4, 3));   // Good Friday
        holidays.Should().Contain(new DateOnly(2026, 4, 5));   // Easter Sunday
        holidays.Should().Contain(new DateOnly(2026, 4, 6));   // Easter Monday
        holidays.Should().Contain(new DateOnly(2026, 5, 14));  // Ascension Day
        holidays.Should().Contain(new DateOnly(2026, 5, 24));  // Whit Sunday
    }

    [Theory]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Midsummer_day_is_the_Saturday_between_20_and_26_June(int year)
    {
        DateOnly midsummer = SwedishHolidays.MidsummerDay(year);

        midsummer.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        midsummer.Should().BeOnOrAfter(new DateOnly(year, 6, 20));
        midsummer.Should().BeOnOrBefore(new DateOnly(year, 6, 26));
    }

    [Theory]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void All_saints_day_is_the_Saturday_between_31_October_and_6_November(int year)
    {
        DateOnly allSaints = SwedishHolidays.AllSaintsDay(year);

        allSaints.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        allSaints.Should().BeOnOrAfter(new DateOnly(year, 10, 31));
        allSaints.Should().BeOnOrBefore(new DateOnly(year, 11, 6));
    }

    [Fact]
    public void There_are_thirteen_public_holidays_a_year() =>
        SwedishHolidays.PublicHolidays(2026).Should().HaveCount(13);

    /// <summary>
    /// §9's footnote adds three days that are ordinary working days in Swedish law. Keeping
    /// the distinction visible matters: it is a term of this agreement, not a fact about the
    /// country, and another customer could be sold a different set.
    /// </summary>
    [Theory]
    [InlineData(2026, 6, 19)]   // Midsummer Eve, a Friday
    [InlineData(2026, 12, 24)]  // Christmas Eve, a Thursday
    [InlineData(2026, 12, 31)]  // New Year's Eve, a Thursday
    public void The_three_contract_eves_are_red_days_but_not_public_holidays(int year, int month, int day)
    {
        DateOnly date = new(year, month, day);

        SwedishHolidays.IsRedDay(date).Should().BeTrue();
        SwedishHolidays.PublicHolidays(year).Should().NotContain(date);
        SwedishHolidays.IsWorkingDay(date).Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_weekday_is_a_working_day() =>
        SwedishHolidays.IsWorkingDay(new DateOnly(2026, 9, 22)).Should().BeTrue();

    [Theory]
    [InlineData(2026, 9, 26)]   // Saturday
    [InlineData(2026, 9, 27)]   // Sunday
    [InlineData(2026, 1, 6)]    // Epiphany, a Tuesday
    [InlineData(2026, 12, 25)]  // Christmas Day, a Friday
    public void Weekends_and_red_days_are_not_working_days(int year, int month, int day) =>
        SwedishHolidays.IsWorkingDay(new DateOnly(year, month, day)).Should().BeFalse();

    /// <summary>
    /// A weekend day that is not also a holiday is not a public holiday — it is a weekend. The
    /// support windows and §13 time categories distinguish the two, so this test pins the
    /// boundary rather than letting "closed" blur into "red".
    /// </summary>
    [Fact]
    public void A_plain_Saturday_is_not_a_red_day() =>
        SwedishHolidays.IsRedDay(new DateOnly(2026, 9, 26)).Should().BeFalse();

    /// <summary>
    /// The worst stretch in the Swedish year: Wednesday 23 December, then Christmas Eve, Christmas Day,
    /// Boxing Day and a weekend. One working day later is the following Monday.
    /// </summary>
    [Fact]
    public void Working_days_step_over_the_Christmas_stretch() =>
        SwedishHolidays.AddWorkingDays(new DateOnly(2026, 12, 23), 1)
            .Should().Be(new DateOnly(2026, 12, 28));

    /// <summary>
    /// Five working days from 23 December reaches 5 January: New Year's Eve and New Year's Day are
    /// both closed, and the weekend between them does not count either.
    /// </summary>
    [Fact]
    public void Five_working_days_from_before_Christmas_lands_in_January() =>
        SwedishHolidays.AddWorkingDays(new DateOnly(2026, 12, 23), 5)
            .Should().Be(new DateOnly(2027, 1, 5));

    [Fact]
    public void Zero_working_days_is_the_same_day() =>
        SwedishHolidays.AddWorkingDays(new DateOnly(2026, 9, 26), 0)
            .Should().Be(new DateOnly(2026, 9, 26));

    [Fact]
    public void A_negative_number_of_working_days_is_rejected()
    {
        Action act = () => SwedishHolidays.AddWorkingDays(new DateOnly(2026, 9, 22), -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(23)]
    public void Counting_working_days_inverts_adding_them(int count)
    {
        DateOnly from = new(2026, 12, 23);
        DateOnly to = SwedishHolidays.AddWorkingDays(from, count);

        SwedishHolidays.WorkingDaysBetween(from, to).Should().Be(count);
    }

    [Fact]
    public void Counting_backwards_gives_zero_rather_than_a_negative() =>
        SwedishHolidays.WorkingDaysBetween(new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 21))
            .Should().Be(0);
}
