using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The property these defend: a queued bootstrap run is executed by exactly one instance.
///
/// <para>A run installs helm releases and creates CNPG / Redis / RabbitMQ clusters on a real
/// customer cluster. Two management-plane instances polling the same queue used to both read
/// <c>Queued</c>, both write <c>Running</c>, and both drive the whole blueprint — the second
/// one landing on top of the first mid-install. The old guard (<c>run.Status != Queued</c> on
/// a value read a moment earlier) only ever excluded two sequential passes in ONE process.</para>
///
/// <para>⚠️ The race is modelled as an interleaving, not as threads: both readers see
/// <c>Queued</c> first and only then attempt the claim, which is precisely the ordering the
/// old code could not survive and the one a wall-clock race would only sometimes produce.
/// One test does run the two attempts concurrently as well, so the claim is exercised the way
/// CostLedgerTests exercises the ledger's period claim.</para>
///
/// <para>Every "the loser did nothing" assertion is paired with the winner's effect, because an
/// absence on its own is also what a claim that never fires at all looks like.</para>
/// </summary>
public sealed class BootstrapRunClaimTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory dbFactory;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid otherClusterId = Guid.NewGuid();
    private readonly Guid blueprintId = Guid.NewGuid();

    public BootstrapRunClaimTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        Guid environmentId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = $"acme-{tenantId:N}" });
        db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Globex", Slug = $"globex-{otherTenantId:N}" });
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = "prod" });
        db.ClusterBlueprints.Add(new ClusterBlueprint { Id = blueprintId, TenantId = tenantId, Name = "Platform" });

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-a", ApiServerUrl = "https://api",
        });

        // A second tenant's cluster, so "another tenant cannot claim it" is a real comparison
        // between two rows rather than a row compared with itself.
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = otherClusterId, TenantId = otherTenantId, EnvironmentId = environmentId,
            Name = "globex-a", ApiServerUrl = "https://api",
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedRun(
        BootstrapRunStatus status = BootstrapRunStatus.Queued,
        Guid? onCluster = null,
        DateTime? startedAt = null)
    {
        Guid runId = Guid.NewGuid();
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.BootstrapRuns.Add(new BootstrapRun
        {
            Id = runId,
            ClusterId = onCluster ?? clusterId,
            BlueprintId = blueprintId,
            BlueprintName = "Platform",
            Status = status,
            StartedAt = startedAt,
            CreatedAt = Noon.AddMinutes(-5),
        });
        db.SaveChanges();
        return runId;
    }

    private async Task<BootstrapRun> ReloadAsync(Guid runId)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.BootstrapRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
    }

    [Fact]
    public async Task Two_instances_that_both_see_the_run_queued_produce_one_winner()
    {
        Guid runId = SeedRun();

        using ApplicationDbContext first = dbFactory.CreateDbContext();
        using ApplicationDbContext second = dbFactory.CreateDbContext();

        // Both poll loops read the run and both see Queued. This is the state the old code
        // acted on, and from here it wrote Running twice.
        (await first.BootstrapRuns.FirstAsync(r => r.Id == runId)).Status
            .Should().Be(BootstrapRunStatus.Queued);
        (await second.BootstrapRuns.FirstAsync(r => r.Id == runId)).Status
            .Should().Be(BootstrapRunStatus.Queued);

        bool firstWon = await BootstrapRunnerService.TryClaimAsync(first, runId, tenantId, Noon);
        bool secondWon = await BootstrapRunnerService.TryClaimAsync(second, runId, tenantId, Noon.AddSeconds(1));

        firstWon.Should().BeTrue("the first instance to reach the conditional update owns the run");
        secondWon.Should().BeFalse("the row is no longer Queued, so the loser's update matches nothing");

        BootstrapRun run = await ReloadAsync(runId);
        run.Status.Should().Be(BootstrapRunStatus.Running);
        run.StartedAt.Should().Be(Noon, "the winner's clock is the one that started the run");
    }

    [Fact]
    public async Task Two_concurrent_attempts_on_one_run_are_won_once()
    {
        Guid runId = SeedRun();

        using ApplicationDbContext first = dbFactory.CreateDbContext();
        using ApplicationDbContext second = dbFactory.CreateDbContext();

        Task<bool> a = BootstrapRunnerService.TryClaimAsync(first, runId, tenantId, Noon);
        Task<bool> b = BootstrapRunnerService.TryClaimAsync(second, runId, tenantId, Noon);

        bool[] results = await Task.WhenAll(a, b);

        results.Count(won => won).Should().Be(1, "exactly one instance may install the blueprint");
        (await ReloadAsync(runId)).Status.Should().Be(BootstrapRunStatus.Running);
    }

    [Fact]
    public async Task A_run_already_running_cannot_be_claimed_again()
    {
        // The crashed-runner case, and the second poll of the same instance: the run is
        // Running with nobody behind it. Re-claiming it would start the steps a second time
        // underneath the first runner, so it stays unclaimable until an operator resumes it
        // (which puts it back to Queued — the test below).
        Guid running = SeedRun(BootstrapRunStatus.Running, startedAt: Noon.AddMinutes(-1));
        Guid queued = SeedRun();

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        (await BootstrapRunnerService.TryClaimAsync(db, running, tenantId, Noon)).Should().BeFalse();
        (await BootstrapRunnerService.TryClaimAsync(db, queued, tenantId, Noon)).Should().BeTrue();

        (await ReloadAsync(running)).StartedAt.Should().Be(Noon.AddMinutes(-1), "nothing was rewritten");
    }

    [Fact]
    public async Task A_cancelled_run_is_not_claimable_but_a_queued_one_beside_it_is()
    {
        Guid cancelled = SeedRun(BootstrapRunStatus.Cancelled);
        Guid queued = SeedRun();

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        (await BootstrapRunnerService.TryClaimAsync(db, cancelled, tenantId, Noon)).Should().BeFalse();
        (await BootstrapRunnerService.TryClaimAsync(db, queued, tenantId, Noon)).Should().BeTrue();

        (await ReloadAsync(cancelled)).Status.Should().Be(BootstrapRunStatus.Cancelled);
        (await ReloadAsync(queued)).Status.Should().Be(BootstrapRunStatus.Running);
    }

    [Fact]
    public async Task A_resumed_run_is_claimable_again_and_keeps_the_time_it_first_started()
    {
        // Resume (ClusterBlueprintService) puts a failed run back to Queued with StartedAt
        // already set. The claim must take it — otherwise resume silently does nothing — and
        // must not restart the clock the UI measures elapsed time from.
        DateTime firstStart = Noon.AddHours(-3);
        Guid runId = SeedRun(startedAt: firstStart);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        (await BootstrapRunnerService.TryClaimAsync(db, runId, tenantId, Noon)).Should().BeTrue();

        BootstrapRun run = await ReloadAsync(runId);
        run.Status.Should().Be(BootstrapRunStatus.Running);
        run.StartedAt.Should().Be(firstStart);
    }

    [Fact]
    public async Task Another_tenant_cannot_claim_this_tenants_run_but_its_owner_can()
    {
        // Stranger first, then the owner: an "it was refused" assertion on its own would also
        // pass if the claim refused everyone.
        Guid runId = SeedRun();

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        (await BootstrapRunnerService.TryClaimAsync(db, runId, otherTenantId, Noon))
            .Should().BeFalse("the run belongs to Acme's cluster, not Globex's");
        (await ReloadAsync(runId)).Status.Should().Be(BootstrapRunStatus.Queued);

        (await BootstrapRunnerService.TryClaimAsync(db, runId, tenantId, Noon)).Should().BeTrue();
        (await ReloadAsync(runId)).Status.Should().Be(BootstrapRunStatus.Running);
    }

    [Fact]
    public async Task Claiming_one_run_leaves_the_other_queued_runs_alone()
    {
        Guid mine = SeedRun();
        Guid sibling = SeedRun();
        Guid otherTenants = SeedRun(onCluster: otherClusterId);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        (await BootstrapRunnerService.TryClaimAsync(db, mine, tenantId, Noon)).Should().BeTrue();

        (await ReloadAsync(mine)).Status.Should().Be(BootstrapRunStatus.Running);
        (await ReloadAsync(sibling)).Status.Should().Be(BootstrapRunStatus.Queued);
        (await ReloadAsync(otherTenants)).Status.Should().Be(BootstrapRunStatus.Queued);
    }
}
