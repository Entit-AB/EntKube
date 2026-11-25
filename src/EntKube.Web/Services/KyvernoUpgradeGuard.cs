using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Clusters;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Stops an install or upgrade to Kyverno 1.20 (chart 3.10) or later while the cluster still holds
/// policies that version does not execute, and says what to do about each.
///
/// <para>The chart refuses the same thing itself. Its refusal names a flag,
/// <c>upgrade.acknowledgeLegacyPoliciesNotEnforced</c>, that gets past it by leaving every one of those
/// policies in place and unenforced. EntKube never sets it. What it can do is replace its own policies,
/// which <see cref="KyvernoPolicyService.ApplyToNamespaceAsync"/> does on any cluster that serves the
/// CEL kinds, so this points there.</para>
///
/// <para>An operator who has set the flag in the component's values has decided, and is let through.</para>
/// </summary>
public class KyvernoUpgradeGuard(
    IDbContextFactory<CatalogDbContext> dbFactory,
    IClusterClientFactory clusterClients)
{
    /// <summary>Last chart line that runs both generations, which is where the move has to happen.</summary>
    private const string LastDualChart = "3.9";

    private static readonly (string Kind, string Resource)[] LegacyKinds =
    [
        ("ClusterPolicy", "clusterpolicies.kyverno.io"),
        ("Policy", "policies.kyverno.io"),
        ("CleanupPolicy", "cleanuppolicies.kyverno.io"),
        ("ClusterCleanupPolicy", "clustercleanuppolicies.kyverno.io"),
        ("PolicyException", "policyexceptions.kyverno.io"),
    ];

    /// <summary>Why the operation must not start, or null when it may.</summary>
    /// <param name="tenantId">Whose component.</param>
    public async Task<string?> DescribeLegacyPolicyConflictAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        ClusterComponent? component;
        await using (CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            component = await db.ClusterComponents
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == componentId && c.Cluster.TenantId == tenantId, ct);
        }

        if (component is null || component.HelmChartName != "kyverno") return null;

        // No recorded version installs whatever is newest, which is past the cut-off.
        if (!string.IsNullOrWhiteSpace(component.HelmChartVersion)
            && !KyvernoApi.RetiresLegacy(component.HelmChartVersion))
            return null;

        if (component.HelmValues is { } values
            && Regex.IsMatch(values, @"^\s*acknowledgeLegacyPoliciesNotEnforced:\s*true\b", RegexOptions.Multiline))
            return null;

        IClusterClient? cluster = await clusterClients.ForAsync(tenantId, component.ClusterId, ct);
        if (cluster is null) return null;

        List<(string Kind, string? Namespace, string Name)> found = [];
        foreach ((string kind, string resource) in LegacyKinds)
        {
            string json;
            try
            {
                json = await cluster.GetJsonAllNamespacesAsync(resource, ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not served, or not readable. The chart makes the same check, so nothing is let
                // through by leaving this one to it.
                continue;
            }

            found.AddRange(Names(json).Select(n => (kind, n.Namespace, n.Name)));
        }

        if (found.Count == 0) return null;

        KyvernoApi api = await KyvernoApi.ReadAsync(cluster, ct);
        return Describe(component.HelmChartVersion, api, found);
    }

    /// <summary>The operator's message. Static so its sorting of policies can be tested without a cluster.</summary>
    public static string Describe(
        string? targetChart, KyvernoApi api, IReadOnlyList<(string Kind, string? Namespace, string Name)> found)
    {
        List<(string Kind, string? Namespace, string Name)> ours = found.Where(IsBuiltIn).ToList();
        List<(string Kind, string? Namespace, string Name)> others = found.Where(f => !IsBuiltIn(f)).ToList();

        StringBuilder text = new();
        text.AppendLine($"Kyverno {targetChart ?? "(newest)"} was not installed: this cluster has {found.Count} "
            + "kyverno.io policies, and Kyverno stopped executing those in 1.20 (chart 3.10). "
            + "Nothing was changed; the Kyverno already running still enforces them.");

        if (api.Error is not null)
        {
            text.AppendLine().AppendLine(api.Error);
        }
        else if (!api.CelServed)
        {
            text.AppendLine().AppendLine($"The running Kyverno ({api.ChartVersion ?? "version unknown"}) does not "
                + $"serve the replacement policy kinds. Upgrade it to chart {LastDualChart}.x first, which runs both.");
        }

        if (ours.Count > 0)
        {
            text.AppendLine().AppendLine($"{ours.Count} carry the names of EntKube's built-in policies. Open Policy Enforcement "
                + "for each environment on this cluster and press Apply to all namespaces: each is replaced, and the old one is removed "
                + "once its replacement is enforced. Any still listed afterwards is disabled in EntKube or in a "
                + "namespace it does not manage: press Detect applied, or delete it if it is not wanted.");
            AppendGrouped(text, ours);
        }

        if (others.Count > 0)
        {
            text.AppendLine().AppendLine($"{others.Count} are not EntKube's and cannot be converted automatically. Rewrite each as a "
                + "policies.kyverno.io/v1 policy and delete the old one "
                + "(https://kyverno.io/docs/guides/migration-to-cel/).");
            AppendGrouped(text, others);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendGrouped(StringBuilder text, IEnumerable<(string Kind, string? Namespace, string Name)> policies)
    {
        foreach (var group in policies.GroupBy(p => (p.Kind, p.Namespace))
                     .OrderBy(g => g.Key.Kind, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Namespace, StringComparer.Ordinal))
        {
            string where = group.Key.Namespace is null ? "" : $" in {group.Key.Namespace}";
            text.AppendLine($"  {group.Key.Kind}{where}: "
                + string.Join(", ", group.Select(p => p.Name).Order(StringComparer.Ordinal)));
        }
    }

    private static bool IsBuiltIn((string Kind, string? Namespace, string Name) policy) => policy.Kind switch
    {
        "Policy" => Enum.GetValues<KyvernoPolicyType>()
            .Any(t => KyvernoCelPolicyBuilder.NameOf(t) == policy.Name),
        "ClusterPolicy" => policy.Name == KyvernoPolicyService.ClusterRbacPolicyName,
        _ => false,
    };

    private static IEnumerable<(string? Namespace, string Name)> Names(string listJson)
    {
        using JsonDocument doc = JsonDocument.Parse(listJson);
        List<(string?, string)> names = [];

        if (doc.RootElement.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("metadata", out JsonElement meta)
                    || !meta.TryGetProperty("name", out JsonElement name))
                    continue;

                names.Add((
                    meta.TryGetProperty("namespace", out JsonElement ns) ? ns.GetString() : null,
                    name.GetString()!));
            }
        }

        return names;
    }
}
