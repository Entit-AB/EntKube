using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services.ClusterChanges;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Jit;

/// <summary>
/// Creates and removes the cluster side of a grant: the per-grant ServiceAccount, Role and
/// RoleBinding, plus the bound token the proxy uses to act for it.
/// </summary>
public class KubernetesJitProvisioner(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8sFactory,
    IClusterChangeGate gate,
    VaultEncryptionService encryption,
    IConfiguration configuration,
    ILogger<KubernetesJitProvisioner> logger) : IJitProvisioner
{
    /// <summary>Prefix on the EntKube token a customer presents. Mirrors <c>ekp_</c> for API tokens.</summary>
    public const string TokenPrefix = "ekj_";

    /// <summary>
    /// Audience stamped on the bound ServiceAccount token. The API server rejects a token whose
    /// audience does not include its own, so this is deliberately the default one — the value
    /// exists to be explicit, not to add a second check.
    /// </summary>
    private static readonly string[] TokenAudiences = ["https://kubernetes.default.svc"];

    public async Task<MintedCredential> MintAsync(
        JitGrant grant, TimeSpan duration, CancellationToken ct = default)
    {
        KubernetesCluster cluster = await LoadClusterAsync(grant.KubernetesClusterId, ct);

        string manifest = JitRbacBuilder.BuildManifest(grant);
        string sa = JitRbacBuilder.ServiceAccountName(grant.Id);

        await gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Apply,
            Kubeconfig = cluster.Kubeconfig!,
            ClusterLabel = cluster.Name,
            Namespace = grant.Namespace,
            Summary = $"Grant {grant.Level} JIT access to {grant.UserId} in {grant.Namespace} "
                      + $"until {grant.ExpiresAt:u}",
            Manifest = manifest,
        }, ct);

        await k8sFactory.ApplyManifestAsync(manifest, cluster.Kubeconfig!, ct);

        (string token, DateTime? tokenExpiry) =
            await RequestBoundTokenAsync(cluster, grant.Namespace, sa, duration, ct);

        // The bound token never leaves EntKube — it is what the proxy presents to the API server.
        // What the customer gets is this, which means nothing to the cluster and everything to
        // the proxy, and can be checked against a revoked row on every request.
        string plaintext = TokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        await StoreClusterTokenAsync(grant.Id, token, ct);

        logger.LogInformation(
            "Minted JIT ServiceAccount {ServiceAccount} in {Namespace} on {Cluster} for grant {GrantId}",
            sa, grant.Namespace, cluster.Name, grant.Id);

        return new MintedCredential
        {
            ServiceAccountName = sa,
            Plaintext = plaintext,
            TokenHash = Hash(plaintext),
            DisplayPrefix = plaintext[..12],
            TokenExpiresAt = tokenExpiry,
            Kubeconfig = BuildKubeconfig(grant, plaintext),
        };
    }

    public async Task TearDownAsync(JitGrant grant, CancellationToken ct = default)
    {
        KubernetesCluster? cluster = await TryLoadClusterAsync(grant.KubernetesClusterId, ct);
        if (cluster?.Kubeconfig is null)
        {
            logger.LogWarning(
                "Cannot tear down JIT grant {GrantId}: cluster {ClusterId} has no kubeconfig",
                grant.Id, grant.KubernetesClusterId);
            return;
        }

        // The RoleBinding goes first and its failure is the one that matters. Deleting it is what
        // actually stops an already-issued bound token working; the Role and ServiceAccount are
        // tidying. Deleting them in the other order would leave a window where the binding still
        // grants against a Role that is gone, which is harmless, and a window where the binding
        // survives a partial failure, which is not.
        await DeleteAsync(cluster, "rolebinding", JitRbacBuilder.RoleBindingName(grant.Id), grant.Namespace, ct);
        await DeleteAsync(cluster, "role", JitRbacBuilder.RoleName(grant.Id), grant.Namespace, ct);
        await DeleteAsync(cluster, "serviceaccount", JitRbacBuilder.ServiceAccountName(grant.Id), grant.Namespace, ct);

        await ForgetClusterTokenAsync(grant.Id, ct);

        logger.LogInformation("Tore down JIT grant {GrantId} in {Namespace}", grant.Id, grant.Namespace);
    }

    /// <summary>
    /// Deletes one object, treating "already gone" as success. Teardown runs from revocation and
    /// from the reaper, and neither can tell a clean previous teardown from a crash mid-mint —
    /// so both call this and it has to be idempotent.
    /// </summary>
    private async Task DeleteAsync(
        KubernetesCluster cluster, string kind, string name, string ns, CancellationToken ct)
    {
        try
        {
            await k8sFactory.DeleteManifestAsync(kind, name, ns, cluster.Kubeconfig!, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting {Kind}/{Name} in {Namespace} failed", kind, name, ns);
        }
    }

    /// <summary>
    /// Asks the API server for a short-lived token bound to the grant's ServiceAccount.
    ///
    /// Requested with an explicit duration rather than mounted as a Secret, so expiry is enforced
    /// by the cluster itself: if every background job in EntKube stopped running, the credential
    /// would still stop working. The API server may return a shorter life than was asked for
    /// (<c>--service-account-max-token-expiration</c>), so the returned timestamp is treated as
    /// authoritative rather than the requested window.
    /// </summary>
    private static async Task<(string Token, DateTime? ExpiresAt)> RequestBoundTokenAsync(
        KubernetesCluster cluster, string ns, string serviceAccount, TimeSpan duration,
        CancellationToken ct)
    {
        using Kubernetes client = CreateClient(cluster.Kubeconfig!);

        Authenticationv1TokenRequest request = new()
        {
            Spec = new V1TokenRequestSpec
            {
                Audiences = TokenAudiences,
                ExpirationSeconds = (long)duration.TotalSeconds,
            }
        };

        Authenticationv1TokenRequest response = await client.CoreV1
            .CreateNamespacedServiceAccountTokenAsync(request, serviceAccount, ns, cancellationToken: ct);

        string? token = response.Status?.Token;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"The API server returned no token for ServiceAccount '{serviceAccount}' in '{ns}'.");

        return (token, response.Status?.ExpirationTimestamp.ToUniversalTime());
    }

    /// <summary>
    /// A kubeconfig pointing at EntKube's proxy, never at the API server.
    ///
    /// The customer's credential is the EntKube token; the cluster's own credential stays here.
    /// That is what makes revocation immediate and every request auditable, and it means the
    /// cluster needs no reachable endpoint of its own.
    /// </summary>
    private string BuildKubeconfig(JitGrant grant, string plaintext)
    {
        string baseUrl = (configuration["Jit:PublicBaseUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // Better an obviously wrong placeholder the operator must fix than a kubeconfig that
            // looks right and silently points at localhost.
            baseUrl = "https://ENTKUBE-PUBLIC-URL-NOT-CONFIGURED";
            logger.LogWarning(
                "Jit:PublicBaseUrl is not configured, so the kubeconfig for grant {GrantId} "
                + "carries a placeholder server URL", grant.Id);
        }

        string context = $"jit-{grant.Namespace}";

        return string.Join("\n", [
            "apiVersion: v1",
            "kind: Config",
            "clusters:",
            $"  - name: {context}",
            "    cluster:",
            $"      server: {baseUrl}/jit/{grant.Id}",
            "users:",
            $"  - name: {context}",
            "    user:",
            $"      token: {plaintext}",
            "contexts:",
            $"  - name: {context}",
            "    context:",
            $"      cluster: {context}",
            $"      user: {context}",
            $"      namespace: {grant.Namespace}",
            $"current-context: {context}",
            "",
        ]);
    }

    // ── Cluster token storage ─────────────────────────────────────────────────
    //
    // The bound token is a live cluster credential, so it is held in the vault rather than in a
    // column on the grant. JitClusterTokenStore owns that decision; this class only needs it
    // written at mint and gone at teardown.

    private async Task StoreClusterTokenAsync(Guid grantId, string token, CancellationToken ct) =>
        await JitClusterTokenStore.StoreAsync(dbFactory, encryption, grantId, token, ct);

    private async Task ForgetClusterTokenAsync(Guid grantId, CancellationToken ct) =>
        await JitClusterTokenStore.ForgetAsync(dbFactory, grantId, ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<KubernetesCluster> LoadClusterAsync(Guid clusterId, CancellationToken ct) =>
        await TryLoadClusterAsync(clusterId, ct)
        ?? throw new InvalidOperationException("The grant's cluster no longer exists.");

    private async Task<KubernetesCluster?> TryLoadClusterAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is not null && string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            throw new InvalidOperationException(
                $"Cluster '{cluster.Name}' has no kubeconfig, so JIT access cannot be provisioned on it.");

        return cluster;
    }

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
        return new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
