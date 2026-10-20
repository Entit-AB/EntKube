using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose app's autoscalers.
///
/// <para>Every entry point in <see cref="KedaScalerService"/> is keyed on the (app, environment)
/// pair rather than on a scaler id, and most of them took that pair with no tenant beside it. An
/// autoscaler decides how many replicas a workload runs, so the reach was: read what somebody
/// else's app scales on (the triggers block names their queues, their hosts, their metrics),
/// change it, or delete it so the next apply drops it.</para>
///
/// <para>Three of the methods already took a <c>tenantId</c> and used it only to stamp
/// <c>TenantId</c> on a newly created row — the update branch of the same method looked the
/// existing row up by bare id. A tenant that is written but never compared is not a check.</para>
/// </summary>
public class KedaScalerTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KedaScalerService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirAppId = Guid.NewGuid();
    private readonly Guid theirEnvId = Guid.NewGuid();

    private const string Triggers =
        "- type: rabbitmq\n  metadata:\n    queueName: journal-export\n    queueLength: \"5\"\n";

    public KedaScalerTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid customerId = Guid.NewGuid();
        Guid theirCustomerId = Guid.NewGuid();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
        db.Environments.AddRange(
            new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" },
            new Data.Environment { Id = theirEnvId, TenantId = otherTenantId, Name = "production" });
        db.Customers.AddRange(
            new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" },
            new Customer { Id = theirCustomerId, TenantId = otherTenantId, Name = "Annan AB" });
        db.Apps.AddRange(
            new App { Id = appId, CustomerId = customerId, Name = "billing" },
            new App { Id = theirAppId, CustomerId = theirCustomerId, Name = "journal" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);

        sut = new KedaScalerService(
            factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                factory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.CatalogApi(factory),
            NullLogger<KedaScalerService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedTheirScaler(Guid? environmentId = null, string name = "journal-export")
    {
        KedaScaler scaler = new()
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenantId,
            AppId = theirAppId,
            EnvironmentId = environmentId ?? theirEnvId,
            Name = name,
            Kind = KedaScalerKind.ScaledObject,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = "journal-export",
            MinReplicaCount = 2,
            MaxReplicaCount = 20,
            TriggersYaml = Triggers,
        };
        db.KedaScalers.Add(scaler);
        db.SaveChanges();
        return scaler.Id;
    }

    [Fact]
    public async Task Another_tenants_autoscalers_are_not_listed()
    {
        SeedTheirScaler();

        List<KedaScaler> found = await sut.GetScalersAsync(tenantId, theirAppId, theirEnvId);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task An_autoscaler_cannot_be_created_against_another_tenants_app()
    {
        Func<Task> act = () => sut.SaveScaledObjectAsync(
            tenantId, theirAppId, theirEnvId, null,
            "stolen", "Deployment", "journal-export", 1, 5, null, null, Triggers);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        (await db.KedaScalers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Half a crossing. The guard asks two questions — is the app ours, is the environment ours
    /// — and every test above gets both wrong at once, which leaves either one of them able to
    /// carry the refusal alone. These two cross one half at a time, so each clause has to earn
    /// its place.
    /// </summary>
    [Fact]
    public async Task Our_own_app_in_another_tenants_environment_is_refused()
    {
        Func<Task> act = () => sut.SaveScaledObjectAsync(
            tenantId, appId, theirEnvId, null,
            "billing-api", "Deployment", "billing-api", 1, 5, null, null, Triggers);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        (await db.KedaScalers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_app_in_our_own_environment_is_refused()
    {
        Func<Task> act = () => sut.SaveScaledObjectAsync(
            tenantId, theirAppId, envId, null,
            "journal-export", "Deployment", "journal-export", 1, 5, null, null, Triggers);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        (await db.KedaScalers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_hpa_cannot_be_created_against_another_tenants_app()
    {
        Func<Task> act = () => sut.SaveHpaAsync(
            tenantId, theirAppId, theirEnvId, null,
            "stolen", "Deployment", "journal-export", 1, 5, 70, null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        (await db.KedaScalers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_custom_autoscaler_cannot_be_created_against_another_tenants_app()
    {
        Func<Task> act = () => sut.SaveCustomAsync(
            tenantId, theirAppId, theirEnvId, null, "stolen",
            "apiVersion: keda.sh/v1alpha1\nkind: ScaledObject\n");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        (await db.KedaScalers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Plant, don't steal. Both halves of the copy filter on <c>TenantId</c> as well as on the
    /// (app, environment) pair, which the pair guard has already vouched for — so a crossing
    /// call cannot reach either clause. What can is a row that is already wrong: a scaler
    /// sitting in our app's environment carrying somebody else's tenant, which is precisely
    /// what the old <c>TenantId = src.TenantId</c> stamp wrote. The copy must neither
    /// propagate it nor delete it.
    /// </summary>
    [Fact]
    public async Task A_foreign_row_inside_our_own_environment_is_neither_copied_nor_deleted()
    {
        Guid stagingId = Guid.NewGuid();
        db.Environments.Add(new Data.Environment
        {
            Id = stagingId, TenantId = tenantId, Name = "staging",
        });
        db.SaveChanges();

        // Our app, our environments — but stamped with their tenant.
        KedaScaler inSource = new()
        {
            Id = Guid.NewGuid(), TenantId = otherTenantId, AppId = appId, EnvironmentId = envId,
            Name = "fran-tidigare-bugg", Kind = KedaScalerKind.ScaledObject,
            ScaleTargetKind = "Deployment", ScaleTargetName = "billing-api",
            TriggersYaml = Triggers,
        };
        KedaScaler inTarget = new()
        {
            Id = Guid.NewGuid(), TenantId = otherTenantId, AppId = appId,
            EnvironmentId = stagingId,
            Name = "ocksa-fran-bugg", Kind = KedaScalerKind.ScaledObject,
            ScaleTargetKind = "Deployment", ScaleTargetName = "billing-api",
            TriggersYaml = Triggers,
        };
        db.KedaScalers.AddRange(inSource, inTarget);
        db.SaveChanges();

        await sut.CopyFromEnvironmentAsync(tenantId, appId, envId, stagingId);

        // Not read from the source: nothing of theirs was cloned into staging.
        (await db.KedaScalers.AsNoTracking()
            .AnyAsync(s => s.EnvironmentId == stagingId && s.Name == "fran-tidigare-bugg"))
            .Should().BeFalse();

        // Not deleted from the target: the replace only clears what is ours.
        (await db.KedaScalers.AsNoTracking().AnyAsync(s => s.Id == inTarget.Id))
            .Should().BeTrue();
    }

    /// <summary>
    /// The update branch. <c>tenantId</c> was already a parameter here — it was stamped onto a
    /// new row and ignored when an existing one was loaded, which is why passing an id was
    /// enough.
    /// </summary>
    [Fact]
    public async Task Another_tenants_autoscaler_cannot_be_rewritten_by_id()
    {
        Guid theirs = SeedTheirScaler();

        Func<Task> act = () => sut.SaveScaledObjectAsync(
            tenantId, appId, envId, theirs,
            "billing-api", "Deployment", "billing-api", 1, 1, null, null,
            "- type: cpu\n  metadata:\n    value: \"90\"\n");

        await act.Should().ThrowAsync<InvalidOperationException>();

        KedaScaler stored = await db.KedaScalers.AsNoTracking().FirstAsync(s => s.Id == theirs);
        stored.Name.Should().Be("journal-export");
        stored.MaxReplicaCount.Should().Be(20);
        stored.TriggersYaml.Should().Be(Triggers);
        stored.AppId.Should().Be(theirAppId);
    }

    [Fact]
    public async Task Another_tenants_hpa_cannot_be_rewritten_by_id()
    {
        Guid theirs = SeedTheirScaler();

        Func<Task> act = () => sut.SaveHpaAsync(
            tenantId, appId, envId, theirs,
            "billing-api", "Deployment", "billing-api", 1, 1, 90, null, null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.KedaScalers.AsNoTracking().FirstAsync(s => s.Id == theirs))
            .Name.Should().Be("journal-export");
    }

    [Fact]
    public async Task Another_tenants_custom_autoscaler_cannot_be_rewritten_by_id()
    {
        Guid theirs = SeedTheirScaler();

        Func<Task> act = () => sut.SaveCustomAsync(
            tenantId, appId, envId, theirs, "billing-api", "apiVersion: keda.sh/v1alpha1\n");

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.KedaScalers.AsNoTracking().FirstAsync(s => s.Id == theirs))
            .Name.Should().Be("journal-export");
    }

    [Fact]
    public async Task Another_tenants_autoscaler_cannot_be_deleted()
    {
        Guid theirs = SeedTheirScaler();

        bool deleted = await sut.DeleteScalerAsync(tenantId, theirs);

        deleted.Should().BeFalse();
        (await db.KedaScalers.AsNoTracking().AnyAsync(s => s.Id == theirs)).Should().BeTrue();
    }

    /// <summary>
    /// The sharpest of the set: the copy replaces the target environment, so it deletes before
    /// it copies. Naming another tenant's app wiped that app's autoscalers in the target
    /// environment — and the rows it wrote back were stamped with the tenant read off the
    /// source row, not the caller's.
    /// </summary>
    [Fact]
    public async Task Copying_between_another_tenants_environments_destroys_nothing()
    {
        Guid theirSecondEnv = Guid.NewGuid();
        db.Environments.Add(new Data.Environment
        {
            Id = theirSecondEnv, TenantId = otherTenantId, Name = "staging",
        });
        db.SaveChanges();

        Guid inTarget = SeedTheirScaler(theirSecondEnv, "journal-export");
        SeedTheirScaler(theirEnvId, "journal-export-prod");

        Func<Task> act = () => sut.CopyFromEnvironmentAsync(
            tenantId, theirAppId, theirEnvId, theirSecondEnv);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");

        (await db.KedaScalers.AsNoTracking().AnyAsync(s => s.Id == inTarget)).Should().BeTrue();
        (await db.KedaScalers.AsNoTracking().CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// The scan reports every cluster and namespace it looked at, so the refusal has to come
    /// before the targets are resolved — not from the cluster seam further down, which would
    /// already have answered with their cluster names in <c>ScannedTargets</c>.
    /// </summary>
    [Fact]
    public async Task Scanning_another_tenants_app_names_none_of_its_clusters()
    {
        Func<Task> act = () => sut.ScanEnvironmentAsync(tenantId, theirAppId, theirEnvId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
    }

    [Fact]
    public async Task Applying_to_another_tenants_app_is_refused()
    {
        Func<Task> act = () => sut.ApplyToEnvironmentAsync(tenantId, theirAppId, theirEnvId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
    }

    [Fact]
    public async Task Our_own_autoscaler_is_created_listed_updated_and_deleted()
    {
        KedaScaler created = await sut.SaveScaledObjectAsync(
            tenantId, appId, envId, null,
            "billing-api", "Deployment", "billing-api", 2, 8, null, null, Triggers);

        created.TenantId.Should().Be(tenantId);
        (await sut.GetScalersAsync(tenantId, appId, envId)).Should().ContainSingle();

        await sut.SaveScaledObjectAsync(
            tenantId, appId, envId, created.Id,
            "billing-api", "Deployment", "billing-api", 3, 12, null, null, Triggers);

        KedaScaler stored = await db.KedaScalers.AsNoTracking().FirstAsync(s => s.Id == created.Id);
        stored.MinReplicaCount.Should().Be(3);
        stored.MaxReplicaCount.Should().Be(12);

        (await sut.DeleteScalerAsync(tenantId, created.Id)).Should().BeTrue();
        (await sut.GetScalersAsync(tenantId, appId, envId)).Should().BeEmpty();
    }

    /// <summary>
    /// The copy, in the direction it is meant to work: our app, our two environments. Proves the
    /// guard did not simply break the method, and that the clones carry our tenant.
    /// </summary>
    [Fact]
    public async Task Copying_between_our_own_environments_still_works()
    {
        Guid stagingId = Guid.NewGuid();
        db.Environments.Add(new Data.Environment
        {
            Id = stagingId, TenantId = tenantId, Name = "staging",
        });
        db.SaveChanges();

        await sut.SaveScaledObjectAsync(
            tenantId, appId, envId, null,
            "billing-api", "Deployment", "billing-api", 2, 8, null, null, Triggers);

        await sut.CopyFromEnvironmentAsync(tenantId, appId, envId, stagingId);

        List<KedaScaler> copied = await sut.GetScalersAsync(tenantId, appId, stagingId);
        copied.Should().ContainSingle();
        copied[0].TenantId.Should().Be(tenantId);
        copied[0].Name.Should().Be("billing-api");
    }
}
