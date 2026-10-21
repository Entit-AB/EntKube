using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
    IDbContextFactory<IdentityDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
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
        Guid tenantId, JitGrant grant, TimeSpan duration, CancellationToken ct = default)
    {
        // ⚠️ The cluster used to be loaded by id ALONE — no tenant predicate anywhere in this file —
        // so a grant row naming any cluster in the installation was provisioned on it. For a feature
        // whose entire purpose is handing out cluster access, that is the worst place for the check
        // to be missing. ForAsync answers "no" both for a cluster that is not this tenant's and for
        // one with no credential stored, which were two separate conditions here before.
        EntKube.Web.Services.Clusters.IClusterClient access =
            await clusterAccess.ForAsync(tenantId, grant.KubernetesClusterId, ct)
            ?? throw new InvalidOperationException(
                "The grant's cluster no longer exists, is not this tenant's, or has no kubeconfig, "
                + "so JIT access cannot be provisioned on it.");

        string manifest = JitRbacBuilder.BuildManifest(grant);
        string sa = JitRbacBuilder.ServiceAccountName(grant.Id);

        // The acknowledgment is raised by the seam on the way through, carrying this summary — the
        // explicit gate call here supplied the credential itself, which is the thing being removed.
        await access.ApplyManifestInNamespaceAsync(
            manifest, grant.Namespace,
            $"Grant {grant.Level} JIT access to {JitAccessService.SubjectName(grant)} in {grant.Namespace} "
            + $"until {grant.ExpiresAt:u}", ct);

        ServiceAccountToken bound = await access.RequestServiceAccountTokenAsync(
            sa, grant.Namespace, duration, TokenAudiences, ct);

        // The bound token never leaves EntKube — it is what the proxy presents to the API server.
        // What the customer gets is this, which means nothing to the cluster and everything to
        // the proxy, and can be checked against a revoked row on every request.
        string plaintext = TokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));


        await StoreClusterTokenAsync(grant.Id, bound.Token, ct);

        logger.LogInformation(
            "Minted JIT ServiceAccount {ServiceAccount} in {Namespace} on cluster {ClusterId} for grant {GrantId}",
            sa, grant.Namespace, grant.KubernetesClusterId, grant.Id);

        return new MintedCredential
        {
            ServiceAccountName = sa,
            Plaintext = plaintext,
            TokenHash = Hash(plaintext),
            DisplayPrefix = plaintext[..12],
            TokenExpiresAt = bound.ExpiresAt,
            Kubeconfig = BuildKubeconfig(grant, plaintext),
        };
    }

    public async Task TearDownAsync(Guid tenantId, JitGrant grant, CancellationToken ct = default)
    {
        EntKube.Web.Services.Clusters.IClusterClient? access =
            await clusterAccess.ForAsync(tenantId, grant.KubernetesClusterId, ct);

        if (access is null)
        {
            logger.LogWarning(
                "Cannot tear down JIT grant {GrantId}: cluster {ClusterId} is not this tenant's or "
                + "has no kubeconfig", grant.Id, grant.KubernetesClusterId);
            return;
        }

        // The RoleBinding goes first and its failure is the one that matters. Deleting it is what
        // actually stops an already-issued bound token working; the Role and ServiceAccount are
        // tidying. Deleting them in the other order would leave a window where the binding still
        // grants against a Role that is gone, which is harmless, and a window where the binding
        // survives a partial failure, which is not.
        await DeleteAsync(access, "rolebinding", JitRbacBuilder.RoleBindingName(grant.Id), grant.Namespace, ct);
        await DeleteAsync(access, "role", JitRbacBuilder.RoleName(grant.Id), grant.Namespace, ct);
        await DeleteAsync(access, "serviceaccount", JitRbacBuilder.ServiceAccountName(grant.Id), grant.Namespace, ct);

        await ForgetClusterTokenAsync(grant.Id, ct);

        logger.LogInformation("Tore down JIT grant {GrantId} in {Namespace}", grant.Id, grant.Namespace);
    }

    /// <summary>
    /// Deletes one object, treating "already gone" as success. Teardown runs from revocation and
    /// from the reaper, and neither can tell a clean previous teardown from a crash mid-mint —
    /// so both call this and it has to be idempotent.
    /// </summary>
    private async Task DeleteAsync(
        EntKube.Web.Services.Clusters.IClusterClient access,
        string kind, string name, string ns, CancellationToken ct)
    {
        try
        {
            await access.DeleteManifestAsync(kind, name, ns, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting {Kind}/{Name} in {Namespace} failed", kind, name, ns);
        }
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

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
