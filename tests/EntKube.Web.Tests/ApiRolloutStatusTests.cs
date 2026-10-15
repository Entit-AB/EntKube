using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ContractStatus = EntKube.Contracts.Delivery.RolloutVerdictStatus;
using EntityStatus = EntKube.Web.Data.DeploymentRolloutStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// The rollout verdict the public API publishes, and who may read it.
///
/// <para><c>GET /api/v1/rollouts</c> was the last route in <c>PublicApiEndpoints</c> querying a
/// module's table directly: it walked rollout → deployment → app → customer to reach its tenant,
/// which is Delivery's own predicate. It asks <c>IDeliveryApi</c> now, so the <c>status</c> string
/// clients parse comes from the <em>contract's</em> copy of the enum rather than the entity's —
/// two separate declarations that happen to agree.</para>
/// </summary>
public class ApiRolloutStatusTests : IDisposable
{
    private readonly TestDbContextFactory factory;
    private readonly Microsoft.Data.Sqlite.SqliteConnection connection;
    private readonly ApplicationDbContext db;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();

    public ApiRolloutStatusTests()
    {
        connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── The wire format ───────────────────────────────────────────────────────

    [Fact]
    public void The_contract_and_entity_rollout_status_names_agree()
    {
        string[] entity = Enum.GetNames<EntityStatus>().Order().ToArray();
        string[] contract = Enum.GetNames<ContractStatus>().Order().ToArray();

        contract.Should().Equal(entity,
            "the route serialises this enum by name, so the two declarations drifting apart "
            + "changes a published JSON contract — and a rollout verdict is exactly the field a "
            + "client automates against");
    }

    /// <summary>
    /// ⚠️ The near-miss worth a test of its own. <c>RolloutStatus</c> is a <em>different</em> enum
    /// belonging to a blueprint rollout, with entirely different members; the names sit one word
    /// apart from <c>DeploymentRolloutStatus</c>, and mapping one onto the other would publish a
    /// wrong verdict for every release. If they ever agree by accident, that is worth knowing.
    /// </summary>
    [Fact]
    public void The_blueprint_rollout_status_is_a_different_enum()
    {
        Enum.GetNames<RolloutStatus>().Should().NotBeEquivalentTo(Enum.GetNames<EntityStatus>(),
            "these two are unrelated lifecycles — a release's analysis window and a blueprint's "
            + "promotion across targets");
    }

    [Fact]
    public void The_published_status_strings_are_what_clients_already_parse()
    {
        Enum.GetValues<ContractStatus>().Select(s => s.ToString())
            .Should().BeEquivalentTo(
                ["Watching", "Promoted", "RolledBack", "Alerted", "Inconclusive",
                 "RollbackFailed", "Superseded"],
                "these exact strings are published by GET /api/v1/rollouts");
    }

    /// <summary>
    /// Every entity member maps to the contract member of the same name. The mapping is a hand
    /// written switch rather than a cast, so a member added to the entity alone reaches the
    /// fallback — this is what makes that visible.
    /// </summary>
    [Fact]
    public async Task Every_status_survives_the_round_trip_by_name()
    {
        foreach (EntityStatus status in Enum.GetValues<EntityStatus>())
        {
            Guid rolloutId = SeedRollout(ourTenant, status, DateTime.UtcNow);

            IReadOnlyList<EntKube.Contracts.Delivery.RolloutSummary> rollouts =
                await new DeliveryApi(factory).GetRolloutsAsync(ourTenant);

            rollouts.Single(r => r.Id == rolloutId).Status.ToString()
                .Should().Be(status.ToString(),
                    $"{status} must publish as its own name, not as the switch's fallback");
        }
    }

    // ── Who may read it ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_sees_only_its_own_rollouts()
    {
        SeedRollout(ourTenant, EntityStatus.Promoted, DateTime.UtcNow);
        SeedRollout(theirTenant, EntityStatus.RolledBack, DateTime.UtcNow);

        DeliveryApi api = new(factory);

        (await api.GetRolloutsAsync(ourTenant)).Should().ContainSingle()
            .Which.Status.Should().Be(ContractStatus.Promoted);
        (await api.GetRolloutsAsync(theirTenant)).Should().ContainSingle()
            .Which.Status.Should().Be(ContractStatus.RolledBack);
    }

    /// <summary>Newest first, and the limit is clamped rather than trusted.</summary>
    [Fact]
    public async Task Rollouts_come_back_newest_first_within_the_limit()
    {
        DateTime baseline = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedRollout(ourTenant, EntityStatus.Promoted, baseline);
        SeedRollout(ourTenant, EntityStatus.Watching, baseline.AddHours(2));
        SeedRollout(ourTenant, EntityStatus.Alerted, baseline.AddHours(1));

        DeliveryApi api = new(factory);

        (await api.GetRolloutsAsync(ourTenant)).Select(r => r.Status)
            .Should().Equal([ContractStatus.Watching, ContractStatus.Alerted, ContractStatus.Promoted]);

        (await api.GetRolloutsAsync(ourTenant, limit: 1)).Should().ContainSingle()
            .Which.Status.Should().Be(ContractStatus.Watching);

        (await api.GetRolloutsAsync(ourTenant, limit: 0)).Should().HaveCount(1,
            "a limit below the floor is clamped, not passed to Take()");
    }

    private Guid SeedRollout(Guid tenantId, EntityStatus status, DateTime startedAt)
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), envId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid(), deploymentId = Guid.NewGuid(), rolloutId = Guid.NewGuid();

        if (!db.Tenants.Any(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = $"t-{tenantId:N}", Slug = $"t-{tenantId:N}" });
        }

        // Names are suffixed per seed: (TenantId, Name) is UNIQUE on customers, environments and
        // clusters, so a test that seeds several rollouts for one tenant collides on the second.
        string tag = $"{rolloutId:N}"[..8];

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"Contoso-{tag}" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });
        db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = $"production-{tag}" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"prod-cluster-{tag}", ApiServerUrl = "https://k8s.example.com",
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, EnvironmentId = envId, ClusterId = clusterId,
            Name = "billing-api", Type = DeploymentType.Yaml, Namespace = "billing",
        });
        db.DeploymentRollouts.Add(new DeploymentRollout
        {
            Id = rolloutId, DeploymentId = deploymentId, Status = status,
            StartedAt = startedAt, DecideAt = startedAt.AddMinutes(10),
            Verdict = $"verdict for {status}", TriggeredBy = "alice",
        });
        db.SaveChanges();

        return rolloutId;
    }
}
