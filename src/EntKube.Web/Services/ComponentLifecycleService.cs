using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BCrypt.Net;
using EntKube.Web.Data;
using EntKube.Web.Services.Telemetry;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Input model for registering a new component on a cluster.
/// Captures all the Helm chart details needed for lifecycle management.
/// </summary>
public class ComponentRegistration
{
    public required string Name { get; set; }
    public required string ComponentType { get; set; }
    public string? Namespace { get; set; }
    public string? HelmRepoUrl { get; set; }
    public string? HelmChartName { get; set; }
    public string? HelmChartVersion { get; set; }
    public string? ReleaseName { get; set; }
    public string? HelmValues { get; set; }
    public string? Configuration { get; set; }
}

/// <summary>
/// Describes a Helm CLI command that can be executed against a cluster.
/// Built by the lifecycle service and executed by the UI or a background worker.
/// This separation keeps the data layer testable without needing the helm binary.
/// </summary>
public class HelmCommand
{
    public required string Operation { get; set; }
    public required string ReleaseName { get; set; }
    public string? ChartReference { get; set; }
    public string? Namespace { get; set; }
    public string? RepoUrl { get; set; }
    public string? Version { get; set; }
    public bool HasValues { get; set; }
    public string? ValuesYaml { get; set; }
    /// <summary>For kubectl-apply-url: the remote manifest URL passed directly to kubectl apply -f.</summary>
    public string? ManifestUrl { get; set; }
    /// <summary>When true, skips --wait so Helm returns immediately after applying values.</summary>
    public bool NoWait { get; set; }
    /// <summary>Helm --wait timeout (Go duration, e.g. "10m0s"). Heavier components can extend it.</summary>
    public string Timeout { get; set; } = "10m0s";
}

/// <summary>
/// Manages the full lifecycle of cluster components — registration, configuration,
/// install preparation, result tracking, and uninstall. The service handles the
/// data/state side of lifecycle management; actual Helm CLI execution is delegated
/// to the caller (UI or background worker) using the HelmCommand objects.
///
/// Lifecycle flow:
/// 1. RegisterComponentAsync    → creates component with NotInstalled status
/// 2. UpdateConfigurationAsync  → sets/updates Helm values, version, etc.
/// 3. PrepareInstallAsync       → validates and transitions to Installing
/// 4. ExecuteHelmAsync          → runs the actual helm command against the cluster
/// 5. MarkInstallResultAsync    → records success (Installed) or failure (Failed)
/// 6. PrepareUninstallAsync     → transitions to Uninstalling
/// 7. ExecuteHelmAsync          → runs helm uninstall
/// 8. MarkUninstallResultAsync  → removes or resets the component
/// </summary>
public class ComponentLifecycleService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    KeycloakService keycloakService,
    IngestTokenService ingestTokens,
    EntKubeTelemetryService entKubeTelemetry,
    IConfiguration configuration,
    ILogger<ComponentLifecycleService> logger)
{
    /// <summary>
    /// OCI registries this process has already logged helm in to. The login persists in helm's config for
    /// the life of the container, so it is worth doing once rather than per install.
    ///
    /// Concurrent, not a plain HashSet: two components can install at the same moment, and a set mutated
    /// from both would corrupt rather than merely duplicate work.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LoggedInRegistries = new();

    /// <summary>
    /// Registers a new component on a cluster. The component starts in NotInstalled
    /// status — it's just a record of what should be deployed, not yet deployed.
    /// Think of this as adding a line item to a deployment plan.
    /// </summary>
    public async Task<ClusterComponent> RegisterComponentAsync(
        Guid clusterId, ComponentRegistration registration, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Check that no component with this name already exists on the cluster.

        bool exists = await db.ClusterComponents
            .AnyAsync(c => c.ClusterId == clusterId && c.Name == registration.Name, ct);

        if (exists)
        {
            throw new InvalidOperationException(
                $"A component named '{registration.Name}' already exists on this cluster.");
        }

        // Create the component with all the Helm details filled in.
        // ReleaseName defaults to the component name if not specified — this is
        // the name Helm will use for the release on the cluster.

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = registration.Name,
            ComponentType = registration.ComponentType,
            Namespace = registration.Namespace,
            HelmRepoUrl = registration.HelmRepoUrl,
            HelmChartName = registration.HelmChartName,
            HelmChartVersion = registration.HelmChartVersion,
            ReleaseName = registration.ReleaseName ?? registration.Name,
            HelmValues = registration.HelmValues,
            Configuration = registration.Configuration,
            Status = ComponentStatus.NotInstalled
        };

        db.ClusterComponents.Add(component);
        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Updates the configuration of an existing component. This can be done
    /// before initial install or to prepare an upgrade of an already-installed
    /// component. Changes to values or version take effect on the next install/upgrade.
    /// </summary>
    public async Task<ClusterComponent> UpdateConfigurationAsync(
        Guid componentId, string? helmValues, string? chartVersion = null,
        string? helmRepoUrl = null, string? configuration = null,
        string? componentNamespace = null, string? releaseName = null,
        string? chartName = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Update only the fields that were provided.

        if (helmValues is not null)
        {
            component.HelmValues = helmValues;
        }

        if (chartVersion is not null)
        {
            component.HelmChartVersion = chartVersion;
        }

        if (helmRepoUrl is not null)
        {
            component.HelmRepoUrl = helmRepoUrl;
        }

        // Swapping the chart of an installed release is a normal helm upgrade — unlike the
        // namespace/name below, it does not change which release is being managed.
        if (chartName is not null)
        {
            component.HelmChartName = chartName;
        }

        if (configuration is not null)
        {
            component.Configuration = configuration;
        }

        // Namespace + release name are the release's identity to Helm. Repointing them on an
        // installed component would not move anything: the next apply would install a *second*
        // release under the new identity and leave the original running, unreferenced and no
        // longer manageable from here. Uninstalling first is the only safe order.
        if (componentNamespace is not null && !string.Equals(componentNamespace, component.Namespace, StringComparison.Ordinal))
        {
            RequireNotInstalled(component, "namespace");
            component.Namespace = componentNamespace;
        }

        if (releaseName is not null && !string.Equals(releaseName, component.ReleaseName, StringComparison.Ordinal))
        {
            RequireNotInstalled(component, "release name");
            component.ReleaseName = releaseName;
        }

        await db.SaveChangesAsync(ct);
        return component;
    }

    private static void RequireNotInstalled(ClusterComponent component, string field)
    {
        if (component.Status == ComponentStatus.Installed)
        {
            throw new InvalidOperationException(
                $"The {field} cannot be changed while '{component.Name}' is installed — Helm identifies a release " +
                $"by its namespace and name, so this would orphan the running release instead of moving it. " +
                $"Uninstall it first, then change the {field} and install again.");
        }
    }

    /// <summary>
    /// Validates that a component is ready to install and transitions it to
    /// Installing status. This is the gatekeeper — if the component doesn't
    /// have the minimum required info (chart name, namespace), we reject early
    /// rather than failing mid-install.
    /// </summary>
    public async Task<ClusterComponent> PrepareInstallAsync(
        Guid componentId, CancellationToken ct = default) =>
        await PrepareApplyAsync(componentId, allowInstalled: false, ct);

    /// <summary>
    /// Same gatekeeping as <see cref="PrepareInstallAsync"/> but for a release that is
    /// already installed: an in-place `helm upgrade` is exactly the same command, so the
    /// only difference is that Installed is an acceptable starting state.
    /// </summary>
    public async Task<ClusterComponent> PrepareUpgradeAsync(
        Guid componentId, CancellationToken ct = default) =>
        await PrepareApplyAsync(componentId, allowInstalled: true, ct);

    private async Task<ClusterComponent> PrepareApplyAsync(
        Guid componentId, bool allowInstalled, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Only NotInstalled or Failed components can be installed.
        // If it's already installed, the user should use upgrade instead.

        if (component.Status == ComponentStatus.Installed && !allowInstalled)
        {
            throw new InvalidOperationException(
                "Component is already installed. Use upgrade to reconfigure.");
        }

        if (component.Status is ComponentStatus.Installing or ComponentStatus.Uninstalling)
        {
            throw new InvalidOperationException(
                "Component has an operation in progress. Wait for it to complete.");
        }

        // Validate minimum required fields.
        // ManifestUrl uses HelmRepoUrl as the URL; Manifest uses HelmValues as raw YAML.
        // Only HelmChart type requires a chart name.

        if (component.ComponentType != "ManifestUrl" && component.ComponentType != "Manifest"
            && string.IsNullOrWhiteSpace(component.HelmChartName))
        {
            throw new InvalidOperationException(
                "Helm chart name is required. Configure the component before installing.");
        }

        // Transition to Installing — the caller should now execute the Helm command.

        component.Status = ComponentStatus.Installing;
        component.LastError = null;
        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Records the result of a Helm install/upgrade operation. Called by the
    /// UI or worker after executing the Helm command against the cluster.
    /// On success, marks as Installed with a timestamp. On failure, marks as
    /// Failed with the error message so the user can diagnose and retry.
    /// </summary>
    public async Task<ClusterComponent> MarkInstallResultAsync(
        Guid componentId, bool success, string? error = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        if (success)
        {
            component.Status = ComponentStatus.Installed;
            component.InstalledAt = DateTime.UtcNow;
            component.LastError = null;
        }
        else
        {
            component.Status = ComponentStatus.Failed;
            component.LastError = error;
        }

        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Checks if a successfully installed component has companion charts defined
    /// Checks if a component has any subchart toggle fields enabled (YamlPath
    /// starting with "subchart:"). For each enabled subchart, runs a helm
    /// upgrade --install using the same repo URL and namespace as the parent.
    /// The chart name is extracted from the YamlPath (e.g. "subchart:barman-cloud").
    ///
    /// For example: cloudnative-pg with "barman-cloud-plugin" toggle enabled will
    /// install the "barman-cloud" chart from the same CNPG charts repo.
    /// </summary>
    public async Task<HelmExecutionResult> InstallSubchartsAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Look up the catalog entry to find subchart toggle fields.

        CatalogEntry? entry = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

        if (entry is null)
        {
            return new HelmExecutionResult { Success = true, Output = "" };
        }

        // Parse the stored form field values from the component's HelmValues isn't
        // how toggles work — they're stored directly. We need to read them from
        // the catalog defaults + any override. For subchart toggles, the value is
        // stored as part of the component's configuration via editFormFieldValues.
        // Since these don't go into YAML, we check the catalog default.

        StringBuilder output = new();
        bool allSuccess = true;

        foreach (ComponentFormField field in entry.FormFields)
        {
            if (!field.YamlPath.StartsWith("subchart:", StringComparison.Ordinal))
            {
                continue;
            }

            // The chart name is after the "subchart:" prefix.

            string subchartName = field.YamlPath["subchart:".Length..];

            // Determine if the toggle is enabled. Check if the component has stored
            // a "false" override — otherwise default to the catalog default value.

            bool enabled = IsSubchartEnabled(component, field);

            if (!enabled)
            {
                continue;
            }

            // Build and execute the helm install for the subchart using the parent's
            // repo URL and namespace.

            string repoUrl = component.HelmRepoUrl ?? entry.HelmRepoUrl ?? "";
            string ns = component.Namespace ?? entry.DefaultNamespace ?? "default";
            string kubeconfig = component.Cluster.Kubeconfig ?? "";

            if (string.IsNullOrWhiteSpace(kubeconfig))
            {
                return new HelmExecutionResult
                {
                    Success = false,
                    Output = "No kubeconfig stored for this cluster."
                };
            }

            string? subchartValues = !string.IsNullOrWhiteSpace(field.SubchartDefaultValues)
                ? field.SubchartDefaultValues
                : null;

            HelmCommand subCommand = new()
            {
                Operation = "upgrade --install",
                ReleaseName = subchartName,
                ChartReference = $"{repoUrl}/{subchartName}",
                Namespace = ns,
                RepoUrl = repoUrl,
                HasValues = subchartValues is not null,
                ValuesYaml = subchartValues
            };

            HelmExecutionResult result = await ExecuteHelmAsync(componentId, subCommand, ct);
            output.AppendLine($"--- Subchart: {subchartName} ---");
            output.AppendLine(result.Output);

            if (!result.Success)
            {
                allSuccess = false;
            }
        }

        return new HelmExecutionResult
        {
            Success = allSuccess,
            Output = output.ToString()
        };
    }

    /// <summary>
    /// Determines if a subchart toggle is enabled for a component by checking
    /// whether the component's HelmValues contains a marker comment for the field.
    /// Since subchart toggles don't map to YAML paths, we check the default value
    /// from the catalog — unless the component has an explicit override stored.
    /// </summary>
    private static bool IsSubchartEnabled(ClusterComponent component, ComponentFormField field)
    {
        // Subchart toggles store their value as a comment marker in HelmValues:
        // "# subchart:barman-cloud=true" or "# subchart:barman-cloud=false"
        // If no marker exists, fall back to the catalog default.

        string marker = $"# {field.YamlPath}=";

        if (!string.IsNullOrWhiteSpace(component.HelmValues))
        {
            foreach (string line in component.HelmValues.Split('\n'))
            {
                if (line.TrimStart().StartsWith(marker, StringComparison.Ordinal))
                {
                    string value = line.TrimStart()[marker.Length..].Trim();
                    return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // No explicit override — use catalog default.

        return string.Equals(field.DefaultValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Uninstalls any subcharts (e.g. Barman Cloud Plugin) that were installed
    /// alongside the parent component. The subchart list is derived from the
    /// catalog entry's toggle fields; only enabled subcharts are removed.
    /// Returns the combined Helm output for all subchart uninstalls.
    /// </summary>
    public async Task<HelmExecutionResult> UninstallSubchartsAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        CatalogEntry? entry = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

        if (entry is null)
        {
            return new HelmExecutionResult { Success = true, Output = "" };
        }

        StringBuilder output = new();
        bool allSuccess = true;

        foreach (ComponentFormField field in entry.FormFields)
        {
            if (!field.YamlPath.StartsWith("subchart:", StringComparison.Ordinal))
            {
                continue;
            }

            string subchartName = field.YamlPath["subchart:".Length..];

            if (!IsSubchartEnabled(component, field))
            {
                continue;
            }

            string ns = component.Namespace ?? entry.DefaultNamespace ?? "default";
            string kubeconfig = component.Cluster.Kubeconfig ?? "";

            if (string.IsNullOrWhiteSpace(kubeconfig))
            {
                return new HelmExecutionResult
                {
                    Success = false,
                    Output = "No kubeconfig stored for this cluster."
                };
            }

            HelmCommand subCommand = new()
            {
                Operation = "uninstall",
                ReleaseName = subchartName,
                Namespace = ns
            };

            HelmExecutionResult result = await ExecuteHelmAsync(componentId, subCommand, ct);

            // "release: not found" means the subchart was never installed (e.g. the install
            // failed before Helm recorded the release). Treat this as already-uninstalled
            // so a missing subchart never blocks the parent component from being removed.
            if (!result.Success && result.Output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                result = new HelmExecutionResult { Success = true, Output = result.Output };
            }

            output.AppendLine($"--- Subchart: {subchartName} ---");
            output.AppendLine(result.Output);

            if (!result.Success)
            {
                allSuccess = false;
            }
        }

        return new HelmExecutionResult
        {
            Success = allSuccess,
            Output = output.ToString()
        };
    }

    /// <summary>
    /// Validates that a component can be uninstalled and transitions it to
    /// Uninstalling status. Only installed or failed components can be uninstalled.
    /// </summary>
    public async Task<ClusterComponent> PrepareUninstallAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        if (component.Status is ComponentStatus.NotInstalled)
        {
            throw new InvalidOperationException(
                "Component is not installed. Nothing to uninstall.");
        }

        if (component.Status is ComponentStatus.Installing or ComponentStatus.Uninstalling)
        {
            throw new InvalidOperationException(
                "Component has an operation in progress. Wait for it to complete.");
        }

        component.Status = ComponentStatus.Uninstalling;
        component.LastError = null;
        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Records the result of a Helm uninstall operation. On success, resets
    /// the component to NotInstalled so it can be reinstalled later if needed.
    /// </summary>
    public async Task<ClusterComponent> MarkUninstallResultAsync(
        Guid componentId, bool success, string? error = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        if (success)
        {
            component.Status = ComponentStatus.NotInstalled;
            component.InstalledAt = null;
            component.LastError = null;
        }
        else
        {
            component.Status = ComponentStatus.Failed;
            component.LastError = error;
        }

        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Clears the LastError on a component without changing its status.
    /// Used when a user dismisses an error notification — the component
    /// stays in its current state, we just stop showing the old error.
    /// </summary>
    public async Task ClearErrorAsync(Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        component.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds a HelmCommand for installing or upgrading a component.
    /// Uses "upgrade --install" which is idempotent — installs if not present,
    /// upgrades if already installed. For Manifest-type components, produces
    /// a "kubectl-apply" operation instead (applies raw YAML to the cluster).
    /// </summary>
    public async Task<HelmCommand> GetInstallCommandAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Manifest components are applied via kubectl, not helm.
        // Their HelmValues field contains raw Kubernetes YAML manifests.
        // Substitute any %%PLACEHOLDER%% tokens from vault-backed FormFields before applying.

        if (component.ComponentType == "Manifest")
        {
            string manifestYaml = await SubstituteManifestPlaceholdersAsync(
                component.HelmValues ?? "", component, ct);

            // Do not pass Namespace — every resource in a Manifest already declares its own
            // namespace in metadata. Passing --namespace would cause kubectl to reject any
            // resource whose metadata.namespace differs from the component's default namespace.
            return new HelmCommand
            {
                Operation = "kubectl-apply",
                ReleaseName = component.ReleaseName ?? component.Name,
                HasValues = !string.IsNullOrWhiteSpace(manifestYaml),
                ValuesYaml = manifestYaml
            };
        }

        // ManifestUrl components use kubectl apply -f <url> directly.
        // HelmRepoUrl holds the manifest URL; no local YAML needed.

        if (component.ComponentType == "ManifestUrl")
        {
            // Follow the catalog's URL, not the one captured when this component was registered.
            // For a ManifestUrl component the URL *is* the version — these are release assets
            // pinned to a tag — so a stored URL means the component re-applies the version it was
            // installed at, for ever, and "upgrade the CRDs" becomes a button that reinstalls the
            // old ones and reports success. There is no version field to bump instead: for Helm
            // components that job belongs to HelmChartVersion, and this path has no equivalent.
            CatalogEntry? urlCatalog =
                ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

            string? catalogUrl = urlCatalog?.ComponentType == "ManifestUrl"
                ? urlCatalog.HelmRepoUrl
                : null;

            if (!string.IsNullOrWhiteSpace(catalogUrl) && catalogUrl != component.HelmRepoUrl)
            {
                logger.LogInformation(
                    "Component {Component} applies the catalog manifest {CatalogUrl} rather than its " +
                    "registered {StoredUrl}", component.Name, catalogUrl, component.HelmRepoUrl);
            }

            return new HelmCommand
            {
                Operation = "kubectl-apply-url",
                ReleaseName = component.ReleaseName ?? component.Name,
                ManifestUrl = catalogUrl ?? component.HelmRepoUrl
            };
        }

        // Resolve any vault secrets and inject them into the values YAML.
        // Secret form fields (like Grafana admin password) are stored encrypted
        // in the vault rather than in plain text in HelmValues. At install time,
        // we decrypt them and merge into the YAML so Helm gets the full picture.

        string? valuesYaml = await InjectSecretsIntoValuesAsync(component, ct);

        // EntKube Telemetry Collector: the ingest URL and per-cluster token are control-plane values the
        // cluster cannot derive, and the catalog ships them as REPLACE_WITH_* placeholders. A collector
        // installed with those still starts, reports Ready and passes its health check while every export
        // fails against a host that does not resolve — the Logs tab then stays empty with nothing looking
        // broken. New registrations get them pre-filled; heal anything older here so one Apply fixes it.
        CatalogEntry? valuesCatalog = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

        if (TelemetryIngestDefaults.IsCollector(valuesCatalog))
        {
            (string? healed, string? mintedToken) = TelemetryIngestDefaults.FillPlaceholders(
                valuesYaml, component.ClusterId, component.Cluster.TenantId, ingestTokens, configuration);
            valuesYaml = healed;

            // The vault can hold the placeholder itself: InjectSecretsIntoValuesAsync recovers a missing
            // secret from the stored values, so a placeholder install writes "REPLACE_WITH_INGEST_TOKEN"
            // back as the token. Overwrite it, or the next install would re-inject the placeholder.
            if (mintedToken is not null)
            {
                await vaultService.SetComponentSecretAsync(
                    component.Cluster.TenantId, component.Id,
                    TelemetryIngestDefaults.TokenSecretName(valuesCatalog!), mintedToken, ct);
            }

            // Once this cluster runs its own telemetry indexer, that is where the collector should ship —
            // keeping the data in the cluster is the whole reason the indexer is there, and a collector
            // still pointed at the management plane leaves the indexer empty. It cannot be decided when
            // the collector is registered: the indexer DEPENDS on the collector, so it is always installed
            // second. Deciding it here means re-applying the collector is what moves it.
            string? inClusterIngest = await entKubeTelemetry.GetInClusterIngestUrlAsync(component.ClusterId, ct);
            (string? repointed, bool didRepoint) =
                TelemetryIngestDefaults.RepointToInCluster(valuesYaml, inClusterIngest, configuration);
            valuesYaml = repointed;

            if (didRepoint)
            {
                // Recorded on the component, not merely rendered into this one invocation. This is now
                // where the collector actually ships, so the Components tab should say so — and the read
                // path decides whether the management plane or the cluster's node holds the data by
                // reading exactly this value, so a repoint it cannot see would send every log and trace
                // view to whichever store is empty.
                //
                // Applied to the STORED values, never to valuesYaml above: that document has had the
                // vault's secrets merged into it and must not be written back in the clear.
                (string? storedRepoint, bool storedChanged) = TelemetryIngestDefaults.RepointToInCluster(
                    component.HelmValues, inClusterIngest, configuration);

                if (storedChanged)
                {
                    component.HelmValues = storedRepoint;
                    await db.SaveChangesAsync(ct);
                }

                logger.LogInformation(
                    "Repointed collector {Component} on cluster {ClusterId} at the in-cluster telemetry "
                    + "indexer ({Url}); its logs and traces now stay in the cluster.",
                    component.Name, component.ClusterId, inClusterIngest);
            }
        }

        // In-cluster telemetry nodes: the tenant/cluster identity and both bearer tokens are control-plane
        // values the cluster cannot derive. New registrations get them written up front; heal anything
        // registered before that existed here, so one Apply fixes it rather than a delete and re-add.
        // The chart refuses to render without an identity, so this is a loud failure — but still one an
        // operator should not have to resolve by hand.
        if (EntKubeTelemetryService.IsTelemetryNode(valuesCatalog))
        {
            valuesYaml = await entKubeTelemetry.FillMissingIdentityAsync(component, valuesYaml, ct);

            // A querier holds no data of its own: it federates the hot tier and the segment list from the
            // indexer's Service. That Service name contains the indexer's RELEASE name, so a querier
            // registered from a catalog literal can be pointed at a host that resolves nowhere — and then
            // every query fails on both tiers with a DNS error while both pods look perfectly healthy.
            // Corrected here so re-applying the component fixes it.
            (string? repointedQuerier, string? indexerUrl) =
                await entKubeTelemetry.FixQuerierIndexerUrlAsync(component, valuesYaml, ct);
            valuesYaml = repointedQuerier;

            if (indexerUrl is not null)
            {
                logger.LogInformation(
                    "Repointed telemetry querier {Component} on cluster {ClusterId} at {Url} — its previous "
                    + "indexer address did not resolve to a Service in this cluster.",
                    component.Name, component.ClusterId, indexerUrl);
            }
        }

        // Components whose image EntKube publishes to a private registry need the CLUSTER to hold a
        // credential — EntKube's own does not reach the kubelet. Created from configuration here, so the
        // install works without an operator hand-building a Secret per cluster. An imagePullSecrets value
        // they set themselves wins: naming an existing Secret is a legitimate choice.
        if (valuesCatalog?.ImageRegistryHost is not null
            && string.IsNullOrWhiteSpace(YamlFormMerger.ExtractValue(valuesYaml ?? "", "imagePullSecrets.0.name")))
        {
            string? pullSecret = await EnsureImagePullSecretAsync(
                component, valuesCatalog, component.Cluster.Kubeconfig ?? "", ct);

            if (pullSecret is not null)
            {
                valuesYaml = YamlFormMerger.MergeFormValues(valuesYaml ?? "",
                    new Dictionary<string, string> { ["imagePullSecrets.0.name"] = pullSecret });
            }
        }

        // Istio gateways: when a wg-easy component is present on the cluster, expose the
        // WireGuard UDP port on the gateway's LoadBalancer so VPN traffic rides the
        // gateway IP. Injected here (like secret injection) so both Apply and
        // Save & Apply pick it up without re-editing the gateway's stored values.
        //
        // The external gateway ("istio") is wg-easy's default target, so it gets the
        // port whenever any wg-easy exists — robust even if the WG_GATEWAY_NAME secret
        // wasn't captured. The internal gateway only gets it when a wg-easy explicitly
        // targets it (its WG_GATEWAY_NAME == this gateway's release name).
        if (component.Name is "istio" or "istio-internal")
        {
            string gatewayRelease = (component.ReleaseName ?? component.Name).Trim();

            // Only installed wg-easy components count — so re-applying the gateway after
            // a wg-easy uninstall (status → NotInstalled) drops the port again.
            List<ClusterComponent> wgComponents = await db.ClusterComponents
                .Include(c => c.Cluster)
                .Where(c => c.ClusterId == component.ClusterId
                    && c.Name == "wg-easy"
                    && c.Status == ComponentStatus.Installed)
                .ToListAsync(ct);

            bool inject = false;

            if (wgComponents.Count > 0)
            {
                // External gateway is the default target → always expose the port.
                if (component.Name == "istio")
                {
                    inject = true;
                }
                else
                {
                    // Internal gateway → only if a wg-easy explicitly targets it.
                    foreach (ClusterComponent wg in wgComponents)
                    {
                        string? target = await vaultService.GetComponentSecretValueAsync(
                            wg.Cluster.TenantId, wg.Id, "WG_GATEWAY_NAME", ct);

                        if (string.Equals(target?.Trim(), gatewayRelease, StringComparison.OrdinalIgnoreCase))
                        {
                            inject = true;
                            break;
                        }
                    }
                }
            }

            if (inject)
            {
                valuesYaml = YamlFormMerger.EnsureWireGuardGatewayPort(valuesYaml ?? "");
            }
        }

        // Keycloak: render any named-theme volumes / copier init container / login mount into
        // the chart's extra* values so Helm manages them alongside the StatefulSet. Without
        // this, `helm upgrade` resets the chart-managed volumes (dropping the out-of-band theme
        // volumes) while the theme init container survives — leaving volumeMounts that point at
        // missing volumes and failing the upgrade.
        if (component.HelmChartName == "keycloakx")
        {
            string themeExtras = await keycloakService.BuildKeycloakThemeHelmExtrasAsync(component.Id, ct);
            valuesYaml = MergeYamlBlocks(valuesYaml, themeExtras);
        }

        // Fill in top-level keys the catalog has grown since this component was registered.
        // Catalog DefaultValues are read once, at registration; from then on the component carries
        // its own copy, so a fix shipped in the catalog reaches new installs and nothing else.
        // (Subcharts do not have this problem — they re-read SubchartDefaultValues every apply,
        // which is why an istiod change lands while the same edit to a gateway does not.)
        valuesYaml = FillMissingCatalogDefaults(valuesYaml, valuesCatalog?.DefaultValues);

        // trust-manager: `secretTargets.enabled: true` without the permission that goes with it produces a
        // controller that watches Secrets it cannot list — a crash loop with nothing in any Bundle status to
        // explain it. The fill-in above cannot repair that, because a component that already carries a
        // `secretTargets` block has the key "present but different" and is left alone by design. Running
        // after it means both a stored block and a just-filled one are corrected, and neither an absent
        // block nor a disabled one is touched.
        if (component.HelmChartName == "trust-manager")
        {
            valuesYaml = YamlFormMerger.EnsureTrustManagerSecretTargets(valuesYaml ?? "");
        }

        string releaseName = component.ReleaseName ?? component.Name;
        string chartRef = !string.IsNullOrWhiteSpace(component.HelmRepoUrl)
            ? $"{component.HelmRepoUrl}/{component.HelmChartName}"
            : component.HelmChartName ?? component.Name;

        // Catalog-registered components keep their key as the component Name, so the entry resolved
        // above also carries an extended install timeout for heavy/DaemonSet charts.
        string installTimeout = valuesCatalog?.InstallTimeout ?? "10m0s";

        return new HelmCommand
        {
            Operation = "upgrade --install",
            ReleaseName = releaseName,
            ChartReference = chartRef,
            Namespace = component.Namespace,
            RepoUrl = component.HelmRepoUrl,
            Version = component.HelmChartVersion,
            HasValues = !string.IsNullOrWhiteSpace(valuesYaml),
            ValuesYaml = valuesYaml,
            Timeout = installTimeout
        };
    }

    /// <summary>
    /// Adds top-level keys from the catalog's current defaults that the component's stored values
    /// do not mention at all. A key the operator has set — to anything, including a value that
    /// differs from the catalog — is left exactly as it is.
    ///
    /// This is deliberately top-level and deliberately additive. Merging deeply would let a
    /// catalog edit reach inside a structure the operator owns: the gateway's stored
    /// <c>service.ports</c> list is shorter than the catalog's, and growing it behind their back
    /// would open a port on an internet-facing LoadBalancer that nobody asked for. Absent-means-
    /// unconsidered is a defensible inference; present-but-different is a decision.
    ///
    /// Empty stored values are left empty rather than filled from the catalog wholesale. A
    /// component installed with no values at all is one where every catalog key is "missing", and
    /// pouring the entire default document into it on the next upgrade is not a fill-in, it is a
    /// reinstall with different settings.
    ///
    /// Works on text rather than a parsed document because these values carry EntKube's own
    /// <c>#{" "}subchart:name=true</c> markers in YAML comments, and a parse/serialise round trip
    /// would drop them — silently disabling the subchart on the next apply.
    /// </summary>
    public static string? FillMissingCatalogDefaults(string? storedValues, string? catalogDefaults)
    {
        if (string.IsNullOrWhiteSpace(storedValues) || string.IsNullOrWhiteSpace(catalogDefaults))
        {
            return storedValues;
        }

        HashSet<string> present = new(TopLevelKeys(storedValues), StringComparer.Ordinal);
        List<string> additions = [];

        foreach ((string key, string block) in TopLevelBlocks(catalogDefaults))
        {
            if (!present.Contains(key))
            {
                additions.Add(block.TrimEnd());
            }
        }

        return additions.Count == 0
            ? storedValues
            : MergeYamlBlocks(storedValues, string.Join("\n", additions) + "\n");
    }

    /// <summary>Top-level mapping keys in a values document (column-zero <c>key:</c> lines).</summary>
    private static IEnumerable<string> TopLevelKeys(string yaml) =>
        TopLevelBlocks(yaml).Select(b => b.Key);

    /// <summary>
    /// Splits a values document into its top-level keys and the text belonging to each — the
    /// key's own line, everything indented under it, and any comment lines immediately above it,
    /// which are almost always that key's explanation and are worth carrying across with it.
    /// </summary>
    private static List<(string Key, string Block)> TopLevelBlocks(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
        List<(string Key, string Block)> blocks = [];

        // Comment/blank lines seen since the last key, held back until we know whether a
        // top-level key follows them (in which case they belong to it).
        List<string> pending = [];
        string? currentKey = null;
        List<string> current = [];

        void Flush()
        {
            if (currentKey is not null) blocks.Add((currentKey, string.Join("\n", current)));
            currentKey = null;
            current = [];
        }

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                // Inside a block, keep the comment with it; between blocks, hold it for the next.
                if (currentKey is not null) current.Add(line);
                else pending.Add(line);
                continue;
            }

            bool isTopLevel = line.Length > 0 && !char.IsWhiteSpace(line[0]) && !trimmed.StartsWith('-');
            int colon = isTopLevel ? line.IndexOf(':') : -1;

            if (colon > 0)
            {
                Flush();
                currentKey = line[..colon].Trim();
                current = [.. pending, line];
                pending = [];
                continue;
            }

            if (currentKey is not null)
            {
                current.AddRange(pending);
                pending = [];
                current.Add(line);
            }
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// Appends a YAML block of additional top-level keys to a values document, ensuring a
    /// newline separates them. Returns the base unchanged when there is nothing to add.
    /// </summary>
    private static string? MergeYamlBlocks(string? baseYaml, string extra)
    {
        if (string.IsNullOrEmpty(extra))
            return baseYaml;

        if (string.IsNullOrEmpty(baseYaml))
            return extra;

        return baseYaml.EndsWith('\n') ? baseYaml + extra : baseYaml + "\n" + extra;
    }

    /// <summary>
    /// Builds a HelmCommand for uninstalling a component.
    /// For Manifest-type components, produces a "kubectl-delete" operation.
    /// </summary>
    public async Task<HelmCommand> GetUninstallCommandAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        string releaseName = component.ReleaseName ?? component.Name;

        // Manifest components are deleted via kubectl, not helm uninstall.

        if (component.ComponentType == "Manifest")
        {
            // Substitute %%PLACEHOLDER%% tokens before deleting — same as install.
            // Without this, kubectl gets raw placeholders (e.g. %%WG_GATEWAY%%) which
            // are invalid YAML and fail to parse, leaving resources orphaned.
            string manifestYaml = await SubstituteManifestPlaceholdersAsync(
                component.HelmValues ?? "", component, ct);

            // No Namespace — same reason as install: resources declare their own namespaces.
            return new HelmCommand
            {
                Operation = "kubectl-delete",
                ReleaseName = releaseName,
                HasValues = !string.IsNullOrWhiteSpace(manifestYaml),
                ValuesYaml = manifestYaml
            };
        }

        // ManifestUrl CRD bundles are cluster-scoped infra — skip uninstall to avoid breaking dependents.

        if (component.ComponentType == "ManifestUrl")
        {
            return new HelmCommand
            {
                Operation = "noop",
                ReleaseName = releaseName
            };
        }

        return new HelmCommand
        {
            Operation = "uninstall",
            ReleaseName = releaseName,
            Namespace = component.Namespace
        };
    }

    /// <summary>
    /// Resolves vault secrets for a component and merges them into the Helm values YAML.
    /// Looks up the component's catalog entry to find which form fields are secret-backed,
    /// then decrypts the corresponding vault secrets and injects them at the correct YAML paths.
    /// Returns the merged YAML (or the original if no secrets exist).
    /// </summary>
    /// <summary>
    /// Queries a Service's external LoadBalancer IP (or hostname for providers that
    /// use DNS names instead of IPs, such as AKS with Azure DNS integration).
    /// Returns null if the LB is not yet assigned or the cluster is unreachable.
    /// </summary>
    public async Task<string?> GetServiceExternalIpAsync(
        Guid clusterId, string serviceName, string ns, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct)
            ?? throw new InvalidOperationException("Cluster not found.");

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return null;

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, cluster.Kubeconfig, ct);

            // Try IP first — most cloud providers and MetalLB set .ingress[0].ip
            HelmExecutionResult ipResult = await RunProcessAsync("kubectl",
                $"get svc {serviceName} -n {ns} --kubeconfig {tempKubeconfig} -o jsonpath={{.status.loadBalancer.ingress[0].ip}}",
                ct);

            string ip = ipResult.Output.Trim();
            if (!string.IsNullOrEmpty(ip))
                return ip;

            // Fallback to hostname — AWS ELB and some AKS configs use hostname
            HelmExecutionResult hostnameResult = await RunProcessAsync("kubectl",
                $"get svc {serviceName} -n {ns} --kubeconfig {tempKubeconfig} -o jsonpath={{.status.loadBalancer.ingress[0].hostname}}",
                ct);

            string hostname = hostnameResult.Output.Trim();
            return string.IsNullOrEmpty(hostname) ? null : hostname;
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    /// <summary>
    /// Replaces %%PLACEHOLDER%% tokens in a Manifest-type component's YAML with
    /// values retrieved from the vault. This allows FormFields like GatewaySelector
    /// to inject values that must appear verbatim in the YAML (e.g. a gateway name
    /// in an EnvoyFilter workloadSelector) rather than at a YAML dot-notation path.
    /// Only fields with both StoreAsSecret=true and ManifestPlaceholder set are processed.
    /// </summary>
    /// <summary>
    /// Resolves the Istio gateway a wg-easy component targets: the one named in its
    /// WG_GATEWAY_NAME secret, falling back to the external "istio" gateway. Only
    /// installed gateways are considered. Returns null if none match. Does not require
    /// wg-easy itself to be installed, so it can be called before an uninstall to
    /// capture the gateway that must later be re-applied to strip the UDP port.
    /// </summary>
    public async Task<Guid?> ResolveWireGuardGatewayIdAsync(
        Guid wgComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? wg = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == wgComponentId, ct);

        if (wg is null || wg.Name != "wg-easy")
            return null;

        string? target = (await vaultService.GetComponentSecretValueAsync(
            wg.Cluster.TenantId, wg.Id, "WG_GATEWAY_NAME", ct))?.Trim();

        List<ClusterComponent> gateways = await db.ClusterComponents
            .Where(c => c.ClusterId == wg.ClusterId
                && (c.Name == "istio" || c.Name == "istio-internal")
                && c.Status == ComponentStatus.Installed)
            .ToListAsync(ct);

        // Prefer the gateway wg-easy explicitly targets; otherwise the external one.
        ClusterComponent? gateway = gateways.FirstOrDefault(g =>
                string.Equals((g.ReleaseName ?? g.Name).Trim(), target, StringComparison.OrdinalIgnoreCase))
            ?? gateways.FirstOrDefault(g => g.Name == "istio");

        return gateway?.Id;
    }

    /// <summary>
    /// Re-applies a gateway via helm upgrade. The WireGuard UDP port is added or dropped
    /// automatically by GetInstallCommandAsync based on whether an installed wg-easy still
    /// targets it — so this both adds the port (after wg-easy install) and removes it
    /// (after wg-easy uninstall).
    /// </summary>
    public async Task<HelmExecutionResult?> ReapplyGatewayAsync(
        Guid gatewayId, CancellationToken ct = default)
    {
        HelmCommand command = await GetInstallCommandAsync(gatewayId, ct);
        return await ExecuteHelmAsync(gatewayId, command, ct);
    }

    /// <summary>
    /// After a wg-easy install, re-applies the Istio gateway it targets so the gateway's
    /// LoadBalancer picks up the WireGuard UDP port. Returns null if no matching installed
    /// gateway exists.
    /// </summary>
    public async Task<HelmExecutionResult?> EnsureGatewayWireGuardPortAsync(
        Guid wgComponentId, CancellationToken ct = default)
    {
        Guid? gatewayId = await ResolveWireGuardGatewayIdAsync(wgComponentId, ct);
        return gatewayId is null ? null : await ReapplyGatewayAsync(gatewayId.Value, ct);
    }

    /// <summary>
    /// Completes the cutover after a telemetry indexer install: repoints the cluster's OpenTelemetry
    /// Collector at the indexer beside it and re-applies it, so telemetry starts arriving where the
    /// management plane has already started reading from.
    ///
    /// <para>Without this the two halves move at different times. Reads follow the indexer the instant it
    /// is installed — the read path finds it by looking for it — while writes stay pointed at the
    /// management plane until somebody happens to re-apply the collector. Nothing prompts them to, the
    /// indexer answers queries perfectly well with nothing in it, and the result is every log and trace
    /// view on every surface going quietly empty. Installing the indexer is the moment the operator asked
    /// for the cutover, so it is the moment both halves move.</para>
    ///
    /// <para>The repointed endpoint is written back to the component's stored values, not merely rendered
    /// into this one helm invocation: it is now the collector's real configuration, the Components tab
    /// shows it, and the read path uses it to know that the data has moved.</para>
    ///
    /// <para>Returns null when there is nothing to do — the component is not an indexer, the cluster has
    /// no installed collector, or that collector already ships somewhere the operator chose.</para>
    /// </summary>
    public async Task<HelmExecutionResult?> EnsureCollectorShipsInClusterAsync(
        Guid indexerComponentId, CancellationToken ct = default)
    {
        if (await RepointCollectorToInClusterAsync(indexerComponentId, ct) is not Guid collectorId)
            return null;

        HelmCommand command = await GetInstallCommandAsync(collectorId, ct);
        return await ExecuteHelmAsync(collectorId, command, ct);
    }

    /// <summary>
    /// The database half of <see cref="EnsureCollectorShipsInClusterAsync"/>: rewrites the collector's
    /// stored endpoint to the cluster's own indexer and returns which collector to re-apply, or null when
    /// there is nothing to move. Separated from the helm invocation so the decision — which is the part
    /// with the rules in it — can be tested, and driven, without a helm invocation.
    /// </summary>
    public async Task<Guid?> RepointCollectorToInClusterAsync(
        Guid indexerComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? indexer = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == indexerComponentId, ct);

        if (indexer is null || indexer.Name != EntKubeTelemetryService.IndexerKey) return null;

        ClusterComponent? collector = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.ClusterId == indexer.ClusterId
                                      && c.Name == TelemetryIngestDefaults.CollectorKey
                                      && c.Status == ComponentStatus.Installed, ct);
        if (collector is null) return null;

        string? inClusterIngest = await entKubeTelemetry.GetInClusterIngestUrlAsync(indexer.ClusterId, ct);
        (string? repointed, bool didRepoint) = TelemetryIngestDefaults.RepointToInCluster(
            collector.HelmValues, inClusterIngest, configuration);

        // An operator-chosen destination is left exactly as it is, and re-applying the collector for no
        // reason would be a surprising side effect of installing something else.
        if (!didRepoint) return null;

        collector.HelmValues = repointed;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Repointed collector on cluster {ClusterId} at the in-cluster telemetry indexer ({Url}) "
            + "after its install; re-applying so its logs and traces stay in the cluster.",
            indexer.ClusterId, inClusterIngest);

        return collector.Id;
    }

    private async Task<string> SubstituteManifestPlaceholdersAsync(
        string manifestYaml, ClusterComponent component, CancellationToken ct)
    {
        CatalogEntry? catalog = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);
        if (catalog is null)
            return manifestYaml;

        IEnumerable<ComponentFormField> placeholderFields = catalog.FormFields
            .Where(f => f.StoreAsSecret && f.ManifestPlaceholder != null);

        Guid tenantId = component.Cluster.TenantId;

        foreach (ComponentFormField field in placeholderFields)
        {
            string secretName = field.SecretName ?? field.Key;
            string? value = await vaultService.GetComponentSecretValueAsync(
                tenantId, component.Id, secretName, ct);

            // Fall back to the field's default when nothing was saved, so a placeholder
            // (e.g. %%WG_ALLOWED_IPS%%) never leaks into the running config — important
            // for fields that have sensible defaults like the cluster CIDRs / DNS.
            if (string.IsNullOrEmpty(value))
                value = field.DefaultValue;

            if (!string.IsNullOrEmpty(value))
                manifestYaml = manifestYaml.Replace(field.ManifestPlaceholder!, value, StringComparison.Ordinal);
        }

        return manifestYaml;
    }

    private async Task<string?> InjectSecretsIntoValuesAsync(
        ClusterComponent component, CancellationToken ct)
    {
        // Look up the catalog entry to know which fields are secret-backed.

        CatalogEntry? catalog = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

        if (catalog is null)
        {
            return component.HelmValues;
        }

        List<ComponentFormField> secretFields = catalog.FormFields
            .Where(f => f.StoreAsSecret)
            .ToList();

        if (secretFields.Count == 0)
        {
            return component.HelmValues;
        }

        // Retrieve each secret from the vault and build a path → value dictionary.
        // If a secret is missing from the vault but exists in the component's HelmValues
        // (e.g. imported release), extract it and store it in the vault so future
        // operations don't lose it.

        Guid tenantId = component.Cluster.TenantId;
        Dictionary<string, string> secretPathValues = new();

        foreach (ComponentFormField field in secretFields)
        {
            string secretName = field.SecretName ?? field.Key;
            string? secretValue = await vaultService.GetComponentSecretValueAsync(
                tenantId, component.Id, secretName, ct);

            if (string.IsNullOrEmpty(secretValue) && !string.IsNullOrWhiteSpace(component.HelmValues))
            {
                // Secret missing from vault — try to recover it from the stored Helm values.
                // This handles imported releases where secrets exist in the config but were
                // never stored in the vault.

                string? existingValue = YamlFormMerger.ExtractValue(component.HelmValues, field.YamlPath);

                if (!string.IsNullOrEmpty(existingValue))
                {
                    await vaultService.SetComponentSecretAsync(tenantId, component.Id, secretName, existingValue, ct);
                    secretValue = existingValue;
                }
            }

            if (!string.IsNullOrEmpty(secretValue))
            {
                secretPathValues[field.YamlPath] = secretValue;
            }
        }

        if (secretPathValues.Count == 0)
        {
            return component.HelmValues;
        }

        // Merge secrets into the values YAML alongside any existing config.

        string baseYaml = component.HelmValues ?? "";
        return YamlFormMerger.MergeFormValues(baseYaml, secretPathValues);
    }

    /// <summary>
    /// Syncs all component secrets marked SyncToKubernetes=true to the cluster
    /// as Kubernetes Secret resources. Groups secrets by target K8s Secret name
    /// and namespace, then creates or updates each Secret resource via the K8s API.
    ///
    /// This is called after a successful install/upgrade so that the component's
    /// pods can mount or reference the secrets. Can also be triggered manually
    /// from the UI to re-sync after secret changes.
    /// </summary>
    public async Task<HelmExecutionResult> SyncComponentSecretsAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load the component with its cluster to get kubeconfig and tenant ID.

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        if (string.IsNullOrWhiteSpace(component.Cluster.Kubeconfig))
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = "No kubeconfig stored for this cluster."
            };
        }

        // Retrieve all secrets for this component that are marked for K8s sync.

        Guid tenantId = component.Cluster.TenantId;
        List<VaultSecret> allSecrets = await db.Set<VaultSecret>()
            .Where(s => s.ComponentId == componentId && s.SyncToKubernetes)
            .ToListAsync(ct);

        // Build a lookup of secret name → FormField so we can apply transformations
        // (e.g. BcryptOnSync) before writing to the K8s Secret.
        CatalogEntry? catalog = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);
        Dictionary<string, ComponentFormField> secretFieldsByName = catalog?.FormFields
            .Where(f => f.StoreAsSecret)
            .ToDictionary(f => f.SecretName ?? f.Key, f => f, StringComparer.OrdinalIgnoreCase)
            ?? [];

        if (allSecrets.Count == 0)
        {
            return new HelmExecutionResult
            {
                Success = true,
                Output = "No secrets marked for Kubernetes sync."
            };
        }

        // Group secrets by their target K8s Secret name + namespace.
        // Multiple vault secrets can be keys in the same K8s Secret resource.

        IEnumerable<IGrouping<(string SecretName, string Namespace), VaultSecret>> groups = allSecrets
            .Where(s => !string.IsNullOrWhiteSpace(s.KubernetesSecretName))
            .GroupBy(s => (
                SecretName: s.KubernetesSecretName!,
                Namespace: s.KubernetesNamespace ?? component.Namespace ?? "default"
            ));

        // Build kubectl apply commands for each K8s Secret.
        // We create an Opaque secret with all grouped vault secret values as data keys.

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        List<string> results = [];

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, component.Cluster.Kubeconfig, ct);

            foreach (IGrouping<(string SecretName, string Namespace), VaultSecret> group in groups)
            {
                string k8sSecretName = group.Key.SecretName;
                string ns = group.Key.Namespace;

                // Ensure the namespace exists before writing the secret.
                // The pod needs the secret at startup, which may be before Helm creates the namespace.
                await RunProcessAsync("kubectl", $"create namespace {ns} --kubeconfig {tempKubeconfig}", ct);

                // Decrypt each secret value and build --from-literal args.

                List<string> literals = [];

                foreach (VaultSecret vaultSecret in group)
                {
                    string? plainValue = await vaultService.GetComponentSecretValueAsync(
                        tenantId, componentId, vaultSecret.Name, ct);

                    if (plainValue is not null)
                    {
                        // If the catalog field requests bcrypt transformation, hash the
                        // plaintext before writing it to the K8s Secret. The vault retains
                        // the original plaintext so it can be revealed in the UI.
                        if (secretFieldsByName.TryGetValue(vaultSecret.Name, out ComponentFormField? field)
                            && field.BcryptOnSync)
                        {
                            plainValue = BCrypt.Net.BCrypt.HashPassword(plainValue, workFactor: 12);
                        }

                        literals.Add($"--from-literal={vaultSecret.Name}={plainValue}");
                    }
                }

                if (literals.Count == 0)
                {
                    continue;
                }

                // Delete existing secret (if any) then recreate.
                // This is simpler than patch/merge for the common case.

                string deleteArgs = $"delete secret {k8sSecretName} --namespace {ns} --ignore-not-found --kubeconfig {tempKubeconfig}";
                await RunProcessAsync("kubectl", deleteArgs, ct);

                string createArgs = $"create secret generic {k8sSecretName} --namespace {ns} {string.Join(" ", literals)} --kubeconfig {tempKubeconfig}";
                HelmExecutionResult createResult = await RunProcessAsync("kubectl", createArgs, ct);

                if (createResult.Success)
                {
                    // Mark as EntKube-managed so the deployment importer won't re-adopt it.
                    await RunProcessAsync("kubectl",
                        $"label secret {k8sSecretName} --namespace {ns} {VaultService.ManagedByLabelKey}={VaultService.ManagedByLabelValue} entkube.io/managed=true --overwrite --kubeconfig {tempKubeconfig}", ct);
                    results.Add($"✓ Secret '{k8sSecretName}' synced to namespace '{ns}' ({group.Count()} keys)");
                }
                else
                {
                    results.Add($"✗ Secret '{k8sSecretName}' failed: {createResult.Output}");
                }
            }

            bool allSucceeded = results.All(r => r.StartsWith("✓"));

            return new HelmExecutionResult
            {
                Success = allSucceeded,
                Output = string.Join("\n", results)
            };
        }
        finally
        {
            if (File.Exists(tempKubeconfig))
            {
                File.Delete(tempKubeconfig);
            }
        }
    }

    /// <summary>
    /// Executes a Helm or kubectl command against a cluster using the stored kubeconfig.
    /// For Helm operations: runs helm CLI with repo add, upgrade --install, or uninstall.
    /// For Manifest operations: runs kubectl apply/delete with the YAML content.
    /// Writes a temporary kubeconfig file, runs the CLI, and cleans up.
    /// </summary>
    public async Task<HelmExecutionResult> ExecuteHelmAsync(
        Guid componentId, HelmCommand command, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load the cluster with its kubeconfig so we can authenticate.

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        if (string.IsNullOrWhiteSpace(component.Cluster.Kubeconfig))
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = "No kubeconfig stored for this cluster."
            };
        }

        // Write the kubeconfig to a temporary file.

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, component.Cluster.Kubeconfig, ct);

            // Route to the appropriate executor based on operation type.

            if (command.Operation == "noop")
            {
                return new HelmExecutionResult { Success = true, Output = "No action taken (CRD bundles are not uninstalled automatically)." };
            }

            if (command.Operation is "kubectl-apply" or "kubectl-delete")
            {
                return await ExecuteKubectlAsync(command, tempKubeconfig, ct);
            }

            if (command.Operation == "kubectl-apply-url")
            {
                return await ExecuteKubectlUrlAsync(command, tempKubeconfig, ct);
            }

            return await ExecuteHelmCliAsync(command, tempKubeconfig, component.Cluster.Kubeconfig, ct);
        }
        finally
        {
            if (File.Exists(tempKubeconfig))
            {
                File.Delete(tempKubeconfig);
            }
        }
    }

    /// <summary>
    /// Applies or deletes a raw YAML manifest against an arbitrary cluster.
    /// Used by VpnService to push StrongSwan ConfigMap and Secret.
    /// </summary>
    public async Task<HelmExecutionResult> ApplyRawManifestAsync(
        KubernetesCluster cluster, string manifestYaml, bool delete = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return new HelmExecutionResult { Success = false, Output = "No kubeconfig stored for this cluster." };

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-manifest-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, cluster.Kubeconfig, ct);
            await File.WriteAllTextAsync(tempManifest, manifestYaml, ct);

            string operation = delete ? "delete" : "apply";
            string args = $"{operation} -f {tempManifest} --kubeconfig {tempKubeconfig}";
            if (delete) args += " --ignore-not-found";

            return await RunProcessAsync("kubectl", args, ct);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
        }
    }

    /// <summary>
    /// Applies or deletes Kubernetes manifests using kubectl.
    /// The manifest YAML is written to a temp file and applied/deleted.
    /// </summary>
    private async Task<HelmExecutionResult> ExecuteKubectlAsync(
        HelmCommand command, string kubeconfigPath, CancellationToken ct)
    {
        if (!command.HasValues || string.IsNullOrWhiteSpace(command.ValuesYaml))
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = "No manifest YAML content to apply."
            };
        }

        // Write the manifest YAML to a temp file.

        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-manifest-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempManifest, command.ValuesYaml, ct);

            string operation = command.Operation == "kubectl-apply" ? "apply" : "delete";
            List<string> args = [operation, "-f", tempManifest, "--kubeconfig", kubeconfigPath];

            if (!string.IsNullOrWhiteSpace(command.Namespace))
            {
                args.Add("--namespace");
                args.Add(command.Namespace);
            }

            // For delete, don't fail if the resource doesn't exist.

            if (operation == "delete")
            {
                args.Add("--ignore-not-found");
            }

            string arguments = string.Join(" ", args);
            return await RunProcessAsync("kubectl", arguments, ct);
        }
        finally
        {
            if (File.Exists(tempManifest))
            {
                File.Delete(tempManifest);
            }
        }
    }

    /// <summary>
    /// Applies a remote manifest URL directly via kubectl apply -f &lt;url&gt;.
    /// Used for components like Gateway API CRDs where the authoritative source
    /// is a GitHub release manifest rather than a Helm chart.
    /// </summary>
    /// <summary>
    /// HTTPRoutes a component used to create from its own manifest, before its hostname was
    /// registered as an ExternalRoute. They are deleted on the next route apply: nothing else
    /// removes them, and a leftover hand-written route keeps serving the hostname on whatever
    /// listener it can still reach.
    ///
    /// Matched on component name rather than catalog key because that is what a ClusterComponent
    /// carries — <see cref="ComponentCatalog.ToRegistration"/> sets Name to the catalog key.
    /// </summary>
    private static IEnumerable<string> LegacyManifestRouteNames(ClusterComponent component) =>
        string.Equals(component.Name, "wg-easy", StringComparison.OrdinalIgnoreCase)
            ? ["wg-easy-ui"]
            : [];

    /// <summary>
    /// The hostnames an AppRoute already speaks for.
    ///
    /// Compared case-insensitively because DNS is: an operator who types <c>Flow.example.com</c>
    /// into one form and <c>flow.example.com</c> into the other has described one hostname twice,
    /// and both descriptions land on the same generated object name.
    /// </summary>
    public static IReadOnlySet<string> HostnamesOwnedByAppRoutes(IEnumerable<AppRoute> appRoutes) =>
        appRoutes.Select(r => r.Hostname).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether applying this ExternalRoute's HTTPRoute would destroy an AppRoute's routing.
    ///
    /// It would, if they describe the same hostname. An AppRoute renders one rule per path — the
    /// rewrites that put <c>/gateway</c> in front of one service and <c>/engine</c> in front of
    /// another — while an ExternalRoute can only say "this whole host goes to this one service".
    /// Both are named <c>ToListenerName(hostname) + "-route"</c>, so they are not two routes: they
    /// are two descriptions of one object, and the one applied last is the one that survives.
    ///
    /// A passthrough ExternalRoute is exempt: it renders a TLSRoute, a different kind, which
    /// shares the name but never overwrites an HTTPRoute.
    /// </summary>
    public static bool WouldOverwriteAppRoute(ExternalRoute route, IReadOnlySet<string> appRouteHostnames) =>
        route.TlsMode != TlsMode.Passthrough && appRouteHostnames.Contains(route.Hostname);

    /// <summary>
    /// Applies all ExternalRoute resources for a component to its cluster via kubectl.
    /// Generates an HTTPRoute + Certificate manifest for each route and applies them.
    /// Safe to call repeatedly — kubectl apply is idempotent.
    /// </summary>
    public async Task<HelmExecutionResult> ApplyExternalRoutesAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load the full cluster (all components + their routes) so the Gateway manifest
        // covers every exposed hostname — not just this component's routes.
        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
                .ThenInclude(cl => cl.Components)
                    .ThenInclude(comp => comp.ExternalRoutes)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        List<ExternalRoute> allRoutes = [];
        List<(string OldName, string Namespace)> orphanedRoutes = [];

        foreach (ClusterComponent comp in component.Cluster.Components)
        {
            foreach (ExternalRoute r in comp.ExternalRoutes)
            {
                r.Component = comp;
                // Compute the old service-name-based route name before fixing — if it differs
                // from the new hostname-based name, the old HTTPRoute is orphaned in the cluster.
                string oldRouteName = r.ServiceName + "-route";
                string newRouteName = ExternalRouteService.ToListenerName(r.Hostname) + "-route";
                if (!string.Equals(oldRouteName, newRouteName, StringComparison.Ordinal))
                    orphanedRoutes.Add((oldRouteName, comp.Namespace ?? "default"));

                // wg-easy used to ship its own HTTPRoute inside the component manifest, under a
                // name derived from neither the service nor the hostname. Applying the replacement
                // route does not remove it, and while it survives it keeps the hostname answering
                // on the port-80 listener in cleartext — the exact thing the replacement fixes.
                foreach (string legacyName in LegacyManifestRouteNames(comp))
                {
                    orphanedRoutes.Add((legacyName, comp.Namespace ?? "default"));
                }

                FixRouteServiceName(r, comp);
            }
            allRoutes.AddRange(comp.ExternalRoutes);
        }

        // Persist any service-name corrections to the DB so subsequent applies don't re-add orphans.
        await db.SaveChangesAsync(ct);

        if (allRoutes.Count == 0)
        {
            return new HelmExecutionResult { Success = true, Output = "" };
        }

        if (string.IsNullOrWhiteSpace(component.Cluster.Kubeconfig))
        {
            return new HelmExecutionResult { Success = false, Output = "No kubeconfig stored for this cluster." };
        }

        (string gatewayName, string gatewayNamespace) = ExternalRouteService.ResolveGateway(
            component.Cluster.Components);

        // Include enabled AppRoutes on this cluster so the Gateway HTTPS listener list is
        // complete — applying ExternalRoutes must not drop AppRoute listeners.
        List<AppRoute> appRoutes = await db.AppRoutes
            .Include(r => r.ClientCaBundle!)
                .ThenInclude(b => b.Certificates)
            // The namespaces behind these routes have to be labelled before the Gateway lands,
            // or the listeners' namespace selector detaches them.
            .Include(r => r.DeploymentRoutes)
                .ThenInclude(dr => dr.AppDeployment)
            .Where(r => r.IsEnabled && r.DeploymentRoutes.Any(dr =>
                dr.IsEnabled && dr.AppDeployment.ClusterId == component.Cluster.Id))
            .ToListAsync(ct);

        string gatewayClass = ExternalRouteService.ResolveGatewayClass(component.Cluster.Components);

        // Gateway resource (HTTPS listeners + HTTP redirect + per-hostname Certificates
        // in the gateway namespace so the Gateway's certificateRefs can resolve them).
        string gatewayYaml = ExternalRouteService.GenerateGatewayYaml(
            gatewayName, gatewayNamespace, allRoutes, appRoutes, gatewayClass: gatewayClass);

        // A hostname can end up described twice: once as an AppRoute — one rule per path, with
        // the rewrites that put /gateway in front of one service and /engine in front of another —
        // and once as an ExternalRoute, which only knows how to say "this whole host goes to this
        // one service". Both generators name the object ToListenerName(hostname) + "-route", so
        // they are not two routes at all: they are two descriptions fighting over one object, and
        // whichever applied last wins.
        //
        // That fight is not hypothetical here. This method re-applies every ExternalRoute on the
        // cluster, so installing any unrelated component flattens such a hostname to a single
        // catch-all backend, and every path that backend does not serve starts answering 404 —
        // with nothing in the deploy that touched the app to explain it.
        //
        // The AppRoute is the fuller description and the post-deploy refresh re-applies it, so the
        // AppRoute owns the hostname and the ExternalRoute's flattened version is the one we drop.
        // Only its HTTPRoute is dropped: the hostname's Gateway listener is built from both lists
        // above and is unaffected, and a passthrough ExternalRoute produces a TLSRoute, a
        // different kind that never overwrites the AppRoute's HTTPRoute.
        IReadOnlySet<string> appRouteHostnames = HostnamesOwnedByAppRoutes(appRoutes);

        foreach (ExternalRoute skipped in allRoutes.Where(r => WouldOverwriteAppRoute(r, appRouteHostnames)))
        {
            // Worth saying out loud: the operator configured this ExternalRoute and it is
            // silently not being applied, which is only obvious once you know an AppRoute
            // already claims the hostname.
            logger.LogWarning(
                "Skipping HTTPRoute for ExternalRoute {Hostname}: an enabled AppRoute already "
                + "serves that hostname, and applying this route would replace its per-path "
                + "rules with a single backend.",
                skipped.Hostname);
        }

        // One route resource per remaining entry — HTTPRoute for terminated TLS, TLSRoute for
        // passthrough.
        IEnumerable<string> httpRoutes = allRoutes
            .Where(r => !WouldOverwriteAppRoute(r, appRouteHostnames))
            .Select(r =>
                r.TlsMode == TlsMode.Passthrough
                    ? ExternalRouteService.GenerateTlsRouteYaml(r)
                    : ExternalRouteService.GenerateHttpRouteYaml(r));

        List<string> documents = [gatewayYaml, .. httpRoutes];

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-routes-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, component.Cluster.Kubeconfig, ct);

            // On Istio, a backend port that serves TLS needs the gateway to originate TLS to it.
            // Without that the gateway connects in plaintext, the backend drops the connection and
            // the browser gets "upstream connect error ... reset reason: connection termination".
            // Nothing is emitted for services whose ports are all plaintext, so clusters that work
            // today are left exactly as they are.
            if (gatewayClass == "istio")
            {
                // Grouped by backend Service, not by route: one DestinationRule exists per
                // Service, so two routes onto the same Service would otherwise produce two
                // documents with the same name and the second would silently overwrite the
                // first — including its session affinity.
                IEnumerable<IGrouping<(string Namespace, string Service), ExternalRoute>> byService = allRoutes
                    .Where(r => r.TlsMode != TlsMode.Passthrough && !string.IsNullOrWhiteSpace(r.ServiceName))
                    .OrderBy(r => r.Hostname, StringComparer.Ordinal)
                    .GroupBy(r => (Namespace: r.Component?.Namespace ?? "default", Service: r.ServiceName!));

                foreach (IGrouping<(string Namespace, string Service), ExternalRoute> group in byService)
                {
                    List<KubeServicePort> ports = await GetServicePortsAsync(
                        tempKubeconfig, group.Key.Namespace, group.Key.Service, ct);

                    string destinationRule = ExternalRouteService.GenerateBackendDestinationRuleYaml(
                        group.Key.Service, group.Key.Namespace, gatewayNamespace, ports, alwaysEmit: false,
                        sessionAffinity: SessionAffinitySpec.Merge(group.Select(SessionAffinitySpec.From)));

                    if (destinationRule.Length > 0)
                    {
                        documents.Add(destinationRule);
                    }
                }
            }

            // Every namespace holding a route for this Gateway must carry the label its listeners
            // select on, and must carry it BEFORE the Gateway arrives — the alternative is a
            // window where the new listeners admit nothing and every hostname on the cluster 404s.
            // Additive and idempotent, so re-running costs nothing.
            HashSet<string> routeNamespaces = [
                .. allRoutes
                    .Select(r => r.Component?.Namespace)
                    .Where(ns => !string.IsNullOrWhiteSpace(ns))
                    .Select(ns => ns!),
                .. appRoutes
                    .SelectMany(r => r.DeploymentRoutes)
                    .Where(dr => dr.IsEnabled && dr.AppDeployment?.ClusterId == component.Cluster.Id)
                    .Select(dr => dr.AppDeployment?.Namespace)
                    .Where(ns => !string.IsNullOrWhiteSpace(ns))
                    .Select(ns => ns!),
            ];

            foreach (string routeNs in routeNamespaces)
            {
                await RunProcessAsync("kubectl",
                    $"label namespace {routeNs} " +
                    $"{ExternalRouteService.RouteNamespaceLabel}={ExternalRouteService.RouteNamespaceLabelValue} " +
                    $"--overwrite --kubeconfig {tempKubeconfig}", ct);
            }

            string combinedYaml = string.Join("\n---\n", documents);
            await File.WriteAllTextAsync(tempManifest, combinedYaml, ct);

            // Delete orphaned service-name-based HTTPRoutes before applying hostname-based ones.
            foreach ((string oldName, string ns) in orphanedRoutes)
            {
                await RunProcessAsync("kubectl",
                    $"delete httproute {oldName} -n {ns} --kubeconfig {tempKubeconfig} --ignore-not-found", ct);
            }

            return await RunProcessAsync("kubectl", $"apply -f {tempManifest} --kubeconfig {tempKubeconfig}", ct);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
        }
    }

    private async Task<HelmExecutionResult> ExecuteKubectlUrlAsync(
        HelmCommand command, string kubeconfigPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.ManifestUrl))
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = "No manifest URL configured for this component."
            };
        }

        // Server-side apply, always. A client-side `kubectl apply` stores the entire submitted
        // document in the kubectl.kubernetes.io/last-applied-configuration annotation, and an
        // annotation may not exceed 262144 bytes. The Gateway API HTTPRoute CRD is larger than
        // that on its own, so upgrading those CRDs client-side fails with:
        //
        //   The CustomResourceDefinition "httproutes.gateway.networking.k8s.io" is invalid:
        //   metadata.annotations: Too long: may not be more than 262144 bytes
        //
        // Worse than failing: it fails PARTWAY. The smaller CRDs in the same bundle apply
        // cleanly first, so the cluster is left with some resources at the new version and the
        // rest at the old one, while the component reports an error nobody reads as "your CRDs
        // are now mixed-version". Server-side apply writes no such annotation and has no
        // equivalent ceiling.
        //
        // --force-conflicts takes ownership of fields last written by a client-side apply. Without
        // it, the first server-side apply over a client-side-managed resource is rejected as a
        // field-manager conflict — which is exactly the state every existing install is in.
        string arguments =
            $"apply --server-side --force-conflicts -f {command.ManifestUrl} --kubeconfig {kubeconfigPath}";
        return await RunProcessAsync("kubectl", arguments, ct);
    }

    /// <summary>
    /// Executes a Helm CLI command — handles repo add, upgrade --install, or uninstall.
    /// When no repo URL is configured for an existing release, extracts the chart
    /// from the Helm release secret on the cluster and uses it as a local chart path.
    /// </summary>
    private async Task<HelmExecutionResult> ExecuteHelmCliAsync(
        HelmCommand command, string kubeconfigPath, string kubeconfig, CancellationToken ct)
    {
        // Build the helm command arguments.

        List<string> args = [command.Operation];
        int chartRefIndex = -1;

        if (command.Operation == "uninstall")
        {
            args.Add(command.ReleaseName);
        }
        else
        {
            args.Add(command.ReleaseName);
            if (!string.IsNullOrWhiteSpace(command.ChartReference))
            {
                chartRefIndex = args.Count;
                args.Add(command.ChartReference);
            }
        }

        if (!string.IsNullOrWhiteSpace(command.Namespace))
        {
            args.Add("--namespace");
            args.Add(command.Namespace);

            if (command.Operation != "uninstall")
            {
                args.Add("--create-namespace");
            }
        }

        if (!string.IsNullOrWhiteSpace(command.Version))
        {
            args.Add("--version");
            args.Add(command.Version);
        }

        // If there are custom values, write them to a temp file.

        string? tempValuesFile = null;
        string? tempChartDir = null;

        try
        {
            if (command.HasValues && !string.IsNullOrWhiteSpace(command.ValuesYaml))
            {
                tempValuesFile = Path.Combine(Path.GetTempPath(), $"entkube-values-{Guid.NewGuid()}.yaml");
                await File.WriteAllTextAsync(tempValuesFile, command.ValuesYaml, ct);
                args.Add("--values");
                args.Add(tempValuesFile);
            }

            args.Add("--kubeconfig");
            args.Add(kubeconfigPath);
            if (!command.NoWait)
            {
                args.Add("--wait");
                args.Add("--timeout");
                args.Add(command.Timeout);
            }

            // If there's a repo URL, add the repo first and resolve the chart reference.
            //
            // OCI registries are the exception: they are NOT chart repositories and `helm repo add` on one
            // fails with "not a valid chart repository or cannot be reached ... invalid reference". A chart
            // in a registry is addressed directly as oci://<registry>/<path>/<chart> with --version, which
            // is exactly the reference already built above — so for OCI there is nothing to add and nothing
            // to rewrite, and doing either is what breaks the install.
            bool isOciRepo = command.RepoUrl?.StartsWith("oci://", StringComparison.OrdinalIgnoreCase) == true;

            if (isOciRepo && command.Operation != "uninstall")
            {
                // A private registry needs an authenticated helm session before the chart can be pulled.
                // Anonymous pull works for public registries, so this is best-effort: an unconfigured
                // registry is not an error here, it is simply one we have no credentials for. When the pull
                // then fails on 401, the helm error says so plainly, which is more useful than refusing up
                // front on a registry that might not have needed credentials at all.
                await EnsureOciRegistryLoginAsync(command.RepoUrl!, ct);
            }

            if (!string.IsNullOrWhiteSpace(command.RepoUrl) && command.Operation != "uninstall" && !isOciRepo)
            {
                string repoName = $"entkube-{command.ReleaseName}";

                // repo add/update are local operations — no kubeconfig needed or wanted.
                HelmExecutionResult repoAddResult = await RunProcessAsync(
                    "helm", $"repo add {repoName} {command.RepoUrl} --force-update", ct);
                if (!repoAddResult.Success)
                {
                    return new HelmExecutionResult
                    {
                        Success = false,
                        Output = $"Failed to add Helm repo '{repoName}' ({command.RepoUrl}):\n{repoAddResult.Output}"
                    };
                }

                await RunProcessAsync("helm", $"repo update {repoName}", ct);

                // Replace the chart reference with repo/chart format.
                if (chartRefIndex >= 0)
                {
                    args[chartRefIndex] = $"{repoName}/{command.ChartReference!.Split('/').Last()}";
                }
            }
            else if (string.IsNullOrWhiteSpace(command.RepoUrl)
                     && !isOciRepo
                     && command.Operation != "uninstall"
                     && !string.IsNullOrWhiteSpace(command.ChartReference)
                     && !command.ChartReference.Contains('/')
                     && !command.ChartReference.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
            {
                // No repo URL and the chart reference is a bare name (e.g. "kube-prometheus-stack").
                // Extract the chart from the existing Helm release secret on the cluster
                // so we can use it as a local chart directory for the upgrade.

                tempChartDir = await ExtractChartFromReleaseAsync(
                    kubeconfig, command.ReleaseName, command.Namespace, ct);

                if (tempChartDir is not null && chartRefIndex >= 0)
                {
                    args[chartRefIndex] = tempChartDir;
                }
            }

            // Ensure the namespace carries the default LimitRange before installing, so any
            // pod the chart creates without its own resources (subchart pods, Helm hook Jobs,
            // injected sidecars) is admitted with defaults on clusters that require limits.
            if (command.Operation != "uninstall" && !string.IsNullOrWhiteSpace(command.Namespace))
            {
                await EnsureNamespaceDefaultsAsync(command.Namespace, kubeconfigPath, ct);
            }

            string arguments = string.Join(" ", args);
            return await RunProcessAsync("helm", arguments, ct);
        }
        finally
        {
            if (tempValuesFile is not null && File.Exists(tempValuesFile))
            {
                File.Delete(tempValuesFile);
            }

            if (tempChartDir is not null && Directory.Exists(tempChartDir))
            {
                Directory.Delete(tempChartDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Logs helm into an OCI registry when credentials for that host are configured, so a chart in a
    /// private registry can be pulled.
    ///
    /// Credentials come from the <c>Helm:Registries</c> configuration section, keyed by host:
    /// <code>Helm__Registries__entit_azurecr_io__Username</code> (dots in the host become underscores,
    /// because a configuration key cannot contain a colon-delimited segment with dots in every provider).
    /// Nothing happens when the host is not configured — public registries need no login, and refusing to
    /// proceed would break them.
    ///
    /// The login is written to helm's own config and persists for the life of the container, so repeating
    /// it per install costs one cheap call and keeps the code stateless — which matters because the
    /// management plane is a container that can be replaced at any time.
    /// </summary>
    private async Task EnsureOciRegistryLoginAsync(string repoUrl, CancellationToken ct)
    {
        string host;
        try
        {
            host = new Uri(repoUrl).Host;
        }
        catch (UriFormatException)
        {
            return;   // malformed URL; the helm command itself will report it far more clearly
        }

        if (string.IsNullOrEmpty(host) || !LoggedInRegistries.TryAdd(host, 0)) return;

        string keyed = host.Replace('.', '_');
        string? username = configuration[$"Helm:Registries:{keyed}:Username"];
        string? password = configuration[$"Helm:Registries:{keyed}:Password"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogDebug(
                "No helm credentials configured for OCI registry {Host}; attempting an anonymous pull. "
                + "Set Helm__Registries__{Keyed}__Username and __Password if it is private.", host, keyed);
            return;
        }

        // Password on stdin, never in the argument list — process arguments are readable by any process
        // on the host and routinely land in logs.
        HelmExecutionResult result = await RunProcessAsync(
            "helm", $"registry login {host} --username {username} --password-stdin", ct, stdin: password);

        if (result.Success)
        {
            logger.LogInformation("Logged helm in to OCI registry {Host}.", host);
        }
        else
        {
            // Not fatal: the pull may still succeed anonymously, and if it does not, helm's own error is
            // the one worth surfacing.
            LoggedInRegistries.TryRemove(host, out _);   // let the next install retry the login
            logger.LogWarning("helm registry login for {Host} failed: {Output}", host, result.Output);
        }
    }

    /// <summary>
    /// Name of the image-pull Secret EntKube creates and maintains in a release namespace.
    /// Fixed rather than configurable: EntKube owns this Secret, and a name an operator could change is a
    /// name that drifts out of step with the <c>imagePullSecrets</c> value written alongside it.
    /// </summary>
    public const string ManagedPullSecretName = "entkube-registry";

    /// <summary>
    /// Ensures the cluster can pull an EntKube-published image, by creating a <c>dockerconfigjson</c>
    /// Secret in the release namespace from EntKube's own registry credentials.
    ///
    /// This is a different pull from the one EntKube makes for the chart. The <b>kubelet in the managed
    /// cluster</b> fetches the image, so EntKube's registry session does not help it — the cluster needs
    /// its own credential, and without one the chart installs cleanly and every pod then sits in
    /// ImagePullBackOff. Requiring an operator to hand-build that Secret per cluster is exactly the kind of
    /// step this platform exists to remove, so it is created from the same configuration that already
    /// authenticates the chart pull.
    ///
    /// <para>Does nothing when no credentials are configured for the host. That is the correct behaviour
    /// for a public registry — every third-party component in the catalog pulls anonymously — and it means
    /// publishing these images publicly later needs no code change, only the credentials removed.</para>
    ///
    /// Returns the Secret's name when one was ensured, so the caller can reference it in the chart values.
    /// </summary>
    private async Task<string?> EnsureImagePullSecretAsync(
        ClusterComponent component, CatalogEntry? catalog, string kubeconfig, CancellationToken ct)
    {
        string? host = catalog?.ImageRegistryHost;
        if (string.IsNullOrWhiteSpace(host)) return null;

        string keyed = host.Replace('.', '_');
        string? username = configuration[$"Helm:Registries:{keyed}:Username"];
        string? password = configuration[$"Helm:Registries:{keyed}:Password"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogDebug(
                "No registry credentials configured for {Host}; assuming its images pull anonymously. "
                + "Set Helm__Registries__{Keyed}__Username and __Password if they do not.", host, keyed);
            return null;
        }

        string ns = component.Namespace ?? "default";

        // The docker config the kubelet reads. "auth" is the base64 of user:password, which is what the
        // Docker credential format expects — username/password alone are not enough for some registries.
        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        string dockerConfig = JsonSerializer.Serialize(new
        {
            auths = new Dictionary<string, object>
            {
                [host] = new { username, password, auth },
            },
        });

        string manifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {ManagedPullSecretName}
              namespace: {ns}
              labels:
                app.kubernetes.io/managed-by: entkube
            type: kubernetes.io/dockerconfigjson
            data:
              .dockerconfigjson: {Convert.ToBase64String(Encoding.UTF8.GetBytes(dockerConfig))}
            """;

        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-pull-{Guid.NewGuid()}.kubeconfig");
        string manifestPath = Path.Combine(Path.GetTempPath(), $"entkube-pull-{Guid.NewGuid()}.yaml");
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            // Written to a file rather than passed as --from-literal: process arguments are readable by
            // any process on the host and routinely end up in logs.
            await File.WriteAllTextAsync(manifestPath, manifest, ct);

            // The Secret has to exist before Helm creates the pods that reference it, and the namespace
            // before the Secret. Both are idempotent.
            await RunProcessAsync("kubectl", $"create namespace {ns} --kubeconfig {kubeconfigPath}", ct);

            HelmExecutionResult applied = await RunProcessAsync(
                "kubectl", $"apply -f {manifestPath} --kubeconfig {kubeconfigPath}", ct);

            if (!applied.Success)
            {
                // Not fatal: a pull secret may already exist by another name, or the registry may permit
                // anonymous pulls after all. Helm's own failure is the more useful one to surface.
                logger.LogWarning(
                    "Could not create the image-pull Secret {Secret} in {Namespace}: {Output}",
                    ManagedPullSecretName, ns, applied.Output);
                return null;
            }

            logger.LogInformation(
                "Ensured image-pull Secret {Secret} in {Namespace} for registry {Host}.",
                ManagedPullSecretName, ns, host);
            return ManagedPullSecretName;
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// Ensures the target namespace exists and carries the EntKube default LimitRange.
    /// The LimitRange injects CPU/memory <c>defaultRequest</c> and <c>default</c> (limit)
    /// values into every container that doesn't set its own, so pods are admitted on
    /// clusters that enforce resource limits — including subchart pods, Helm hook Jobs and
    /// injected sidecars that per-chart Helm values can't reach. Applied before the install
    /// so pods created during --wait pass admission. Idempotent (kubectl apply).
    /// </summary>
    private static async Task EnsureNamespaceDefaultsAsync(string ns, string kubeconfigPath, CancellationToken ct)
    {
        // Create the namespace up front (helm --create-namespace would otherwise make it,
        // but the LimitRange must exist before any pod is admitted). Ignore AlreadyExists.
        await RunProcessAsync("kubectl", $"create namespace {ns} --kubeconfig {kubeconfigPath}", ct);

        // Containers that set their own requests/limits keep them; this only fills the gaps.
        string limitRange = $$"""
            apiVersion: v1
            kind: LimitRange
            metadata:
              name: entkube-defaults
              namespace: {{ns}}
            spec:
              limits:
                - type: Container
                  defaultRequest:
                    cpu: 50m
                    memory: 128Mi
                  default:
                    cpu: "1"
                    memory: 1Gi
            """;

        string tempFile = Path.Combine(Path.GetTempPath(), $"entkube-limitrange-{Guid.NewGuid()}.yaml");
        try
        {
            await File.WriteAllTextAsync(tempFile, limitRange, ct);
            await RunProcessAsync("kubectl", $"apply -f {tempFile} --kubeconfig {kubeconfigPath}", ct);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Extracts the full chart from a Helm release secret stored on the cluster.
    /// Helm stores the chart (metadata, templates, default values) inside the release
    /// data, so we can reconstruct a local chart directory without needing the repo URL.
    /// Returns the path to a temp chart directory, or null if extraction failed.
    /// </summary>
    private static async Task<string?> ExtractChartFromReleaseAsync(
        string kubeconfig, string releaseName, string? ns, CancellationToken ct)
    {
        try
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));
            KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
            using Kubernetes client = new(config);

            // Helm stores releases as secrets with label owner=helm, name=<release>.
            // The latest revision is the one with the highest version number.

            string targetNs = ns ?? "default";
            V1SecretList secrets = await client.ListNamespacedSecretAsync(
                targetNs,
                labelSelector: $"owner=helm,name={releaseName}",
                cancellationToken: ct);

            if (secrets.Items.Count == 0)
            {
                return null;
            }

            // Find the latest revision by sorting on the "version" label.

            V1Secret latest = secrets.Items
                .OrderByDescending(s =>
                    s.Metadata.Labels.TryGetValue("version", out string? v) && int.TryParse(v, out int ver) ? ver : 0)
                .First();

            if (latest.Data is null || !latest.Data.TryGetValue("release", out byte[]? rawData) || rawData is null)
            {
                return null;
            }

            // Decode: UTF-8 → base64 → gzip → JSON (same format as ComponentScanService).

            string helmBase64 = Encoding.UTF8.GetString(rawData);
            byte[] gzipped = Convert.FromBase64String(helmBase64);

            using MemoryStream compressedStream = new(gzipped);
            using GZipStream gzipStream = new(compressedStream, CompressionMode.Decompress);
            using MemoryStream decompressedStream = new();
            await gzipStream.CopyToAsync(decompressedStream, ct);
            byte[] jsonBytes = decompressedStream.ToArray();

            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("chart", out JsonElement chart))
            {
                return null;
            }

            // Create a temp chart directory and write Chart.yaml + templates.

            string chartDir = Path.Combine(Path.GetTempPath(), $"entkube-chart-{Guid.NewGuid()}");
            Directory.CreateDirectory(chartDir);

            // Write Chart.yaml from metadata.

            if (chart.TryGetProperty("metadata", out JsonElement metadata))
            {
                StringBuilder chartYaml = new();
                chartYaml.AppendLine($"apiVersion: v2");

                if (metadata.TryGetProperty("name", out JsonElement name))
                {
                    chartYaml.AppendLine($"name: {name.GetString()}");
                }

                if (metadata.TryGetProperty("version", out JsonElement version))
                {
                    chartYaml.AppendLine($"version: {version.GetString()}");
                }

                if (metadata.TryGetProperty("appVersion", out JsonElement appVersion))
                {
                    chartYaml.AppendLine($"appVersion: \"{appVersion.GetString()}\"");
                }

                if (metadata.TryGetProperty("description", out JsonElement desc))
                {
                    chartYaml.AppendLine($"description: {desc.GetString()}");
                }

                if (metadata.TryGetProperty("type", out JsonElement type))
                {
                    chartYaml.AppendLine($"type: {type.GetString()}");
                }

                await File.WriteAllTextAsync(
                    Path.Combine(chartDir, "Chart.yaml"), chartYaml.ToString(), ct);
            }

            // Write default values.yaml.

            if (chart.TryGetProperty("values", out JsonElement values))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(chartDir, "values.yaml"), values.GetRawText(), ct);
            }

            // Write templates.

            if (chart.TryGetProperty("templates", out JsonElement templates)
                && templates.ValueKind == JsonValueKind.Array)
            {
                string templatesDir = Path.Combine(chartDir, "templates");
                Directory.CreateDirectory(templatesDir);

                foreach (JsonElement tmpl in templates.EnumerateArray())
                {
                    string? tmplName = tmpl.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? tmplData = tmpl.TryGetProperty("data", out JsonElement d) ? d.GetString() : null;

                    if (tmplName is null || tmplData is null)
                    {
                        continue;
                    }

                    // Template data is base64-encoded.

                    byte[] tmplBytes = Convert.FromBase64String(tmplData);
                    string tmplPath = Path.Combine(templatesDir, tmplName.Replace("templates/", ""));
                    string? tmplDir = Path.GetDirectoryName(tmplPath);

                    if (tmplDir is not null)
                    {
                        Directory.CreateDirectory(tmplDir);
                    }

                    await File.WriteAllBytesAsync(tmplPath, tmplBytes, ct);
                }
            }

            // Write CRDs if present.

            if (chart.TryGetProperty("files", out JsonElement files)
                && files.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement file in files.EnumerateArray())
                {
                    string? fileName = file.TryGetProperty("name", out JsonElement fn) ? fn.GetString() : null;
                    string? fileData = file.TryGetProperty("data", out JsonElement fd) ? fd.GetString() : null;

                    if (fileName is null || fileData is null)
                    {
                        continue;
                    }

                    byte[] fileBytes = Convert.FromBase64String(fileData);
                    string filePath = Path.Combine(chartDir, fileName);
                    string? fileDir = Path.GetDirectoryName(filePath);

                    if (fileDir is not null)
                    {
                        Directory.CreateDirectory(fileDir);
                    }

                    await File.WriteAllBytesAsync(filePath, fileBytes, ct);
                }
            }

            return chartDir;
        }
        catch
        {
            // If extraction fails for any reason, return null so the caller
            // proceeds with the bare chart name (which will likely fail with
            // a clear Helm error message).
            return null;
        }
    }

    /// <summary>
    /// Returns the names of all cert-manager ClusterIssuer resources on the cluster.
    /// Used to populate ClusterIssuer selector dropdowns in the UI.
    /// Returns an empty list if kubectl fails or cert-manager is not installed.
    /// </summary>
    public async Task<List<string>> ListClusterIssuersAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return [];

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, cluster.Kubeconfig, ct);

            HelmExecutionResult result = await RunProcessAsync(
                "kubectl",
                $"get clusterissuers.cert-manager.io -o json --kubeconfig {tempKubeconfig}",
                ct);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return [];

            return ParseJsonResourceNames(result.Output);
        }
        catch
        {
            return [];
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    private static List<string> ParseJsonResourceNames(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items))
                return [];

            return items.EnumerateArray()
                .Select(item => item.GetProperty("metadata").GetProperty("name").GetString() ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Runs a CLI process (helm or kubectl) and captures its output.
    /// </summary>
    /// <summary>
    /// Reads a Service's ports with kubectl, for deciding how the Istio gateway should connect to
    /// each of them. Returns an empty list when the service can't be read or parsed — callers must
    /// read that as "nothing known about the ports", never as "no TLS ports".
    /// </summary>
    private static async Task<List<KubeServicePort>> GetServicePortsAsync(
        string kubeconfigPath, string ns, string serviceName, CancellationToken ct)
    {
        HelmExecutionResult result = await RunProcessAsync("kubectl",
            $"get svc {serviceName} -n {ns} -o json --kubeconfig {kubeconfigPath}", ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(result.Output);
            if (!doc.RootElement.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                || !spec.TryGetProperty("ports", out System.Text.Json.JsonElement ports)
                || ports.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }

            List<KubeServicePort> parsed = [];
            foreach (System.Text.Json.JsonElement port in ports.EnumerateArray())
            {
                if (!port.TryGetProperty("port", out System.Text.Json.JsonElement number)
                    || !number.TryGetInt32(out int portNumber))
                {
                    continue;
                }

                parsed.Add(new KubeServicePort(
                    port.TryGetProperty("name", out System.Text.Json.JsonElement name) ? name.GetString() : null,
                    portNumber,
                    port.TryGetProperty("protocol", out System.Text.Json.JsonElement proto) ? proto.GetString() ?? "TCP" : "TCP",
                    port.TryGetProperty("appProtocol", out System.Text.Json.JsonElement app) ? app.GetString() : null));
            }

            return parsed;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <param name="stdin">Written to the process's standard input and then closed. Used for secrets —
    /// a password in <paramref name="arguments"/> is readable by any process on the host and lands in
    /// logs, so anything sensitive comes through here instead.</param>
    private static async Task<HelmExecutionResult> RunProcessAsync(
        string program, string arguments, CancellationToken ct, string? stdin = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = program,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["HOME"] = "/tmp";

        using Process process = new() { StartInfo = psi };

        try
        {
            process.Start();

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            // Always combine stdout and stderr so errors aren't silently dropped.
            // helm prints informational messages (e.g. "Installing it now.") to stdout
            // and errors (timeouts, render failures) to stderr. If we only show stdout,
            // the real failure reason is invisible.
            string combined = (stdout.Trim() + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim())).Trim();

            return new HelmExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = HelmExecutionResult.DescribeLaunchFailure(program, ex)
            };
        }
    }

    /// <summary>
    /// Corrects the ServiceName on a route so it matches the actual Kubernetes Service
    /// created by the Helm chart, which may differ from the bare release name.
    ///
    /// keycloakx: Helm creates "{releaseName}-keycloakx", not "{releaseName}".
    /// Older routes stored just the release name — this fixes them at apply time.
    /// </summary>
    private static void FixRouteServiceName(ExternalRoute route, ClusterComponent comp)
    {
        if (comp.HelmChartName != "keycloakx")
            return;

        string releaseName = comp.ReleaseName ?? comp.Name;
        // keycloakx chart creates two services: {rel}-keycloakx-headless (headless/StatefulSet)
        // and {rel}-keycloakx-http (ClusterIP with ports 80/8443/9000). Route to the latter.
        string expected = $"{releaseName}-keycloakx-http";

        if (!string.Equals(route.ServiceName, expected, StringComparison.OrdinalIgnoreCase))
            route.ServiceName = expected;
    }
}

/// <summary>
/// Result of executing a Helm CLI command against a cluster.
/// </summary>
public class HelmExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";

    /// <summary>
    /// Explains why an external CLI could not be launched.
    ///
    /// A missing binary surfaces as a bare ENOENT whose message names the working directory and
    /// not the executable ("...with working directory '/app'. No such file or directory"), which
    /// reads as a path problem inside the app rather than a tool absent from the image. Saying so
    /// outright turns it into something an operator can act on.
    /// </summary>
    public static string DescribeLaunchFailure(string program, Exception ex)
    {
        bool notFound = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 };

        return notFound
            ? $"'{program}' is not installed in the EntKube container image, so this operation "
              + $"cannot run. Rebuild or update the image, then verify with: "
              + $"docker run --rm --entrypoint {program} <image> version"
            : $"Failed to run {program}: {ex.Message}";
    }
}
