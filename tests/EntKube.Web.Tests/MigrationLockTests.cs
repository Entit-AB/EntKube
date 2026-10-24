using EntKube.Web.Data;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The lock that stops two instances reconciling the schema at the same time.
///
/// <para><b>What the retry loop already covered, and what it did not.</b> Startup has run
/// reconcile-then-migrate inside a retry loop for a long time, and that loop made a concurrent
/// <c>Migrate()</c> survivable by accident: the loser fails, backs off, and finds nothing pending
/// next time round. What it never protected is <c>MigrationReconciler.Reconcile</c>, which runs
/// FIRST and compares the live schema against the history table. Two of those interleaving can
/// have one instance stamping a migration as applied while the other is still creating its
/// objects — and acting when schema and history disagree is the reconciler's entire job.</para>
///
/// <para>⚠️ Production has no database console (docs/decomposition.md §9.3), so a schema left
/// half-decided is the worst outcome in the whole multi-instance audit. That is why this is
/// locked rather than left to the retry loop.</para>
///
/// <para>These tests cover the per-provider mapping and the grant decision — the parts that are
/// pure. Whether Postgres actually blocks the second caller is Postgres's contract, not
/// EntKube's, and testing it would need a real server.</para>
/// </summary>
public class MigrationLockTests
{
    private const string Postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// ⭐ Non-blocking, on both providers, because the caller polls with a bounded budget.
    ///
    /// <para>A blocking acquire — plain <c>pg_advisory_lock</c>, or <c>@LockTimeout = -1</c> —
    /// would turn a wedged holder into an instance that hangs forever with no further log line.
    /// The whole point of the budget above this is that it ends in a loud failure the
    /// orchestrator can restart.</para>
    /// </summary>
    [Fact]
    public void The_acquire_never_blocks()
    {
        MigrationLock.TryAcquireSql(Postgres).Should().Contain("pg_try_advisory_lock")
            .And.NotContain("pg_advisory_lock(",
                "the blocking form would wait forever on a wedged holder");

        MigrationLock.TryAcquireSql(SqlServer).Should().Contain("@LockTimeout = 0");
    }

    /// <summary>
    /// ⚠️ Session-owned, not transaction-owned. A migration is several transactions, and a
    /// transaction-owned lock is released by the first one that commits — which would free the
    /// lock in the middle of the work it is protecting.
    /// </summary>
    [Fact]
    public void The_sql_server_lock_outlives_a_transaction()
    {
        MigrationLock.TryAcquireSql(SqlServer).Should().Contain("@LockOwner = 'Session'")
            .And.NotContain("'Transaction'");

        MigrationLock.ReleaseSql(SqlServer).Should().Contain("@LockOwner = 'Session'");
    }

    /// <summary>Acquire and release must name the same lock, or the release is a no-op.</summary>
    [Fact]
    public void Release_addresses_the_lock_that_was_taken()
    {
        MigrationLock.TryAcquireSql(Postgres).Should().Contain(MigrationLock.Key.ToString());
        MigrationLock.ReleaseSql(Postgres).Should().Contain(MigrationLock.Key.ToString());

        MigrationLock.TryAcquireSql(SqlServer).Should().Contain(MigrationLock.ResourceName);
        MigrationLock.ReleaseSql(SqlServer).Should().Contain(MigrationLock.ResourceName);
    }

    /// <summary>
    /// ⚠️ Postgres advisory-lock keys are <c>bigint</c>. A key that overflowed or went negative
    /// would still compile and still be "a number", and the failure would be a runtime error on
    /// one provider only.
    /// </summary>
    [Fact]
    public void The_key_is_a_valid_postgres_advisory_key()
    {
        MigrationLock.Key.Should().BePositive();
        MigrationLock.Key.Should().BeLessThan(long.MaxValue);
    }

    /// <summary>
    /// ⭐ Two silences, and only one of them is fine. SQLite genuinely needs no lock — a single
    /// file used as the local-development fallback. An unrecognised provider also takes no lock,
    /// but that is a gap, and the caller must be able to tell them apart to decide whether to
    /// warn.
    /// </summary>
    [Fact]
    public void A_provider_with_no_lock_is_either_expected_or_a_gap()
    {
        MigrationLock.TryAcquireSql(Sqlite).Should().BeNull();
        MigrationLock.NeedsNoLock(Sqlite).Should().BeTrue("one file, one process — nothing to lock");

        MigrationLock.TryAcquireSql("Some.Future.Provider").Should().BeNull();
        MigrationLock.NeedsNoLock("Some.Future.Provider").Should().BeFalse(
            "an unrecognised provider migrating unlocked is a gap the operator should hear about, "
            + "not a silent default");

        MigrationLock.NeedsNoLock(null).Should().BeFalse();
    }

    /// <summary>
    /// The two providers answer in different types, and reading them wrongly fails in the
    /// dangerous direction — treating "not granted" as granted means both instances reconcile.
    ///
    /// <para>⚠️ <c>sp_getapplock</c> returns <b>0</b> for "granted synchronously" and 1 for
    /// "granted after waiting". Both are success. Only a negative value is a failure, which is
    /// why this is <c>&gt;= 0</c> and not <c>&gt; 0</c> — the natural-looking test would reject
    /// the most common success case.</para>
    /// </summary>
    [Theory]
    [InlineData(true, true)]      // Postgres: granted
    [InlineData(false, false)]    // Postgres: held by someone else
    [InlineData(0, true)]         // sp_getapplock: granted immediately
    [InlineData(1, true)]         // sp_getapplock: granted after waiting
    [InlineData(-1, false)]       // sp_getapplock: timeout
    [InlineData(-3, false)]       // sp_getapplock: deadlock victim
    public void A_grant_is_read_the_same_way_on_both_providers(object scalar, bool granted)
        => MigrationLock.WasGranted(scalar).Should().Be(granted);

    /// <summary>Anything unexpected is NOT a grant — the safe direction.</summary>
    [Fact]
    public void An_unreadable_answer_is_not_a_grant()
    {
        MigrationLock.WasGranted(null).Should().BeFalse();
        MigrationLock.WasGranted("yes").Should().BeFalse(
            "a value this cannot interpret must not be read as permission to migrate");
        MigrationLock.WasGranted(DBNull.Value).Should().BeFalse();
    }
}
