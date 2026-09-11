using System.Net.Http.Headers;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>A Nova flavor, as much of it as sizing a cluster needs.</summary>
public sealed record OpenStackFlavor(string Id, string Name, int VCpus, int RamMb, int DiskGb)
{
    /// <summary>"b.4c8gb — 4 vCPU, 8 GB RAM, 40 GB disk", for a picker.</summary>
    public string Label => $"{Name} — {VCpus} vCPU, {RamMb / 1024.0:0.#} GB RAM, {DiskGb} GB disk";
}

/// <summary>
/// A Glance image. <see cref="KubernetesVersion"/> is set only for images EntKube built, which
/// carry the version in their metadata — that is what lets a cluster be created for a version
/// rather than for an image name somebody has to remember.
/// </summary>
public sealed record OpenStackImage(
    string Id, string Name, string Status, string? KubernetesVersion, DateTime? BuiltAt)
{
    public bool IsEntKubeBuilt => KubernetesVersion is not null;
    public bool IsUsable => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
}

public sealed record OpenStackNetwork(string Id, string Name, bool IsExternal);

public sealed record OpenStackVolumeType(string Id, string Name, bool IsDefault);

/// <summary>
/// What a cloud can actually do, discovered rather than assumed. The two capability flags decide
/// real things: without Octavia a cluster's API server needs a different endpoint strategy, and
/// without an object store CubeFS is the only way to offer S3.
/// </summary>
public sealed record OpenStackCapabilities(
    bool HasOctavia,
    bool HasObjectStore,
    bool HasVolumeSnapshots,
    IReadOnlyList<string> AvailabilityZones)
{
    /// <summary>
    /// How a cluster's API server should be reached on this cloud. Octavia gives a load balancer
    /// in front of every control-plane node, which is the only arrangement where losing one of
    /// them is survivable; without it the endpoint has to ride a floating IP that moves.
    /// </summary>
    public ApiEndpointStrategy ApiEndpoint => HasOctavia ? ApiEndpointStrategy.Octavia : ApiEndpointStrategy.FloatingIp;
}

public enum ApiEndpointStrategy
{
    /// <summary>An Octavia load balancer across the control-plane nodes. The default when available.</summary>
    Octavia,

    /// <summary>A floating IP held by kube-vip and moved between control-plane nodes on failure.</summary>
    FloatingIp
}

/// <summary>Everything discovered about one cloud in a single pass.</summary>
public sealed record OpenStackInventory(
    IReadOnlyList<OpenStackFlavor> Flavors,
    IReadOnlyList<OpenStackImage> Images,
    IReadOnlyList<OpenStackNetwork> Networks,
    IReadOnlyList<OpenStackVolumeType> VolumeTypes,
    OpenStackCapabilities Capabilities)
{
    /// <summary>External networks — the candidates for floating IPs and the API load balancer.</summary>
    public IEnumerable<OpenStackNetwork> ExternalNetworks => Networks.Where(n => n.IsExternal);

    /// <summary>Tenant networks — the candidates for attaching nodes to.</summary>
    public IEnumerable<OpenStackNetwork> TenantNetworks => Networks.Where(n => !n.IsExternal);

    /// <summary>The newest EntKube-built image for a Kubernetes version, if one has been built.</summary>
    public OpenStackImage? NodeImageFor(string kubernetesVersion) =>
        Images.Where(i => i.IsUsable && i.KubernetesVersion == MachineImageNaming.Normalize(kubernetesVersion))
              .MaxBy(i => i.BuiltAt ?? DateTime.MinValue);
}

/// <summary>
/// Reads what an OpenStack project offers, so the cluster form can present choices instead of
/// asking an operator to type a flavor name exactly right and find out at boot whether they did.
///
/// <para>Everything here is read-only and best-effort per resource: a cloud that will not list its
/// volume types should still let you pick a flavor. A missing service is reported as a missing
/// capability rather than as an error, because "this cloud has no Octavia" is an answer, not a
/// failure.</para>
/// </summary>
public class OpenStackDiscoveryService(
    OpenStackHttpFactory httpFactory,
    OpenStackKeystoneClient keystone,
    ILogger<OpenStackDiscoveryService> logger)
{
    public async Task<OpenStackInventory> DiscoverAsync(
        Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        KeystoneSession session = await keystone.AuthenticateAsync(tenantId, connectionId, ct);
        return await DiscoverAsync(session, ct);
    }

    public async Task<OpenStackInventory> DiscoverAsync(KeystoneSession session, CancellationToken ct = default)
    {
        // Independent reads against four services; one slow catalog should not serialize the rest.
        Task<IReadOnlyList<OpenStackFlavor>> flavors = ListFlavorsAsync(session, ct);
        Task<IReadOnlyList<OpenStackImage>> images = ListImagesAsync(session, ct);
        Task<IReadOnlyList<OpenStackNetwork>> networks = ListNetworksAsync(session, ct);
        Task<IReadOnlyList<OpenStackVolumeType>> volumeTypes = ListVolumeTypesAsync(session, ct);
        Task<IReadOnlyList<string>> zones = ListAvailabilityZonesAsync(session, ct);

        await Task.WhenAll(flavors, images, networks, volumeTypes, zones);

        OpenStackCapabilities capabilities = new(
            HasOctavia: session.GetEndpoint("load-balancer") is not null,
            HasObjectStore: session.GetEndpoint("object-store") is not null,
            // The Cinder v3 catalog entry is what the CSI driver's snapshot support rides on.
            HasVolumeSnapshots: session.GetEndpoint("volumev3") is not null || session.GetEndpoint("block-storage") is not null,
            AvailabilityZones: await zones);

        return new OpenStackInventory(
            await flavors, await images, await networks, await volumeTypes, capabilities);
    }

    public async Task<IReadOnlyList<OpenStackFlavor>> ListFlavorsAsync(
        KeystoneSession session, CancellationToken ct = default)
    {
        return await ReadAsync(session, "compute", "/flavors/detail", ct, doc =>
        {
            List<OpenStackFlavor> result = [];
            foreach (JsonElement f in doc.RootElement.GetProperty("flavors").EnumerateArray())
            {
                result.Add(new OpenStackFlavor(
                    Id: f.GetProperty("id").GetString() ?? "",
                    Name: f.GetProperty("name").GetString() ?? "",
                    VCpus: f.TryGetProperty("vcpus", out JsonElement c) ? c.GetInt32() : 0,
                    RamMb: f.TryGetProperty("ram", out JsonElement r) ? r.GetInt32() : 0,
                    DiskGb: f.TryGetProperty("disk", out JsonElement d) ? d.GetInt32() : 0));
            }
            return (IReadOnlyList<OpenStackFlavor>)result
                .OrderBy(x => x.VCpus).ThenBy(x => x.RamMb).ToList();
        });
    }

    public async Task<IReadOnlyList<OpenStackImage>> ListImagesAsync(
        KeystoneSession session, CancellationToken ct = default)
    {
        return await ReadAsync(session, "image", "/v2/images?limit=500", ct, doc =>
        {
            List<OpenStackImage> result = [];
            foreach (JsonElement i in doc.RootElement.GetProperty("images").EnumerateArray())
            {
                // Images EntKube built carry their Kubernetes version as image metadata, which is
                // the whole point: it is what makes "give me a 1.31 cluster" resolvable.
                string? version = i.TryGetProperty(MachineImageNaming.VersionProperty, out JsonElement v)
                    ? v.GetString()
                    : null;

                DateTime? builtAt = i.TryGetProperty(MachineImageNaming.BuiltAtProperty, out JsonElement b)
                    && DateTime.TryParse(b.GetString(), out DateTime parsed)
                        ? parsed
                        : null;

                result.Add(new OpenStackImage(
                    Id: i.GetProperty("id").GetString() ?? "",
                    Name: i.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                    Status: i.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "",
                    KubernetesVersion: version,
                    BuiltAt: builtAt));
            }
            return (IReadOnlyList<OpenStackImage>)result.OrderBy(x => x.Name).ToList();
        });
    }

    public async Task<IReadOnlyList<OpenStackNetwork>> ListNetworksAsync(
        KeystoneSession session, CancellationToken ct = default)
    {
        return await ReadAsync(session, "network", "/v2.0/networks", ct, doc =>
        {
            List<OpenStackNetwork> result = [];
            foreach (JsonElement n in doc.RootElement.GetProperty("networks").EnumerateArray())
            {
                result.Add(new OpenStackNetwork(
                    Id: n.GetProperty("id").GetString() ?? "",
                    Name: n.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                    IsExternal: n.TryGetProperty("router:external", out JsonElement ext) && ext.ValueKind == JsonValueKind.True));
            }
            return (IReadOnlyList<OpenStackNetwork>)result.OrderBy(x => x.Name).ToList();
        });
    }

    public async Task<IReadOnlyList<OpenStackVolumeType>> ListVolumeTypesAsync(
        KeystoneSession session, CancellationToken ct = default)
    {
        string? endpoint = session.GetEndpoint("volumev3") ?? session.GetEndpoint("block-storage");
        if (endpoint is null)
        {
            return [];
        }

        return await ReadAsync(session, endpoint, "/types", ct, doc =>
        {
            List<OpenStackVolumeType> result = [];
            foreach (JsonElement t in doc.RootElement.GetProperty("volume_types").EnumerateArray())
            {
                bool isDefault = t.TryGetProperty("extra_specs", out JsonElement specs)
                    && specs.TryGetProperty("is_default", out JsonElement d)
                    && string.Equals(d.GetString(), "true", StringComparison.OrdinalIgnoreCase);

                result.Add(new OpenStackVolumeType(
                    Id: t.GetProperty("id").GetString() ?? "",
                    Name: t.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                    IsDefault: isDefault));
            }
            return (IReadOnlyList<OpenStackVolumeType>)result.OrderBy(x => x.Name).ToList();
        }, absoluteEndpoint: true);
    }

    public async Task<IReadOnlyList<string>> ListAvailabilityZonesAsync(
        KeystoneSession session, CancellationToken ct = default)
    {
        return await ReadAsync(session, "compute", "/os-availability-zone", ct, doc =>
        {
            List<string> zones = [];
            foreach (JsonElement z in doc.RootElement.GetProperty("availabilityZoneInfo").EnumerateArray())
            {
                bool available = z.TryGetProperty("zoneState", out JsonElement state)
                    && state.TryGetProperty("available", out JsonElement a)
                    && a.ValueKind == JsonValueKind.True;

                string? name = z.TryGetProperty("zoneName", out JsonElement n) ? n.GetString() : null;

                // "internal" is Nova's own services, not somewhere a machine can be scheduled.
                if (available && name is not null && !string.Equals(name, "internal", StringComparison.Ordinal))
                {
                    zones.Add(name);
                }
            }
            return (IReadOnlyList<string>)zones.OrderBy(x => x).ToList();
        });
    }

    /// <summary>
    /// Names of resources in a collection that start with a prefix. Used to sweep a project for
    /// anything a deleted cluster left behind, which is why it matches on the name rather than on
    /// a tag: CAPO names what it creates after the cluster, and a resource that lost its tags still
    /// carries the name.
    /// </summary>
    public async Task<List<string>> ListNamedAsync(
        KeystoneSession session,
        string serviceType,
        string path,
        string collection,
        string nameProperty,
        string prefix,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> names = await ReadAsync(session, serviceType, path, ct, doc =>
        {
            List<string> found = [];
            if (!doc.RootElement.TryGetProperty(collection, out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return (IReadOnlyList<string>)found;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                string? name = item.TryGetProperty(nameProperty, out JsonElement n) ? n.GetString() : null;
                if (!string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    found.Add(name);
                }
            }
            return (IReadOnlyList<string>)found;
        });

        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// One read against one service. A service the catalog does not advertise, or that refuses the
    /// call, yields an empty list and a warning: discovery is here to fill in a form, and half a
    /// form beats an error page.
    /// </summary>
    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        KeystoneSession session,
        string serviceTypeOrUrl,
        string path,
        CancellationToken ct,
        Func<JsonDocument, IReadOnlyList<T>> parse,
        bool absoluteEndpoint = false)
    {
        string? root = absoluteEndpoint ? serviceTypeOrUrl : session.GetEndpoint(serviceTypeOrUrl);
        if (root is null)
        {
            logger.LogInformation("OpenStack discovery: no '{Service}' endpoint in the catalog", serviceTypeOrUrl);
            return [];
        }

        try
        {
            using HttpClient client = httpFactory.CreateClient(session.Egress);
            using HttpRequestMessage request = new(HttpMethod.Get, $"{root.TrimEnd('/')}{path}");
            request.Headers.Add("X-Auth-Token", session.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OpenStack discovery: GET {Path} on {Service} failed ({Status}): {Body}",
                    path, serviceTypeOrUrl, (int)response.StatusCode, body);
                return [];
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            return parse(doc);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "OpenStack discovery: could not read {Path} from {Service}", path, serviceTypeOrUrl);
            return [];
        }
    }
}
