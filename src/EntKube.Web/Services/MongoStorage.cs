using System.Text.Json;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services;

/// <summary>
/// The storage a MongoDB Community cluster actually has, read from the cluster rather than from
/// EntKube's own record.
///
/// <para><b>Why this exists.</b> EntKube stored <c>StorageSize</c> once, at create time, and
/// rebuilt every MongoDBCommunity manifest from it. Volumes grown after a disk filled left the
/// record behind, so the page showed the old size and the resize dialog judged "can only grow"
/// against it.</para>
///
/// <para><b>Two sizes, on purpose.</b> The operator turns the manifest's
/// <c>volumeClaimTemplates</c> into a StatefulSet, and Kubernetes forbids changing a
/// StatefulSet's claim templates after it is created. So the manifest must keep carrying
/// <see cref="TemplateSize"/>, the size the StatefulSet already has. Volumes grow by patching
/// each claim, which is why the claims can be larger than the template.
/// <see cref="EffectiveSize"/> is what the cluster really holds, and is what people are shown,
/// what is recorded, and what a restored copy is sized to.</para>
/// </summary>
public sealed class MongoStorageSnapshot
{
    /// <summary>
    /// The <c>data-volume</c> claim template's storage request: the StatefulSet's own when it
    /// could be read, else the MongoDBCommunity resource's. Null when neither declares one.
    /// </summary>
    public string? TemplateSize { get; set; }

    /// <summary>Where <see cref="TemplateSize"/> was read: "StatefulSet" or "MongoDBCommunity".</summary>
    public string? TemplateSource { get; set; }

    /// <summary>The storage class the data volumes use.</summary>
    public string? StorageClass { get; set; }

    /// <summary>The storage class's <c>allowVolumeExpansion</c>; null when it could not be read.</summary>
    public bool? AllowVolumeExpansion { get; set; }

    /// <summary>
    /// Every claim of the cluster's StatefulSet. <see cref="CnpgVolumeInfo.Role"/> is the claim
    /// template's name, <see cref="MongoStorage.DataVolume"/> or <see cref="MongoStorage.LogsVolume"/>.
    /// </summary>
    public List<CnpgVolumeInfo> Volumes { get; set; } = [];

    public IEnumerable<CnpgVolumeInfo> DataVolumes => Volumes.Where(v => v.Role == MongoStorage.DataVolume);

    /// <summary>
    /// What each member really has: the larger of the claim template and every data claim's
    /// request. Requests count, capacities do not, because a provisioner may round a claim up and
    /// that rounding is not a size anybody chose.
    /// </summary>
    public string? EffectiveSize =>
        CnpgStorage.Largest(DataVolumes.Select(v => v.Requested).Prepend(TemplateSize));
}

/// <summary>
/// Parsing for MongoDB Community storage. Pure, with no kubectl, so the decisions are tested
/// against the JSON the cluster returns. Quantity arithmetic is shared with <see cref="CnpgStorage"/>.
/// </summary>
public static class MongoStorage
{
    /// <summary>The claim template holding the database files. The operator's name, and ours.</summary>
    public const string DataVolume = "data-volume";

    /// <summary>The operator's second claim template, for mongod and agent logs.</summary>
    public const string LogsVolume = "logs-volume";

    /// <summary>
    /// The <c>data-volume</c> template of a StatefulSet (<c>spec.volumeClaimTemplates</c>).
    /// </summary>
    public static (string? Size, string? StorageClass) ReadStatefulSetTemplate(string? statefulSetJson) =>
        ReadTemplate(statefulSetJson, "spec");

    /// <summary>
    /// The <c>data-volume</c> template a MongoDBCommunity resource declares
    /// (<c>spec.statefulSet.spec.volumeClaimTemplates</c>).
    /// </summary>
    public static (string? Size, string? StorageClass) ReadCommunityTemplate(string? communityJson) =>
        ReadTemplate(communityJson, "spec", "statefulSet", "spec");

    private static (string? Size, string? StorageClass) ReadTemplate(string? json, params string[] pathToSpec)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement current = doc.RootElement;

        foreach (string segment in pathToSpec)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return (null, null);
            }
        }

        if (current.ValueKind != JsonValueKind.Object
            || !current.TryGetProperty("volumeClaimTemplates", out JsonElement templates)
            || templates.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (JsonElement template in templates.EnumerateArray())
        {
            if (Str(template, "metadata", "name") == DataVolume)
            {
                return (Str(template, "spec", "resources", "requests", "storage"),
                        Str(template, "spec", "storageClassName"));
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The claims a cluster's StatefulSet owns, picked out of a namespace's claim list by the
    /// names a StatefulSet gives them: <c>{template}-{statefulset}-{ordinal}</c>. Names are used
    /// rather than labels because the operator's labels have changed between versions.
    /// </summary>
    public static List<CnpgVolumeInfo> ReadClaims(string? pvcListJson, string clusterName)
    {
        Regex owned = new(
            $"^({DataVolume}|{LogsVolume})-{Regex.Escape(clusterName)}-([0-9]+)$",
            RegexOptions.CultureInvariant);

        List<CnpgVolumeInfo> claims = [];

        foreach (CnpgVolumeInfo claim in CnpgStorage.ReadClaims(pvcListJson))
        {
            Match match = owned.Match(claim.PvcName);

            if (match.Success)
            {
                claims.Add(claim with
                {
                    Role = match.Groups[1].Value,
                    Instance = $"{clusterName}-{match.Groups[2].Value}",
                });
            }
        }

        return claims;
    }

    /// <summary>The merge patch that sets only a claim's storage request.</summary>
    public static string BuildClaimResizePatch(string size) =>
        JsonSerializer.Serialize(new { spec = new { resources = new { requests = new { storage = size.Trim() } } } });

    private static string? Str(JsonElement element, params string[] path)
    {
        JsonElement current = element;

        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
