using System.ComponentModel.DataAnnotations.Schema;

namespace EntKube.Web.Data;

/// <summary>
/// A registered front-end origin that may push Real User Monitoring (RUM) telemetry to the public RUM
/// ingest endpoint. Identified by a non-secret <see cref="PublicKey"/> embedded in the browser snippet;
/// abuse is bounded by the per-site <see cref="AllowedOrigins"/> allow-list, <see cref="SampleRate"/>, and
/// the shared ingest rate limiter. Tenant-scoped; <see cref="ClusterId"/> is an optional association used
/// only to cross-link browser AJAX events into that cluster's native trace waterfall.
/// </summary>
public class RumSite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Optional cluster this front-end's API calls hit — used to resolve AJAX → trace links.</summary>
    public Guid? ClusterId { get; set; }

    /// <summary>Optional owning application. When set, the site (and its RUM data) is scoped to this app's
    /// customer, so the customer portal only surfaces sites for apps the signed-in customer can access.
    /// Null = tenant-level site with no app owner (admin-managed, hidden from the customer portal).</summary>
    public Guid? AppId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Public (non-secret) site identifier placed in the browser snippet and the ingest URL.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Newline-separated allow-list of exact request Origins (scheme+host+port). Empty = allow any
    /// (not recommended). Enforced server-side against the browser's Origin header on every ingest call.</summary>
    public string AllowedOrigins { get; set; } = "";

    /// <summary>Fraction of batches to keep [0..1]; 1 = keep all.</summary>
    public double SampleRate { get; set; } = 1.0;

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The parsed, trimmed origin allow-list.</summary>
    [NotMapped]
    public IReadOnlyList<string> Origins =>
        AllowedOrigins.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
