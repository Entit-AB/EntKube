using EntKube.Web.Services;

namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// Writes gate outcomes to the existing audit trail.
///
/// <para>Reuses <see cref="AuditEvent"/> rather than adding a table, which is why this needs no
/// migration on any of the three providers.</para>
///
/// <para><b>The cost, stated rather than hidden: these rows cannot be grouped by cluster.</b>
/// <see cref="PlannedClusterChange"/> carries no cluster id — only a <c>ClusterLabel</c>, and that
/// is a non-nullable string whose default is the literal <c>"cluster"</c>. So a change raised by a
/// path that never set it records as <c>cluster=cluster</c>, which is indistinguishable from a
/// cluster someone actually named that. The label is written verbatim here instead of being
/// second-guessed, because inventing a placeholder for a placeholder would only move the
/// ambiguity.</para>
///
/// <para>That missing identity is the same one that stops the gate moving into
/// <c>IClusterClient</c> (docs/decomposition.md §4.0.1): none of the eighteen factory methods
/// receives a cluster to put in the model. Giving <c>PlannedClusterChange</c> a real
/// <c>ClusterId</c> fixes both, and belongs with that work rather than ahead of it.</para>
/// </summary>
public sealed class AuditClusterChangeRecorder(
    AuditService audit, ILogger<AuditClusterChangeRecorder> logger) : IClusterChangeRecorder
{
    public async Task RecordAsync(
        PlannedClusterChange change, ClusterChangeOutcome outcome, ClusterChangeDiff? diff,
        CancellationToken ct = default)
    {
        try
        {
            await audit.RecordAsync(
                deploymentId: null,
                action: $"ClusterChange{outcome}",
                resourceKind: change.Kind ?? change.Resource ?? change.Verb.ToString(),
                resourceName: change.Name,
                details: Describe(change, diff),
                // Left null on purpose, including for Acknowledged: the gate is handed a change
                // and a sink, never an identity. Recording a guess here would be worse than
                // recording nothing, because it would look like evidence of who approved it.
                performedBy: null,
                ct: ct);
        }
        catch (Exception ex)
        {
            // A change an operator already approved must not fail because the record did.
            logger.LogError(ex, "Could not record cluster change outcome {Outcome} for {Change}",
                outcome, change.Describe());
        }
    }

    /// <summary>
    /// What happened and where — never the manifest or the diff body, which routinely carry
    /// Secret values. The diff is reduced to its size, which is enough to tell a one-field patch
    /// from a wholesale replacement.
    /// </summary>
    private static string Describe(PlannedClusterChange change, ClusterChangeDiff? diff)
    {
        List<string> parts =
        [
            $"verb={change.Verb}",
            $"cluster={change.ClusterLabel}",
        ];

        if (!string.IsNullOrWhiteSpace(change.Namespace)) parts.Add($"namespace={change.Namespace}");

        parts.Add($"summary={change.Describe()}");

        if (diff is not null)
        {
            parts.Add($"diffLines={diff.DiffText?.Count(c => c == '\n') ?? 0}");
            if (diff.Warning is not null) parts.Add($"warning={diff.Warning}");
        }

        return string.Join("; ", parts);
    }
}
