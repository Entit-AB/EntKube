using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
    IDbContextFactory<DeliveryDbContext> dbFactory,
    AuditService auditService,
    DeploymentStatusNotifier statusNotifier,
    ILogger<DeploymentService> logger)
{
    /// <summary>
    /// Whether a deployment is this tenant's. <c>AppDeployment</c> has no <c>TenantId</c>, so the
    /// scope travels through its app's customer — the same path every read below takes inline.
    /// </summary>
    private async Task<bool> DeploymentBelongsToTenantAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.AppDeployments.AnyAsync(
            d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);
    }

    /// <summary>
    /// Whether the app, the environment and the cluster a new deployment names are all this
    /// tenant's. Three, not two: the cluster is where the manifests are actually applied, so a
    /// deployment pointed at somebody else's cluster is the crossing that reaches a machine.
    /// </summary>
    private async Task<bool> TargetBelongsToTenantAsync(
        Guid tenantId, Guid appId, Guid environmentId, Guid clusterId, CancellationToken ct)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.Apps.AnyAsync(a => a.Id == appId && a.Customer.TenantId == tenantId, ct)
            && await db.Environments.AnyAsync(e => e.Id == environmentId && e.TenantId == tenantId, ct)
            && await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
    }

    // ══════════════════════════════════════════════════════════════
    //  Deployment CRUD
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new deployment for an app. The deployment starts in Unknown
    /// sync/health status until resources are actually applied to the cluster.
    /// For Helm deployments, pass the chart details; for Manual/Yaml, leave them null.
    /// </summary>
    /// <summary>
    /// Checks a namespace before it is written to a deployment: it must be present, and it must
    /// match the environment's governance lock when one is set.
    ///
    /// The emptiness check matters beyond the failed install it prevents — a stored blank
    /// namespace also trips the edit form's own required-field rule, so the deployment becomes
    /// uneditable and every later change (chart version included) is refused.
    /// </summary>
    private async Task EnforceNamespaceAsync(
        Guid appId, Guid environmentId, string ns, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ns))
            throw new InvalidOperationException("A namespace is required.");

        using DeliveryDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == environmentId, ct);
        if (ae?.Namespace is { Length: > 0 } locked
            && !string.Equals(ns.Trim(), locked, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Namespace '{ns}' is not allowed. Governance policy locks this environment to namespace '{locked}'.");
    }

    /// <param name="tenantId">
    /// Whose app, environment and cluster. A deployment is the record the sync loop acts on, so
    /// creating one against another tenant's app is how work gets applied to their cluster under
    /// our name.
    /// </param>
    public async Task<AppDeployment> CreateDeploymentAsync(
        Guid tenantId,
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
        if (!await TargetBelongsToTenantAsync(tenantId, appId, environmentId, clusterId, ct))
        {
            throw new InvalidOperationException(
                "The app, the environment or the cluster does not belong to this tenant.");
        }

        await EnforceNamespaceAsync(appId, environmentId, ns, ct);

        using DeliveryDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">
    /// Whose app. A deployment row carries the namespace, the cluster and the git source, so
    /// listing them for an arbitrary app id describes another tenant's estate — and
    /// <c>CustomerPortal</c> is one of the eleven callers.
    /// </param>
    public async Task<List<AppDeployment>> GetDeploymentsAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.AppDeployments
            .Include(d => d.Environment)
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.App.Customer.TenantId == tenantId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Updates the editable properties of a deployment. Type, environment, and cluster
    /// are structural and cannot change after creation.
    /// </summary>
    /// <param name="tenantId">
    /// Whose deployment. The namespace is editable here, and it decides where the next apply lands.
    /// </param>
    public async Task UpdateDeploymentAsync(
        Guid tenantId,
        Guid deploymentId,
        string name,
        string ns,
        string? helmRepoUrl = null,
        string? helmChartName = null,
        string? helmChartVersion = null,
        CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments
            .FirstOrDefaultAsync(
                d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose deployment. The git URL is what the sync loop pulls from, so an edit here redirects
    /// another tenant's workload at a repository of the editor's choosing.
    /// </param>
    public async Task UpdateGitSettingsAsync(
        Guid tenantId,
        Guid deploymentId,
        string? gitUrl,
        string? gitPath,
        string? gitRevision,
        bool gitAutoSync,
        CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments
            .FirstOrDefaultAsync(
                d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">Whose deployment. The cascade takes its manifests and resources too.</param>
    public async Task DeleteDeploymentAsync(
        Guid tenantId, Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments
            .FirstOrDefaultAsync(
                d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose deployment. A manifest is raw YAML that will be applied to the deployment's cluster,
    /// so adding one to a stranger's deployment is arbitrary cluster write access.
    /// </param>
    public async Task<DeploymentManifest> AddManifestAsync(
        Guid tenantId,
        Guid deploymentId,
        string kind,
        string name,
        string yamlContent,
        int sortOrder,
        CancellationToken ct = default)
    {
        if (!await DeploymentBelongsToTenantAsync(tenantId, deploymentId, ct))
        {
            throw new InvalidOperationException("The deployment does not belong to this tenant.");
        }

        using DeliveryDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">Whose deployment. A manifest is the YAML of a running workload.</param>
    public async Task<List<DeploymentManifest>> GetManifestsAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.DeploymentManifests
            // DeploymentManifest has no TenantId; the scope runs manifest → deployment → app.
            .Where(m => m.DeploymentId == deploymentId
                     && m.Deployment.App.Customer.TenantId == tenantId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Updates the YAML content of an existing manifest. Called when the user
    /// edits a manifest in the UI or re-imports updated YAML.
    /// </summary>
    /// <param name="tenantId">
    /// Whose manifest. Editing one rewrites what the next sync applies, so this is the write that
    /// turns a stranger's deployment into whatever the editor pasted.
    /// </param>
    public async Task UpdateManifestAsync(
        Guid tenantId, Guid manifestId, string yamlContent, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        DeploymentManifest? manifest = await db.DeploymentManifests
            .FirstOrDefaultAsync(
                m => m.Id == manifestId
                  && m.Deployment.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">Whose manifest.</param>
    public async Task DeleteManifestAsync(
        Guid tenantId, Guid manifestId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        DeploymentManifest? manifest = await db.DeploymentManifests
            .FirstOrDefaultAsync(
                m => m.Id == manifestId
                  && m.Deployment.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose deployment. Helm values are arbitrary chart input — image, replicas, mounted
    /// secrets — so writing them into a stranger's release is remote control of their workload.
    /// </param>
    public async Task UpdateHelmValuesAsync(
        Guid tenantId, Guid deploymentId, string valuesYaml, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments
            .FirstOrDefaultAsync(
                d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose deployment. The caller is the sync sweep, which already walks one tenant at a time
    /// and holds the tenant beside each deployment id — it simply never passed it.
    /// </param>
    public async Task UpdateDeploymentStatusAsync(
        Guid tenantId,
        Guid deploymentId,
        SyncStatus syncStatus,
        HealthStatus healthStatus,
        string? message = null,
        CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        AppDeployment? deployment = await db.AppDeployments
            .FirstOrDefaultAsync(
                d => d.Id == deploymentId && d.App.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose deployment. This is the watcher's write — the resource tree is what the UI shows as
    /// live cluster state, so an unscoped upsert lets one tenant's reconciler post rows under
    /// another's deployment and misreport their health.
    /// </param>
    public async Task<DeploymentResource> UpsertResourceAsync(
        Guid tenantId,
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
        if (!await DeploymentBelongsToTenantAsync(tenantId, deploymentId, ct))
        {
            throw new InvalidOperationException("The deployment does not belong to this tenant.");
        }

        using DeliveryDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">
    /// Whose deployment. The tree names every live object and namespace the deployment owns.
    /// </param>
    public async Task<List<DeploymentResource>> GetResourceTreeAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default)
    {
        using DeliveryDbContext db = dbFactory.CreateDbContext();

        return await db.DeploymentResources
            .Include(r => r.ChildResources)
            // DeploymentResource has no TenantId; the scope runs resource → deployment → app.
            .Where(r => r.DeploymentId == deploymentId && r.ParentResourceId == null
                     && r.Deployment.App.Customer.TenantId == tenantId)
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
    }
}
