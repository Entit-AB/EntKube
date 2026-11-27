using EntKube.Web.Data;
using EntKube.Web.Data.Backup;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Export a database, import it into an empty one, and check what came out the other side.
///
/// <para><b>What only this can catch.</b> The restore inserts tables in a hand-maintained
/// order, and a row inserted before the row it points at fails on a foreign key. Reading
/// the order and satisfying yourself it is right is exactly the kind of check that has
/// already been wrong here — and the failure happens during somebody's server migration,
/// with the old server already off.</para>
///
/// <para>Restoring with the wipe is Postgres-only (it uses <c>TRUNCATE … CASCADE</c>), so
/// these import into a fresh database instead — which is the case that matters, since
/// that is what a migration actually does.</para>
/// </summary>
public class BackupRoundTripTests : IDisposable
{
    private readonly SqliteConnection source = new("DataSource=:memory:");
    private readonly SqliteConnection destination = new("DataSource=:memory:");

    private readonly BackupService exporter;
    private readonly BackupService importer;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();

    public BackupRoundTripTests()
    {
        source.Open();
        destination.Open();

        foreach (SqliteConnection connection in (SqliteConnection[])[source, destination])
        {
            using ApplicationDbContext db = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
        }

        // Any 32 bytes: the bundle re-encrypts under the destination's key anyway.
        VaultEncryptionService encryption = new(new byte[32]);

        exporter = new BackupService(
            new TestDbContextFactory(source), encryption, NullLogger<BackupService>.Instance);

        importer = new BackupService(
            new TestDbContextFactory(destination), encryption, NullLogger<BackupService>.Instance);
    }

    public void Dispose()
    {
        source.Dispose();
        destination.Dispose();
        GC.SuppressFinalize(this);
    }

    private ApplicationDbContext Source() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(source).Options);

    private ApplicationDbContext Destination() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(destination).Options);

    /// <summary>
    /// A tenant with a cluster, and one row from each of the table groups the coverage
    /// test most recently brought into the bundle — the ones whose insert order has never
    /// been exercised.
    /// </summary>
    private void Seed()
    {
        using ApplicationDbContext db = Source();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-1", ApiServerUrl = "https://k8s.example.com",
        });

        // The audit trail: one event inside the retention window and one beyond it. Both are
        // deliberate — the window is a stated partial loss, and a test that seeded only a recent
        // event would pass just as well against a bundle that carried the whole table.
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            Id = Guid.NewGuid(), Action = "HelmInstall", ResourceKind = "Deployment",
            ResourceName = "harbor-core", PerformedBy = "operator@entit.se",
            Details = "chart 2.11.0", OccurredAt = DateTime.UtcNow.AddDays(-30),
        });
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            Id = Guid.NewGuid(), Action = "DeleteNamespace", ResourceKind = "Namespace",
            ResourceName = "ancient", PerformedBy = "operator@entit.se",
            OccurredAt = DateTime.UtcNow - EntKube.Web.Services.BackupService.AuditRetention
                         - TimeSpan.FromDays(1),
        });

        // Configuration nothing live recreates.
        db.ApiTokens.Add(new ApiToken
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "CI pipeline",
            TokenHash = new string('a', 64), DisplayPrefix = "ek_abcd",
        });
        db.ClusterCostRates.Add(new ClusterCostRate
        {
            Id = Guid.NewGuid(), ClusterId = clusterId,
            CpuCoreHourCost = 0.42m, MemoryGiBHourCost = 0.05m,
        });
        db.MeshMtlsPolicies.Add(new MeshMtlsPolicy
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterId = clusterId,
            Namespace = "entit-prod",
        });

        // The support agreement.
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Entit AB" };
        db.Customers.Add(customer);
        db.PortfolioAgreements.Add(new PortfolioAgreement
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customer.Id,
            PricingModel = PricingModel.HourBank, HourBankHoursPerMonth = 20m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.SupportMailboxes.Add(new SupportMailbox
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            Host = "imap.example.com", Username = "support@entit.se",
        });

        // Where the maintenance-notice work landed.
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterId = clusterId,
            Title = "Database upgrade", CreatedBy = "nils",
            Kind = MaintenanceKind.Emergency,
            StartsAt = new DateTime(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc),
            EndsAt = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc),
        });

        db.SaveChanges();
    }

    private async Task RoundTripAsync()
    {
        byte[] bundle = await exporter.ExportAsync("nils");

        using MemoryStream stream = new(bundle);
        await importer.ImportAsync(stream, wipeExisting: false);
    }

    /// <summary>
    /// The one that matters: the bundle this build writes is one this build reads.
    ///
    /// <para>The import guard used to name its accepted versions as a literal, separate
    /// from the literal the bundle stamped on itself. Bumping one and not the other made
    /// every bundle written afterwards un-importable — by the same build that wrote it,
    /// refused with "unsupported backup version". Nothing catches that until somebody
    /// tries to restore, which is the worst possible moment to find out.</para>
    /// </summary>
    [Fact]
    public async Task A_bundle_this_build_writes_imports_into_an_empty_database()
    {
        Seed();

        await RoundTripAsync();

        using ApplicationDbContext db = Destination();
        db.Tenants.Should().ContainSingle().Which.Name.Should().Be("ENTIT");
    }

    /// <summary>
    /// Restore inserts in a hand-maintained order, and a row placed before the row it
    /// points at fails its foreign key. Every table seeded above is checked, so an entry
    /// added to the wrong place in that sequence fails here rather than mid-migration.
    /// </summary>
    [Fact]
    public async Task Nothing_seeded_is_lost_on_the_way_through()
    {
        Seed();

        await RoundTripAsync();

        using ApplicationDbContext db = Destination();

        db.KubernetesClusters.Should().ContainSingle();
        db.Customers.Should().ContainSingle();
        db.ApiTokens.Should().ContainSingle();
        db.ClusterCostRates.Should().ContainSingle();
        db.MeshMtlsPolicies.Should().ContainSingle();
        db.PortfolioAgreements.Should().ContainSingle();
        db.SupportMailboxes.Should().ContainSingle();
        db.MaintenanceWindows.Should().ContainSingle();
    }

    /// <summary>
    /// An application's knowledge travels whole (knowledge merge, release A): the answer tied to
    /// its app, its history, and the move ledger. Without the ledger a restored database would move
    /// its sections again and bring back answers somebody had deleted.
    /// </summary>
    [Fact]
    public async Task Application_knowledge_its_history_and_the_move_ledger_travel()
    {
        Seed();
        Guid appId = Guid.NewGuid(), categoryId = Guid.NewGuid(), answerId = Guid.NewGuid();

        using (ApplicationDbContext db = Source())
        {
            Guid customerId = db.Customers.Single().Id;
            db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
            db.KbCategories.Add(new KbCategory { Id = categoryId, TenantId = tenantId, Name = "Applications" });
            db.KbAnswers.Add(new KbAnswer
            {
                Id = answerId, TenantId = tenantId, CategoryId = categoryId, Title = "Runbook", BodyHtml = "<p>Now</p>",
                Visibility = KbVisibility.Operations, AppId = appId, Section = KnowledgeSectionKind.Runbook, CustomerId = customerId,
            });
            db.KbAnswerRevisions.Add(new KbAnswerRevision
            {
                Id = Guid.NewGuid(), AnswerId = answerId, TenantId = tenantId, Body = "Before", BodyFormat = KbBodyFormat.Markdown,
            });
            db.KnowledgeSectionMoves.Add(new KnowledgeSectionMove { SectionId = answerId, TenantId = tenantId, AppId = appId, AnswerId = answerId });
            db.SaveChanges();
        }

        await RoundTripAsync();

        using ApplicationDbContext restored = Destination();
        restored.KbAnswers.Should().ContainSingle(a => a.AppId == appId && a.Visibility == KbVisibility.Operations);
        restored.KbAnswerRevisions.Should().ContainSingle(r => r.AnswerId == answerId && r.BodyFormat == KbBodyFormat.Markdown);
        restored.KnowledgeSectionMoves.Should().ContainSingle(m => m.SectionId == answerId);
    }

    /// <summary>
    /// The audit trail travels, within its window. This was the last entry on
    /// <c>BackupCoverageTests</c>'s gap list: pure evidence rather than configuration, kept out
    /// because a busy installation's whole history could be most of a one-document bundle.
    /// </summary>
    [Fact]
    public async Task The_audit_trail_travels_within_its_window()
    {
        Seed();

        await RoundTripAsync();

        using ApplicationDbContext db = Destination();

        AuditEvent carried = db.Set<AuditEvent>().Should().ContainSingle().Subject;

        carried.Action.Should().Be("HelmInstall");
        carried.ResourceName.Should().Be("harbor-core");
        carried.PerformedBy.Should().Be("operator@entit.se",
            "who did it is the whole reason to carry an audit event");
        carried.Details.Should().Be("chart 2.11.0");

        db.Set<AuditEvent>().Should().NotContain(a => a.Action == "DeleteNamespace",
            "an event older than BackupService.AuditRetention is deliberately not carried — a "
            + "stated partial loss, which is what keeps the bundle one document");
    }

    /// <summary>
    /// Values, not only row counts. A restore that carried the rows but flattened what
    /// was on them would pass every count and still lose the answer.
    /// </summary>
    [Fact]
    public async Task What_was_on_the_rows_comes_back()
    {
        Seed();

        await RoundTripAsync();

        using ApplicationDbContext db = Destination();

        db.ClusterCostRates.Single().CpuCoreHourCost.Should().Be(0.42m);

        PortfolioAgreement agreement = db.PortfolioAgreements.Single();
        agreement.PricingModel.Should().Be(PricingModel.HourBank);
        agreement.HourBankHoursPerMonth.Should().Be(20m);

        // An enum added late is the sort of thing a restore drops by forgetting a column.
        db.MaintenanceWindows.Single().Kind.Should().Be(MaintenanceKind.Emergency);

        db.SupportMailboxes.Single().Host.Should().Be("imap.example.com");
    }

    /// <summary>
    /// A role restored from a bundle written before role permissions were enforced is carried
    /// across as part of the restore, like a role that never left. Left alone it would read as
    /// already carried across — its members would lose the support access they had when the
    /// bundle was written.
    /// </summary>
    [Fact]
    public async Task A_role_from_a_bundle_older_than_enforced_permissions_is_carried_across_after_the_restore()
    {
        Seed();
        Guid member = Guid.NewGuid();
        using (ApplicationDbContext db = Source())
        {
            db.TenantRoles.Add(new TenantRole { Id = member, TenantId = tenantId, Name = "Member" });
            db.SaveChanges();
        }

        byte[] exported = await exporter.ExportAsync("nils");

        // An older bundle: stamped before the schema existed, and so without it on its roles.
        using MemoryStream raw = new(exported);
        using System.IO.Compression.GZipStream unzip = new(raw, System.IO.Compression.CompressionMode.Decompress);
        using StreamReader reader = new(unzip);
        string json = (await reader.ReadToEndAsync())
            .Replace($"\"Version\":{BackupBundle.CurrentVersion}", $"\"Version\":{BackupService.FirstVersionWithPermissionSchema - 1}")
            .Replace($"\"PermissionSchema\":{TenantRole.CurrentPermissionSchema},", "");

        using MemoryStream older = new(System.Text.Encoding.UTF8.GetBytes(json));
        await importer.ImportAsync(older, wipeExisting: false);

        // Carried across by the restore itself, not left for the next start: until then, with
        // permissions enforced, the restored members would have no support or customers at all.
        using ApplicationDbContext restored = Destination();
        TenantRole role = restored.TenantRoles.Single(r => r.Id == member);
        role.PermissionSchema.Should().Be(TenantRole.CurrentPermissionSchema);
        TenantRoleService.DecodePermissions(role.PermissionsJson).Should().Contain(TenantFeature.SupportTickets, AccessLevel.Manage);
    }

    [Fact]
    public async Task A_role_from_a_current_bundle_keeps_its_permission_schema()
    {
        Seed();
        Guid member = Guid.NewGuid();
        using (ApplicationDbContext db = Source())
        {
            db.TenantRoles.Add(new TenantRole { Id = member, TenantId = tenantId, Name = "Member" });
            db.SaveChanges();
        }

        await RoundTripAsync();

        using ApplicationDbContext restored = Destination();
        restored.TenantRoles.Single(r => r.Id == member).PermissionSchema.Should().Be(TenantRole.CurrentPermissionSchema);
    }

    /// <summary>
    /// A bundle from a newer EntKube is refused rather than half-restored: it may carry
    /// tables this build cannot place, and stopping at the first of them would leave the
    /// database neither the old state nor the new one.
    /// </summary>
    [Fact]
    public async Task A_bundle_from_a_newer_build_is_refused()
    {
        Seed();

        byte[] exported = await exporter.ExportAsync("nils");

        // Rewrite the stamped version to one beyond what this build understands.
        using MemoryStream raw = new(exported);
        using System.IO.Compression.GZipStream unzip =
            new(raw, System.IO.Compression.CompressionMode.Decompress);
        using StreamReader reader = new(unzip);
        string json = (await reader.ReadToEndAsync()).Replace(
            $"\"Version\":{BackupBundle.CurrentVersion}",
            $"\"Version\":{BackupBundle.CurrentVersion + 1}");

        using MemoryStream forward = new(System.Text.Encoding.UTF8.GetBytes(json));

        Func<Task> import = () => importer.ImportAsync(forward, wipeExisting: false);

        await import.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*newer EntKube*");
    }
}
