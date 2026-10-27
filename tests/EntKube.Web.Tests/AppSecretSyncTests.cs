using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The "re-sync every bound credential for this app" operation, lifted out of
/// <c>PortalSecretsDetail.razor</c> where it was 126 lines reachable only by pressing a button.
///
/// <para>⚠️ WHAT IS AND IS NOT COVERED HERE, stated rather than implied. The nine services the
/// operation needs are concrete classes with non-virtual methods and nothing in the suite mocks
/// any of them, so Moq cannot stand in for them (docs/decomposition.md §6.2). The behaviour —
/// filtering, both layers of failure isolation, the shape of the report, and which service owns a
/// database binding — is therefore expressed behind delegates and tested here in full. What is
/// NOT tested is <c>FamiliesFor</c> and <c>SyncDatabaseAsync</c>: one line per family and one
/// line per target, deliberately kept free of decisions so that being untested costs little.
/// Covering those needs a nine-service fixture, which is a project rather than an afternoon.</para>
/// </summary>
public class AppSecretSyncTests
{
    private static BindingToSync Ok(string name, List<string> ran, bool enabled = true) =>
        new(name, enabled, _ => { ran.Add(name); return Task.CompletedTask; });

    private static BindingToSync Throws(string name, string message) =>
        new(name, true, _ => throw new InvalidOperationException(message));

    private static BindingFamily Family(SyncFamily kind, params BindingToSync[] bindings) =>
        new(kind, _ => Task.FromResult<IReadOnlyList<BindingToSync>>(bindings));

    private static BindingFamily Unlistable(SyncFamily kind, string message) =>
        new(kind, _ => throw new InvalidOperationException(message));

    // ── Which service owns a database binding ─────────────────────────────────────────

    [Theory]
    [InlineData(true, false, false, DatabaseBindingTarget.Cnpg)]
    [InlineData(false, true, false, DatabaseBindingTarget.Mongo)]
    [InlineData(false, false, true, DatabaseBindingTarget.RegisteredPostgres)]
    public void A_binding_resolves_to_the_service_that_owns_its_database(
        bool cnpg, bool mongo, bool registered, DatabaseBindingTarget expected)
    {
        DatabaseBinding binding = new()
        {
            KubernetesSecretName = "app-db",
            CnpgDatabaseId = cnpg ? Guid.NewGuid() : null,
            MongoDatabaseId = mongo ? Guid.NewGuid() : null,
            RegisteredPostgresDatabaseId = registered ? Guid.NewGuid() : null,
        };

        AppSecretSyncService.ResolveTarget(binding).Should().Be(expected);
    }

    /// <summary>
    /// ⚠️ The case the page could not express. Its dispatch was
    /// <c>if / else if / else if</c> with no final branch, and it reported <c>"✓ db/{name}"</c>
    /// afterwards regardless — so a binding naming no database at all was reported as synced while
    /// nothing was sent. The three ids are each <c>Guid?</c> and documented "mutually exclusive",
    /// which guarantees at most one and not at least one, and no schema constraint requires one.
    /// Not a defect anyone has hit; a silent success is worth one enum member to remove.
    /// </summary>
    [Fact]
    public void A_binding_that_names_no_database_resolves_to_nothing_rather_than_to_the_first_branch()
    {
        DatabaseBinding orphan = new() { KubernetesSecretName = "app-db" };

        AppSecretSyncService.ResolveTarget(orphan).Should().Be(DatabaseBindingTarget.None,
            "falling through the chain and reporting success is the one outcome worse than failing");
    }

    // ── The orchestration ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_enabled_binding_in_every_family_is_synced()
    {
        List<string> ran = [];

        (List<BindingSyncOutcome> outcomes, List<FamilyLoadFailure> failures) =
            await AppSecretSyncService.RunFamiliesAsync(
            [
                Family(SyncFamily.Database, Ok("db-1", ran), Ok("db-2", ran)),
                Family(SyncFamily.Cache, Ok("cache-1", ran)),
                Family(SyncFamily.Identity, Ok("idp-1", ran)),
            ]);

        ran.Should().Equal("db-1", "db-2", "cache-1", "idp-1");
        failures.Should().BeEmpty();
        outcomes.Should().OnlyContain(o => o.Succeeded);
        outcomes.Select(o => o.SecretName).Should().Equal("db-1", "db-2", "cache-1", "idp-1");
        outcomes.Select(o => o.Family).Should().Equal(
            SyncFamily.Database, SyncFamily.Database, SyncFamily.Cache, SyncFamily.Identity);
    }

    [Fact]
    public async Task A_binding_with_sync_switched_off_is_left_alone()
    {
        List<string> ran = [];

        (List<BindingSyncOutcome> outcomes, _) = await AppSecretSyncService.RunFamiliesAsync(
            [Family(SyncFamily.Storage, Ok("on", ran), Ok("off", ran, enabled: false))]);

        ran.Should().Equal(["on"],
            "the operator turned sync off for that binding deliberately");
        outcomes.Should().ContainSingle().Which.SecretName.Should().Be("on",
            "a binding that was never attempted must not be reported as synced either");
    }

    /// <summary>
    /// ⚠️ Inner isolation. An operator who rotates one secret and presses "sync everything" must
    /// not be stopped by an unrelated binding whose cluster is unreachable.
    /// </summary>
    [Fact]
    public async Task One_binding_that_will_not_sync_does_not_stop_its_siblings()
    {
        List<string> ran = [];

        (List<BindingSyncOutcome> outcomes, List<FamilyLoadFailure> failures) =
            await AppSecretSyncService.RunFamiliesAsync(
            [
                Family(SyncFamily.Database,
                    Ok("before", ran),
                    Throws("broken", "cluster unreachable"),
                    Ok("after", ran)),
            ]);

        ran.Should().Equal(["before", "after"],
            "the binding after the failure is the one a non-isolated loop would skip");
        failures.Should().BeEmpty("a binding failing is not the family failing to load");

        outcomes.Should().HaveCount(3);
        BindingSyncOutcome broken = outcomes.Single(o => o.SecretName == "broken");
        broken.Succeeded.Should().BeFalse();
        broken.Error.Should().Be("cluster unreachable",
            "the operator needs to know which binding and why, not just that something failed");
    }

    /// <summary>
    /// ⚠️ Outer isolation. One family that cannot even be listed — a down data service, a bad
    /// credential — must not cost the other four.
    /// </summary>
    [Fact]
    public async Task One_family_that_cannot_be_listed_does_not_stop_the_others()
    {
        List<string> ran = [];

        (List<BindingSyncOutcome> outcomes, List<FamilyLoadFailure> failures) =
            await AppSecretSyncService.RunFamiliesAsync(
            [
                Family(SyncFamily.Database, Ok("db-1", ran)),
                Unlistable(SyncFamily.Messaging, "rabbit is down"),
                Family(SyncFamily.Identity, Ok("idp-1", ran)),
            ]);

        ran.Should().Equal(["db-1", "idp-1"],
            "the family listed AFTER the broken one is what a non-isolated loop would lose");

        failures.Should().ContainSingle().Which.Should()
            .BeEquivalentTo(new FamilyLoadFailure(SyncFamily.Messaging, "rabbit is down"));

        outcomes.Should().OnlyContain(o => o.Succeeded);
        outcomes.Should().HaveCount(2,
            "an unlistable family contributes no binding outcomes, not empty successful ones");
    }

    [Fact]
    public async Task A_binding_that_names_no_database_is_reported_as_a_failure_not_a_success()
    {
        // What the wiring produces for an orphaned binding: a syncer that refuses. The page's
        // version reported "✓ db/orphan" for exactly this row.
        (List<BindingSyncOutcome> outcomes, _) = await AppSecretSyncService.RunFamiliesAsync(
            [Family(SyncFamily.Database, Throws("orphan", "names no database"))]);

        BindingSyncOutcome only = outcomes.Should().ContainSingle().Subject;
        only.Succeeded.Should().BeFalse();
        only.Error.Should().Contain("names no database");
    }

    // ── The report ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void The_report_says_whether_anything_failed(
        bool bindingFailed, bool familyFailed, bool fatal, bool expected)
    {
        AppSecretSyncReport report = new(
            VaultOutput: "ok",
            Bindings: bindingFailed
                ? [new BindingSyncOutcome(SyncFamily.Database, "db", "boom")]
                : [new BindingSyncOutcome(SyncFamily.Database, "db", null)],
            LoadFailures: familyFailed ? [new FamilyLoadFailure(SyncFamily.Cache, "down")] : [],
            FatalError: fatal ? "stopped early" : null);

        report.AnythingFailed.Should().Be(expected,
            "the caller decides how loudly to report, and cannot without one honest flag");
    }
}
