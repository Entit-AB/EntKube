using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// A project-scoped Keystone session: the auth token plus the identifiers and
/// the public service-catalog endpoints needed to call the other OpenStack APIs
/// (Nova/Neutron/Glance/Octavia/Swift). Produced by
/// <see cref="OpenStackKeystoneClient.AuthenticateAsync"/>.
/// </summary>
public sealed class KeystoneSession
{
    public required string Token { get; init; }
    public required string UserId { get; init; }
    public required string ProjectId { get; init; }

    /// <summary>
    /// Transport the session was established over, or null for a direct
    /// connection. Carried here so Nova/Neutron/Glance/Swift calls made with this
    /// session automatically leave from the same address Keystone accepted.
    /// </summary>
    public ResolvedEgress? Egress { get; init; }

    /// <summary>Public endpoint URL per service type ("compute", "network", "image", "load-balancer", "object-store", "identity").</summary>
    public required IReadOnlyDictionary<string, string> Endpoints { get; init; }

    /// <summary>Returns the public endpoint for a service type, or null if the catalog did not advertise one.</summary>
    public string? GetEndpoint(string serviceType) =>
        Endpoints.TryGetValue(serviceType, out string? url) ? url : null;

    /// <summary>Returns the public endpoint for a service type, throwing a clear error if it is missing.</summary>
    public string RequireEndpoint(string serviceType) =>
        GetEndpoint(serviceType)
        ?? throw new InvalidOperationException(
            $"No public '{serviceType}' endpoint found in the Keystone service catalog. " +
            "The OpenStack project may not have this service enabled.");
}

/// <summary>
/// An OpenStack application credential — a scoped, revocable secret that acts on
/// behalf of the user without exposing the password. Used as the in-cluster
/// <c>cloud-config</c> credential for the cloud-controller-manager and Cinder CSI.
/// </summary>
public sealed class ApplicationCredential
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Secret { get; init; }
}

/// <summary>
/// Shared OpenStack Keystone v3 client. Owns password authentication, service
/// catalog discovery, EC2 credential creation (for S3), and application
/// credential creation (for in-cluster CCM/CSI). Both
/// <see cref="OpenStackS3Service"/> and the compute/provisioning services build
/// on top of the <see cref="KeystoneSession"/> it returns.
/// </summary>
public class OpenStackKeystoneClient(
    OpenStackHttpFactory httpFactory,
    VaultService vaultService,
    ClusterEgressTunnel egressTunnel,
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>Vault key holding the proxy password for an OpenStack connection.</summary>
    public const string ProxyPasswordSecretName = "OS_PROXY_PASSWORD";

    /// <summary>
    /// Works out how this connection's traffic should leave EntKube, bringing up
    /// the cluster relay tunnel if that is the configured route. Returns null when
    /// the connection talks to OpenStack directly.
    ///
    /// Routing through a cluster takes precedence over an explicit proxy: it is the
    /// more specific choice, and the UI offers them as alternatives.
    /// </summary>
    public async Task<ResolvedEgress?> ResolveEgressAsync(
        OpenStackConnection connection, CancellationToken ct = default)
    {
        if (connection.RouteViaClusterId is { } clusterId)
        {
            string kubeconfig = await LoadKubeconfigAsync(clusterId, ct);
            int localPort = await egressTunnel.GetLocalPortAsync(clusterId, kubeconfig, ct);

            return new ResolvedEgress(RelayLocalPort: localPort);
        }

        if (string.IsNullOrWhiteSpace(connection.ProxyUrl))
        {
            return null;
        }

        string? proxyPassword = string.IsNullOrWhiteSpace(connection.ProxyUsername)
            ? null
            : await vaultService.GetOpenStackSecretValueAsync(
                connection.TenantId, connection.Id, ProxyPasswordSecretName, ct);

        return new ResolvedEgress(new OpenStackProxy(connection.ProxyUrl, connection.ProxyUsername, proxyPassword));
    }

    /// <summary>
    /// Loads the kubeconfig for the relay cluster, with an error that names the
    /// actual problem — a cluster that was deleted or never had one attached is a
    /// configuration mistake the operator can fix.
    /// </summary>
    private async Task<string> LoadKubeconfigAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null)
        {
            throw new InvalidOperationException(
                "The cluster this connection routes through no longer exists. "
                + "Pick another cluster, or switch the connection back to a direct connection.");
        }

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            throw new InvalidOperationException(
                $"Cluster '{cluster.Name}' has no kubeconfig, so EntKube cannot reach its egress relay.");
        }

        return cluster.Kubeconfig;
    }

    /// <summary>
    /// Authenticates against Keystone v3 with password auth and returns a
    /// project-scoped session including the discovered service catalog.
    /// </summary>
    public async Task<KeystoneSession> AuthenticateAsync(
        OpenStackConnection connection, string password, CancellationToken ct = default)
    {
        // Request a project-scoped token so derived credentials inherit the right scope.
        object authBody = new
        {
            auth = new
            {
                identity = new
                {
                    methods = new[] { "password" },
                    password = new
                    {
                        user = new
                        {
                            name = connection.Username,
                            password,
                            domain = new { name = connection.UserDomainName ?? "Default" }
                        }
                    }
                },
                scope = new
                {
                    project = connection.ProjectId is not null
                        ? (object)new { id = connection.ProjectId }
                        : new { name = connection.ProjectName, domain = new { name = connection.ProjectDomainName ?? "Default" } }
                }
            }
        };

        string json = JsonSerializer.Serialize(authBody);

        ResolvedEgress? egress = await ResolveEgressAsync(connection, ct);

        using HttpClient client = httpFactory.CreateClient(egress);
        string authUrl = NormalizeV3(connection.AuthUrl);

        using HttpRequestMessage request = new(HttpMethod.Post, $"{authUrl}/auth/tokens")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Keystone authentication failed ({response.StatusCode}): {errorBody}");
        }

        // The token is returned in the X-Subject-Token header.
        string token = response.Headers.GetValues("X-Subject-Token").First();

        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement tokenElement = doc.RootElement.GetProperty("token");

        string userId = tokenElement.GetProperty("user").GetProperty("id").GetString()!;
        string projectId = tokenElement.GetProperty("project").GetProperty("id").GetString()!;

        return new KeystoneSession
        {
            Token = token,
            UserId = userId,
            ProjectId = projectId,
            Egress = egress,
            Endpoints = ParsePublicEndpoints(tokenElement)
        };
    }

    /// <summary>
    /// Creates EC2 credentials (access/secret key pair) via the Keystone v3
    /// credentials API, for use with any S3-compatible client.
    /// </summary>
    public async Task<(string AccessKey, string SecretKey)> CreateEc2CredentialsAsync(
        KeystoneSession session, string rawAuthUrl, CancellationToken ct = default)
    {
        string authUrl = NormalizeV3(rawAuthUrl);
        string url = $"{authUrl}/users/{session.UserId}/credentials/OS-EC2";

        object requestBody = new { tenant_id = session.ProjectId };
        string json = JsonSerializer.Serialize(requestBody);

        using HttpClient client = httpFactory.CreateClient(session.Egress);
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Auth-Token", session.Token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Failed to create EC2 credentials ({response.StatusCode}): {errorBody}");
        }

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement credential = doc.RootElement.GetProperty("credential");

        return (credential.GetProperty("access").GetString()!, credential.GetProperty("secret").GetString()!);
    }

    /// <summary>
    /// Creates a scoped application credential for the authenticated user. Returned
    /// secret is only available once (at creation), so the caller must persist it.
    /// </summary>
    public async Task<ApplicationCredential> CreateApplicationCredentialAsync(
        KeystoneSession session, string rawAuthUrl, string name, CancellationToken ct = default)
    {
        string authUrl = NormalizeV3(rawAuthUrl);
        string url = $"{authUrl}/users/{session.UserId}/application_credentials";

        // Omit "roles" so the credential inherits all of the user's current roles on
        // the project — CCM/CSI need compute/network read+volume-attach permissions.
        object requestBody = new
        {
            application_credential = new
            {
                name,
                description = "EntKube in-cluster credential (cloud-controller-manager / Cinder CSI)"
            }
        };
        string json = JsonSerializer.Serialize(requestBody);

        using HttpClient client = httpFactory.CreateClient(session.Egress);
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Auth-Token", session.Token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Failed to create application credential ({response.StatusCode}): {errorBody}");
        }

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement cred = doc.RootElement.GetProperty("application_credential");

        return new ApplicationCredential
        {
            Id = cred.GetProperty("id").GetString()!,
            Name = cred.GetProperty("name").GetString()!,
            Secret = cred.GetProperty("secret").GetString()!
        };
    }

    /// <summary>
    /// Ensures a Keystone auth URL ends with the <c>/v3</c> version segment.
    /// Users may store just the base or the full path.
    /// </summary>
    public static string NormalizeV3(string rawAuthUrl)
    {
        string authUrl = rawAuthUrl.TrimEnd('/');
        if (!authUrl.EndsWith("/v3", StringComparison.OrdinalIgnoreCase))
        {
            authUrl += "/v3";
        }
        return authUrl;
    }

    /// <summary>
    /// Extracts the public endpoint URL for every service type in the token's
    /// service catalog. The first public interface wins per service type.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParsePublicEndpoints(JsonElement tokenElement)
    {
        Dictionary<string, string> endpoints = new(StringComparer.OrdinalIgnoreCase);

        if (!tokenElement.TryGetProperty("catalog", out JsonElement catalog))
        {
            return endpoints;
        }

        foreach (JsonElement service in catalog.EnumerateArray())
        {
            if (!service.TryGetProperty("type", out JsonElement typeEl)) continue;
            string? type = typeEl.GetString();
            if (string.IsNullOrEmpty(type) || endpoints.ContainsKey(type)) continue;

            if (!service.TryGetProperty("endpoints", out JsonElement eps)) continue;

            foreach (JsonElement ep in eps.EnumerateArray())
            {
                if (ep.TryGetProperty("interface", out JsonElement iface)
                    && iface.GetString() == "public"
                    && ep.TryGetProperty("url", out JsonElement urlEl)
                    && urlEl.GetString() is { Length: > 0 } url)
                {
                    endpoints[type] = url.TrimEnd('/');
                    break;
                }
            }
        }

        return endpoints;
    }
}
