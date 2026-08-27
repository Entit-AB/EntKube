using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>One workload's mesh membership, as observed on the cluster.</summary>
/// <param name="PodName">Pod name, for naming the workloads that would break.</param>
/// <param name="HasSidecar">True when the pod runs an <c>istio-proxy</c> container.</param>
public record MeshPodStatus(string PodName, bool HasSidecar);

/// <summary>
/// Whether a namespace can move to STRICT without cutting traffic off.
/// </summary>
/// <param name="IsAmbient">The namespace is enrolled in Istio's ambient dataplane (no sidecar needed).</param>
/// <param name="TotalPods">Pods considered.</param>
/// <param name="PodsOutsideMesh">Pods with neither a sidecar nor ambient enrolment — these would break.</param>
public record MeshReadiness(bool IsAmbient, int TotalPods, IReadOnlyList<string> PodsOutsideMesh)
{
    /// <summary>True when every workload in the namespace can speak mTLS.</summary>
    public bool IsReady => PodsOutsideMesh.Count == 0;
}

/// <summary>
/// Service-to-service mTLS inside the mesh: manages the per-namespace posture and renders the
/// Istio <c>PeerAuthentication</c> that enforces it.
///
/// This inverts a deliberate platform default, so it is worth being explicit about what is being
/// undone. EntKube applies a PERMISSIVE PeerAuthentication to every backend namespace when it
/// publishes a route, because an Istio ingress gateway attempts mTLS to backends and a pod with no
/// sidecar cannot complete that handshake. PERMISSIVE lets both kinds of pod work. STRICT removes
/// that accommodation: any workload without a mesh identity — including the platform's own
/// probes — stops being able to reach the namespace. That is the point of it, and also why
/// <see cref="EvaluateReadiness"/> gates the change on every pod actually being in the mesh.
/// </summary>
public class MeshMtlsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<MeshMtlsService> logger)
{
    /// <summary>
    /// Name of the namespace-wide PeerAuthentication. Deliberately unchanged from when this
    /// resource only ever carried PERMISSIVE: renaming it would leave the old object in the
    /// cluster next to the new one, and two namespace-wide PeerAuthentications leave which policy
    /// applies up to Istio. The name says where it came from, the spec says what it does.
    /// </summary>
    public const string PeerAuthenticationName = "entkube-permissive";

    /// <summary>Container name Istio injects as the sidecar proxy.</summary>
    public const string SidecarContainerName = "istio-proxy";

    /// <summary>Namespace label enrolling a namespace in Istio's ambient dataplane.</summary>
    public const string AmbientLabelKey = "istio.io/dataplane-mode";

    // ──────── Posture (CRUD) ────────

    public async Task<List<MeshMtlsPolicy>> GetPoliciesAsync(
        Guid tenantId, Guid? clusterId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.MeshMtlsPolicies
            .Where(p => p.TenantId == tenantId && (clusterId == null || p.ClusterId == clusterId))
            .OrderBy(p => p.Namespace)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The configured posture for a namespace. A namespace with no row is PERMISSIVE — the
    /// platform default, and what every namespace was before this feature existed.
    /// </summary>
    public async Task<MeshMtlsMode> GetModeAsync(Guid clusterId, string ns, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MeshMtlsPolicy? policy = await db.MeshMtlsPolicies
            .FirstOrDefaultAsync(p => p.ClusterId == clusterId && p.Namespace == ns, ct);

        return policy?.Mode ?? MeshMtlsMode.Permissive;
    }

    public async Task<MeshMtlsPolicy> SetModeAsync(
        Guid tenantId, Guid clusterId, string ns, MeshMtlsMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ns))
            throw new InvalidOperationException("Namespace is required.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        string trimmed = ns.Trim();
        MeshMtlsPolicy? policy = await db.MeshMtlsPolicies
            .FirstOrDefaultAsync(p => p.ClusterId == clusterId && p.Namespace == trimmed, ct);

        if (policy is null)
        {
            policy = new MeshMtlsPolicy
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterId = clusterId,
                Namespace = trimmed,
                Mode = mode
            };
            db.MeshMtlsPolicies.Add(policy);
        }
        else
        {
            policy.Mode = mode;
            policy.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Mesh mTLS for {Namespace} on cluster {ClusterId} set to {Mode}",
            trimmed, clusterId, mode);

        return policy;
    }

    /// <summary>Records that the PeerAuthentication for a namespace was applied.</summary>
    public async Task MarkAppliedAsync(Guid clusterId, string ns, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        MeshMtlsPolicy? policy = await db.MeshMtlsPolicies
            .FirstOrDefaultAsync(p => p.ClusterId == clusterId && p.Namespace == ns, ct);
        if (policy is null) return;

        policy.AppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ──────── Readiness ────────

    /// <summary>
    /// Decides whether a namespace can take STRICT. A pod is in the mesh if it carries a sidecar,
    /// or if the namespace runs in ambient mode — where ztunnel provides the identity and no
    /// sidecar container appears in the pod at all.
    /// </summary>
    public static MeshReadiness EvaluateReadiness(bool isAmbient, IEnumerable<MeshPodStatus> pods)
    {
        List<MeshPodStatus> list = pods.ToList();

        // Under ambient every pod in the namespace has a mesh identity whether or not it has a
        // sidecar, so sidecar absence is not evidence of anything.
        IReadOnlyList<string> outside = isAmbient
            ? []
            : list.Where(p => !p.HasSidecar).Select(p => p.PodName).ToList();

        return new MeshReadiness(isAmbient, list.Count, outside);
    }

    // ──────── Rendering ────────

    /// <summary>
    /// Renders the namespace-wide PeerAuthentication for a posture.
    ///
    /// Always emitted, in both modes and under the same resource name, so switching back to
    /// PERMISSIVE overwrites STRICT rather than leaving it standing.
    /// </summary>
    public static string BuildPeerAuthenticationYaml(string ns, MeshMtlsMode mode) =>
        $"apiVersion: security.istio.io/v1beta1\n" +
        $"kind: PeerAuthentication\n" +
        $"metadata:\n" +
        $"  name: {PeerAuthenticationName}\n" +
        $"  namespace: {ns}\n" +
        $"  annotations:\n" +
        $"    app.kubernetes.io/managed-by: entkube\n" +
        $"spec:\n" +
        $"  mtls:\n" +
        $"    mode: {(mode == MeshMtlsMode.Strict ? "STRICT" : "PERMISSIVE")}\n";

    /// <summary>
    /// The service-wide <c>trafficPolicy.tls.mode</c> the ingress gateway should use when dialling
    /// backends in this namespace.
    ///
    /// PERMISSIVE keeps DISABLE — plaintext to the pod — which is what lets a sidecar-less backend
    /// serve traffic at all. Under STRICT that same rule is what would take the route down: the
    /// namespace refuses plaintext, so the gateway must present a mesh identity instead. The rule
    /// is rewritten in place under its existing name rather than deleted, so the switch is a
    /// single idempotent apply in both directions.
    ///
    /// This governs the mesh hop only. A backend that terminates TLS itself is handled separately
    /// by portLevelSettings, which is unaffected by the namespace posture.
    /// </summary>
    public static string BackendTlsMode(MeshMtlsMode mode) =>
        mode == MeshMtlsMode.Strict ? "ISTIO_MUTUAL" : "DISABLE";
}
