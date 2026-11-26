using System.Globalization;
using EntKube.Web.Data;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The SQL an advisory lock is taken and given back with. The key is a number put into SQL text,
/// and the text is read by Postgres, not by a person: it is written the same on every host.
/// </summary>
public sealed class AdvisoryLockTests
{
    private const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// ⭐ sv-SE writes a negative number with U+2212 (MINUS SIGN) where Postgres needs '-', and Postgres
    /// then reads the key as a column name: <c>42703: column "−5306212924162610147" does not exist</c>.
    /// A host in that culture could never take or give back a lock whose key is negative.
    /// </summary>
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("fa-IR")]
    [InlineData("en-US")]
    public void A_negative_key_is_written_with_an_ascii_minus_in_any_culture(string culture)
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            AdvisoryLock.TryAcquireSql(Npgsql, -5306212924162610147, "r")
                .Should().Be("SELECT pg_try_advisory_lock(-5306212924162610147)");
            AdvisoryLock.ReleaseSql(Npgsql, -5306212924162610147, "r")
                .Should().Be("SELECT pg_advisory_unlock(-5306212924162610147)");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_positive_key_is_written_as_plain_digits()
    {
        AdvisoryLock.TryAcquireSql(Npgsql, AdvisoryLock.MigrationsKey, AdvisoryLock.MigrationsResource)
            .Should().Be($"SELECT pg_try_advisory_lock({AdvisoryLock.MigrationsKey.ToString(CultureInfo.InvariantCulture)})")
            .And.MatchRegex(@"^SELECT pg_try_advisory_lock\([0-9]+\)$");
    }
}
