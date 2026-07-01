using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages the lifecycle of app deployments — creating, configuring, and tracking
/// deployments that target Kubernetes clusters. Supports three deployment types:
///
/// - Manual: structured form entry → generated YAML manifests
/// - Yaml: raw K8s YAML documents pasted/uploaded by the user
/// - HelmChart: any Helm chart with dynamic values
///
/// Also manages the ArgoCD-style resource tree that tracks live cluster state
/// for each deployment (sync status, health status, resource hierarchy).
/// </summary>
public class DeploymentService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuditService auditService,
    DeploymentStatusNotifier statusNotifier,
    ILogger<DeploymentService> logger)
{
    // ══════════════════════════════════════════════════════════════
    //  Deployment CRUD
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new deployment for an app. The deployment starts in Unknown
    /// sync/health status until resources are actually applied to the cluster.
    /// For Helm deployments, pass the chart details; for Manual/Yaml, leave them null.
    /// </summary>
    private async Task EnforceNamespaceAsync(
        Guid appId, Guid environmentId, string ns, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == environmentId, ct);
        if (ae?.Namespace is { Length: > 0 } locked
            && !string.Equals(ns.Trim(), locked, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Namespace '{ns}' is not allowed. Governance policy locks this environment to namespace '{locked}'.");
    }

    public async Task<AppDeployment> CreateDeploymentAsync(
        Guid appId,
        string name,
        DeploymentType type,
        Guid environmentId,
        Guid clusterId,
        string ns,
        string? helmRepoUrl = null,
        string? helmChartName = null,
        string? helmChartVersion = null,
        string? performedBy = null,
        CancellationToken ct = default,
        string? gitUrl = null,
        string? gitPath = null,
        string? gitRevision = null,
        bool gitAutoSync = true,
        bool isManaged = true)
    {
        await EnforceNamespaceAsync(appId, environmentId, ns, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Name = name,
            Type = type,
            EnvironmentId = environmentId,
            ClusterId = clusterId,
            Namespace = ns,
            HelmRepoUrl = helmRepoUrl,
            HelmChartName = helmChartName,
            HelmChartVersion = helmChartVersion,
            GitUrl = gitUrl,
            GitPath = gitPath,
            GitRevision = gitRevision,
            GitAutoSync = gitAutoSync,
            IsManaged = isManaged
        };

        db.AppDeployments.Add(deployment);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deployment {Name} created in namespace {Namespace} by {User}",
            name, ns, performedBy ?? "system");

        await auditService.RecordAsync(deployment.Id, "CreateDeployment", "AppDeployment",
            name, $"type={type}", performedBy, ct);

        return deployment;
    }

    /// <summary>
    /// Returns all deployments for an app, including the related environment
    /// and cluster so the UI can show where each deployment targets.
    /// </summary>
    public async Task<List<AppDeployment>> GetDeploymentsAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.AppDeployments
            .Include(d => d.Environment)
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Updates the editable properties of a deployment. Type, environment, and cluster
    /// are structural and cannot change after creation.
    /// </summary>
    public async Task UpdateDeploymentAsync(
        Guid deploymentId,
        string name,
        string ns,
        string? helmRepoUrl = null,
        string? helmChartName = null,
        string? helmChartVersion = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments.FindAsync([deploymentId], ct);

        if (deployment is not null)
        {
            await EnforceNamespaceAsync(deployment.AppId, deployment.EnvironmentId, ns, ct);
            deployment.Name = name;
            deployment.Namespace = ns;
            deployment.HelmRepoUrl = helmRepoUrl;
            deployment.HelmChartName = helmChartName;
            deployment.HelmChartVersion = helmChartVersion;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Updates the git source settings for a deployment. Resets sync state so the
    /// next sync cycle picks up the new configuration from scratch.
    /// </summary>
    public async Task UpdateGitSettingsAsync(
        Guid deploymentId,
        string? gitUrl,
        string? gitPath,
        string? gitRevision,
        bool gitAutoSync,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments.FindAsync([deploymentId], ct);

        if (deployment is not null)
        {
            deployment.GitUrl = string.IsNullOrWhiteSpace(gitUrl) ? null : gitUrl.Trim();
            deployment.GitPath = string.IsNullOrWhiteSpace(gitPath) ? null : gitPath.Trim();
            deployment.GitRevision = string.IsNullOrWhiteSpace(gitRevision) ? null : gitRevision.Trim();
            deployment.GitAutoSync = gitAutoSync;
            // Reset sync state so the next cycle re-syncs from new settings.
            deployment.GitLastSyncedCommit = null;
            deployment.GitLastSyncedAt = null;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Deletes a deployment and all its manifests and tracked resources.
    /// EF cascade delete handles the child entities.
    /// </summary>
    public async Task DeleteDeploymentAsync(Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments.FindAsync([deploymentId], ct);

        if (deployment is not null)
        {
            string name = deployment.Name;
            db.AppDeployments.Remove(deployment);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Deployment {Name} ({DeploymentId}) deleted by {User}",
                name, deploymentId, performedBy ?? "system");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Manifest CRUD
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds a YAML manifest to a deployment. Manifests are applied in SortOrder
    /// sequence — use lower numbers for foundational resources (PVCs, ConfigMaps)
    /// and higher numbers for workloads (Deployments, Services).
    /// </summary>
    public async Task<DeploymentManifest> AddManifestAsync(
        Guid deploymentId,
        string kind,
        string name,
        string yamlContent,
        int sortOrder,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DeploymentManifest manifest = new()
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Kind = kind,
            Name = name,
            YamlContent = yamlContent,
            SortOrder = sortOrder
        };

        db.DeploymentManifests.Add(manifest);
        await db.SaveChangesAsync(ct);
        return manifest;
    }

    /// <summary>
    /// Returns all manifests for a deployment, ordered by SortOrder so they
    /// can be applied to the cluster in the correct sequence.
    /// </summary>
    public async Task<List<DeploymentManifest>> GetManifestsAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Updates the YAML content of an existing manifest. Called when the user
    /// edits a manifest in the UI or re-imports updated YAML.
    /// </summary>
    public async Task UpdateManifestAsync(
        Guid manifestId, string yamlContent, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DeploymentManifest? manifest = await db.DeploymentManifests.FindAsync([manifestId], ct);

        if (manifest is not null)
        {
            manifest.YamlContent = yamlContent;
            manifest.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Removes a single manifest from a deployment.
    /// </summary>
    public async Task DeleteManifestAsync(Guid manifestId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DeploymentManifest? manifest = await db.DeploymentManifests.FindAsync([manifestId], ct);

        if (manifest is not null)
        {
            db.DeploymentManifests.Remove(manifest);
            await db.SaveChangesAsync(ct);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Helm Values
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Stores the Helm values YAML for a Helm chart deployment. These values
    /// override the chart's defaults when the Helm release is installed/upgraded.
    /// </summary>
    public async Task UpdateHelmValuesAsync(
        Guid deploymentId, string valuesYaml, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments.FindAsync([deploymentId], ct);

        if (deployment is not null)
        {
            deployment.HelmValues = valuesYaml;
            await db.SaveChangesAsync(ct);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Deployment Status
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Updates the overall sync and health status of a deployment. Called by
    /// the cluster watcher after reconciling live state against desired state.
    /// </summary>
    public async Task UpdateDeploymentStatusAsync(
        Guid deploymentId,
        SyncStatus syncStatus,
        HealthStatus healthStatus,
        string? message = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments.FindAsync([deploymentId], ct);

        if (deployment is not null)
        {
            deployment.SyncStatus = syncStatus;
            deployment.HealthStatus = healthStatus;
            deployment.StatusMessage = message;
            deployment.LastSyncedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            statusNotifier.Notify(deploymentId, syncStatus, healthStatus);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Resource Tree (ArgoCD-style)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates or updates a tracked resource in the cluster. Resources are
    /// identified by (deploymentId, group, version, kind, name, namespace).
    /// If a matching resource already exists, its status is updated in place.
    /// This is how the cluster watcher reports live resource state.
    /// </summary>
    public async Task<DeploymentResource> UpsertResourceAsync(
        Guid deploymentId,
        string group,
        string version,
        string kind,
        string name,
        string? ns,
        SyncStatus syncStatus,
        HealthStatus healthStatus,
        string? message,
        Guid? parentResourceId = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Look for an existing resource with the same identity.
        DeploymentResource? existing = await db.DeploymentResources
            .FirstOrDefaultAsync(r =>
                r.DeploymentId == deploymentId &&
                r.Group == group &&
                r.Version == version &&
                r.Kind == kind &&
                r.Name == name &&
                r.Namespace == ns, ct);

        if (existing is not null)
        {
            // Update the existing resource's status.
            existing.SyncStatus = syncStatus;
            existing.HealthStatus = healthStatus;
            existing.StatusMessage = message;
            existing.LastUpdatedAt = DateTime.UtcNow;

            if (parentResourceId is not null)
            {
                existing.ParentResourceId = parentResourceId;
            }

            await db.SaveChangesAsync(ct);
            return existing;
        }

        // Create a new tracked resource.
        DeploymentResource resource = new()
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Group = group,
            Version = version,
            Kind = kind,
            Name = name,
            Namespace = ns,
            SyncStatus = syncStatus,
            HealthStatus = healthStatus,
            StatusMessage = message,
            ParentResourceId = parentResourceId
        };

        db.DeploymentResources.Add(resource);
        await db.SaveChangesAsync(ct);
        return resource;
    }

    /// <summary>
    /// Returns the root-level resources for a deployment with their children
    /// eagerly loaded. This gives the UI the full resource tree for rendering
    /// the ArgoCD-style resource hierarchy.
    /// </summary>
    public async Task<List<DeploymentResource>> GetResourceTreeAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.DeploymentResources
            .Include(r => r.ChildResources)
            .Where(r => r.DeploymentId == deploymentId && r.ParentResourceId == null)
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
    }
}
