namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// The kind of mutating operation about to be performed against a cluster.
/// Used for display and to decide how the diff/preview is computed.
/// </summary>
public enum ChangeVerb
{
    Apply,
    Delete,
    Patch,
    Scale,
    Restart,
    Helm,
    EnsureNamespace,
}

/// <summary>
/// A single mutating operation that is about to be carried out against a Kubernetes
/// cluster. Constructed at the mutation choke-point and handed to
/// <see cref="IClusterChangeGate.AcknowledgeAsync"/> BEFORE the write happens.
///
/// For <see cref="ChangeVerb.Apply"/> the <see cref="Manifest"/> carries the (possibly
/// multi-document) YAML. For <see cref="ChangeVerb.Delete"/> the target is identified by
/// <see cref="Kind"/>/<see cref="Name"/>/<see cref="Namespace"/>. For patches/scales the
/// <see cref="Patch"/> holds the JSON that will be sent.
/// </summary>
public sealed class PlannedClusterChange
{
    public required ChangeVerb Verb { get; init; }

    /// <summary>Kubeconfig used both to compute the dry-run diff and to carry out the change.</summary>
    public required string Kubeconfig { get; init; }

    /// <summary>Human-readable cluster label shown in the acknowledgment dialog (e.g. "prod-eu").</summary>
    public string ClusterLabel { get; init; } = "cluster";

    /// <summary>Short human summary of the operation, e.g. "Scale Deployment/api" or "Apply routes".</summary>
    public string? Summary { get; init; }

    // Apply / EnsureNamespace
    public string? Manifest { get; init; }

    // Delete
    public string? Kind { get; init; }
    public string? Name { get; init; }
    public string? Namespace { get; init; }

    // Patch / Scale / Restart — the resource ref + JSON patch that will be sent.
    public string? Resource { get; init; }
    public string? Patch { get; init; }
    public bool StrategicPatch { get; init; }

    /// <summary>A concise one-line description used for logging and the dialog header.</summary>
    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Summary)) return Summary!;
        return Verb switch
        {
            ChangeVerb.Delete => $"Delete {Kind}/{Name} in {Namespace}",
            ChangeVerb.Patch or ChangeVerb.Scale or ChangeVerb.Restart
                => $"{Verb} {Resource}/{Name} in {Namespace}",
            ChangeVerb.EnsureNamespace => $"Ensure namespace {Namespace ?? Name}",
            _ => $"{Verb} manifest",
        };
    }
}

/// <summary>
/// The computed preview of what a <see cref="PlannedClusterChange"/> will do to the cluster.
/// Produced by the gate via a server-side dry-run before the operator is asked to acknowledge.
/// </summary>
public sealed class ClusterChangeDiff
{
    /// <summary>True when the dry-run reports the cluster state will actually change.</summary>
    public bool HasChanges { get; init; } = true;

    /// <summary>
    /// Unified/rendered diff text shown to the operator. May be empty when
    /// <see cref="HasChanges"/> is false.
    ///
    /// <para><b>Secret values are removed on the way in</b>, by
    /// <see cref="SecretRedaction.Scrub"/>. It is done here rather than at the call sites because
    /// the gate builds this text in nine places and six of them can carry a Secret — the
    /// <c>kubectl diff</c> output, a server-side dry-run rendering, the raw manifest shown when the
    /// dry-run itself fails, a deleted resource read back with <c>get -o yaml</c>, a JSON patch
    /// body, and the catch-all that shows the manifest when no diff could be computed. Scrubbing
    /// six call sites would leave the seventh to whoever adds it; a property cannot be
    /// bypassed.</para>
    ///
    /// <para>What survives is the keys and a fingerprint per value, which is what the decision
    /// needs: whether this adds, changes or removes a key, and where. See
    /// <see cref="SecretRedaction"/> for why the default is to redact rather than to detect.</para>
    /// </summary>
    public string DiffText
    {
        get => _diffText;
        init => _diffText = SecretRedaction.Scrub(value) ?? "";
    }

    private readonly string _diffText = "";

    /// <summary>
    /// Set when the diff could not be computed (e.g. kubectl unavailable, cluster unreachable).
    /// The gate fails safe: it still asks for acknowledgment, showing the raw manifest + this warning.
    /// </summary>
    public string? Warning { get; init; }

    public static ClusterChangeDiff NoChange() => new() { HasChanges = false, DiffText = "" };
}

/// <summary>Operator's decision on a planned change.</summary>
public enum ClusterChangeDecision
{
    Acknowledged,
    Cancelled,
}
