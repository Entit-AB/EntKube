using EntKube.Web.Services;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Who is running the work that must run on exactly one instance — and whether anybody notices
/// when the answer is "both of us" or "nobody".
///
/// <para><b>The gap.</b> <c>HostedWork</c> made <em>N web + 1 worker</em> expressible with a
/// configuration flag, and <c>docs/running-two-instances.md</c> says of it: "⚠️ The flag is
/// placement, not a lease. <b>Nothing checks that exactly one container has it true.</b>" Both
/// ways of getting it wrong are silent, and the worse one is silent in the hardest way to spot:
/// with the flag false everywhere, no background work happens at all and the symptom is the
/// ABSENCE of events — no accrual rows, no alert evaluations, no expiry warnings. Nothing to find
/// in a log, because nothing is written.</para>
///
/// <para>⚠️ Only the decision is tested here. The probe itself opens a connection, runs one
/// statement and keeps the connection if it won — thin wiring, deliberately untested, and said
/// out loud rather than implied.</para>
/// </summary>
public class SingletonWorkWitnessTests
{
    private const string Postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// The whole table, so no combination is left to be discovered in production. The two
    /// misconfigurations have named tests of their own below; this proves the other four are NOT
    /// reported as problems, which matters just as much — a witness that cried wolf on a correct
    /// deployment would be turned off.
    /// </summary>
    [Theory]
    // provider,  runsSingletonWork, granted, expected
    [InlineData(Postgres, true, true, SingletonWorkPlacement.Holding)]
    [InlineData(Postgres, true, false, SingletonWorkPlacement.Contended)]
    [InlineData(Postgres, false, true, SingletonWorkPlacement.Unclaimed)]
    [InlineData(Postgres, false, false, SingletonWorkPlacement.Delegated)]
    [InlineData(SqlServer, true, true, SingletonWorkPlacement.Holding)]
    [InlineData(SqlServer, false, false, SingletonWorkPlacement.Delegated)]
    public void The_claim_decides_who_is_sweeping(
        string provider, bool runs, bool granted, SingletonWorkPlacement expected)
    {
        SingletonWorkWitness.Classify(runs, provider, granted).Should().Be(expected);
    }

    /// <summary>
    /// ⚠️ The mistake <c>running-two-instances.md</c> names as the likely one: "it is never 'I
    /// forgot to turn it on', it is 'I forgot to turn it off on the new instance'."
    /// </summary>
    [Fact]
    public void Two_instances_both_configured_to_sweep_is_reported()
    {
        SingletonWorkPlacement placement =
            SingletonWorkWitness.Classify(runsSingletonWork: true, Postgres, granted: false);

        placement.Should().Be(SingletonWorkPlacement.Contended);
        SingletonWorkWitness.IsMisconfiguration(placement).Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ The worse one, because its symptom is nothing happening: no cost accrual, no alert
    /// evaluation, no certificate-expiry check, anywhere.
    /// </summary>
    [Fact]
    public void Nobody_sweeping_is_reported()
    {
        SingletonWorkPlacement placement =
            SingletonWorkWitness.Classify(runsSingletonWork: false, Postgres, granted: true);

        placement.Should().Be(SingletonWorkPlacement.Unclaimed);
        SingletonWorkWitness.IsMisconfiguration(placement).Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ <b>The row worth reading twice.</b> SQLite takes no lock because two processes sharing
    /// one file is not a deployment EntKube supports — so on SQLite there IS no other instance,
    /// and opting out means nothing sweeps, full stop. It is the one case diagnosable with no
    /// lock at all, and folding it into "cannot tell" would have missed it.
    /// </summary>
    [Fact]
    public void On_a_single_file_database_opting_out_means_nothing_sweeps()
    {
        SingletonWorkWitness.Classify(runsSingletonWork: false, Sqlite, granted: null)
            .Should().Be(
                SingletonWorkPlacement.Unclaimed,
                "there is no second instance on SQLite, so there is nobody else it could be");

        SingletonWorkWitness.Classify(runsSingletonWork: true, Sqlite, granted: null)
            .Should().Be(
                SingletonWorkPlacement.NotApplicable,
                "and a single instance that sweeps is simply correct — warning here would be noise");
    }

    /// <summary>
    /// A provider nobody taught a lock is reported as unanswerable rather than as healthy. The
    /// same distinction <c>AdvisoryLock.NeedsNoLock</c> draws: SQLite taking no lock is correct,
    /// an unrecognised provider taking no lock is a gap, and the two must not share a branch.
    /// </summary>
    [Fact]
    public void An_unrecognised_provider_is_a_gap_not_a_clean_bill_of_health()
    {
        SingletonWorkPlacement placement =
            SingletonWorkWitness.Classify(runsSingletonWork: true, "Some.Other.Provider", granted: null);

        placement.Should().Be(SingletonWorkPlacement.ProviderUnknown);
        SingletonWorkWitness.IsMisconfiguration(placement).Should().BeFalse(
            "it is not a misconfiguration, it is an inability to tell — and reporting it as the "
            + "former would send an operator looking for a flag that is already right");
    }

    /// <summary>
    /// The shipped interval, pinned for the reason <c>KubeconfigResolver.DefaultFreshness</c> is:
    /// the decision tests above pass whatever it is, so a value left at zero or at a day would go
    /// unnoticed.
    /// </summary>
    [Fact]
    public void The_shipped_probe_interval_is_a_real_value()
    {
        SingletonWorkWitness.DefaultInterval.Should().BeGreaterThan(TimeSpan.FromSeconds(10));
        SingletonWorkWitness.DefaultInterval.Should().BeLessThan(TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// ⚠️ One key per question. Two purposes sharing a key would make the migration lock look
    /// like it was answering "who sweeps", and an instance mid-migration would be reported as the
    /// sweeper.
    /// </summary>
    [Fact]
    public void The_singleton_claim_is_not_the_migration_lock()
    {
        EntKube.Web.Data.AdvisoryLock.SingletonWorkKey
            .Should().NotBe(EntKube.Web.Data.AdvisoryLock.MigrationsKey);

        EntKube.Web.Data.AdvisoryLock.SingletonWorkResource
            .Should().NotBe(EntKube.Web.Data.AdvisoryLock.MigrationsResource);
    }
}
