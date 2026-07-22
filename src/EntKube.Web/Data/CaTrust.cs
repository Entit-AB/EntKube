namespace EntKube.Web.Data;

/// <summary>
/// Which namespaces a trust bundle or certificate distribution reaches.
/// </summary>
public enum TrustDistributionScope
{
    /// <summary>Every namespace on the target cluster.</summary>
    AllNamespaces = 0,

    /// <summary>Only the namespaces this tenant's apps are deployed into on the target cluster.</summary>
    TenantNamespaces = 1,

    /// <summary>Namespaces matching a set of labels (stored as JSON in NamespaceSelectorJson).</summary>
    MatchLabels = 2,

    /// <summary>Only the namespaces of one specific customer app (see AppId/EnvironmentId).</summary>
    App = 3,
}

/// <summary>Where a trust-manager Bundle writes its assembled trust store.</summary>
public enum TrustBundleTargetKind
{
    /// <summary>Write the trust store into a ConfigMap (the default; no extra Helm config needed).</summary>
    ConfigMap = 0,

    /// <summary>Write it into a Secret (requires <c>secretTargets.enabled=true</c> on trust-manager).</summary>
    Secret = 1,
}

/// <summary>
/// A managed CA trust bundle. Rendered to a trust-manager <c>Bundle</c> custom resource
/// that distributes its assembled CA certificates (public certs only — never private keys)
/// into a ConfigMap/Secret in every selected namespace, so all customer-solution pods on the
/// cluster can mount a common trust store. Tenant-scoped and targeted at a single cluster.
/// </summary>
public class CaTrustBundle
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The cluster whose namespaces receive the trust store. FK to <see cref="KubernetesCluster"/> (no nav — scalar, like VaultSecret.KubernetesClusterId).</summary>
    public Guid ClusterId { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    public TrustBundleTargetKind TargetKind { get; set; } = TrustBundleTargetKind.ConfigMap;

    /// <summary>Name of the Bundle CR and of the ConfigMap/Secret it produces in each namespace.</summary>
    public string TargetName { get; set; } = "entkube-trust-bundle";

    /// <summary>The data key under which the concatenated PEM bundle is written (e.g. <c>ca-certificates.crt</c>).</summary>
    public string TargetKey { get; set; } = "ca-certificates.crt";

    /// <summary>Also include the cluster's default/public CA certificates in the bundle (trust-manager <c>useDefaultCAs</c>).</summary>
    public bool IncludeDefaultCAs { get; set; }

    public TrustDistributionScope Scope { get; set; } = TrustDistributionScope.TenantNamespaces;

    /// <summary>For <see cref="TrustDistributionScope.MatchLabels"/>: JSON object of label key→value.</summary>
    public string? NamespaceSelectorJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CaTrustBundleSource> Sources { get; set; } = [];
}

/// <summary>
/// One CA certificate (PEM, public material) that contributes to a <see cref="CaTrustBundle"/>.
/// Parsed on read for validity display; never encrypted — CA certificates are public.
/// </summary>
public class CaTrustBundleSource
{
    public Guid Id { get; set; }
    public Guid BundleId { get; set; }

    /// <summary>Friendly label for this CA (e.g. "Corp Root CA 2026").</summary>
    public required string Name { get; set; }

    /// <summary>The CA certificate(s) in PEM format.</summary>
    public required string Pem { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public CaTrustBundle Bundle { get; set; } = null!;
}

/// <summary>
/// Mirrors a vault certificate (with or without its private key) into a Kubernetes Secret in
/// every selected namespace on a cluster. trust-manager cannot carry private keys, so this is
/// the second distribution mechanism: a cert+key becomes a <c>kubernetes.io/tls</c> Secret in
/// each namespace; a cert-only distribution becomes an Opaque Secret carrying just the public
/// certificate. Kept fresh (new namespaces + renewed certs) by a background reconciler.
/// </summary>
public class CertificateDistribution
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>
    /// Target cluster (scalar FK to <see cref="KubernetesCluster"/>, no nav). Null means "resolve
    /// clusters dynamically": for <see cref="TrustDistributionScope.App"/> from the app's deployments,
    /// otherwise across all of the tenant's clusters (tenant-wide distribution).
    /// </summary>
    public Guid? ClusterId { get; set; }

    public required string Name { get; set; }

    /// <summary>The source certificate: a <see cref="VaultSecret"/> of type Certificate in this tenant's vault.</summary>
    public Guid VaultSecretId { get; set; }

    /// <summary>For <see cref="TrustDistributionScope.App"/>: the target customer app.</summary>
    public Guid? AppId { get; set; }

    /// <summary>For <see cref="TrustDistributionScope.App"/>: optionally restrict to one environment (null = all envs of the app).</summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>Name of the Secret written into every target namespace.</summary>
    public required string TargetSecretName { get; set; }

    /// <summary>
    /// When true (and the source cert carries a private key) a <c>kubernetes.io/tls</c> Secret is
    /// written; otherwise only the public certificate is distributed as an Opaque Secret.
    /// </summary>
    public bool IncludeKey { get; set; } = true;

    public TrustDistributionScope Scope { get; set; } = TrustDistributionScope.TenantNamespaces;

    /// <summary>For <see cref="TrustDistributionScope.MatchLabels"/>: JSON object of label key→value.</summary>
    public string? NamespaceSelectorJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the reconciler (or a manual apply) successfully mirrored this to the cluster.</summary>
    public DateTime? LastSyncedAt { get; set; }
}
