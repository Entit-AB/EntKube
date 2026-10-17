using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose blueprint <see cref="ClusterBlueprintService"/> is willing to read, change and run.
///
/// <para><b>Why this matters more than most.</b> A blueprint is an ordered recipe of components
/// and services, and a bootstrap run <em>installs</em> it on a cluster. Twenty-four of the
/// service's twenty-six public methods took a bare id — a blueprint, a variable, a step, a run or
/// a rollout id — and only <c>GetBlueprintsAsync</c> and <c>CreateBlueprintAsync</c> ever took a
/// tenant. So appending a step was arbitrary software queued for a stranger's next bootstrap, and
/// starting a run was that software installed on their machines.</para>
///
/// <para>⚠️ <c>ClusterBlueprint</c> is the only entity here with a <c>TenantId</c>. Variables,
/// steps and rollouts reach it through one or two navigations; a <c>BootstrapRun</c> has no
/// blueprint navigation at all, only a cluster, so runs scope by <c>r.Cluster.TenantId</c> —
/// three different paths, each of which can be written wrong on its own.</para>
/// </summary>
public sealed class ClusterBlueprintTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory dbFactory;
    private readonly ClusterBlueprintService sut;

    public ClusterBlueprintTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);
        sut = new ClusterBlueprintService(dbFactory);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    /// <summary>A tenant with an environment and a cluster of its own.</summary>
    private (Guid Tenant, Guid Environment, Guid Cluster) Seed(string slug)
    {
        Guid tenant = Guid.NewGuid(), environment = Guid.NewGuid(), cluster = Guid.NewGuid();

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
        db.Environments.Add(new Data.Environment
        {
            Id = environment, TenantId = tenant, Name = $"{slug}-prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster, TenantId = tenant, EnvironmentId = environment,
            Name = $"{slug}-cluster", ApiServerUrl = "https://api.example.com",
        });
        db.SaveChanges();

        return (tenant, environment, cluster);
    }

    // ──────── Blueprints, variables, steps ────────

    [Fact]
    public async Task A_blueprint_is_not_readable_or_editable_by_another_tenant()
    {
        var us = Seed("us");
        var them = Seed("them");

        ClusterBlueprint theirs = await sut.CreateBlueprintAsync(them.Tenant, "Their platform", "theirs");
        await sut.AddStepAsync(
            them.Tenant, theirs.Id, BlueprintStepType.Component, "cert-manager", "cert-manager",
            "cert-manager", null);

        // Reads.
        (await sut.GetBlueprintAsync(us.Tenant, theirs.Id)).Should().BeNull(
            "the blueprint exists, so this is a refusal");
        (await sut.GetBlueprintAsync(them.Tenant, theirs.Id)).Should().NotBeNull("the owner still sees it");
        (await sut.GetBlueprintsAsync(us.Tenant)).Should().BeEmpty();

        // Writes. Each refusal is a throw, because a blueprint we cannot see is not found at all.
        Func<Task> rename = () => sut.UpdateBlueprintAsync(us.Tenant, theirs.Id, "hijacked", null);
        Func<Task> repoint = () => sut.SetProvisioningAsync(us.Tenant, theirs.Id, null, null);
        Func<Task> plantStep = () => sut.AddStepAsync(
            us.Tenant, theirs.Id, BlueprintStepType.Component, "miner", "miner", "kube-system", null);

        await rename.Should().ThrowAsync<InvalidOperationException>();
        await repoint.Should().ThrowAsync<InvalidOperationException>();
        await plantStep.Should().ThrowAsync<InvalidOperationException>();

        await sut.DeleteBlueprintAsync(us.Tenant, theirs.Id);

        ClusterBlueprint survived = (await sut.GetBlueprintAsync(them.Tenant, theirs.Id))!;
        survived.Name.Should().Be("Their platform");
        survived.Steps.Should().ContainSingle("no step of ours was appended to their recipe");

        // And the owner can still delete it, so the refusal above was about scope.
        await sut.DeleteBlueprintAsync(them.Tenant, theirs.Id);
        (await sut.GetBlueprintAsync(them.Tenant, theirs.Id)).Should().BeNull();
    }

    /// <summary>
    /// Variables and steps are the parts with no <c>TenantId</c> of their own, and both are
    /// substituted into what a run installs: a variable value can carry a storage link id, a
    /// step's parameters are what the installer is handed.
    /// </summary>
    [Fact]
    public async Task A_variable_or_step_cannot_be_reached_through_its_blueprint()
    {
        var us = Seed("us");
        var them = Seed("them");

        ClusterBlueprint theirs = await sut.CreateBlueprintAsync(them.Tenant, "Their platform", null);
        BlueprintVariable variable = await sut.AddVariableAsync(
            them.Tenant, theirs.Id, "storage_link", null, "original");
        BlueprintStep step = await sut.AddStepAsync(
            them.Tenant, theirs.Id, BlueprintStepType.Component, "cert-manager", "cert-manager",
            "cert-manager", null);
        await sut.SetVariableValueAsync(them.Tenant, variable.Id, them.Environment, "their-value");

        (await sut.GetVariablesAsync(us.Tenant, theirs.Id)).Should().BeEmpty(
            "the variable exists, so this is a refusal");

        Func<Task> editVariable = () => sut.UpdateVariableAsync(
            us.Tenant, variable.Id, "storage_link", null, "hijacked");
        Func<Task> setValue = () => sut.SetVariableValueAsync(
            us.Tenant, variable.Id, us.Environment, "hijacked");
        Func<Task> editStep = () => sut.UpdateStepAsync(us.Tenant, step.Id, "miner", "kube-system", null);
        Func<Task> moveStep = () => sut.MoveStepAsync(us.Tenant, step.Id, 1);

        await editVariable.Should().ThrowAsync<InvalidOperationException>();
        await setValue.Should().ThrowAsync<InvalidOperationException>();
        await editStep.Should().ThrowAsync<InvalidOperationException>();
        await moveStep.Should().ThrowAsync<InvalidOperationException>();

        // ⚠️ The two deletes refuse SILENTLY: they look the row up with FirstOrDefaultAsync and
        // return when it is null, so there is no exception to catch and the surviving row is the
        // only evidence. An earlier draft of this test expected a throw from them and failed —
        // the refusal shape is not uniform across this service, and asserting the wrong one
        // would have passed against no guard at all once the assertion was "fixed" to match.
        await sut.DeleteVariableAsync(us.Tenant, variable.Id);
        await sut.DeleteStepAsync(us.Tenant, step.Id);

        List<BlueprintVariable> theirVariables = await sut.GetVariablesAsync(them.Tenant, theirs.Id);
        theirVariables.Should().ContainSingle();
        theirVariables[0].DefaultValue.Should().Be("original");
        theirVariables[0].Values.Should().ContainSingle()
            .Which.Value.Should().Be("their-value");
        (await sut.GetBlueprintAsync(them.Tenant, theirs.Id))!.Steps.Should().ContainSingle()
            .Which.Name.Should().Be("cert-manager");
    }

    /// <summary>
    /// ⚠️ <c>SetVariableValueAsync</c> names two rows — the variable and the environment — so it
    /// is guarded as a pair, and the crossing only the environment half can catch is tested
    /// separately: our own variable with their environment.
    /// </summary>
    [Fact]
    public async Task A_variable_value_cannot_be_set_for_another_tenants_environment()
    {
        var us = Seed("us");
        var them = Seed("them");

        ClusterBlueprint ours = await sut.CreateBlueprintAsync(us.Tenant, "Our platform", null);
        BlueprintVariable ourVariable = await sut.AddVariableAsync(us.Tenant, ours.Id, "region", null, null);

        Func<Task> theirEnvironment = () => sut.SetVariableValueAsync(
            us.Tenant, ourVariable.Id, them.Environment, "value");
        await theirEnvironment.Should().ThrowAsync<InvalidOperationException>();

        // Our own pair still works, so the refusal is about the environment and not the variable.
        await sut.SetVariableValueAsync(us.Tenant, ourVariable.Id, us.Environment, "value");
        (await sut.GetVariablesAsync(us.Tenant, ours.Id))[0].Values.Should().ContainSingle();
    }

    // ──────── Runs ────────

    /// <summary>
    /// The sharp end. A run installs the blueprint's steps on a cluster, so the pairing is what
    /// matters: our blueprint on their cluster would install our software on their machines, and
    /// their blueprint on our cluster installs software we never authored.
    /// </summary>
    [Fact]
    public async Task A_bootstrap_run_cannot_pair_a_blueprint_with_another_tenants_cluster()
    {
        var us = Seed("us");
        var them = Seed("them");

        ClusterBlueprint ours = await sut.CreateBlueprintAsync(us.Tenant, "Our platform", null);
        await sut.AddStepAsync(
            us.Tenant, ours.Id, BlueprintStepType.Component, "cert-manager", "cert-manager",
            "cert-manager", null);

        ClusterBlueprint theirs = await sut.CreateBlueprintAsync(them.Tenant, "Their platform", null);
        await sut.AddStepAsync(
            them.Tenant, theirs.Id, BlueprintStepType.Component, "cert-manager", "cert-manager",
            "cert-manager", null);

        // Our blueprint, their cluster.
        Func<Task> ontoTheirCluster = () => sut.StartBootstrapAsync(
            us.Tenant, them.Cluster, ours.Id, null, "test");
        // Their blueprint, our cluster.
        Func<Task> theirRecipe = () => sut.StartBootstrapAsync(
            us.Tenant, us.Cluster, theirs.Id, null, "test");
        // And the dry run of the same pairing.
        Func<Task> preview = () => sut.PreviewVariablesAsync(us.Tenant, them.Cluster, ours.Id);

        await ontoTheirCluster.Should().ThrowAsync<InvalidOperationException>();
        await theirRecipe.Should().ThrowAsync<InvalidOperationException>();
        await preview.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetRunsForClusterAsync(them.Tenant, them.Cluster)).Should().BeEmpty(
            "nothing may be queued against their cluster");

        // Our own pairing still starts.
        BootstrapRun run = await sut.StartBootstrapAsync(us.Tenant, us.Cluster, ours.Id, null, "test");
        run.ClusterId.Should().Be(us.Cluster);
    }

    /// <summary>
    /// A run's own lifecycle. Resuming or cancelling one reaches into an install in progress, and
    /// reading one returns its step runs — whose error text is install output from their cluster.
    /// </summary>
    [Fact]
    public async Task A_run_cannot_be_read_resumed_or_cancelled_by_another_tenant()
    {
        var us = Seed("us");
        var them = Seed("them");

        ClusterBlueprint theirs = await sut.CreateBlueprintAsync(them.Tenant, "Their platform", null);
        await sut.AddStepAsync(
            them.Tenant, theirs.Id, BlueprintStepType.Component, "cert-manager", "cert-manager",
            "cert-manager", null);
        BootstrapRun run = await sut.StartBootstrapAsync(
            them.Tenant, them.Cluster, theirs.Id, null, "test");

        (await sut.GetRunAsync(us.Tenant, run.Id)).Should().BeNull("the run exists, so this is a refusal");
        (await sut.GetRunsForClusterAsync(us.Tenant, them.Cluster)).Should().BeEmpty();
        (await sut.GetBootstrappedClustersAsync(us.Tenant, theirs.Id)).Should().BeEmpty();

        Func<Task> resume = () => sut.ResumeAsync(us.Tenant, run.Id);
        await resume.Should().ThrowAsync<InvalidOperationException>();

        await sut.CancelAsync(us.Tenant, run.Id);
        (await sut.GetRunAsync(them.Tenant, run.Id))!.Status.Should().Be(BootstrapRunStatus.Queued,
            "cancelling a stranger's run halts their install midway");

        // The owner can cancel it.
        await sut.CancelAsync(them.Tenant, run.Id);
        (await sut.GetRunAsync(them.Tenant, run.Id))!.Status.Should().Be(BootstrapRunStatus.Cancelled);
    }

    // ──────── Rollouts ────────

    /// <summary>
    /// A rollout pushes a blueprint to a list of clusters, one at a time. The target list is
    /// checked as a set and refused whole if any cluster is not this tenant's — a rollout that
    /// silently skipped a foreign target would report success over a cluster it never touched.
    /// </summary>
    [Fact]
    public async Task A_rollout_refuses_a_target_list_that_is_not_all_this_tenants()
    {
        var us = Seed("us");
        var them = Seed("them");

        ClusterBlueprint ours = await sut.CreateBlueprintAsync(us.Tenant, "Our platform", null);
        await sut.AddStepAsync(
            us.Tenant, ours.Id, BlueprintStepType.Component, "cert-manager", "cert-manager",
            "cert-manager", null);

        Func<Task> mixed = () => sut.CreateRolloutAsync(
            us.Tenant, ours.Id, [us.Cluster, them.Cluster], autoAdvance: false, triggeredBy: "test");
        await mixed.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetRolloutsForBlueprintAsync(us.Tenant, ours.Id)).Should().BeEmpty(
            "a rollout that cannot reach every target must not exist at all");
        (await sut.GetRunsForClusterAsync(them.Tenant, them.Cluster)).Should().BeEmpty();

        // Ours alone goes through.
        BlueprintRollout rollout = await sut.CreateRolloutAsync(
            us.Tenant, ours.Id, [us.Cluster], autoAdvance: false, triggeredBy: "test");
        rollout.Targets.Should().ContainSingle();

        // And a stranger can neither read it, promote it nor cancel it.
        (await sut.GetRolloutAsync(them.Tenant, rollout.Id)).Should().BeNull();
        (await sut.GetRolloutsForBlueprintAsync(them.Tenant, ours.Id)).Should().BeEmpty();

        Func<Task> promote = () => sut.StartNextTargetAsync(them.Tenant, rollout.Id);
        await promote.Should().ThrowAsync<InvalidOperationException>();

        await sut.CancelRolloutAsync(them.Tenant, rollout.Id);
        (await sut.GetRolloutAsync(us.Tenant, rollout.Id))!.Status.Should().Be(RolloutStatus.InProgress,
            "cancelling a stranger's rollout halts their staged update");
    }
}
