using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Rollouts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// That a rollout is judged once, however many instances of EntKube are running.
///
/// <para><b>The bug these defend against.</b> <c>RolloutWatcherService</c> polls every minute
/// for rollouts that are <c>Watching</c> and past their deadline. On two instances both polls
/// return the same rollout, both measure the same failing window, and both call
/// <c>kubectl rollout undo</c>. Undo has no <c>--to-revision</c>, so it means "go to the
/// previous revision" — and once the first undo has landed, the previous revision <em>is</em>
/// the bad release. The second undo therefore rolls forward onto it. The net effect of the
/// automatic rollback was nothing at all, the bad release stayed live, and both instances
/// recorded a verdict reading "rolled back automatically". A wrong number on a dashboard is
/// recoverable; a written record of a rollback that did not happen is what sends an engineer
/// looking in the wrong place at 3am.</para>
///
/// <para><b>What makes it a claim and not a lock.</b> The fix is one compare-and-set on
/// <c>DecideAt</c>, in the shape <c>CostLedgerWriter</c> already uses for its per-cluster
/// period cursor. Two instances race, exactly one wins, and the loser does nothing and reports
/// no error. Nothing blocks, nothing is held, and no lease table was added.</para>
///
/// <para>⚠️ Every "nothing happened" assertion below is paired with a positive one in the same
/// class — <see cref="The_instance_that_wins_the_claim_rolls_back_once_and_records_the_verdict"/>
/// is what stops "no rollback happened" from passing because the fixture never could have
/// produced one.</para>
/// </summary>
public class RolloutJudgementClaimTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    /// <summary>The watcher's tick. Every time below is expressed relative to it.</summary>
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly RolloutService rollouts;
    private readonly RolloutWatcherService watcher;
    private readonly NotificationService notifications;

    /// <summary>Every namespace an undo was asked for. The count is the whole point of this class.</summary>
    private readonly List<string> undone = [];

    private Guid tenantId, deploymentId, rolloutId;
    private AppDeployment deployment = null!;

    public RolloutJudgementClaimTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.UndoDeploymentsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string ns, string _, string? __, CancellationToken ___) => undone.Add(ns))
            .ReturnsAsync(new WorkloadUndoResult(true, "deployment.apps/api: rolled back"));

        // Only readiness is judged here, and readiness comes from the health snapshots in this
        // same database. The metric, trace and error-budget collaborators are passed null on
        // purpose: MeasureAsync guards each signal with its own try/catch, so an absent
        // collaborator reads as "could not be measured" — exactly as it does in production when
        // Prometheus is down — and dragging the whole metrics stack into a test about who may
        // roll back would hide what is being asserted.
        rollouts = new RolloutService(
            testDb.Factory, null!, null!, null!,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            NullLogger<RolloutService>.Instance);

        notifications = new NotificationService(
            new Mock<IHttpClientFactory>().Object, testDb.Factory,
            TestServices.TestConfiguration(), NullLogger<NotificationService>.Instance);

        // The scope factory is only touched by the polling loop, which these tests do not run:
        // they call DecideAsync, which is the part that must happen once.
        watcher = new RolloutWatcherService(null!, NullLogger<RolloutWatcherService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A release applied 15 minutes ago whose pods never came ready, with a policy that rolls
    /// back on failure. Its window closed 3 minutes before the tick, so it is due.
    /// </summary>
    private async Task SeedFailingRolloutAsync()
    {
        tenantId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid(), envId = Guid.NewGuid(),
             customerId = Guid.NewGuid(), appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = $"acme-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod", ApiServerUrl = "https://k8s.example.com",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Acme AB" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });

        deploymentId = Guid.NewGuid();
        deployment = new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "Billing API", Type = DeploymentType.Manual,
            EnvironmentId = envId, ClusterId = clusterId, Namespace = "acme-prod", IsManaged = true,
        };
        db.AppDeployments.Add(deployment);

        db.RolloutPolicies.Add(new RolloutPolicy
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            IsEnabled = true,
            WarmupMinutes = 2,
            AnalysisWindowMinutes = 10,
            // Readiness alone. Every other threshold is nulled so that an unmeasurable signal
            // cannot quietly turn the verdict Inconclusive and take the rollback path away.
            MinReadyFraction = 1.0,
            MaxRestarts = null,
            MaxErrorRatePercent = null,
            MaxLatencyP95Ms = null,
            MaxErrorBudgetBurnRate = null,
            OnFailure = RolloutFailureAction.Rollback,
        });

        rolloutId = Guid.NewGuid();
        db.DeploymentRollouts.Add(new DeploymentRollout
        {
            Id = rolloutId,
            DeploymentId = deploymentId,
            Status = DeploymentRolloutStatus.Watching,
            StartedAt = Now.AddMinutes(-15),
            DecideAt = Now.AddMinutes(-3),
        });

        // None of the three replicas ever became ready: a breach of MinReadyFraction, which is
        // what puts this rollout on the rollback path rather than the promote one.
        db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            SnapshotAt = Now.AddMinutes(-1),
            ReadyReplicas = 0,
            TotalReplicas = 3,
        });

        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    private Task DecideAsync() =>
        watcher.DecideAsync(tenantId, rolloutId, Now, testDb.Factory, rollouts, notifications, default);

    private async Task<DeploymentRollout> ReadRolloutAsync()
    {
        await using DeliveryDbContext fresh = ((IDbContextFactory<DeliveryDbContext>)testDb.Factory)
            .CreateDbContext();
        return await fresh.DeploymentRollouts.AsNoTracking().SingleAsync(r => r.Id == rolloutId);
    }

    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The positive half. Without this, every "it did nothing" assertion in this class could be
    /// passing because the fixture is incapable of rolling anything back.
    /// </summary>
    [Fact]
    public async Task The_instance_that_wins_the_claim_rolls_back_once_and_records_the_verdict()
    {
        await SeedFailingRolloutAsync();

        await DecideAsync();

        undone.Should().ContainSingle().Which.Should().Be("acme-prod");

        DeploymentRollout closed = await ReadRolloutAsync();
        closed.Status.Should().Be(DeploymentRolloutStatus.RolledBack);
        closed.Verdict.Should().Contain("rolled back automatically");
        closed.FinishedAt.Should().Be(Now);
    }

    /// <summary>
    /// ⭐ The race itself: two instances that both read the rollout before either wrote.
    ///
    /// <para>Passing the <em>same</em> <c>decideAt</c> to both calls is what makes this the
    /// concurrent case rather than a sequential one — it is precisely the state two instances
    /// are in when their one-minute polls land together, each holding a snapshot taken before
    /// the other acted. A compare-and-set is the only thing that can separate them, and this is
    /// the assertion that a plain write could not pass.</para>
    /// </summary>
    [Fact]
    public async Task Two_instances_holding_the_same_snapshot_cannot_both_claim_the_judgement()
    {
        await SeedFailingRolloutAsync();
        DateTime decideAt = (await ReadRolloutAsync()).DecideAt;

        bool first = await rollouts.TryClaimForJudgementAsync(rolloutId, decideAt, Now.AddMinutes(10));
        bool second = await rollouts.TryClaimForJudgementAsync(rolloutId, decideAt, Now.AddMinutes(10));

        first.Should().BeTrue("one of the two instances must be allowed to judge");
        second.Should().BeFalse("the other must not, or production is undone twice");

        // And the loser lost without erroring and without moving the deadline a second time.
        (await ReadRolloutAsync()).DecideAt.Should().Be(Now.AddMinutes(10));
    }

    /// <summary>
    /// The sibling that arrives a moment late, after the winner has already claimed. It never
    /// reaches the cluster and it never writes a verdict — the rollout is left exactly as the
    /// winner's in-flight judgement expects to find it.
    /// </summary>
    [Fact]
    public async Task An_instance_that_lost_the_claim_neither_rolls_back_nor_writes_a_verdict()
    {
        await SeedFailingRolloutAsync();
        DateTime decideAt = (await ReadRolloutAsync()).DecideAt;

        // Instance A claims and is now measuring.
        (await rollouts.TryClaimForJudgementAsync(rolloutId, decideAt, Now.AddMinutes(10)))
            .Should().BeTrue();

        // Instance B, whose poll found the same rollout a moment earlier, now tries to judge it.
        await DecideAsync();

        undone.Should().BeEmpty("the second undo would roll forward onto the bad release again");

        DeploymentRollout untouched = await ReadRolloutAsync();
        untouched.Status.Should().Be(DeploymentRolloutStatus.Watching,
            "the rollout belongs to the instance that claimed it");
        untouched.Verdict.Should().BeNull();
        untouched.FinishedAt.Should().BeNull();
    }

    /// <summary>
    /// The claim re-arms rather than disarms: if the winner dies mid-judgement, the rollout
    /// becomes due again once the claim expires instead of sitting in <c>Watching</c> forever.
    /// </summary>
    [Fact]
    public async Task A_claim_that_expires_without_a_verdict_lets_the_rollout_be_judged_again()
    {
        await SeedFailingRolloutAsync();
        DateTime decideAt = (await ReadRolloutAsync()).DecideAt;

        // The winner claims — and then its process is killed, so no verdict is ever written.
        (await rollouts.TryClaimForJudgementAsync(rolloutId, decideAt, Now.AddMinutes(10)))
            .Should().BeTrue();

        // Eleven minutes later another instance finds it due again.
        DateTime later = Now.AddMinutes(11);
        await watcher.DecideAsync(tenantId, rolloutId, later, testDb.Factory, rollouts, notifications, default);

        undone.Should().ContainSingle("the judgement was retried, not abandoned");
        (await ReadRolloutAsync()).Status.Should().Be(DeploymentRolloutStatus.RolledBack);
    }

    /// <summary>
    /// The second read-modify-write: <c>CloseAsync</c>. It used to read the row, set the status
    /// and save, with nothing in between — so the later writer simply overwrote the earlier
    /// verdict. It matters beyond the two-watcher case: a new apply landing mid-analysis marks
    /// the open watch <c>Superseded</c>, and the unguarded close would have replaced that with a
    /// verdict about a release that was no longer running.
    /// </summary>
    [Fact]
    public async Task A_rollout_already_closed_is_not_reopened_by_a_second_verdict()
    {
        await SeedFailingRolloutAsync();

        bool firstClose = await rollouts.CloseAsync(
            rolloutId, DeploymentRolloutStatus.Superseded, "A newer release was applied.",
            new RolloutSignals(), Now);

        bool secondClose = await rollouts.CloseAsync(
            rolloutId, DeploymentRolloutStatus.RolledBack, "rolled back automatically",
            new RolloutSignals(), Now.AddMinutes(1));

        firstClose.Should().BeTrue();
        secondClose.Should().BeFalse("the rollout was no longer Watching");

        DeploymentRollout row = await ReadRolloutAsync();
        row.Status.Should().Be(DeploymentRolloutStatus.Superseded);
        row.Verdict.Should().Be("A newer release was applied.");
        row.FinishedAt.Should().Be(Now);
    }
}
