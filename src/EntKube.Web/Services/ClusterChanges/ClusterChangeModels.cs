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

    /// <summary>
    /// Restoring workloads to the revision Kubernetes last recorded for them
    /// (<c>kubectl rollout undo</c>). Not a Patch: nothing here describes the state being
    /// moved to, because the cluster — not EntKube — is the one that remembers it.
    /// </summary>
    Rollback,

    /// <summary>
    /// Cordoning a node and evicting everything evictable on it (<c>kubectl drain</c>).
    ///
    /// <para>⚠️ Deliberately <b>not</b> <see cref="Delete"/>, which it was first written as. A
    /// drain does not delete the Node object — so the gate's delete preview described the wrong
    /// thing entirely ("about to delete this node"), and when the object could not be read it
    /// concluded <em>no change</em> and applied the drain <b>without asking anyone</b>. A drain
    /// has no dry run: what it is about to evict depends on budgets and on what is running at
    /// that moment, so the honest preview is its summary, which is what the fall-through in
    /// <c>ClusterChangeGate.ComputeDiffAsync</c> gives it.</para>
    /// </summary>
    Drain,

    /// <summary>
    /// Calling an action endpoint on an object — a subresource such as KubeVirt's
    /// <c>virtualmachineinstances/{name}/pause</c>. Nothing is written to the object, so there is
    /// nothing to diff and nothing to read back. The honest preview is the summary, which is what the
    /// fall-through in <c>ClusterChangeGate.ComputeDiffAsync</c> gives it. That fall-through also
    /// reports a change, so an invocation is never let through as "no change".
    /// </summary>
    Invoke,
}

/// <summary>
/// A planned change as a <em>caller</em> can describe it: everything about the operation and
/// nothing about the credential.
///
/// <para><b>Why this exists.</b> <see cref="PlannedClusterChange.Kubeconfig"/> is
/// <c>required</c>, which is what guarantees the gate can always compute a dry-run preview — and
/// it also meant that every caller raising an acknowledgment had to be holding a kubeconfig.
/// Callers that mutate through the k8s SDK rather than kubectl cannot get the gate raised for
/// them from inside the factory, so for them that requirement was the whole reason they still
/// read the credential off a cluster row.</para>
///
/// <para>So a caller fills this in and hands it to <c>IClusterClient.AcknowledgeAsync</c>; the
/// seam supplies the credential and the cluster's label. The gate's invariant is untouched —
/// <see cref="PlannedClusterChange"/> is still only ever constructed with one.</para>
/// </summary>
public sealed class PlannedChange
{
    public required ChangeVerb Verb { get; init; }

    /// <summary>Short human summary, e.g. "Scale Deployment/api to 3".</summary>
    public string? Summary { get; init; }

    public string? Manifest { get; init; }

    public string? Kind { get; init; }
    public string? Name { get; init; }
    public string? Namespace { get; init; }

    public string? Resource { get; init; }
    public string? Patch { get; init; }
    public bool StrategicPatch { get; init; }

    /// <summary>
    /// Completes this into what the gate takes. Called by the seam, which is the only thing that
    /// has both halves; a caller cannot reach a credential through it because it has to supply
    /// one to get anything back.
    /// </summary>
    public PlannedClusterChange WithCredential(string kubeconfig, string clusterLabel) => new()
    {
        Verb = Verb,
        Kubeconfig = kubeconfig,
        ClusterLabel = string.IsNullOrWhiteSpace(clusterLabel) ? "cluster" : clusterLabel,
        Summary = Summary,
        Manifest = Manifest,
        Kind = Kind,
        Name = Name,
        Namespace = Namespace,
        Resource = Resource,
        Patch = Patch,
        StrategicPatch = StrategicPatch,
    };
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
            ChangeVerb.Rollback => $"Roll back {Resource ?? "workloads"} in {Namespace}",
            ChangeVerb.Drain => $"Drain {Kind ?? "Node"}/{Name}",
            ChangeVerb.Invoke => $"Invoke {Resource} on {Name} in {Namespace}",
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
