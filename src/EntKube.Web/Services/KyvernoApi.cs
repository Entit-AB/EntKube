using System.Text.Json;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Upgrades;

namespace EntKube.Web.Services;

/// <summary>
/// Which policy API the Kyverno on one cluster speaks.
///
/// <para>Kyverno 1.20 (chart 3.10) stopped executing the <c>kyverno.io</c> kinds EntKube wrote until
/// then — <c>Policy</c> and <c>ClusterPolicy</c>. The API still accepts them, so writing one to such a
/// cluster succeeds and enforces nothing. The CEL kinds in <c>policies.kyverno.io/v1</c> replace them
/// and are served from Kyverno 1.17 (chart 3.7), so 1.17 to 1.19 run both, which is the window in
/// which a cluster can be moved without ever being unenforced.</para>
/// </summary>
/// <param name="CelServed">The cluster serves <c>NamespacedValidatingPolicy</c> at <c>v1</c>.</param>
/// <param name="ChartVersion">The installed Kyverno chart, from its CRD label; null when unlabelled.</param>
/// <param name="Error">Set when the cluster could not be asked. Nothing may be assumed then.</param>
public sealed record KyvernoApi(bool CelServed, string? ChartVersion = null, string? Error = null)
{
    public const string ValidatingCrd = "namespacedvalidatingpolicies.policies.kyverno.io";

    private const string LegacyPolicyCrd = "policies.kyverno.io";
    private const string CrdResource = "customresourcedefinitions.apiextensions.k8s.io";
    private const string VersionLabel = "app.kubernetes.io/version";

    /// <summary>
    /// Whether this Kyverno still runs the <c>kyverno.io</c> kinds. An unreadable version counts as
    /// "yes" because every caller uses "no" to withhold a policy an operator asked for.
    /// </summary>
    public bool ExecutesLegacy => !RetiresLegacy(ChartVersion);

    /// <summary>
    /// True for a chart known to have dropped the legacy engine: 3.10 and later, its release
    /// candidates included, since they carry the same engine.
    /// </summary>
    public static bool RetiresLegacy(string? chartVersion) =>
        SemVer.Parse(chartVersion) is { } v && (v.Major > 3 || (v.Major == 3 && v.Minor >= 10));

    /// <summary>Asks the cluster. A missing CRD is an answer; anything else that fails is an <see cref="Error"/>.</summary>
    public static async Task<KyvernoApi> ReadAsync(IClusterClient cluster, CancellationToken ct = default)
    {
        (bool celFound, JsonElement celCrd, string? celError) = await ReadCrdAsync(cluster, ValidatingCrd, ct);
        if (celError is not null) return new KyvernoApi(false, Error: celError);

        (bool legacyFound, JsonElement legacyCrd, string? legacyError) = await ReadCrdAsync(cluster, LegacyPolicyCrd, ct);
        if (legacyError is not null) return new KyvernoApi(false, Error: legacyError);

        return new KyvernoApi(
            celFound && ServesV1(celCrd),
            legacyFound ? Label(legacyCrd) : null);
    }

    private static async Task<(bool Found, JsonElement Crd, string? Error)> ReadCrdAsync(
        IClusterClient cluster, string name, CancellationToken ct)
    {
        HelmExecutionResult result = await cluster.GetResourceJsonAsync(CrdResource, name, "", ct);

        if (!result.Success)
        {
            // kubectl's wording for an absent object. Reading any other failure as "absent" would
            // send legacy policies to a Kyverno that ignores them whenever the API server blinked.
            return result.Output.Contains("(NotFound)", StringComparison.Ordinal)
                ? (false, default, null)
                : (false, default, $"Could not read the Kyverno API on {cluster.ClusterName}: {result.Output}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.Output);
            return (true, doc.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return (false, default, $"Could not read the Kyverno API on {cluster.ClusterName}: {ex.Message}");
        }
    }

    private static bool ServesV1(JsonElement crd) =>
        crd.TryGetProperty("spec", out JsonElement spec)
        && spec.TryGetProperty("versions", out JsonElement versions)
        && versions.ValueKind == JsonValueKind.Array
        && versions.EnumerateArray().Any(v =>
            v.TryGetProperty("name", out JsonElement name) && name.GetString() == "v1"
            && v.TryGetProperty("served", out JsonElement served) && served.ValueKind == JsonValueKind.True);

    private static string? Label(JsonElement crd) =>
        crd.TryGetProperty("metadata", out JsonElement meta)
        && meta.TryGetProperty("labels", out JsonElement labels)
        && labels.ValueKind == JsonValueKind.Object
        && labels.TryGetProperty(VersionLabel, out JsonElement version)
            ? version.GetString()
            : null;
}
