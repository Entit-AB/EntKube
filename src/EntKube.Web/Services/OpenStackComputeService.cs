using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// The set of OpenStack resources created for one ephemeral bootstrap VM. Carried
/// end-to-end so teardown (and resume-after-failure) can release exactly what was
/// created, in reverse order.
/// </summary>
public sealed class BootstrapVm
{
    public required string ServerId { get; init; }
    public required string FloatingIp { get; init; }
    public string? FloatingIpId { get; init; }
    public string? SecurityGroupId { get; init; }
    public string? KeypairName { get; init; }
}

/// <summary>
/// Thin Nova/Neutron/Glance client scoped to what provisioning needs: standing up
/// (and tearing down) the throwaway k3s bootstrap VM, plus the read-only inventory
/// the provisioning form offers as pickers (images, flavors, networks, AZs). All
/// calls use the public service-catalog endpoints from a <see cref="KeystoneSession"/>
/// and the token as <c>X-Auth-Token</c>. This is deliberately not a general-purpose
/// OpenStack SDK.
/// </summary>
public class OpenStackComputeService(OpenStackHttpFactory httpFactory, ILogger<OpenStackComputeService> logger)
{
    /// <summary>
    /// Allocates an unassociated floating IP from the external network. Done before
    /// boot so the address can be baked into the bootstrap VM's cloud-init (k3s
    /// <c>--tls-san</c>), then associated once the server is ACTIVE.
    /// </summary>
    public async Task<(string Id, string Address)> AllocateFloatingIpAsync(
        KeystoneSession session, string externalNetworkId, CancellationToken ct = default)
    {
        string network = session.RequireEndpoint("network");
        object body = new { floatingip = new { floating_network_id = externalNetworkId } };
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, $"{network}/v2.0/floatingips", session, body, ct);
        JsonElement fip = doc!.RootElement.GetProperty("floatingip");
        return (fip.GetProperty("id").GetString()!, fip.GetProperty("floating_ip_address").GetString()!);
    }

    /// <summary>
    /// Boots a bootstrap VM: imports the SSH public key, creates a security group
    /// opening SSH (22) and the k3s API (6443), boots the server with the given
    /// cloud-init, waits until it is ACTIVE, and associates the pre-allocated
    /// floating IP.
    /// </summary>
    public async Task<BootstrapVm> CreateBootstrapVmAsync(
        KeystoneSession session,
        OpenStackProvisioningConfig config,
        string sshPublicKey,
        string cloudInitUserData,
        string floatingIpId,
        string floatingIpAddress,
        CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        string network = session.RequireEndpoint("network");

        string name = $"{config.ClusterName}-bootstrap";
        string keypairName = $"{name}-key";

        // 1. Import the SSH keypair so we can fetch the k3s kubeconfig later.
        await CreateKeypairAsync(compute, session, keypairName, sshPublicKey, ct);

        // 2. Security group allowing inbound SSH + k3s API.
        string sgId = await CreateSecurityGroupAsync(network, session, name, ct);
        await AddIngressRuleAsync(network, session, sgId, 22, ct);
        await AddIngressRuleAsync(network, session, sgId, 6443, ct);

        // 3. Resolve image + boot the server.
        string imageId = await ResolveImageIdAsync(session, config.BootstrapImageName, ct);
        string serverId = await CreateServerAsync(
            compute, session, name, imageId, config.BootstrapFlavor,
            config.BootstrapNetworkId, keypairName, sgId, cloudInitUserData, ct);

        logger.LogInformation("Bootstrap VM {ServerId} created; waiting for ACTIVE", serverId);

        // 4. Wait for ACTIVE, then associate the pre-allocated floating IP.
        await WaitForServerActiveAsync(compute, session, serverId, ct);

        string portId = await GetServerPortIdAsync(network, session, serverId, config.BootstrapNetworkId, ct);
        await AssociateFloatingIpAsync(network, session, floatingIpId, portId, ct);

        logger.LogInformation("Bootstrap VM {ServerId} ACTIVE with floating IP {Ip}", serverId, floatingIpAddress);

        return new BootstrapVm
        {
            ServerId = serverId,
            FloatingIp = floatingIpAddress,
            FloatingIpId = floatingIpId,
            SecurityGroupId = sgId,
            KeypairName = keypairName
        };
    }

    /// <summary>
    /// Releases every resource in <paramref name="vm"/>, tolerating "already gone".
    /// Order: server → floating IP → security group → keypair.
    /// </summary>
    public async Task DeleteBootstrapVmAsync(KeystoneSession session, BootstrapVm vm, CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        string network = session.RequireEndpoint("network");

        await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{compute}/servers/{vm.ServerId}", session, null, ct), "server", vm.ServerId);

        if (vm.FloatingIpId is not null)
            await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{network}/v2.0/floatingips/{vm.FloatingIpId}", session, null, ct), "floating IP", vm.FloatingIpId);

        if (vm.SecurityGroupId is not null)
            await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{network}/v2.0/security-groups/{vm.SecurityGroupId}", session, null, ct), "security group", vm.SecurityGroupId);

        if (vm.KeypairName is not null)
            await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{compute}/os-keypairs/{vm.KeypairName}", session, null, ct), "keypair", vm.KeypairName);
    }

    // ──────── Nova ────────

    private async Task CreateKeypairAsync(string compute, KeystoneSession session, string name, string publicKey, CancellationToken ct)
    {
        object body = new { keypair = new { name, public_key = publicKey } };
        await SendAsync(HttpMethod.Post, $"{compute}/os-keypairs", session, body, ct);
    }

    private async Task<string> CreateServerAsync(
        string compute, KeystoneSession session, string name, string imageId, string flavor,
        string networkId, string keypairName, string securityGroupId, string cloudInit, CancellationToken ct)
    {
        // Nova accepts a flavorRef by name or id; we pass the configured flavor name.
        object body = new
        {
            server = new
            {
                name,
                imageRef = imageId,
                flavorRef = flavor,
                key_name = keypairName,
                networks = new[] { new { uuid = networkId } },
                security_groups = new[] { new { name } },
                user_data = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInit))
            }
        };
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, $"{compute}/servers", session, body, ct);
        return doc!.RootElement.GetProperty("server").GetProperty("id").GetString()!;
    }

    private async Task WaitForServerActiveAsync(string compute, KeystoneSession session, string serverId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{compute}/servers/{serverId}", session, null, ct);
            string status = doc!.RootElement.GetProperty("server").GetProperty("status").GetString() ?? "";
            if (status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)) return;
            if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Bootstrap VM {serverId} entered ERROR state.");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        throw new TimeoutException($"Bootstrap VM {serverId} did not become ACTIVE within 10 minutes.");
    }

    private async Task<string> ResolveImageIdAsync(KeystoneSession session, string imageName, CancellationToken ct)
    {
        string image = session.RequireEndpoint("image");
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{image}/v2/images?name={Uri.EscapeDataString(imageName)}", session, null, ct);
        foreach (JsonElement img in doc!.RootElement.GetProperty("images").EnumerateArray())
        {
            return img.GetProperty("id").GetString()!;
        }
        throw new InvalidOperationException($"OpenStack image '{imageName}' not found in the project.");
    }

    // ──────── Neutron ────────

    private async Task<string> CreateSecurityGroupAsync(string network, KeystoneSession session, string name, CancellationToken ct)
    {
        object body = new { security_group = new { name, description = "EntKube bootstrap VM (ephemeral)" } };
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, $"{network}/v2.0/security-groups", session, body, ct);
        return doc!.RootElement.GetProperty("security_group").GetProperty("id").GetString()!;
    }

    private async Task AddIngressRuleAsync(string network, KeystoneSession session, string sgId, int port, CancellationToken ct)
    {
        object body = new
        {
            security_group_rule = new
            {
                security_group_id = sgId,
                direction = "ingress",
                ethertype = "IPv4",
                protocol = "tcp",
                port_range_min = port,
                port_range_max = port,
                remote_ip_prefix = "0.0.0.0/0"
            }
        };
        await SendAsync(HttpMethod.Post, $"{network}/v2.0/security-group-rules", session, body, ct);
    }

    private async Task<string> GetServerPortIdAsync(string network, KeystoneSession session, string serverId, string networkId, CancellationToken ct)
    {
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{network}/v2.0/ports?device_id={serverId}", session, null, ct);
        foreach (JsonElement port in doc!.RootElement.GetProperty("ports").EnumerateArray())
        {
            // Prefer the port on the bootstrap network in case the VM has several.
            if (port.TryGetProperty("network_id", out JsonElement nid) && nid.GetString() == networkId)
                return port.GetProperty("id").GetString()!;
        }
        // Fall back to the first port if the network filter did not match.
        foreach (JsonElement port in doc!.RootElement.GetProperty("ports").EnumerateArray())
            return port.GetProperty("id").GetString()!;
        throw new InvalidOperationException($"No Neutron port found for server {serverId}.");
    }

    private async Task AssociateFloatingIpAsync(string network, KeystoneSession session, string floatingIpId, string portId, CancellationToken ct)
    {
        object body = new { floatingip = new { port_id = portId } };
        await SendAsync(HttpMethod.Put, $"{network}/v2.0/floatingips/{floatingIpId}", session, body, ct);
    }

    // ──────── Discovery (read-only inventory for the provisioning UI) ────────

    /// <summary>
    /// Lists the Glance images the project can boot, following pagination. Only active
    /// images are returned — a queued or deactivated image cannot boot a node.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackImage>> ListImagesAsync(KeystoneSession session, CancellationToken ct = default)
    {
        string image = session.RequireEndpoint("image");
        Uri baseUri = new(image + "/");
        string? next = $"{image}/v2/images?status=active&limit=200&sort_key=name&sort_dir=asc";

        List<OpenStackImage> images = [];

        // Glance pages with an opaque root-relative "next"; cap the walk so a huge
        // catalog cannot hold the wizard open indefinitely.
        for (int page = 0; page < 20 && next is not null; page++)
        {
            using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, next, session, null, ct);

            foreach (JsonElement img in doc.RootElement.GetProperty("images").EnumerateArray())
            {
                if (!TryReadString(img, "id", out string id) || !TryReadString(img, "name", out string name)) continue;
                images.Add(new OpenStackImage(id, name, ReadInt(img, "min_disk"), ReadInt(img, "min_ram")));
            }

            next = TryReadString(doc.RootElement, "next", out string path)
                ? new Uri(baseUri, path).ToString()
                : null;
        }

        return images;
    }

    /// <summary>
    /// Lists the flavors available to the project, with their sizing so the picker can
    /// show "4 vCPU / 8 GiB" rather than an opaque name.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackFlavor>> ListFlavorsAsync(KeystoneSession session, CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        string? next = $"{compute}/flavors/detail?limit=500";

        List<OpenStackFlavor> flavors = [];

        for (int page = 0; page < 20 && next is not null; page++)
        {
            using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, next, session, null, ct);

            foreach (JsonElement f in doc.RootElement.GetProperty("flavors").EnumerateArray())
            {
                if (!TryReadString(f, "name", out string name)) continue;
                TryReadString(f, "id", out string id);
                flavors.Add(new OpenStackFlavor(
                    id, name, ReadInt(f, "vcpus") ?? 0, ReadInt(f, "ram") ?? 0, ReadInt(f, "disk") ?? 0));
            }

            next = NextLink(doc.RootElement, "flavors_links");
        }

        return flavors;
    }

    /// <summary>
    /// Lists the Neutron networks visible to the project. Whether a network is external
    /// is what separates "floating IPs come from here" from "nodes attach here", so it is
    /// carried through rather than guessed from the name.
    /// </summary>
    public async Task<IReadOnlyList<OpenStackNetwork>> ListNetworksAsync(KeystoneSession session, CancellationToken ct = default)
    {
        string network = session.RequireEndpoint("network");
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{network}/v2.0/networks", session, null, ct);

        List<OpenStackNetwork> networks = [];

        foreach (JsonElement n in doc.RootElement.GetProperty("networks").EnumerateArray())
        {
            if (!TryReadString(n, "id", out string id)) continue;
            TryReadString(n, "name", out string name);

            networks.Add(new OpenStackNetwork(
                id,
                name,
                External: n.TryGetProperty("router:external", out JsonElement ext) && ext.ValueKind == JsonValueKind.True,
                Shared: n.TryGetProperty("shared", out JsonElement shared) && shared.ValueKind == JsonValueKind.True,
                SubnetCount: n.TryGetProperty("subnets", out JsonElement subnets) && subnets.ValueKind == JsonValueKind.Array
                    ? subnets.GetArrayLength()
                    : 0));
        }

        return networks;
    }

    /// <summary>
    /// Lists the Nova availability zones the project may schedule into. Zones reporting
    /// themselves unavailable are dropped — offering a failure domain that cannot take a
    /// boot request only moves the failure later.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailabilityZonesAsync(KeystoneSession session, CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{compute}/os-availability-zone", session, null, ct);

        List<string> zones = [];

        foreach (JsonElement z in doc.RootElement.GetProperty("availabilityZoneInfo").EnumerateArray())
        {
            if (!TryReadString(z, "zoneName", out string name)) continue;
            if (z.TryGetProperty("zoneState", out JsonElement state)
                && state.TryGetProperty("available", out JsonElement available)
                && available.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            zones.Add(name);
        }

        return zones;
    }

    /// <summary>Reads a non-empty string property, tolerating absent and null fields.</summary>
    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = element.TryGetProperty(property, out JsonElement found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? ""
            : "";
        return value.Length > 0;
    }

    /// <summary>Reads a numeric property, tolerating absent, null and out-of-range values.</summary>
    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
            ? number
            : null;

    /// <summary>Follows Nova's "next" rel in a <c>*_links</c> collection, or null at the last page.</summary>
    private static string? NextLink(JsonElement root, string linksProperty)
    {
        if (!root.TryGetProperty(linksProperty, out JsonElement links) || links.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out JsonElement rel) && rel.GetString() == "next"
                && TryReadString(link, "href", out string url))
            {
                return url;
            }
        }

        return null;
    }

    // ──────── HTTP plumbing ────────

    /// <summary>As <see cref="SendAsync"/>, but for calls whose JSON response body we read — throws if empty.</summary>
    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, KeystoneSession session, object? body, CancellationToken ct)
        => await SendAsync(method, url, session, body, ct)
           ?? throw new InvalidOperationException($"OpenStack {method} {url} returned an empty response body.");

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, KeystoneSession session, object? body, CancellationToken ct)
    {
        using HttpClient client = httpFactory.CreateClient(session.Egress);
        using HttpRequestMessage request = new(method, url);
        request.Headers.Add("X-Auth-Token", session.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            string json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenStack {method} {url} failed ({(int)response.StatusCode}): {responseBody}");
        }

        return string.IsNullOrWhiteSpace(responseBody) ? null : JsonDocument.Parse(responseBody);
    }

    private async Task SafeDeleteAsync(Func<Task> action, string kind, string id)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // Teardown is best-effort — a leftover ephemeral resource must not fail the run.
            logger.LogWarning(ex, "Failed to delete bootstrap {Kind} {Id} during teardown (continuing)", kind, id);
        }
    }
}
