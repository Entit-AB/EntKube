using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

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

        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IJitProvisioner>();

        await TearDownEndedGrantsAsync(dbFactory, provisioner, ct);
        await SweepOrphansAsync(dbFactory, scope.ServiceProvider, ct);
    }

    /// <summary>
    /// Tears down grants that are approved, past their expiry, and not yet cleaned up.
    ///
    /// Revoked grants are handled at revocation, not here — waiting for a sweep would leave a
    /// window where a revoked credential still worked. This is only for the ones nobody revoked
    /// because they simply ran out.
    /// </summary>
    private async Task TearDownEndedGrantsAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, IJitProvisioner provisioner, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<JitGrant> ended = await db.JitGrants
            .Where(g => g.ApprovedAt != null
                        && g.TornDownAt == null
                        && g.ExpiresAt != null
                        && g.ExpiresAt <= now)
            .ToListAsync(ct);

        foreach (JitGrant grant in ended)
        {
            try
            {
                await provisioner.TearDownAsync(grant, ct);
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
    /// Deletes JIT-labelled objects that no live grant claims.
    ///
    /// Scoped per cluster, and driven by the label rather than the database, because the whole
    /// point is to find things the database does not know about.
    /// </summary>
    private async Task SweepOrphansAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, IServiceProvider services, CancellationToken ct)
    {
        var k8sFactory = services.GetRequiredService<IKubernetesClientFactory>();

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Only clusters that have ever hosted a grant. Scanning every registered cluster for a
        // label that has never been applied there is work with no possible result.
        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Where(c => db.JitGrants.Any(g => g.KubernetesClusterId == c.Id))
            .ToListAsync(ct);

        if (clusters.Count == 0) return;

        // A grant is "live" for this purpose if it has not been torn down — including one still
        // pending mint. Being conservative here costs a sweep; being wrong deletes live RBAC.
        HashSet<Guid> claimed = (await db.JitGrants
            .Where(g => g.TornDownAt == null)
            .Select(g => g.Id)
            .ToListAsync(ct))
            .ToHashSet();

        DateTime cutoff = DateTime.UtcNow - OrphanGrace;

        foreach (KubernetesCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                // Said out loud rather than skipped silently: Kubeconfig is materialized from the
                // vault, so a null here means the secret is missing or undecryptable, and a
                // cluster that is quietly never swept accumulates JIT RBAC forever.
                logger.LogWarning(
                    "Skipping JIT orphan sweep on cluster {Cluster}: no kubeconfig could be resolved",
                    cluster.Name);
                continue;
            }

            try
            {
                string json = await k8sFactory.GetJsonAllNamespacesAsync(
                    "serviceaccounts", cluster.Kubeconfig,
                    $"{JitRbacBuilder.ManagedLabel}={JitRbacBuilder.ManagedValue}", ct);

                foreach ((Guid grantId, string ns) in SelectOrphans(json, claimed, cutoff))
                {
                    logger.LogWarning(
                        "Deleting orphaned JIT objects for grant {GrantId} in {Cluster}/{Namespace} "
                        + "— no grant claims them", grantId, cluster.Name, ns);

                    await DeleteOrphanAsync(k8sFactory, cluster, grantId, ns, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Orphan sweep failed on cluster {Cluster}", cluster.Name);
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
        IKubernetesClientFactory k8sFactory, KubernetesCluster cluster,
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
                await k8sFactory.DeleteManifestAsync(kind, name, ns, cluster.Kubeconfig!, ct);
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
