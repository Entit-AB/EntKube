using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// What already serves an aggregated API on a cluster: the server a catalog component would
/// otherwise install.
/// </summary>
/// <param name="ApiService">The APIService name, e.g. <c>v1beta1.metrics.k8s.io</c>.</param>
/// <param name="Namespace">Namespace of the Service behind it; null when the API server serves it itself.</param>
/// <param name="ServiceName">That Service's name; null when the API server serves it itself.</param>
/// <param name="ServiceExists">False when the APIService names a Service that is not there: a stale registration.</param>
/// <param name="HelmRelease">The Helm release that owns the Service, when one does.</param>
/// <param name="HelmReleaseNamespace">That release's namespace.</param>
/// <param name="Available">Whether the API server reports the APIService as Available.</param>
/// <param name="UnavailableReason">The Available condition's message when it is not.</param>
public sealed record ExistingProvider(
    string ApiService,
    string? Namespace,
    string? ServiceName,
    bool ServiceExists,
    string? HelmRelease,
    string? HelmReleaseNamespace,
    bool Available,
    string? UnavailableReason)
{
    /// <summary>"namespace/service", or a phrase when the API server serves it itself.</summary>
    public string Where => ServiceName is null ? "the API server itself" : $"{Namespace}/{ServiceName}";

    /// <summary>Who installed it, as far as the cluster says.</summary>
    public string Owner => HelmRelease is not null
        ? $"Helm release '{HelmRelease}' in namespace '{HelmReleaseNamespace}'"
        : "the cluster itself (not a Helm release: a provider add-on or part of the distribution)";
}

/// <summary>What an install of a component the cluster may already provide should do.</summary>
public abstract record ProvidedDecision
{
    private ProvidedDecision() { }

    /// <summary>Nothing else serves the API, or what serves it is this component's own release.</summary>
    public sealed record Install : ProvidedDecision;

    /// <summary>Something else serves it: record that server instead of installing another.</summary>
    public sealed record Track(ExistingProvider Provider) : ProvidedDecision;

    /// <summary>The API is registered to a Service that does not exist; an install would fail on it.</summary>
    public sealed record Refuse(string Reason) : ProvidedDecision;
}

/// <summary>
/// Installs of components that a cluster very often already has (<see cref="CatalogEntry.ProvidedByApiService"/>),
/// most of all metrics-server on managed Kubernetes.
///
/// <para>Only one server can back an aggregated API, so a second install fails on the existing
/// APIService; on a blueprint that is a failed bootstrap step on every managed cluster. The
/// APIService is cluster-scoped and names the Service behind it, so it locates the existing server
/// whichever namespace it runs in. When one exists and is not this component's own release, the
/// component is recorded as <see cref="ComponentType"/>: installed, in that namespace, and never
/// touched by Helm. EntKube did not install it and must not upgrade or uninstall it.</para>
/// </summary>
public class ClusterProvidedComponents(
    IDbContextFactory<CatalogDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients,
    ILogger<ClusterProvidedComponents> logger)
{
    /// <summary>
    /// <see cref="ClusterComponent.ComponentType"/> of a component the cluster provides and
    /// EntKube only tracks. Lifecycle commands treat it as nothing to install and nothing to uninstall.
    /// </summary>
    public const string ComponentType = "ClusterProvided";

    private const string HelmReleaseNameAnnotation = "meta.helm.sh/release-name";
    private const string HelmReleaseNamespaceAnnotation = "meta.helm.sh/release-namespace";

    /// <summary>
    /// Run before an install or upgrade. Returns the result to report instead of installing, or
    /// null to carry on with the install.
    /// </summary>
    public async Task<HelmExecutionResult?> TrackIfProvidedAsync(
        Guid tenantId, Guid componentId, bool isUpgrade, CancellationToken ct = default)
    {
        // The cluster read is the only part not exercised by tests: a one-line hand-over to the SDK.
        return await TrackIfProvidedAsync(tenantId, componentId, isUpgrade, async (clusterId, apiService, token) =>
        {
            EntKube.Web.Services.Clusters.IClusterClient? cluster = await clusterClients.ForAsync(tenantId, clusterId, token);
            return cluster is null ? null : await LocateAsync(cluster.GetPooledSdkClient(), apiService, token);
        }, ct);
    }

    /// <summary>
    /// <see cref="TrackIfProvidedAsync(Guid, Guid, bool, CancellationToken)"/> with the cluster read
    /// supplied, so the decision and what it records can be tested without a cluster.
    /// </summary>
    public async Task<HelmExecutionResult?> TrackIfProvidedAsync(
        Guid tenantId, Guid componentId, bool isUpgrade,
        Func<Guid, string, CancellationToken, Task<ExistingProvider?>> locate, CancellationToken ct)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == componentId && c.Cluster.TenantId == tenantId, ct);

        CatalogEntry? entry = component is null
            ? null
            : ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

        if (component is null || entry?.ProvidedByApiService is not { } apiService)
        {
            return null;
        }

        bool tracked = component.ComponentType == ComponentType;

        // An upgrade of EntKube's own release is a helm upgrade like any other. Only a tracked
        // component is re-examined, since whatever provided it may have gone.
        if (isUpgrade && !tracked)
        {
            return null;
        }

        ExistingProvider? found;

        try
        {
            found = await locate(component.ClusterId, apiService, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable (permissions, unreachable): let the install run. If something does serve the
            // API, Helm refuses on the existing APIService and says so; nothing is overwritten.
            logger.LogWarning(ex, "Could not check whether {ApiService} is already served on cluster {ClusterId}",
                apiService, component.ClusterId);
            return null;
        }

        switch (Decide(component, found))
        {
            case ProvidedDecision.Track track:
                RecordAsProvided(component, track.Provider);
                await db.SaveChangesAsync(ct);
                return new HelmExecutionResult { Success = true, Output = DescribeTracking(entry, track.Provider) };

            case ProvidedDecision.Refuse refuse:
                return new HelmExecutionResult { Success = false, Output = refuse.Reason };

            default:
                if (tracked)
                {
                    // It was the cluster's, and the cluster no longer serves it: install the catalog's own.
                    RecordAsCatalogInstall(component, entry);
                    await db.SaveChangesAsync(ct);
                }

                return null;
        }
    }

    /// <summary>Install, track what is there, or refuse, for one component and what serves its API.</summary>
    public static ProvidedDecision Decide(ClusterComponent component, ExistingProvider? found)
    {
        if (found is null)
        {
            return new ProvidedDecision.Install();
        }

        if (found.ServiceName is not null && !found.ServiceExists)
        {
            return new ProvidedDecision.Refuse(
                $"The cluster registers the {found.ApiService} API to the Service {found.Where}, which does not exist, "
                + "so the API is broken and an install would fail on the existing registration. If nothing "
                + $"should serve it any more, delete the registration (kubectl delete apiservice {found.ApiService}) "
                + "and install again.");
        }

        // EntKube's own release: helm upgrade --install is the right command for it.
        bool ownRelease = component.ComponentType != ComponentType
            && found.HelmRelease is not null
            && string.Equals(found.HelmRelease, component.ReleaseName ?? component.Name, StringComparison.Ordinal)
            && string.Equals(found.HelmReleaseNamespace, component.Namespace, StringComparison.Ordinal);

        return ownRelease ? new ProvidedDecision.Install() : new ProvidedDecision.Track(found);
    }

    /// <summary>Records the component as the cluster's: installed, where it runs, and outside Helm's reach.</summary>
    public static void RecordAsProvided(ClusterComponent component, ExistingProvider provider)
    {
        component.ComponentType = ComponentType;
        component.Status = ComponentStatus.Installed;
        component.Namespace = provider.Namespace ?? component.Namespace;
        // A release name is how lifecycle commands find what to act on. This one names someone
        // else's release, kept so the Helm scan sees it as tracked and does not offer it twice.
        component.ReleaseName = provider.HelmRelease;
        // Its chart and version are not EntKube's to know; claiming the catalog's would be a guess.
        component.HelmChartVersion = null;
        component.LastError = null;
        component.InstalledAt ??= DateTime.UtcNow;
    }

    /// <summary>Turns a tracked component back into the catalog's own install, at the catalog's identity.</summary>
    public static void RecordAsCatalogInstall(ClusterComponent component, CatalogEntry entry)
    {
        component.ComponentType = entry.ComponentType;
        component.Status = ComponentStatus.NotInstalled;
        component.Namespace = entry.DefaultNamespace;
        component.ReleaseName = entry.DefaultReleaseName;
        component.HelmRepoUrl = entry.HelmRepoUrl;
        component.HelmChartName = entry.HelmChartName;
        component.HelmChartVersion = entry.HelmChartVersion;
        component.InstalledAt = null;
    }

    /// <summary>
    /// Finds what serves <paramref name="apiService"/>, or null when nothing is registered for it.
    /// Reads only.
    /// </summary>
    public static async Task<ExistingProvider?> LocateAsync(Kubernetes client, string apiService, CancellationToken ct)
    {
        V1APIService api;

        try
        {
            api = await client.ApiregistrationV1.ReadAPIServiceAsync(apiService, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        V1Service? service = null;

        if (api.Spec?.Service is { } reference)
        {
            try
            {
                service = await client.CoreV1.ReadNamespacedServiceAsync(
                    reference.Name, reference.NamespaceProperty, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                service = null;
            }
        }

        return Describe(api, service);
    }

    /// <summary>What an APIService and the Service it names say about who serves the API.</summary>
    public static ExistingProvider Describe(V1APIService api, V1Service? service)
    {
        Apiregistrationv1ServiceReference? reference = api.Spec?.Service;
        IDictionary<string, string>? annotations = service?.Metadata?.Annotations;

        string? release = annotations?.TryGetValue(HelmReleaseNameAnnotation, out string? r) == true ? r : null;
        string? releaseNamespace = release is null
            ? null
            : annotations!.TryGetValue(HelmReleaseNamespaceAnnotation, out string? rn) ? rn : service!.Metadata.NamespaceProperty;

        V1APIServiceCondition? availability = api.Status?.Conditions?
            .FirstOrDefault(c => c.Type == "Available");
        bool available = string.Equals(availability?.Status, "True", StringComparison.OrdinalIgnoreCase);

        return new ExistingProvider(
            ApiService: api.Metadata?.Name ?? "",
            Namespace: reference?.NamespaceProperty,
            ServiceName: reference?.Name,
            ServiceExists: reference is null || service is not null,
            HelmRelease: release,
            HelmReleaseNamespace: releaseNamespace,
            Available: available,
            UnavailableReason: available ? null : availability?.Message ?? availability?.Reason);
    }

    /// <summary>What the operator reads after an install that tracked instead.</summary>
    public static string DescribeTracking(CatalogEntry entry, ExistingProvider provider)
    {
        string text = $"{entry.DisplayName} is already running on this cluster: {provider.ApiService} is served by "
            + $"{provider.Where}, installed by {provider.Owner}. Nothing was installed. EntKube now tracks it and "
            + "will not upgrade or uninstall it.";

        return provider.Available
            ? text
            : text + $"\n\nIt is registered but NOT available: {provider.UnavailableReason ?? "no reason given"}. "
                + "Whatever depends on this API will fail until that server is repaired.";
    }
}
