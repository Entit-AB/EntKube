using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Native HorizontalPodAutoscaler support in KedaScalerService: manifest rendering, the
/// guardrails that stop an HPA that would never scale (or be rejected by the API server),
/// and detection of two autoscalers fighting over one workload.
/// </summary>
public class HpaAutoscalerTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KedaScalerService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();

    public HpaAutoscalerTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid customerId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "HpaTenant", Slug = "hpa" });
        db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "HpaCustomer" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });
        db.SaveChanges();

        sut = new KedaScalerService(
            new TestDbContextFactory(connection),
            new Mock<IClusterChangeGate>().Object,
            NullLogger<KedaScalerService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Manifest rendering ────────────────────────────────────────────────────

    [Fact]
    public void BuildScalerYaml_RendersAutoscalingV2WithBothMetrics()
    {
        KedaScaler hpa = new()
        {
            Name = "billing-api",
            Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = "billing-api",
            MinReplicaCount = 2,
            MaxReplicaCount = 8,
            TargetCpuUtilization = 70,
            TargetMemoryUtilization = 80,
        };

        string yaml = KedaScalerService.BuildScalerYaml(hpa, "billing-prod")!;

        yaml.Should().Contain("apiVersion: autoscaling/v2");
        yaml.Should().Contain("kind: HorizontalPodAutoscaler");
        yaml.Should().Contain("  name: billing-api");
        // scaleTargetRef.apiVersion is required by the API server — a missing one is rejected.
        yaml.Should().Contain("    apiVersion: apps/v1");
        yaml.Should().Contain("    kind: Deployment");
        yaml.Should().Contain("  minReplicas: 2");
        yaml.Should().Contain("  maxReplicas: 8");
        yaml.Should().Contain("        name: cpu");
        yaml.Should().Contain("        name: memory");
        yaml.Should().Contain("          averageUtilization: 70");
        yaml.Should().Contain("          averageUtilization: 80");
    }

    [Fact]
    public void BuildScalerYaml_OmitsTheMetricThatIsNotSet()
    {
        KedaScaler hpa = new()
        {
            Name = "cpu-only",
            Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = "api",
            TargetCpuUtilization = 60,
        };

        string yaml = KedaScalerService.BuildScalerYaml(hpa, "ns")!;

        yaml.Should().Contain("        name: cpu");
        yaml.Should().NotContain("        name: memory");
    }

    [Fact]
    public void BuildScalerYaml_DefaultsMinToOneAndKeepsMaxAboveIt()
    {
        // maxReplicas is a required field, so a scaler saved without one must still render a
        // valid object — and never a maximum below the minimum.
        KedaScaler hpa = new()
        {
            Name = "defaults",
            Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = "api",
            MinReplicaCount = 20,
            TargetCpuUtilization = 75,
        };

        string yaml = KedaScalerService.BuildScalerYaml(hpa, "ns")!;

        yaml.Should().Contain("  minReplicas: 20");
        yaml.Should().Contain("  maxReplicas: 20");
    }

    [Fact]
    public void BuildScalerYaml_IndentsBehaviorUnderSpec()
    {
        KedaScaler hpa = new()
        {
            Name = "with-behavior",
            Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = "api",
            MaxReplicaCount = 5,
            TargetCpuUtilization = 70,
            BehaviorYaml = "scaleDown:\n  stabilizationWindowSeconds: 600\n",
        };

        string yaml = KedaScalerService.BuildScalerYaml(hpa, "ns")!;

        yaml.Should().Contain("  behavior:\n    scaleDown:\n      stabilizationWindowSeconds: 600");
    }

    [Fact]
    public void BuildScalerYaml_ReturnsNullWhenTheHpaCouldNeverScale()
    {
        KedaScaler noMetrics = new()
        {
            Name = "no-metrics", Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment", ScaleTargetName = "api", MaxReplicaCount = 5,
        };
        KedaScaler noTarget = new()
        {
            Name = "no-target", Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment", TargetCpuUtilization = 70,
        };

        KedaScalerService.BuildScalerYaml(noMetrics, "ns").Should().BeNull();
        KedaScalerService.BuildScalerYaml(noTarget, "ns").Should().BeNull();
    }

    [Fact]
    public void BuildManifest_SeparatesHpaAndScaledObjectDocuments()
    {
        List<KedaScaler> scalers =
        [
            new KedaScaler
            {
                Name = "api-hpa", Kind = KedaScalerKind.Hpa,
                ScaleTargetKind = "Deployment", ScaleTargetName = "api",
                MaxReplicaCount = 5, TargetCpuUtilization = 70,
            },
            new KedaScaler
            {
                Name = "worker-queue", Kind = KedaScalerKind.ScaledObject,
                ScaleTargetKind = "Deployment", ScaleTargetName = "worker",
                TriggersYaml = "- type: rabbitmq\n  metadata:\n    queueName: jobs\n",
            },
        ];

        string yaml = KedaScalerService.BuildManifest(scalers, "ns");

        yaml.Should().Contain("kind: HorizontalPodAutoscaler");
        yaml.Should().Contain("kind: ScaledObject");
        yaml.Should().Contain("\n---\n");
    }

    // ── Conflict detection ────────────────────────────────────────────────────

    [Fact]
    public void FindTargetConflict_FlagsAnHpaAndScaledObjectOnTheSameWorkload()
    {
        List<KedaScaler> existing =
        [
            new KedaScaler
            {
                Id = Guid.NewGuid(), Name = "api-events", Kind = KedaScalerKind.ScaledObject,
                ScaleTargetKind = "Deployment", ScaleTargetName = "api",
            },
        ];

        KedaScaler candidate = new()
        {
            Id = Guid.NewGuid(), Name = "api-cpu", Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment", ScaleTargetName = "api",
        };

        KedaScalerService.FindTargetConflict(existing, candidate)
            .Should().NotBeNull().And.Contain("api-events");
    }

    [Fact]
    public void FindTargetConflict_MatchesTargetNameCaseInsensitively()
    {
        List<KedaScaler> existing =
        [
            new KedaScaler
            {
                Id = Guid.NewGuid(), Name = "existing", Kind = KedaScalerKind.Hpa,
                ScaleTargetKind = "Deployment", ScaleTargetName = "API",
            },
        ];

        KedaScaler candidate = new()
        {
            Id = Guid.NewGuid(), Name = "new", Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "deployment", ScaleTargetName = "api",
        };

        KedaScalerService.FindTargetConflict(existing, candidate).Should().NotBeNull();
    }

    [Fact]
    public void FindTargetConflict_IgnoresSelfDifferentTargetsAndCustomYaml()
    {
        Guid selfId = Guid.NewGuid();
        KedaScaler self = new()
        {
            Id = selfId, Name = "api-cpu", Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment", ScaleTargetName = "api",
        };

        List<KedaScaler> existing =
        [
            self,                                                       // editing itself
            new KedaScaler                                              // a different workload
            {
                Id = Guid.NewGuid(), Name = "worker-cpu", Kind = KedaScalerKind.Hpa,
                ScaleTargetKind = "Deployment", ScaleTargetName = "worker",
            },
            new KedaScaler                                              // same name, different kind
            {
                Id = Guid.NewGuid(), Name = "api-sts", Kind = KedaScalerKind.Hpa,
                ScaleTargetKind = "StatefulSet", ScaleTargetName = "api",
            },
            new KedaScaler                                              // target hidden in raw YAML
            {
                Id = Guid.NewGuid(), Name = "raw", Kind = KedaScalerKind.Custom,
                ScaleTargetKind = "Deployment", ScaleTargetName = "api",
                CustomYaml = "apiVersion: keda.sh/v1alpha1",
            },
        ];

        KedaScalerService.FindTargetConflict(existing, self).Should().BeNull();
    }

    // ── Save-time validation ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveHpaAsync_PersistsAndRendersTheStructuredFields()
    {
        KedaScaler saved = await sut.SaveHpaAsync(
            tenantId, appId, envId, null, "Billing-API", "Deployment", "billing-api",
            minReplicas: 2, maxReplicas: 10,
            targetCpuUtilization: 70, targetMemoryUtilization: null, behaviorYaml: null);

        saved.Name.Should().Be("billing-api");           // normalized to a valid resource name
        saved.Kind.Should().Be(KedaScalerKind.Hpa);
        saved.TargetCpuUtilization.Should().Be(70);

        List<KedaScaler> all = await sut.GetScalersAsync(appId, envId);
        all.Should().ContainSingle().Which.MaxReplicaCount.Should().Be(10);
    }

    [Fact]
    public async Task SaveHpaAsync_RejectsAnHpaWithNoMetrics()
    {
        Func<Task> act = () => sut.SaveHpaAsync(
            tenantId, appId, envId, null, "no-metrics", "Deployment", "api",
            minReplicas: 1, maxReplicas: 5,
            targetCpuUtilization: null, targetMemoryUtilization: null, behaviorYaml: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never scales*");
    }

    [Fact]
    public async Task SaveHpaAsync_RejectsScaleToZero()
    {
        Func<Task> act = () => sut.SaveHpaAsync(
            tenantId, appId, envId, null, "zero", "Deployment", "api",
            minReplicas: 0, maxReplicas: 5,
            targetCpuUtilization: 70, targetMemoryUtilization: null, behaviorYaml: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least 1*");
    }

    [Fact]
    public async Task SaveHpaAsync_RejectsMaxBelowMin()
    {
        Func<Task> act = () => sut.SaveHpaAsync(
            tenantId, appId, envId, null, "inverted", "Deployment", "api",
            minReplicas: 5, maxReplicas: 2,
            targetCpuUtilization: 70, targetMemoryUtilization: null, behaviorYaml: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*greater than or equal*");
    }

    [Fact]
    public async Task SaveHpaAsync_RejectsAWorkloadAlreadyScaledByAScaledObject()
    {
        await sut.SaveScaledObjectAsync(
            tenantId, appId, envId, null, "api-events", "Deployment", "api",
            minReplicaCount: 0, maxReplicaCount: 20, pollingInterval: null, cooldownPeriod: null,
            triggersYaml: "- type: rabbitmq\n  metadata:\n    queueName: jobs\n");

        Func<Task> act = () => sut.SaveHpaAsync(
            tenantId, appId, envId, null, "api-cpu", "Deployment", "api",
            minReplicas: 1, maxReplicas: 5,
            targetCpuUtilization: 70, targetMemoryUtilization: null, behaviorYaml: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*api-events*");

        (await sut.GetScalersAsync(appId, envId)).Should().ContainSingle();
    }

    [Fact]
    public async Task SaveHpaAsync_AllowsEditingAnExistingHpaInPlace()
    {
        KedaScaler saved = await sut.SaveHpaAsync(
            tenantId, appId, envId, null, "api-cpu", "Deployment", "api",
            minReplicas: 1, maxReplicas: 5,
            targetCpuUtilization: 70, targetMemoryUtilization: null, behaviorYaml: null);

        // The conflict check must not treat the record being edited as its own rival.
        KedaScaler updated = await sut.SaveHpaAsync(
            tenantId, appId, envId, saved.Id, "api-cpu", "Deployment", "api",
            minReplicas: 2, maxReplicas: 12,
            targetCpuUtilization: 60, targetMemoryUtilization: 85, behaviorYaml: null);

        updated.MaxReplicaCount.Should().Be(12);
        updated.TargetMemoryUtilization.Should().Be(85);
        (await sut.GetScalersAsync(appId, envId)).Should().ContainSingle();
    }
}
