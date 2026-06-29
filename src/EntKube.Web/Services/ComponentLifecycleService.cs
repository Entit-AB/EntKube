using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BCrypt.Net;
using EntKube.Web.Data;
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
public class ComponentLifecycleService(IDbContextFactory<ApplicationDbContext> dbFactory, VaultService vaultService)
{
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
        string? helmRepoUrl = null, string? configuration = null, CancellationToken ct = default)
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

        if (configuration is not null)
        {
            component.Configuration = configuration;
        }

        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Validates that a component is ready to install and transitions it to
    /// Installing status. This is the gatekeeper — if the component doesn't
    /// have the minimum required info (chart name, namespace), we reject early
    /// rather than failing mid-install.
    /// </summary>
    public async Task<ClusterComponent> PrepareInstallAsync(
        Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // Only NotInstalled or Failed components can be installed.
        // If it's already installed, the user should use upgrade instead.

        if (component.Status == ComponentStatus.Installed)
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

        CatalogEntry? entry = ComponentCatalog.GetByKey(component.Name);

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

        CatalogEntry? entry = ComponentCatalog.GetByKey(component.Name);

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
            return new HelmCommand
            {
                Operation = "kubectl-apply-url",
                ReleaseName = component.ReleaseName ?? component.Name,
                ManifestUrl = component.HelmRepoUrl
            };
        }

        // Resolve any vault secrets and inject them into the values YAML.
        // Secret form fields (like Grafana admin password) are stored encrypted
        // in the vault rather than in plain text in HelmValues. At install time,
        // we decrypt them and merge into the YAML so Helm gets the full picture.

        string? valuesYaml = await InjectSecretsIntoValuesAsync(component, ct);

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

        string releaseName = component.ReleaseName ?? component.Name;
        string chartRef = !string.IsNullOrWhiteSpace(component.HelmRepoUrl)
            ? $"{component.HelmRepoUrl}/{component.HelmChartName}"
            : component.HelmChartName ?? component.Name;

        // Catalog-registered components keep their key as the component Name, so we can
        // look the entry back up for an extended install timeout (heavy/DaemonSet charts).
        string installTimeout = ComponentCatalog.GetByKey(component.Name)?.InstallTimeout ?? "10m0s";

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

    private async Task<string> SubstituteManifestPlaceholdersAsync(
        string manifestYaml, ClusterComponent component, CancellationToken ct)
    {
        CatalogEntry? catalog = ComponentCatalog.GetByKey(component.Name);
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

        CatalogEntry? catalog = ComponentCatalog.GetByKey(component.Name);

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
        CatalogEntry? catalog = ComponentCatalog.GetByKey(component.Name);
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
            .Where(r => r.IsEnabled && r.DeploymentRoutes.Any(dr =>
                dr.IsEnabled && dr.AppDeployment.ClusterId == component.Cluster.Id))
            .ToListAsync(ct);

        string gatewayClass = ExternalRouteService.ResolveGatewayClass(component.Cluster.Components);

        // Gateway resource (HTTPS listeners + HTTP redirect + per-hostname Certificates
        // in the gateway namespace so the Gateway's certificateRefs can resolve them).
        string gatewayYaml = ExternalRouteService.GenerateGatewayYaml(
            gatewayName, gatewayNamespace, allRoutes, appRoutes, gatewayClass: gatewayClass);

        // One route resource per entry — HTTPRoute for terminated TLS, TLSRoute for passthrough.
        IEnumerable<string> httpRoutes = allRoutes.Select(r =>
            r.TlsMode == TlsMode.Passthrough
                ? ExternalRouteService.GenerateTlsRouteYaml(r)
                : ExternalRouteService.GenerateHttpRouteYaml(r));

        string combinedYaml = string.Join("\n---\n", new[] { gatewayYaml }.Concat(httpRoutes));

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-routes-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, component.Cluster.Kubeconfig, ct);
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

        string arguments = $"apply -f {command.ManifestUrl} --kubeconfig {kubeconfigPath}";
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

            if (!string.IsNullOrWhiteSpace(command.RepoUrl) && command.Operation != "uninstall")
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
    private static async Task<HelmExecutionResult> RunProcessAsync(
        string program, string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = program,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["HOME"] = "/tmp";

        using Process process = new() { StartInfo = psi };

        try
        {
            process.Start();

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
                Output = $"Failed to run {program}: {ex.Message}"
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
}
