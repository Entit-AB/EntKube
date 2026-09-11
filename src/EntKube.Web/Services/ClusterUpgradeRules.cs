namespace EntKube.Web.Services;

/// <summary>The kinds of change day-2 makes, ordered by what they can cost if they go wrong.</summary>
public enum ClusterOperation
{
    /// <summary>Change a pool's replica count. Adds or removes machines; touches nothing existing.</summary>
    ScalePool,

    /// <summary>Add a pool. Purely additive.</summary>
    AddPool,

    /// <summary>Remove a pool. Drains and deletes its machines.</summary>
    RemovePool,

    /// <summary>Change a pool's machine shape. Replaces every machine in it, one rollout.</summary>
    ReshapePool,

    /// <summary>Move the control plane to a new version. Replaces every control-plane machine.</summary>
    UpgradeControlPlane,

    /// <summary>Move a pool to a new version. Replaces every machine in it.</summary>
    UpgradePool
}

/// <summary>Whether an operation may start, and if not, why not in words an operator can act on.</summary>
public sealed record OperationVerdict(bool Allowed, string? Reason = null)
{
    public static OperationVerdict Ok { get; } = new(true);
    public static OperationVerdict No(string reason) => new(false, reason);
}

/// <summary>
/// The rules about when a cluster may be changed and which versions it may be moved between.
///
/// <para>Pure, and separate from the code that performs the operations, because these are the
/// judgements that prevent the expensive mistakes — starting a second rollout on top of one
/// already running, or moving a control plane two minors at once — and they need to be assertable
/// without a cluster to try them on.</para>
/// </summary>
public static class ClusterUpgradeRules
{
    /// <summary>
    /// Whether an operation may start against a cluster in this observed state.
    ///
    /// <para>The central rule: never more than one thing that replaces machines in flight at a
    /// time. CAPI will happily accept a second rollout on top of a first, and the result is a
    /// cluster replacing more machines at once than its own surge budget intended — which on a
    /// three-node control plane is how quorum is lost.</para>
    /// </summary>
    public static OperationVerdict CanStart(ClusterObservation observation, ClusterOperation operation)
    {
        if (observation.Health == ClusterHealth.Unreachable)
        {
            return OperationVerdict.No(
                "The cluster is not reachable, so there is no way to tell what changing it would do. "
                + (observation.Summary ?? ""));
        }

        // Scaling and adding do not replace anything that exists, so they are safe alongside a
        // rollout — and refusing them would mean a cluster under load could not be grown while it
        // happened to be updating.
        bool additive = operation is ClusterOperation.ScalePool or ClusterOperation.AddPool;

        if (observation.Health == ClusterHealth.Degraded && !additive)
        {
            return OperationVerdict.No(
                $"The cluster is degraded ({observation.Summary}). Fix that before replacing machines — "
                + "a rollout on top of a fault usually turns one broken node into several.");
        }

        if (observation.Health == ClusterHealth.Progressing && !additive)
        {
            return OperationVerdict.No(
                $"Something is already in flight ({observation.Summary}). Wait for it to settle — two "
                + "rollouts at once replace more machines than either intended.");
        }

        return OperationVerdict.Ok;
    }

    /// <summary>
    /// Whether a Kubernetes version move is one kubeadm will accept.
    ///
    /// <para>Upstream allows one minor at a time, forwards only. Two at once is refused by kubeadm
    /// itself, but late — after the first control-plane machine has already been replaced, which
    /// leaves a cluster mid-upgrade between two versions it was never meant to sit between.</para>
    /// </summary>
    public static OperationVerdict CanUpgrade(string currentVersion, string targetVersion)
    {
        if (!TryParse(currentVersion, out int currentMajor, out int currentMinor, out int currentPatch)
            || !TryParse(targetVersion, out int targetMajor, out int targetMinor, out int targetPatch))
        {
            return OperationVerdict.No($"'{currentVersion}' or '{targetVersion}' is not a Kubernetes version.");
        }

        if (targetMajor == currentMajor && targetMinor == currentMinor && targetPatch == currentPatch)
        {
            return OperationVerdict.No($"The cluster is already on {MachineImageNaming.Normalize(targetVersion)}.");
        }

        if (targetMajor < currentMajor || (targetMajor == currentMajor && targetMinor < currentMinor))
        {
            return OperationVerdict.No(
                "Kubernetes does not support downgrades. Restore from a backup instead of moving a "
                + "running cluster backwards.");
        }

        if (targetMajor != currentMajor)
        {
            return OperationVerdict.No("A major version change is not something to do in one step.");
        }

        if (targetMinor - currentMinor > 1)
        {
            return OperationVerdict.No(
                $"That is {targetMinor - currentMinor} minor versions at once. Kubernetes supports one at a "
                + $"time: go to v{currentMajor}.{currentMinor + 1} first.");
        }

        return OperationVerdict.Ok;
    }

    /// <summary>
    /// Whether a pool may move to this version given where the control plane is.
    ///
    /// <para>Workers may lag the control plane by one minor and must never lead it — kubelet is not
    /// supported ahead of the API server, and a pool upgraded first talks to a control plane that
    /// does not know its API yet.</para>
    /// </summary>
    public static OperationVerdict CanUpgradePool(string controlPlaneVersion, string poolCurrent, string poolTarget)
    {
        OperationVerdict step = CanUpgrade(poolCurrent, poolTarget);
        if (!step.Allowed)
        {
            return step;
        }

        if (!TryParse(controlPlaneVersion, out int cpMajor, out int cpMinor, out _)
            || !TryParse(poolTarget, out int targetMajor, out int targetMinor, out _))
        {
            return OperationVerdict.No("The control plane's version could not be read.");
        }

        if (targetMajor > cpMajor || (targetMajor == cpMajor && targetMinor > cpMinor))
        {
            return OperationVerdict.No(
                $"Workers cannot run ahead of the control plane, which is on "
                + $"v{cpMajor}.{cpMinor}. Upgrade the control plane first.");
        }

        return OperationVerdict.Ok;
    }

    /// <summary>The order pools should be upgraded in: the furthest behind first.</summary>
    public static IReadOnlyList<PoolObservation> UpgradeOrder(IEnumerable<PoolObservation> pools) =>
        pools.OrderBy(p => p.Version ?? "", StringComparer.Ordinal).ToList();

    private static bool TryParse(string version, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        string[] parts = MachineImageNaming.Normalize(version).TrimStart('v').Split('.');

        return parts.Length >= 3
            && int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor)
            && int.TryParse(parts[2], out patch);
    }
}
