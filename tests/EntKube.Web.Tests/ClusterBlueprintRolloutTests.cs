using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Exercises the staged blueprint-rollout state machine in ClusterBlueprintService:
/// one-at-a-time promotion, halt-on-failure, and auto-advance. The runner is simulated
/// by setting a run's status and invoking OnRunFinishedAsync (what the runner does).
/// </summary>
public sealed class ClusterBlueprintRolloutTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly ClusterBlueprintService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid blueprintId = Guid.NewGuid();
    private readonly Guid clusterA = Guid.NewGuid();
    private readonly Guid clusterB = Guid.NewGuid();

    public ClusterBlueprintRolloutTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);
        sut = new ClusterBlueprintService(dbFactory);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        db.Environments.Add(new EntKube.Web.Data.Environment { Id = environmentId, TenantId = tenantId, Name = "prod" });
        db.ClusterBlueprints.Add(new ClusterBlueprint { Id = blueprintId, TenantId = tenantId, Name = "Platform" });
        db.BlueprintSteps.Add(new BlueprintStep
        {
            Id = Guid.NewGuid(), BlueprintId = blueprintId, Order = 0,
            StepType = BlueprintStepType.Component, Key = "cert-manager", Name = "cert-manager"
        });

        // Two clusters, each already bootstrapped from the blueprint (a past, succeeded run).
        foreach ((Guid id, string name) in new[] { (clusterA, "prod-a"), (clusterB, "prod-b") })
        {
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = id, TenantId = tenantId, EnvironmentId = environmentId, Name = name, ApiServerUrl = "https://api"
            });
            db.BootstrapRuns.Add(new BootstrapRun
            {
                Id = Guid.NewGuid(), ClusterId = id, BlueprintId = blueprintId,
                BlueprintName = "Platform", Status = BootstrapRunStatus.Succeeded
            });
        }

        db.SaveChanges();
    }

    private async Task CompleteRunningTargetAsync(Guid rolloutId, bool success)
    {
        BlueprintRollout rollout = (await sut.GetRolloutAsync(rolloutId))!;
        BlueprintRolloutTarget running = rollout.Targets.Single(t => t.Status == RolloutTargetStatus.Running);
        Guid runId = running.BootstrapRunId!.Value;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BootstrapRun run = await db.BootstrapRuns.FirstAsync(r => r.Id == runId);
            run.Status = success ? BootstrapRunStatus.Succeeded : BootstrapRunStatus.Failed;
            await db.SaveChangesAsync();
        }

        await sut.OnRunFinishedAsync(runId);
    }

    [Fact]
    public async Task GetBootstrappedClusters_ReturnsBothClusters()
    {
        List<(Guid ClusterId, string ClusterName)> clusters = await sut.GetBootstrappedClustersAsync(blueprintId);
        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, c => c.ClusterName == "prod-a");
        Assert.Contains(clusters, c => c.ClusterName == "prod-b");
    }

    [Fact]
    public async Task CreateRollout_StartsFirstTargetOnly()
    {
        BlueprintRollout rollout = await sut.CreateRolloutAsync(
            blueprintId, [clusterA, clusterB], autoAdvance: false, triggeredBy: "test");

        Assert.Equal(RolloutStatus.InProgress, rollout.Status);
        Assert.Equal(2, rollout.Targets.Count);
        Assert.Equal(RolloutTargetStatus.Running, rollout.Targets.Single(t => t.Order == 0).Status);
        Assert.Equal(RolloutTargetStatus.Pending, rollout.Targets.Single(t => t.Order == 1).Status);

        // An Update-mode run was created for the first cluster.
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BootstrapRun run = await db.BootstrapRuns.FirstAsync(r => r.RolloutTargetId != null);
        Assert.Equal(BootstrapRunMode.Update, run.Mode);
        Assert.Equal(clusterA, run.ClusterId);
    }

    [Fact]
    public async Task ManualPromotion_AdvancesOneAtATimeToCompletion()
    {
        BlueprintRollout rollout = await sut.CreateRolloutAsync(
            blueprintId, [clusterA, clusterB], autoAdvance: false, triggeredBy: "test");

        // First cluster succeeds; rollout waits for manual promotion (second still pending).
        await CompleteRunningTargetAsync(rollout.Id, success: true);
        BlueprintRollout afterFirst = (await sut.GetRolloutAsync(rollout.Id))!;
        Assert.Equal(RolloutStatus.InProgress, afterFirst.Status);
        Assert.Equal(RolloutTargetStatus.Succeeded, afterFirst.Targets.Single(t => t.Order == 0).Status);
        Assert.Equal(RolloutTargetStatus.Pending, afterFirst.Targets.Single(t => t.Order == 1).Status);

        // Promote the next cluster.
        await sut.StartNextTargetAsync(rollout.Id);
        BlueprintRollout afterPromote = (await sut.GetRolloutAsync(rollout.Id))!;
        Assert.Equal(RolloutTargetStatus.Running, afterPromote.Targets.Single(t => t.Order == 1).Status);

        // Second cluster succeeds → rollout completes.
        await CompleteRunningTargetAsync(rollout.Id, success: true);
        BlueprintRollout done = (await sut.GetRolloutAsync(rollout.Id))!;
        Assert.Equal(RolloutStatus.Completed, done.Status);
        Assert.All(done.Targets, t => Assert.Equal(RolloutTargetStatus.Succeeded, t.Status));
    }

    [Fact]
    public async Task FailedTarget_HaltsRollout()
    {
        BlueprintRollout rollout = await sut.CreateRolloutAsync(
            blueprintId, [clusterA, clusterB], autoAdvance: false, triggeredBy: "test");

        await CompleteRunningTargetAsync(rollout.Id, success: false);

        BlueprintRollout failed = (await sut.GetRolloutAsync(rollout.Id))!;
        Assert.Equal(RolloutStatus.Failed, failed.Status);
        Assert.Equal(RolloutTargetStatus.Failed, failed.Targets.Single(t => t.Order == 0).Status);
        Assert.Equal(RolloutTargetStatus.Pending, failed.Targets.Single(t => t.Order == 1).Status);
    }

    [Fact]
    public async Task AutoAdvance_PromotesNextAutomaticallyOnSuccess()
    {
        BlueprintRollout rollout = await sut.CreateRolloutAsync(
            blueprintId, [clusterA, clusterB], autoAdvance: true, triggeredBy: "test");

        // Completing the first target auto-starts the second (no manual promotion).
        await CompleteRunningTargetAsync(rollout.Id, success: true);

        BlueprintRollout afterFirst = (await sut.GetRolloutAsync(rollout.Id))!;
        Assert.Equal(RolloutTargetStatus.Succeeded, afterFirst.Targets.Single(t => t.Order == 0).Status);
        Assert.Equal(RolloutTargetStatus.Running, afterFirst.Targets.Single(t => t.Order == 1).Status);

        await CompleteRunningTargetAsync(rollout.Id, success: true);
        Assert.Equal(RolloutStatus.Completed, (await sut.GetRolloutAsync(rollout.Id))!.Status);
    }

    [Fact]
    public async Task CreateRollout_RejectsSecondConcurrentRollout()
    {
        await sut.CreateRolloutAsync(blueprintId, [clusterA], autoAdvance: false, triggeredBy: "test");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateRolloutAsync(blueprintId, [clusterB], autoAdvance: false, triggeredBy: "test"));
    }

    public void Dispose() => connection.Dispose();
}
