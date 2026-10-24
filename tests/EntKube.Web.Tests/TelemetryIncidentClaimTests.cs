using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// What happens when two instances of <see cref="TelemetryAlertEvaluator"/> evaluate the same
/// rule in the same minute.
///
/// <para>⚠️ The evaluator deduped incidents by a fingerprint <b>lookup</b>: read
/// <c>AlertIncidents</c> for (ClusterId, Fingerprint), and insert one if nothing came back. A
/// read decides nothing — both instances found nothing, both inserted, and the unique index on
/// (ClusterId, Fingerprint) refused the second. The damage was not the lost incident: there was
/// exactly ONE <c>SaveChangesAsync</c> for the whole installation, after every tenant had been
/// evaluated, so the collision rolled back every tenant's raises and resolutions and skipped the
/// dispatch loop entirely. One duplicate on one rule, and nobody anywhere was paged.</para>
///
/// <para>⭐ Each state change is now claimed on its own row, in the shape CostLedgerWriter uses
/// for the per-cluster period claim: the losers here return false and do nothing. These tests
/// race two claims against one database and assert that exactly one effect comes out — and, for
/// the raise, that the loser's context survives to save the rest of the cycle.</para>
///
/// <para>The two instances are two <see cref="TelemetryDbContext"/>s over one connection. They
/// are driven in sequence because that is the interleaving that bites: both read before either
/// wrote, so the second write arrives at a database that already has the row.</para>
/// </summary>
public class TelemetryIncidentClaimTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly TelemetryDbContext db;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid otherClusterId = Guid.NewGuid();

    private const string Fingerprint = "telemetry:rule-a:cluster-a";
    private const string OtherFingerprint = "telemetry:rule-b:cluster-b";

    public TelemetryIncidentClaimTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        factory = new TestDbContextFactory(connection);
        db = NewInstance();
        db.Database.EnsureCreated();

        Data.Environment environment = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };

        using ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        seed.Environments.Add(environment);
        seed.KubernetesClusters.AddRange(
            new KubernetesCluster
            {
                Id = clusterId,
                TenantId = tenantId,
                EnvironmentId = environment.Id,
                Name = "prod",
                ApiServerUrl = "https://prod.example.com:6443",
            },
            new KubernetesCluster
            {
                Id = otherClusterId,
                TenantId = tenantId,
                EnvironmentId = environment.Id,
                Name = "prod-2",
                ApiServerUrl = "https://prod2.example.com:6443",
            });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A second web instance, with its own context over the same database.</summary>
    private TelemetryDbContext NewInstance() => ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();

    private static AlertIncident Candidate(Guid cluster, string fingerprint, string summary, DateTime now) => new()
    {
        ClusterId = cluster,
        Fingerprint = fingerprint,
        AlertName = "Journal p95",
        Severity = "critical",
        Summary = summary,
        Description = summary,
        StartsAt = now,
        Status = IncidentStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private AlertIncident Stored(string fingerprint)
        => db.AlertIncidents.AsNoTracking().Single(i => i.Fingerprint == fingerprint);

    // ═══════════════════════════════════════════════════════════════════
    // Raising: the collision the unique index already refused
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Two_instances_raising_the_same_incident_raise_it_once()
    {
        using TelemetryDbContext one = NewInstance();
        using TelemetryDbContext two = NewInstance();

        // Both looked, both found nothing — the state the old lookup left them in.
        one.AlertIncidents.Any(i => i.Fingerprint == Fingerprint).Should().BeFalse();
        two.AlertIncidents.Any(i => i.Fingerprint == Fingerprint).Should().BeFalse();

        AlertIncident mine = Candidate(clusterId, Fingerprint, "mine", Noon);
        AlertIncident theirs = Candidate(clusterId, Fingerprint, "theirs", Noon);

        bool wonFirst = await TelemetryAlertEvaluator.TryRaiseAsync(one, mine);
        bool wonSecond = await TelemetryAlertEvaluator.TryRaiseAsync(two, theirs);

        // Exactly one winner — and a winner, not two losers. Only the winner goes in the
        // dispatch bucket, so the customer is paged once.
        wonFirst.Should().BeTrue();
        wonSecond.Should().BeFalse();

        db.AlertIncidents.AsNoTracking().Count(i => i.Fingerprint == Fingerprint).Should().Be(1);
        Stored(Fingerprint).Summary.Should().Be("mine");

        // The winner's incident has an id, which is what dispatch needs — it is already durable
        // before the end-of-cycle save, where it used to be written.
        mine.Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// ⭐ The point of the whole change. The loser's cycle must carry on: it still has the rest
    /// of the installation's work pending in the same context, and that work used to be thrown
    /// away by the collision — along with the dispatch that followed it.
    /// </summary>
    [Fact]
    public async Task A_lost_race_does_not_cost_every_other_tenant_their_cycle()
    {
        using TelemetryDbContext winner = NewInstance();
        using TelemetryDbContext loser = NewInstance();

        await TelemetryAlertEvaluator.TryRaiseAsync(winner, Candidate(clusterId, Fingerprint, "winner", Noon));

        // The loser is mid-cycle: another cluster's incident is already queued in its context
        // when it hits the collision.
        AlertIncident elsewhere = Candidate(otherClusterId, OtherFingerprint, "unrelated", Noon);
        loser.AlertIncidents.Add(elsewhere);

        bool won = await TelemetryAlertEvaluator.TryRaiseAsync(loser, Candidate(clusterId, Fingerprint, "loser", Noon));
        won.Should().BeFalse();

        // No throw out of the claim, and the context is usable afterwards: the end-of-cycle save
        // goes through and the other cluster's incident survives.
        Func<Task> save = () => loser.SaveChangesAsync();
        await save.Should().NotThrowAsync();

        db.AlertIncidents.AsNoTracking().Count(i => i.Fingerprint == OtherFingerprint).Should().Be(1);
        db.AlertIncidents.AsNoTracking().Count(i => i.Fingerprint == Fingerprint).Should().Be(1);
        Stored(Fingerprint).Summary.Should().Be("winner");
    }

    /// <summary>
    /// Pairs with the absence assertions above: the claim refuses a duplicate, not every raise.
    /// A claim that always said no would pass every test on this page but silence alerting.
    /// </summary>
    [Fact]
    public async Task A_different_rule_on_the_same_cluster_still_raises()
    {
        using TelemetryDbContext one = NewInstance();

        (await TelemetryAlertEvaluator.TryRaiseAsync(one, Candidate(clusterId, Fingerprint, "first", Noon)))
            .Should().BeTrue();
        (await TelemetryAlertEvaluator.TryRaiseAsync(one, Candidate(clusterId, "telemetry:rule-c:cluster-a", "second", Noon)))
            .Should().BeTrue();

        db.AlertIncidents.AsNoTracking().Count(i => i.ClusterId == clusterId).Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Resolving and re-raising: no exception, but two notifications
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Two_instances_resolving_the_same_incident_send_one_all_clear()
    {
        using (TelemetryDbContext setup = NewInstance())
            await TelemetryAlertEvaluator.TryRaiseAsync(setup, Candidate(clusterId, Fingerprint, "firing", Noon));

        using TelemetryDbContext one = NewInstance();
        using TelemetryDbContext two = NewInstance();

        // Both read the open incident before either wrote.
        AlertIncident mine = one.AlertIncidents.Single(i => i.Fingerprint == Fingerprint);
        AlertIncident theirs = two.AlertIncidents.Single(i => i.Fingerprint == Fingerprint);
        mine.Status.Should().Be(IncidentStatus.Active);
        theirs.Status.Should().Be(IncidentStatus.Active);

        bool wonFirst = await TelemetryAlertEvaluator.TryResolveAsync(one, mine, Noon.AddMinutes(1));
        bool wonSecond = await TelemetryAlertEvaluator.TryResolveAsync(two, theirs, Noon.AddMinutes(2));

        wonFirst.Should().BeTrue();
        wonSecond.Should().BeFalse();

        // Resolved once, at the moment the winner saw it clear — the loser did not move the
        // timestamp, and did not tell the customer a second time.
        AlertIncident stored = Stored(Fingerprint);
        stored.Status.Should().Be(IncidentStatus.Resolved);
        stored.ResolvedAt.Should().Be(Noon.AddMinutes(1));
        stored.EndsAt.Should().Be(Noon.AddMinutes(1));
    }

    [Fact]
    public async Task Two_instances_re_raising_a_resolved_incident_page_once()
    {
        using (TelemetryDbContext setup = NewInstance())
        {
            await TelemetryAlertEvaluator.TryRaiseAsync(setup, Candidate(clusterId, Fingerprint, "firing", Noon));
            AlertIncident open = setup.AlertIncidents.Single(i => i.Fingerprint == Fingerprint);
            await TelemetryAlertEvaluator.TryResolveAsync(setup, open, Noon.AddMinutes(1));
        }

        using TelemetryDbContext one = NewInstance();
        using TelemetryDbContext two = NewInstance();

        AlertIncident mine = one.AlertIncidents.Single(i => i.Fingerprint == Fingerprint);
        AlertIncident theirs = two.AlertIncidents.Single(i => i.Fingerprint == Fingerprint);

        DateTime again = Noon.AddMinutes(5);
        bool wonFirst = await TelemetryAlertEvaluator.TryReactivateAsync(one, mine, "critical", "mine", "mine", again);
        bool wonSecond = await TelemetryAlertEvaluator.TryReactivateAsync(two, theirs, "warning", "theirs", "theirs", again.AddMinutes(1));

        wonFirst.Should().BeTrue();
        wonSecond.Should().BeFalse();

        // The whole re-raise landed in the winner's claim, not half of it: active again, old
        // resolution cleared, and the loser's text nowhere near it.
        AlertIncident stored = Stored(Fingerprint);
        stored.Status.Should().Be(IncidentStatus.Active);
        stored.Summary.Should().Be("mine");
        stored.Severity.Should().Be("critical");
        stored.StartsAt.Should().Be(again);
        stored.ResolvedAt.Should().BeNull();
        stored.EndsAt.Should().BeNull();
    }

    /// <summary>
    /// The same pairing for the transition claims: they refuse a second mover, not the first one.
    /// </summary>
    [Fact]
    public async Task One_instance_can_resolve_and_re_raise_the_same_incident()
    {
        using TelemetryDbContext one = NewInstance();
        await TelemetryAlertEvaluator.TryRaiseAsync(one, Candidate(clusterId, Fingerprint, "firing", Noon));

        AlertIncident inc = one.AlertIncidents.Single(i => i.Fingerprint == Fingerprint);

        (await TelemetryAlertEvaluator.TryResolveAsync(one, inc, Noon.AddMinutes(1))).Should().BeTrue();
        Stored(Fingerprint).Status.Should().Be(IncidentStatus.Resolved);

        (await TelemetryAlertEvaluator.TryReactivateAsync(one, inc, "critical", "back", "back", Noon.AddMinutes(9)))
            .Should().BeTrue();
        Stored(Fingerprint).Status.Should().Be(IncidentStatus.Active);
        Stored(Fingerprint).Summary.Should().Be("back");
    }
}
