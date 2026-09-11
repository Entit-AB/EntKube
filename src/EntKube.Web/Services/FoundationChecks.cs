using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>One thing that was checked, and what was found.</summary>
public sealed record FoundationCheck(string Key, string Title, bool Passed, string Detail)
{
    public static FoundationCheck Pass(string key, string title, string detail) => new(key, title, true, detail);
    public static FoundationCheck Fail(string key, string title, string detail) => new(key, title, false, detail);
}

/// <summary>
/// The judgements behind foundation verification, as functions over the JSON a cluster returns.
///
/// <para><b>Why verification exists at all.</b> Every one of these components installs cleanly and
/// reports success while being useless. A CNI whose pods are running but whose nodes never go
/// Ready. A cloud-controller-manager that is up but never removed the uninitialized taint, so
/// nothing schedules. A CSI driver with a StorageClass that no volume ever binds against. Helm
/// exiting zero says the chart was applied, which is not the same claim.</para>
///
/// <para>Pure, so the judgement can be asserted without a cluster — the fetching is the part that
/// needs one.</para>
/// </summary>
public static class FoundationChecks
{
    /// <summary>Nodes are the CNI's verdict: without a working pod network they never go Ready.</summary>
    public static FoundationCheck Nodes(string nodesJson)
    {
        List<JsonElement> nodes = Items(nodesJson);
        if (nodes.Count == 0)
        {
            return FoundationCheck.Fail("nodes", "Nodes ready",
                "No nodes were returned, so either the cluster is empty or it could not be read.");
        }

        List<string> notReady = nodes
            .Where(n => !IsNodeReady(n))
            .Select(NameOf)
            .ToList();

        return notReady.Count == 0
            ? FoundationCheck.Pass("nodes", "Nodes ready", $"All {nodes.Count} node(s) are Ready.")
            : FoundationCheck.Fail("nodes", "Nodes ready",
                $"{notReady.Count} of {nodes.Count} node(s) are not Ready: {string.Join(", ", notReady)}. "
                + "A node that stays NotReady after the CNI is installed usually means the pod network "
                + "never came up.");
    }

    /// <summary>
    /// The cloud-controller-manager's verdict. It has two jobs at node level, and both leave a
    /// trace: it stamps each node with a provider ID, and it removes the uninitialized taint. A
    /// node still carrying that taint schedules nothing, which looks like a scheduling problem
    /// rather than a missing CCM.
    /// </summary>
    public static FoundationCheck CloudControllerManager(string nodesJson)
    {
        List<JsonElement> nodes = Items(nodesJson);
        if (nodes.Count == 0)
        {
            return FoundationCheck.Fail("ccm", "Cloud controller manager", "No nodes were returned.");
        }

        List<string> uninitialized = nodes
            .Where(n => Taints(n).Any(t => t.StartsWith("node.cloudprovider.kubernetes.io/uninitialized", StringComparison.Ordinal)))
            .Select(NameOf)
            .ToList();

        if (uninitialized.Count > 0)
        {
            return FoundationCheck.Fail("ccm", "Cloud controller manager",
                $"{uninitialized.Count} node(s) still carry the uninitialized taint: {string.Join(", ", uninitialized)}. "
                + "Nothing will schedule on them until the cloud-controller-manager clears it.");
        }

        List<string> withoutProviderId = nodes
            .Where(n => string.IsNullOrEmpty(ProviderIdOf(n)))
            .Select(NameOf)
            .ToList();

        return withoutProviderId.Count == 0
            ? FoundationCheck.Pass("ccm", "Cloud controller manager",
                $"All {nodes.Count} node(s) are initialized and carry a provider ID.")
            : FoundationCheck.Fail("ccm", "Cloud controller manager",
                $"{withoutProviderId.Count} node(s) have no provider ID: {string.Join(", ", withoutProviderId)}. "
                + "Load balancers and node lifecycle will not work without it.");
    }

    /// <summary>A default StorageClass, because a PVC without one specified goes nowhere.</summary>
    public static FoundationCheck DefaultStorageClass(string storageClassJson)
    {
        List<JsonElement> classes = Items(storageClassJson);

        List<string> defaults = classes
            .Where(IsDefaultStorageClass)
            .Select(NameOf)
            .ToList();

        if (defaults.Count == 1)
        {
            return FoundationCheck.Pass("storageclass", "Default storage class",
                $"'{defaults[0]}' is the default.");
        }

        if (defaults.Count == 0)
        {
            return FoundationCheck.Fail("storageclass", "Default storage class",
                classes.Count == 0
                    ? "No storage classes exist, so no PersistentVolumeClaim can ever bind."
                    : $"None of the {classes.Count} storage class(es) is marked default, so a claim that does "
                      + "not name one will stay Pending forever.");
        }

        // More than one default is worse than none: which one wins is not defined, so the same
        // claim can land on different storage on different days.
        return FoundationCheck.Fail("storageclass", "Default storage class",
            $"{defaults.Count} storage classes are marked default ({string.Join(", ", defaults)}). "
            + "Which one a claim gets is undefined.");
    }

    /// <summary>Whether the probe claim actually bound. The only proof that storage works.</summary>
    public static FoundationCheck VolumeBinding(string pvcJson, string claimName)
    {
        string? phase = Items(pvcJson)
            .Where(p => NameOf(p) == claimName)
            .Select(p => Str(Child(p, "status"), "phase"))
            .FirstOrDefault();

        return phase switch
        {
            "Bound" => FoundationCheck.Pass("volume", "Volume provisioning",
                "A test claim was created and bound, so the CSI driver is working end to end."),
            null => FoundationCheck.Fail("volume", "Volume provisioning",
                "The test claim could not be found after it was created."),
            _ => FoundationCheck.Fail("volume", "Volume provisioning",
                $"The test claim is {phase} rather than Bound. The storage class exists but nothing is "
                + "serving it — check the CSI controller's logs.")
        };
    }

    /// <summary>A Deployment that has at least one available replica, by name.</summary>
    public static FoundationCheck Deployment(string deploymentsJson, string key, string title, string name)
    {
        JsonElement? deployment = Items(deploymentsJson).FirstOrDefault(d => NameOf(d) == name) is { } found
            && found.ValueKind != JsonValueKind.Undefined
                ? found
                : null;

        if (deployment is null)
        {
            return FoundationCheck.Fail(key, title, $"No deployment called '{name}' exists.");
        }

        int available = Int(Child(deployment.Value, "status"), "availableReplicas") ?? 0;

        return available > 0
            ? FoundationCheck.Pass(key, title, $"{available} replica(s) available.")
            : FoundationCheck.Fail(key, title,
                $"'{name}' exists but has no available replica, so it is installed and doing nothing.");
    }

    /// <summary>
    /// Velero's backup location. It reports Unavailable when the bucket is unreachable or the
    /// credentials are wrong, which is the difference between having backups and believing you do.
    /// </summary>
    public static FoundationCheck BackupLocation(string locationsJson)
    {
        List<JsonElement> locations = Items(locationsJson);
        if (locations.Count == 0)
        {
            return FoundationCheck.Fail("backup", "Backup location",
                "Velero has no backup storage location, so nothing is being backed up.");
        }

        List<string> unavailable = locations
            .Where(l => !string.Equals(Str(Child(l, "status"), "phase"), "Available", StringComparison.Ordinal))
            .Select(NameOf)
            .ToList();

        return unavailable.Count == 0
            ? FoundationCheck.Pass("backup", "Backup location",
                $"{locations.Count} location(s) available.")
            : FoundationCheck.Fail("backup", "Backup location",
                $"{string.Join(", ", unavailable)} not available. Velero will accept backup requests and "
                + "fail every one of them.");
    }

    // ──────── JSON helpers ────────

    private static bool IsNodeReady(JsonElement node)
    {
        JsonElement status = Child(node, "status");
        if (!status.TryGetProperty("conditions", out JsonElement conditions)
            || conditions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement condition in conditions.EnumerateArray())
        {
            if (Str(condition, "type") == "Ready")
            {
                return Str(condition, "status") == "True";
            }
        }

        return false;
    }

    private static IEnumerable<string> Taints(JsonElement node)
    {
        JsonElement spec = Child(node, "spec");
        if (!spec.TryGetProperty("taints", out JsonElement taints) || taints.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement taint in taints.EnumerateArray())
        {
            string? key = Str(taint, "key");
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static string? ProviderIdOf(JsonElement node) => Str(Child(node, "spec"), "providerID");

    private static bool IsDefaultStorageClass(JsonElement storageClass)
    {
        JsonElement annotations = Child(Child(storageClass, "metadata"), "annotations");

        // Both spellings exist in the wild; the beta one is still what several charts write.
        return Str(annotations, "storageclass.kubernetes.io/is-default-class") == "true"
            || Str(annotations, "storageclass.beta.kubernetes.io/is-default-class") == "true";
    }

    private static string NameOf(JsonElement item) => Str(Child(item, "metadata"), "name") ?? "(unnamed)";

    private static List<JsonElement> Items(string json)
    {
        List<JsonElement> items = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return items;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in array.EnumerateArray())
                {
                    items.Add(item.Clone());
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable response is "nothing found", which every caller reports as a failure.
        }

        return items;
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement child)
            ? child
            : default;

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;
}
