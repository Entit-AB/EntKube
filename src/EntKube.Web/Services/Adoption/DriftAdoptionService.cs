using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Adoption;

/// <summary>How one stored manifest compares to what is actually running.</summary>
public enum AdoptionStatus
{
    /// <summary>Live state differs — this is what adopting would change.</summary>
    Changed = 0,
    /// <summary>Live state cannot become desired state (a Secret).</summary>
    Refused = 1,
    /// <summary>The manifest defines something that no longer exists on the cluster.</summary>
    MissingFromCluster = 2,
    /// <summary>The live object could not be read.</summary>
    Unreadable = 3,
    /// <summary>Already matches; adopting it would be a no-op.</summary>
    Unchanged = 4,
}

/// <summary>One stored manifest, and what adopting live state would do to it.</summary>
public sealed record AdoptionEntry
{
    public required Guid ManifestId { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required AdoptionStatus Status { get; init; }

    /// <summary>What would be stored. Null unless <see cref="Status"/> is Changed.</summary>
    public string? ProposedYaml { get; init; }

    /// <summary>What is stored today, so the operator can compare before replacing it.</summary>
    public string? CurrentYaml { get; init; }

    /// <summary>What was stripped, or why this cannot be adopted.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Only a Changed entry is worth selecting.</summary>
    public bool IsSelectable => Status == AdoptionStatus.Changed;
}

/// <summary>A per-resource proposal for replacing stored manifests with live state.</summary>
public sealed record AdoptionProposal
{
    public required Guid DeploymentId { get; init; }
    public required IReadOnlyList<AdoptionEntry> Entries { get; init; }
    public string? Error { get; init; }

    public int ChangedCount => Entries.Count(e => e.Status == AdoptionStatus.Changed);
    public int RefusedCount => Entries.Count(e => e.Status == AdoptionStatus.Refused);
}

/// <summary>
/// The other answer to drift: when the change made on the cluster was the right one,
/// pull it back into the stored manifests so the next sync stops reverting it.
///
/// Adoption is done <b>per resource</b>, never by replacing the whole manifest set with a
/// dump of live state. Wholesale replacement quietly loses anything that cannot be
/// adopted — a Secret above all — and because EntKube prunes resources that disappear
/// from the manifest set, "lost from the manifests" becomes "deleted from the cluster"
/// on the next apply. Matching each stored manifest to its live object by kind and name
/// keeps everything else exactly as it was.
///
/// Nothing is written until an operator picks entries and confirms. This rewrites the
/// deployment's desired state, and there is no manifest history to fall back on.
/// </summary>
public class DriftAdoptionService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    ILogger<DriftAdoptionService> logger)
{
    /// <summary>
    /// Builds the proposal. Read-only — it fetches live objects and compares, and changes
    /// neither the cluster nor the stored manifests.
    /// </summary>
    public async Task<AdoptionProposal> BuildProposalAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var deployment = await db.AppDeployments
            .AsNoTracking()
            .Where(d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId)
            .Select(d => new { d.Id, d.Namespace, Kubeconfig = d.Cluster.Kubeconfig })
            .FirstOrDefaultAsync(ct);

        if (deployment is null)
        {
            return Failed(deploymentId, "Deployment not found in this tenant.");
        }

        if (string.IsNullOrWhiteSpace(deployment.Kubeconfig))
        {
            return Failed(deploymentId, "The cluster has no kubeconfig configured.");
        }

        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .AsNoTracking()
            .Where(m => m.DeploymentId == deploymentId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        if (manifests.Count == 0)
        {
            return Failed(deploymentId, "This deployment has no stored manifests to adopt into.");
        }

        List<AdoptionEntry> entries = [];

        foreach (DeploymentManifest manifest in manifests)
        {
            entries.Add(await BuildEntryAsync(manifest, deployment.Namespace, deployment.Kubeconfig, ct));
        }

        return new AdoptionProposal { DeploymentId = deploymentId, Entries = entries };
    }

    private async Task<AdoptionEntry> BuildEntryAsync(
        DeploymentManifest manifest, string ns, string kubeconfig, CancellationToken ct)
    {
        AdoptionEntry Base(AdoptionStatus status, params string[] notes) => new()
        {
            ManifestId = manifest.Id,
            Kind = manifest.Kind,
            Name = manifest.Name,
            Status = status,
            CurrentYaml = manifest.YamlContent,
            Notes = notes,
        };

        string liveJson;
        try
        {
            // kubectl resolves "kind/name" against the cluster's own API discovery, so an
            // abbreviation or a custom resource works without EntKube knowing its group.
            liveJson = await k8s.GetJsonAsync(
                $"{manifest.Kind.ToLowerInvariant()}/{manifest.Name}", ns, kubeconfig, "", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read {Kind}/{Name} for adoption", manifest.Kind, manifest.Name);
            return Base(AdoptionStatus.Unreadable, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(liveJson))
        {
            // The manifest describes something that is not there. Adopting nothing would
            // be adopting a deletion, which is not what this action is for.
            return Base(AdoptionStatus.MissingFromCluster,
                "This resource is not on the cluster. Adopting cannot represent that — "
                + "remove it from the manifests instead if it is genuinely gone.");
        }

        SanitisedResource sanitised = LiveStateSanitiser.Sanitise(liveJson);

        if (!sanitised.IsAdoptable)
        {
            return Base(AdoptionStatus.Refused, sanitised.Refusal ?? "Cannot be adopted.");
        }

        // Compare on normalised text so indentation or key order alone does not read as a
        // change — an entry offered as "changed" that turns out identical trains people to
        // click through the list without looking.
        bool differs = !string.Equals(
            Normalise(sanitised.Yaml!), Normalise(manifest.YamlContent), StringComparison.Ordinal);

        return new AdoptionEntry
        {
            ManifestId = manifest.Id,
            Kind = manifest.Kind,
            Name = manifest.Name,
            Status = differs ? AdoptionStatus.Changed : AdoptionStatus.Unchanged,
            ProposedYaml = differs ? sanitised.Yaml : null,
            CurrentYaml = manifest.YamlContent,
            Notes = sanitised.Notes,
        };
    }

    /// <summary>
    /// Replaces the selected manifests with the proposed live state.
    ///
    /// The proposal is rebuilt here rather than trusting YAML posted back from the browser:
    /// this writes the deployment's desired state, and accepting arbitrary content from the
    /// client would make that an open door. It also means a resource that changed again
    /// between preview and confirm is adopted as it is now, not as it was.
    /// </summary>
    public async Task<(int Adopted, string Message)> AdoptAsync(
        Guid tenantId, Guid deploymentId, IReadOnlyCollection<Guid> manifestIds,
        string? performedBy, CancellationToken ct = default)
    {
        if (manifestIds.Count == 0)
        {
            return (0, "Nothing was selected.");
        }

        AdoptionProposal proposal = await BuildProposalAsync(tenantId, deploymentId, ct);
        if (proposal.Error is not null)
        {
            return (0, proposal.Error);
        }

        Dictionary<Guid, AdoptionEntry> selectable = proposal.Entries
            .Where(e => e.IsSelectable && manifestIds.Contains(e.ManifestId))
            .ToDictionary(e => e.ManifestId);

        if (selectable.Count == 0)
        {
            return (0, "Nothing left to adopt — the selected resources already match their manifests.");
        }

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<DeploymentManifest> rows = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId && selectable.Keys.Contains(m.Id))
            .ToListAsync(ct);

        foreach (DeploymentManifest row in rows)
        {
            row.YamlContent = selectable[row.Id].ProposedYaml!;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Adopted live state into {Count} manifest(s) of deployment {DeploymentId} for {User}",
            rows.Count, deploymentId, performedBy ?? "unknown");

        return (rows.Count,
            $"Adopted live state into {rows.Count} manifest{(rows.Count == 1 ? "" : "s")}. "
            + "The deployment now shows as in sync; no cluster change was made.");
    }

    /// <summary>Trims trailing whitespace and blank lines so formatting alone is not a difference.</summary>
    public static string Normalise(string? yaml) =>
        string.Join('\n', (yaml ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .SkipWhile(l => l == "---"));

    private static AdoptionProposal Failed(Guid deploymentId, string error) =>
        new() { DeploymentId = deploymentId, Entries = [], Error = error };
}
