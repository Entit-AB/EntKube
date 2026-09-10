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
/// (and tearing down) ephemeral VMs and node images. All calls use the public
/// service-catalog endpoints from a <see cref="KeystoneSession"/> and the token as
/// <c>X-Auth-Token</c>. This is deliberately not a general-purpose OpenStack SDK.
/// </summary>
public class OpenStackComputeService(OpenStackHttpFactory httpFactory, ILogger<OpenStackComputeService> logger)
{
    /// <summary>
    /// Allocates an unassociated floating IP from the external network. Done before
    /// boot so the address can be baked into the bootstrap VM's cloud-init as a kubeadm
    /// <c>certSAN</c>, then associated once the server is ACTIVE. Without it in the certificate,
    /// the kubeconfig we fetch cannot be pointed at an address we can reach.
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
    /// opening SSH (22) and the API server port (6443), boots the server with the given
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
        => await CreateEphemeralVmAsync(
            session,
            name: $"{config.ClusterName}-bootstrap",
            imageName: config.EffectiveBootstrapImageName,
            flavor: config.BootstrapFlavor,
            networkId: config.BootstrapNetworkId,
            sshPublicKey: sshPublicKey,
            cloudInitUserData: cloudInitUserData,
            // 22 to fetch admin.conf, 6443 because this node is a management cluster we drive.
            ingressPorts: [22, 6443],
            floatingIpId: floatingIpId,
            floatingIpAddress: floatingIpAddress,
            ct: ct);

    /// <summary>
    /// Boots a short-lived VM with its own keypair, security group and floating IP: imports the
    /// key, opens the given ports, boots with the supplied cloud-init, waits for ACTIVE and
    /// associates the address. Used for the bootstrap cluster and for the VM a node image is baked
    /// on — both are machines EntKube creates, reaches over SSH, and destroys.
    /// </summary>
    public async Task<BootstrapVm> CreateEphemeralVmAsync(
        KeystoneSession session,
        string name,
        string imageName,
        string flavor,
        string networkId,
        string sshPublicKey,
        string cloudInitUserData,
        IReadOnlyList<int> ingressPorts,
        string floatingIpId,
        string floatingIpAddress,
        CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        string network = session.RequireEndpoint("network");

        string keypairName = $"{name}-key";

        await CreateKeypairAsync(compute, session, keypairName, sshPublicKey, ct);

        string sgId = await CreateSecurityGroupAsync(network, session, name, ct);
        foreach (int port in ingressPorts)
        {
            await AddIngressRuleAsync(network, session, sgId, port, ct);
        }

        string imageId = await ResolveImageIdAsync(session, imageName, ct);
        string serverId = await CreateServerAsync(
            compute, session, name, imageId, flavor, networkId, keypairName, sgId, cloudInitUserData, ct);

        logger.LogInformation("Ephemeral VM {ServerId} ({Name}) created; waiting for ACTIVE", serverId, name);

        await WaitForServerActiveAsync(compute, session, serverId, ct);

        string portId = await GetServerPortIdAsync(network, session, serverId, networkId, ct);
        await AssociateFloatingIpAsync(network, session, floatingIpId, portId, ct);

        logger.LogInformation("Ephemeral VM {ServerId} ACTIVE with floating IP {Ip}", serverId, floatingIpAddress);

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
    /// Stops a server and waits for SHUTOFF. A snapshot of a running machine catches whatever was
    /// mid-write, which for a node image means a package database that may or may not be coherent.
    /// </summary>
    public async Task StopServerAsync(KeystoneSession session, string serverId, CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        await SendAsync(HttpMethod.Post, $"{compute}/servers/{serverId}/action", session, new { os_stop = (object?)null }, ct);

        for (int attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{compute}/servers/{serverId}", session, null, ct);
            string status = doc!.RootElement.GetProperty("server").GetProperty("status").GetString() ?? "";
            if (status.Equals("SHUTOFF", StringComparison.OrdinalIgnoreCase)) return;
            if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Server {serverId} entered ERROR while stopping.");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"Server {serverId} did not stop within 5 minutes.");
    }

    /// <summary>
    /// Snapshots a stopped server into a Glance image and returns its id once the image is active.
    ///
    /// <para>Nova reports the new image's id in a <c>Location</c> header, or in the body only from
    /// microversion 2.45. Rather than negotiate a microversion, the image is found by the name we
    /// gave it — which is unique because image names carry a build timestamp.</para>
    /// </summary>
    public async Task<string> SnapshotServerAsync(
        KeystoneSession session, string serverId, string imageName, CancellationToken ct = default)
    {
        string compute = session.RequireEndpoint("compute");
        await SendAsync(HttpMethod.Post, $"{compute}/servers/{serverId}/action", session,
            new { createImage = new { name = imageName, metadata = new { } } }, ct);

        string imageId = await FindImageIdByNameAsync(session, imageName, ct);
        await WaitForImageActiveAsync(session, imageId, ct);
        return imageId;
    }

    /// <summary>
    /// Attaches EntKube's metadata to a finished image. Glance takes a JSON-patch dialect of its
    /// own, and "add" fails on a property that already exists — so each is replaced when present.
    /// </summary>
    public async Task SetImagePropertiesAsync(
        KeystoneSession session, string imageId, IReadOnlyDictionary<string, string> properties, CancellationToken ct = default)
    {
        string image = session.RequireEndpoint("image");

        HashSet<string> existing = [];
        using (JsonDocument current = await SendJsonAsync(HttpMethod.Get, $"{image}/v2/images/{imageId}", session, null, ct))
        {
            foreach (JsonProperty prop in current!.RootElement.EnumerateObject())
            {
                existing.Add(prop.Name);
            }
        }

        List<object> patch = properties
            .Select(kv => (object)new
            {
                op = existing.Contains(kv.Key) ? "replace" : "add",
                path = $"/{kv.Key}",
                value = kv.Value
            })
            .ToList();

        using HttpClient client = httpFactory.CreateClient(session.Egress);
        using HttpRequestMessage request = new(HttpMethod.Patch, $"{image}/v2/images/{imageId}");
        request.Headers.Add("X-Auth-Token", session.Token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(patch), Encoding.UTF8, "application/openstack-images-v2.1-json-patch");

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Setting image properties on {imageId} failed ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary>Deletes a Glance image. Used to retire a superseded node image.</summary>
    public async Task DeleteImageAsync(KeystoneSession session, string imageId, CancellationToken ct = default)
    {
        string image = session.RequireEndpoint("image");
        await SendAsync(HttpMethod.Delete, $"{image}/v2/images/{imageId}", session, null, ct);
    }

    private async Task<string> FindImageIdByNameAsync(KeystoneSession session, string imageName, CancellationToken ct)
    {
        string image = session.RequireEndpoint("image");

        // The snapshot is queued asynchronously, so the image may not exist for a moment.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using JsonDocument doc = await SendJsonAsync(
                HttpMethod.Get, $"{image}/v2/images?name={Uri.EscapeDataString(imageName)}", session, null, ct);

            foreach (JsonElement img in doc!.RootElement.GetProperty("images").EnumerateArray())
            {
                return img.GetProperty("id").GetString()!;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"Snapshot image '{imageName}' never appeared in Glance.");
    }

    private async Task WaitForImageActiveAsync(KeystoneSession session, string imageId, CancellationToken ct)
    {
        string image = session.RequireEndpoint("image");

        // Uploading a multi-gigabyte snapshot is not quick; this is the long pole of a build.
        for (int attempt = 0; attempt < 120; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{image}/v2/images/{imageId}", session, null, ct);
            string status = doc!.RootElement.GetProperty("status").GetString() ?? "";

            if (status.Equals("active", StringComparison.OrdinalIgnoreCase)) return;
            if (status.Equals("killed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Image {imageId} ended in status '{status}'.");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        throw new TimeoutException($"Image {imageId} did not become active within 30 minutes.");
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
