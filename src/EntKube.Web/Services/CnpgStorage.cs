using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using k8s.Models;

namespace EntKube.Web.Services;

/// <summary>
/// One PersistentVolumeClaim of a CNPG cluster, as the cluster reports it.
/// </summary>
/// <param name="Role">CNPG's <c>cnpg.io/pvcRole</c>: <c>PG_DATA</c>, <c>PG_WAL</c> or <c>PG_TABLESPACE</c>.</param>
/// <param name="Requested">What the claim asks for — <c>spec.resources.requests.storage</c>.</param>
/// <param name="Capacity">What the claim has been given — <c>status.capacity.storage</c>. Lags
/// <paramref name="Requested"/> while a resize is in flight.</param>
/// <param name="VolumeCapacity">The bound PersistentVolume's <c>spec.capacity.storage</c>, when it could be read.</param>
/// <param name="Resizing">The claim's resize condition (<c>Resizing</c>,
/// <c>FileSystemResizePending</c>, …) when one is set, so a half-finished expansion is visible.</param>
public sealed record CnpgVolumeInfo(
    string PvcName,
    string Role,
    string? Instance,
    string? Requested,
    string? Capacity,
    string? VolumeName,
    string? VolumeCapacity,
    string? StorageClass,
    string? Resizing)
{
    public bool IsData => Role is "" or "PG_DATA";
}

/// <summary>
/// The storage a CNPG cluster actually has, read from the cluster rather than from EntKube's
/// own record.
///
/// <para><b>Why this exists.</b> EntKube stored <c>StorageSize</c> once, at create or register,
/// and rebuilt every Cluster manifest from it. When a database filled its disk and the volumes
/// were grown by hand from 50Gi to 300Gi, the record still said 50Gi, so every action that
/// re-applies the manifest (backup, upgrade, re-apply, the drift check) asked CNPG to
/// <em>shrink</em> the cluster, which its webhook refuses — and an in-place restore would
/// have recreated the cluster on 50Gi volumes that cannot hold the data.</para>
/// </summary>
public sealed class CnpgStorageSnapshot
{
    /// <summary><c>spec.storage.size</c> of the live Cluster resource (or its <c>pvcTemplate</c> request).</summary>
    public string? SpecSize { get; set; }

    /// <summary><c>spec.walStorage.size</c>, when the cluster keeps WAL on a separate volume.</summary>
    public string? WalSpecSize { get; set; }

    /// <summary>The storage class the cluster's data volumes use.</summary>
    public string? StorageClass { get; set; }

    /// <summary>
    /// <c>spec.storage.resizeInUseVolumes</c>. CNPG defaults it to true; when false, raising the
    /// size does not touch existing volumes, so an expansion would silently do nothing.
    /// </summary>
    public bool ResizeInUseVolumes { get; set; } = true;

    /// <summary>The storage class's <c>allowVolumeExpansion</c>; null when it could not be read.</summary>
    public bool? AllowVolumeExpansion { get; set; }

    /// <summary>True when the Cluster resource itself was read. Without it the snapshot is partial.</summary>
    public bool ClusterRead { get; set; }

    public List<CnpgVolumeInfo> Volumes { get; set; } = [];

    /// <summary>
    /// The size a Cluster manifest must carry so that applying it never asks CNPG to shrink:
    /// the larger of the live spec and every data claim's request. Null when nothing live was
    /// read, in which case the caller keeps what it has.
    ///
    /// <para>Claim <em>requests</em> count, and capacities do not: a provisioner may round a
    /// 50Gi claim up to 53687091200 bytes, and writing that back as the spec would be noise.
    /// Requests are what an operator set — including one who grew the claims by hand and not
    /// the Cluster resource, where a new replica would otherwise be created too small for the
    /// data it must hold.</para>
    /// </summary>
    public string? EffectiveSize =>
        CnpgStorage.Largest(Volumes.Where(v => v.IsData).Select(v => v.Requested).Prepend(SpecSize));
}

/// <summary>
/// Parsing and arithmetic for CNPG storage. Pure — no kubectl — so the decisions are tested
/// against the JSON the cluster returns.
/// </summary>
public static partial class CnpgStorage
{
    /// <summary>
    /// A plain size quantity — digits, an optional fraction and an SI or binary suffix. Narrower
    /// than what <see cref="ResourceQuantity"/> accepts (no exponents), because the value is also
    /// shown to people and written into a patch.
    /// </summary>
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)?(Ki|Mi|Gi|Ti|Pi|Ei|k|M|G|T|P|E)?$")]
    private static partial Regex SizePattern();

    /// <summary>The number of bytes in a Kubernetes quantity, or null when it is not one.</summary>
    public static decimal? ToBytes(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
        {
            return null;
        }

        try
        {
            return new ResourceQuantity(quantity.Trim()).ToDecimal();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True for a size an operator may ask for: well-formed and greater than zero.</summary>
    public static bool IsValidSize(string? quantity) =>
        quantity is not null
        && SizePattern().IsMatch(quantity.Trim())
        && ToBytes(quantity) is > 0;

    /// <summary>The largest of the parseable sizes, as written; the first wins a tie.</summary>
    public static string? Largest(IEnumerable<string?> sizes)
    {
        string? best = null;
        decimal bestBytes = -1;

        foreach (string? size in sizes)
        {
            if (ToBytes(size) is decimal bytes && bytes > bestBytes)
            {
                best = size!.Trim();
                bestBytes = bytes;
            }
        }

        return best;
    }

    /// <summary>A quantity for people: "300 GiB", "1.5 TiB". Falls back to the raw text.</summary>
    public static string Describe(string? quantity)
    {
        if (ToBytes(quantity) is not decimal bytes)
        {
            return string.IsNullOrWhiteSpace(quantity) ? "—" : quantity;
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        int unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return $"{Math.Round(bytes, 1).ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>
    /// Reads <c>spec.storage</c> and <c>spec.walStorage</c> from a CNPG Cluster resource.
    /// Leaves the snapshot untouched when the JSON is empty or not a Cluster.
    /// </summary>
    public static void ReadClusterSpec(string? clusterJson, CnpgStorageSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(clusterJson))
        {
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(clusterJson);

        if (!doc.RootElement.TryGetProperty("spec", out JsonElement spec))
        {
            return;
        }

        if (spec.TryGetProperty("storage", out JsonElement storage))
        {
            snapshot.ClusterRead = true;
            snapshot.SpecSize = SizeOf(storage);

            if (storage.TryGetProperty("storageClass", out JsonElement sc) && sc.ValueKind == JsonValueKind.String)
            {
                snapshot.StorageClass = sc.GetString();
            }

            if (storage.TryGetProperty("resizeInUseVolumes", out JsonElement resize)
                && resize.ValueKind is JsonValueKind.False)
            {
                snapshot.ResizeInUseVolumes = false;
            }
        }

        if (spec.TryGetProperty("walStorage", out JsonElement wal))
        {
            snapshot.WalSpecSize = SizeOf(wal);
        }
    }

    /// <summary>
    /// <c>size</c>, or — for a cluster that declares its claims through a template — the
    /// template's storage request. CNPG accepts either.
    /// </summary>
    private static string? SizeOf(JsonElement storage)
    {
        if (storage.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.String)
        {
            return size.GetString();
        }

        return storage.TryGetProperty("pvcTemplate", out JsonElement template)
               && template.TryGetProperty("resources", out JsonElement resources)
               && resources.TryGetProperty("requests", out JsonElement requests)
               && requests.TryGetProperty("storage", out JsonElement requested)
               && requested.ValueKind == JsonValueKind.String
            ? requested.GetString()
            : null;
    }

    /// <summary>Reads a <c>kubectl get pvc -o json</c> list into volume rows, ordered by name.</summary>
    public static List<CnpgVolumeInfo> ReadClaims(string? pvcListJson)
    {
        List<CnpgVolumeInfo> volumes = [];

        if (string.IsNullOrWhiteSpace(pvcListJson))
        {
            return volumes;
        }

        using JsonDocument doc = JsonDocument.Parse(pvcListJson);

        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return volumes;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            string? name = Str(item, "metadata", "name");

            if (name is null)
            {
                continue;
            }

            string role = Str(item, "metadata", "labels", "cnpg.io/pvcRole") ?? "";
            string? instance = Str(item, "metadata", "labels", "cnpg.io/instanceName");

            volumes.Add(new CnpgVolumeInfo(
                PvcName: name,
                Role: role,
                Instance: instance,
                Requested: Str(item, "spec", "resources", "requests", "storage"),
                Capacity: Str(item, "status", "capacity", "storage"),
                VolumeName: Str(item, "spec", "volumeName"),
                VolumeCapacity: null,
                StorageClass: Str(item, "spec", "storageClassName"),
                Resizing: ResizeCondition(item)));
        }

        return volumes.OrderBy(v => v.PvcName, StringComparer.Ordinal).ToList();
    }

    /// <summary>A PersistentVolume's <c>spec.capacity.storage</c>, or null.</summary>
    public static string? ReadVolumeCapacity(string? pvJson)
    {
        if (string.IsNullOrWhiteSpace(pvJson))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(pvJson);
        return Str(doc.RootElement, "spec", "capacity", "storage");
    }

    /// <summary>
    /// A StorageClass's <c>allowVolumeExpansion</c>. Absent means false — that is the
    /// Kubernetes default, and the reason expansion fails on classes nobody configured.
    /// </summary>
    public static bool? ReadAllowVolumeExpansion(string? storageClassJson)
    {
        if (string.IsNullOrWhiteSpace(storageClassJson))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(storageClassJson);

        if (!doc.RootElement.TryGetProperty("metadata", out _))
        {
            return null;
        }

        return doc.RootElement.TryGetProperty("allowVolumeExpansion", out JsonElement allow)
               && allow.ValueKind == JsonValueKind.True;
    }

    /// <summary>The merge patch that sets only <c>spec.storage.size</c>.</summary>
    public static string BuildResizePatch(string size) =>
        JsonSerializer.Serialize(new { spec = new { storage = new { size = size.Trim() } } });

    private static string? ResizeCondition(JsonElement pvc)
    {
        if (!pvc.TryGetProperty("status", out JsonElement status)
            || !status.TryGetProperty("conditions", out JsonElement conditions)
            || conditions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement condition in conditions.EnumerateArray())
        {
            if (Str(condition, "status") == "True" && Str(condition, "type") is string type)
            {
                return type;
            }
        }

        return null;
    }

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
