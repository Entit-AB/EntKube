using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EntKube.Web.Services;

/// <summary>
/// Parses ArgoCD Application CRD YAML files and reconciles child AppDeployment rows.
///
/// Supported input: one or more YAML documents, each containing an ArgoCD Application
/// CRD (apiVersion: argoproj.io/v1alpha1, kind: Application). The service maps each
/// Application to an AppDeployment under the parent app and ensures the set of child
/// deployments matches the set of Applications in Git — creating new ones, updating
/// changed ones, and deleting removed ones.
///
/// CRD → AppDeployment field mapping:
///   metadata.name           → AppDeployment.Name
///   spec.source.repoURL     → resolved to GitRepository by URL
///   spec.source.targetRevision → GitRevision (fallback: repo DefaultBranch)
///   spec.source.path        → GitPath
///   spec.source.helm        → DeploymentType = GitHelm, HelmValues from valuesFiles/values
///   spec.destination.namespace → Namespace
///   spec.destination.server → matched to KubernetesCluster by ApiServerUrl
/// </summary>
public class AppOfAppsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AppOfAppsService> logger)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Reconciles child deployments for a GitAppOfApps deployment from the parsed
    /// YAML files. Returns the number of deployments created/updated/deleted.
    /// </summary>
    public async Task<AppOfAppsReconcileResult> ReconcileAsync(
        AppDeployment parent,
        Dictionary<string, string> yamlFiles,
        CancellationToken ct = default)
    {
        List<ArgoCdApplication> applications = ParseApplications(yamlFiles);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load existing child deployments for this parent.
        List<AppDeployment> existing = await db.AppDeployments
            .Where(d => d.ParentDeploymentId == parent.Id)
            .ToListAsync(ct);

        // Load clusters for this tenant so we can resolve server references.
        Guid tenantId = await db.Apps
            .Where(a => a.Id == parent.AppId)
            .Select(a => a.Customer.TenantId)
            .FirstAsync(ct);

        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct);

        // Load the environment for the parent deployment to use as default.
        Guid defaultEnvId = parent.EnvironmentId;
        Guid defaultClusterId = parent.ClusterId;

        int created = 0, updated = 0, deleted = 0;

        HashSet<string> seenNames = [];

        foreach (ArgoCdApplication app in applications)
        {
            seenNames.Add(app.Metadata.Name);

            string? repoUrl = app.Spec.Source.RepoUrl;
            KubernetesCluster? cluster = ResolveCluster(clusters, app.Spec.Destination.Server)
                ?? clusters.FirstOrDefault(c => c.Id == defaultClusterId);

            if (string.IsNullOrWhiteSpace(repoUrl))
            {
                logger.LogWarning(
                    "AppOfApps {Parent}: child '{Child}' has no repoURL — skipping.",
                    parent.Name, app.Metadata.Name);
                continue;
            }

            if (cluster is null)
            {
                logger.LogWarning(
                    "AppOfApps {Parent}: no cluster matches server '{Server}' for child '{Child}' — skipping.",
                    parent.Name, app.Spec.Destination.Server, app.Metadata.Name);
                continue;
            }

            AppDeployment? child = existing.FirstOrDefault(d => d.Name == app.Metadata.Name);

            DeploymentType type = app.Spec.Source.Helm is not null ? DeploymentType.GitHelm : DeploymentType.GitYaml;
            string revision = app.Spec.Source.TargetRevision ?? "main";
            string ns = app.Spec.Destination.Namespace ?? "default";
            string path = app.Spec.Source.Path ?? ".";

            if (child is null)
            {
                child = new AppDeployment
                {
                    Id = Guid.NewGuid(),
                    AppId = parent.AppId,
                    Name = app.Metadata.Name,
                    Type = type,
                    EnvironmentId = defaultEnvId,
                    ClusterId = cluster.Id,
                    Namespace = ns,
                    GitUrl = repoUrl,
                    GitPath = path,
                    GitRevision = revision,
                    GitAutoSync = true,
                    ParentDeploymentId = parent.Id
                };

                if (app.Spec.Source.Helm is not null)
                {
                    child.HelmValues = BuildHelmValues(app.Spec.Source.Helm);
                }

                db.AppDeployments.Add(child);
                created++;
            }
            else
            {
                bool changed = child.Type != type
                    || child.ClusterId != cluster.Id
                    || child.Namespace != ns
                    || child.GitUrl != repoUrl
                    || child.GitPath != path
                    || child.GitRevision != revision;

                if (changed)
                {
                    child.Type = type;
                    child.ClusterId = cluster.Id;
                    child.Namespace = ns;
                    child.GitUrl = repoUrl;
                    child.GitPath = path;
                    child.GitRevision = revision;

                    if (app.Spec.Source.Helm is not null)
                        child.HelmValues = BuildHelmValues(app.Spec.Source.Helm);

                    updated++;
                }
            }
        }

        // Delete child deployments whose Application was removed from Git.
        foreach (AppDeployment orphan in existing.Where(d => !seenNames.Contains(d.Name)))
        {
            db.AppDeployments.Remove(orphan);
            deleted++;
            logger.LogInformation(
                "AppOfApps {Parent}: removing child '{Child}' (no longer in Git).",
                parent.Name, orphan.Name);
        }

        await db.SaveChangesAsync(ct);

        return new AppOfAppsReconcileResult
        {
            Created = created,
            Updated = updated,
            Deleted = deleted
        };
    }

    // ── Parsing ──────────────────────────────────────────────────────────────────

    private List<ArgoCdApplication> ParseApplications(Dictionary<string, string> files)
    {
        List<ArgoCdApplication> result = [];

        foreach ((string filePath, string content) in files)
        {
            if (!filePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !filePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                continue;

            // A single file may contain multiple YAML documents separated by "---".
            foreach (string document in SplitDocuments(content))
            {
                try
                {
                    ArgoCdApplication? app = Yaml.Deserialize<ArgoCdApplication>(document);

                    if (app?.ApiVersion?.Contains("argoproj.io") == true
                        && app.Kind == "Application"
                        && app.Metadata?.Name is not null
                        && app.Spec?.Source is not null
                        && app.Spec.Destination is not null)
                    {
                        result.Add(app);
                    }
                }
                catch (YamlException ex)
                {
                    logger.LogWarning(ex, "Failed to parse YAML document in {File}", filePath);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitDocuments(string yaml)
    {
        // Split on lines that are exactly "---" (document separator).
        string[] parts = yaml.Split("\n---", StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                yield return trimmed;
        }
    }

    private static KubernetesCluster? ResolveCluster(List<KubernetesCluster> clusters, string? server)
    {
        if (string.IsNullOrEmpty(server)) return null;

        // ArgoCD uses "https://kubernetes.default.svc" to mean in-cluster.
        if (server.Contains("kubernetes.default"))
            return clusters.FirstOrDefault();

        return clusters.FirstOrDefault(c =>
            string.Equals(c.ApiServerUrl, server, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeUrl(string url) =>
        url.TrimEnd('/').TrimEnd(".git".ToCharArray()).ToLowerInvariant();

    private static string? BuildHelmValues(ArgoCdHelmSource helm)
    {
        // Merge inline values object into a flat YAML string for HelmValues field.
        if (helm.Values is not null) return helm.Values;

        if (helm.Parameters?.Count > 0)
        {
            var lines = helm.Parameters.Select(p => $"{p.Name}: {p.Value}");
            return string.Join("\n", lines);
        }

        return null;
    }
}

// ── DTOs for deserializing ArgoCD Application CRD ────────────────────────────

#pragma warning disable CS8618
internal class ArgoCdApplication
{
    public string ApiVersion { get; set; }
    public string Kind { get; set; }
    public ArgoCdMetadata Metadata { get; set; }
    public ArgoCdApplicationSpec Spec { get; set; }
}

internal class ArgoCdMetadata
{
    public string Name { get; set; }
    public string? Namespace { get; set; }
}

internal class ArgoCdApplicationSpec
{
    public ArgoCdSource Source { get; set; }
    public ArgoCdDestination Destination { get; set; }
    public string? Project { get; set; }
}

internal class ArgoCdSource
{
    public string? RepoUrl { get; set; }
    public string? TargetRevision { get; set; }
    public string? Path { get; set; }
    public ArgoCdHelmSource? Helm { get; set; }
}

internal class ArgoCdDestination
{
    public string? Server { get; set; }
    public string? Namespace { get; set; }
    public string? Name { get; set; }
}

internal class ArgoCdHelmSource
{
    public string? Values { get; set; }
    public string? ValueFiles { get; set; }
    public List<ArgoCdHelmParameter>? Parameters { get; set; }
}

internal class ArgoCdHelmParameter
{
    public string Name { get; set; }
    public string? Value { get; set; }
}
#pragma warning restore CS8618

public class AppOfAppsReconcileResult
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }

    public override string ToString() =>
        $"created={Created} updated={Updated} deleted={Deleted}";
}
