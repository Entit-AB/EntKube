using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services.Jit;

/// <summary>
/// Removes the cluster objects of grants that have ended, and sweeps up any that were left behind.
///
/// Expiry itself does not depend on this service running — the bound ServiceAccount token carries
/// its own lifetime, so a grant stops working on time whether or not anything reaps it. What this
/// removes is the RBAC, which would otherwise accumulate in customer namespaces forever, and which
/// is the thing that would matter if a token were somehow re-minted against a ServiceAccount that
/// should not still exist.
///
/// The orphan sweep is what recovers from a crash between applying the manifest and committing the
/// grant row: the objects exist in the cluster with nothing in the database pointing at them, so
/// nothing would ever delete them. They are found by label rather than by name, so an object whose
/// grant id cannot be parsed is still identifiable as ours.
/// </summary>
public class JitGrantReaperService(
    IServiceScopeFactory scopeFactory,
    ILogger<JitGrantReaperService> logger) : BackgroundService
{
    /// <summary>
    /// How often to sweep. Frequent enough that a revoked grant's RBAC does not linger visibly,
    /// infrequent enough that it is not a meaningful load on the API server.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long an unrecognised JIT object must have existed before the orphan sweep removes it.
    ///
    /// Without this, the sweep races minting: the manifest lands a moment before the grant row is
    /// committed, and a sweep in that window would delete the RBAC of a grant that is about to
    /// become live. Anything younger than this is assumed to be mid-mint.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short initial delay keeps the sweep out of the startup path, where the database may
        // still be migrating.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad cluster stop the loop — the next sweep is the recovery.
                logger.LogError(ex, "JIT grant sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass: tear down every grant that has ended, then look for objects no grant claims.
    /// Public so it can be driven directly in a test rather than by waiting for a timer.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("jit-grant-reaper");

        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<IdentityDbContext>>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IJitProvisioner>();
        var clusterAccess = scope.ServiceProvider
            .GetRequiredService<EntKube.Web.Services.Clusters.IClusterClientFactory>();

        // One tenant at a time, outermost (docs/decomposition.md §4.0.1.2). This swept every grant
        // and every cluster in the installation and took the tenant off nothing at all — it reached
        // clusters by reading Kubeconfig off each row. What it does there is DELETE RBAC, so acting
        // as the wrong tenant removes somebody else's access.
        List<Guid> tenants;
        using (IdentityDbContext db = dbFactory.CreateDbContext())
        {
            tenants = await db.Tenants.Select(x => x.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenants)
        {
            if (ct.IsCancellationRequested) return;

            // Per tenant, so one unreachable cluster cannot end the sweep for the rest.
            try
            {
                await TearDownEndedGrantsAsync(dbFactory, provisioner, tenantId, ct);
                await SweepOrphansAsync(dbFactory, clusterAccess, tenantId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JIT sweep failed for tenant {TenantId}", tenantId);
            }
        }
    }

    /// <summary>
    /// Tears down grants that are approved, past their expiry, and not yet cleaned up.
    ///
    /// Revoked grants are handled at revocation, not here — waiting for a sweep would leave a
    /// window where a revoked credential still worked. This is only for the ones nobody revoked
    /// because they simply ran out.
    /// </summary>
    private async Task TearDownEndedGrantsAsync(
        IDbContextFactory<IdentityDbContext> dbFactory, IJitProvisioner provisioner,
        Guid tenantId, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;

        using IdentityDbContext db = dbFactory.CreateDbContext();

        List<JitGrant> ended = await EndedFor(db, tenantId, now).ToListAsync(ct);

        foreach (JitGrant grant in ended)
        {
            try
            {
                await provisioner.TearDownAsync(tenantId, grant, ct);
                grant.TornDownAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Reaped expired JIT grant {GrantId} in {Namespace}", grant.Id, grant.Namespace);
            }
            catch (Exception ex)
            {
                // Leave TornDownAt null so the next sweep tries again. A grant whose cluster is
                // temporarily unreachable must not be marked clean.
                logger.LogWarning(ex, "Tearing down expired JIT grant {GrantId} failed", grant.Id);
            }
        }
    }

    /// <summary>
    /// One tenant's grants that have ended and not been cleaned up.
    ///
    /// <para>Public and static so the scoping can be asserted without running the sweep: the
    /// previous version selected every ended grant in the installation, and a test that only
    /// counted what came back would pass against either.</para>
    /// </summary>
    public static IQueryable<JitGrant> EndedFor(IdentityDbContext db, Guid tenantId, DateTime now)
        => db.JitGrants
            .Where(g => g.TenantId == tenantId
                        && g.ApprovedAt != null
                        && g.TornDownAt == null
                        && g.ExpiresAt != null
                        && g.ExpiresAt <= now);

    /// <summary>
    /// Deletes JIT-labelled objects that no live grant claims.
    ///
    /// Scoped per cluster, and driven by the label rather than the database, because the whole
    /// point is to find things the database does not know about.
    /// </summary>
    private async Task SweepOrphansAsync(
        IDbContextFactory<IdentityDbContext> dbFactory,
        EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
        Guid tenantId, CancellationToken ct)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        // Only this tenant's clusters, and only ones that have ever hosted a grant: scanning a
        // cluster for a label that has never been applied there is work with no possible result.
        List<Guid> clusterIds = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId
                        && db.JitGrants.Any(g => g.KubernetesClusterId == c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (clusterIds.Count == 0) return;

        // A grant is "live" for this purpose if it has not been torn down — including one still
        // pending mint. Being conservative here costs a sweep; being wrong deletes live RBAC.
        //
        // ⚠️ Deliberately NOT narrowed to this tenant, unlike everything else here. The set is
        // protective: an id in it means "do not delete". Narrowing it could only ever make the
        // sweep delete MORE, and the one case where it would differ — RBAC on this tenant's
        // cluster claimed by another tenant's grant — is exactly the broken state the scoping
        // above now prevents, so reaping it is not worth the risk of being wrong about it.
        HashSet<Guid> claimed = (await db.JitGrants
            .Where(g => g.TornDownAt == null)
            .Select(g => g.Id)
            .ToListAsync(ct))
            .ToHashSet();

        DateTime cutoff = DateTime.UtcNow - OrphanGrace;

        foreach (Guid clusterId in clusterIds)
        {
            EntKube.Web.Services.Clusters.IClusterClient? access =
                await clusterAccess.ForAsync(tenantId, clusterId, ct);

            if (access is null)
            {
                // Said out loud rather than skipped silently: the credential is materialized from
                // the vault, so "no" here means the secret is missing or undecryptable, and a
                // cluster that is quietly never swept accumulates JIT RBAC forever.
                logger.LogWarning(
                    "Skipping JIT orphan sweep on cluster {ClusterId}: no credential could be resolved",
                    clusterId);
                continue;
            }

            try
            {
                string json = await access.GetJsonAllNamespacesAsync(
                    "serviceaccounts",
                    $"{JitRbacBuilder.ManagedLabel}={JitRbacBuilder.ManagedValue}", ct);

                foreach ((Guid grantId, string ns) in SelectOrphans(json, claimed, cutoff))
                {
                    logger.LogWarning(
                        "Deleting orphaned JIT objects for grant {GrantId} in {ClusterId}/{Namespace} "
                        + "— no grant claims them", grantId, clusterId, ns);

                    await DeleteOrphanAsync(access, grantId, ns, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Orphan sweep failed on cluster {ClusterId}", clusterId);
            }
        }
    }

    /// <summary>
    /// Decides which of the cluster's JIT objects are orphans.
    ///
    /// Separated from the loop that deletes them because this is the part with judgment in it —
    /// what counts as claimed, and how young is too young to touch — while the loop is just
    /// kubectl calls. Getting this wrong deletes live RBAC out from under a working grant, so it
    /// is worth being able to exercise directly.
    /// </summary>
    public static IEnumerable<(Guid GrantId, string Namespace)> SelectOrphans(
        string json, ISet<Guid> claimed, DateTime cutoff)
    {
        foreach ((Guid grantId, string ns, DateTime created) in ParseJitServiceAccounts(json))
        {
            // Claimed includes grants still pending mint. Being conservative costs a sweep;
            // being wrong deletes RBAC somebody is using.
            if (claimed.Contains(grantId)) continue;

            // Anything inside the grace window is assumed to be mid-mint: the manifest lands a
            // moment before the grant row commits, and a sweep in that gap would delete the RBAC
            // of a grant that is about to become live.
            if (created > cutoff) continue;

            yield return (grantId, ns);
        }
    }

    private static async Task DeleteOrphanAsync(
        EntKube.Web.Services.Clusters.IClusterClient access,
        Guid grantId, string ns, CancellationToken ct)
    {
        // Same order as a normal teardown: the binding is what grants, so it goes first.
        foreach ((string kind, string name) in new[]
                 {
                     ("rolebinding", JitRbacBuilder.RoleBindingName(grantId)),
                     ("role", JitRbacBuilder.RoleName(grantId)),
                     ("serviceaccount", JitRbacBuilder.ServiceAccountName(grantId)),
                 })
        {
            try
            {
                await access.DeleteManifestAsync(kind, name, ns, ct);
            }
            catch
            {
                // Already gone, or gone by the time we got here. Either is the desired state.
            }
        }
    }

    /// <summary>
    /// Pulls (grant id, namespace, creation time) out of a ServiceAccount list.
    ///
    /// Internal and pure so the parsing can be tested against real API-server output without a
    /// cluster. Entries whose grant label is missing or unparseable are skipped rather than
    /// guessed at — deleting something because its label looked wrong is worse than leaving it.
    /// </summary>
    public static IEnumerable<(Guid GrantId, string Namespace, DateTime CreatedAt)> ParseJitServiceAccounts(
        string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("metadata", out JsonElement metadata)) continue;

                if (!metadata.TryGetProperty("labels", out JsonElement labels)) continue;
                if (!labels.TryGetProperty(JitRbacBuilder.GrantLabel, out JsonElement grantLabel)) continue;
                if (!Guid.TryParse(grantLabel.GetString(), out Guid grantId)) continue;

                string? ns = metadata.TryGetProperty("namespace", out JsonElement nsElement)
                    ? nsElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(ns)) continue;

                // A ServiceAccount with no creationTimestamp cannot be aged out of the grace
                // window, so treat it as brand new and leave it for a later sweep.
                DateTime created = metadata.TryGetProperty("creationTimestamp", out JsonElement ts)
                                   && ts.TryGetDateTime(out DateTime parsed)
                    ? parsed.ToUniversalTime()
                    : DateTime.UtcNow;

                yield return (grantId, ns!, created);
            }
        }
    }
}
