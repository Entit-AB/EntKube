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
/// (and tearing down) the throwaway k3s bootstrap VM. All calls use the public
/// service-catalog endpoints from a <see cref="KeystoneSession"/> and the token as
/// <c>X-Auth-Token</c>. This is deliberately not a general-purpose OpenStack SDK.
/// </summary>
public class OpenStackComputeService(IHttpClientFactory httpClientFactory, ILogger<OpenStackComputeService> logger)
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
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, $"{network}/v2.0/floatingips", session.Token, body, ct);
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
        await CreateKeypairAsync(compute, session.Token, keypairName, sshPublicKey, ct);

        // 2. Security group allowing inbound SSH + k3s API.
        string sgId = await CreateSecurityGroupAsync(network, session.Token, name, ct);
        await AddIngressRuleAsync(network, session.Token, sgId, 22, ct);
        await AddIngressRuleAsync(network, session.Token, sgId, 6443, ct);

        // 3. Resolve image + boot the server.
        string imageId = await ResolveImageIdAsync(session, config.BootstrapImageName, ct);
        string serverId = await CreateServerAsync(
            compute, session.Token, name, imageId, config.BootstrapFlavor,
            config.BootstrapNetworkId, keypairName, sgId, cloudInitUserData, ct);

        logger.LogInformation("Bootstrap VM {ServerId} created; waiting for ACTIVE", serverId);

        // 4. Wait for ACTIVE, then associate the pre-allocated floating IP.
        await WaitForServerActiveAsync(compute, session.Token, serverId, ct);

        string portId = await GetServerPortIdAsync(network, session.Token, serverId, config.BootstrapNetworkId, ct);
        await AssociateFloatingIpAsync(network, session.Token, floatingIpId, portId, ct);

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

        await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{compute}/servers/{vm.ServerId}", session.Token, null, ct), "server", vm.ServerId);

        if (vm.FloatingIpId is not null)
            await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{network}/v2.0/floatingips/{vm.FloatingIpId}", session.Token, null, ct), "floating IP", vm.FloatingIpId);

        if (vm.SecurityGroupId is not null)
            await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{network}/v2.0/security-groups/{vm.SecurityGroupId}", session.Token, null, ct), "security group", vm.SecurityGroupId);

        if (vm.KeypairName is not null)
            await SafeDeleteAsync(() => SendAsync(HttpMethod.Delete, $"{compute}/os-keypairs/{vm.KeypairName}", session.Token, null, ct), "keypair", vm.KeypairName);
    }

    // ──────── Nova ────────

    private async Task CreateKeypairAsync(string compute, string token, string name, string publicKey, CancellationToken ct)
    {
        object body = new { keypair = new { name, public_key = publicKey } };
        await SendAsync(HttpMethod.Post, $"{compute}/os-keypairs", token, body, ct);
    }

    private async Task<string> CreateServerAsync(
        string compute, string token, string name, string imageId, string flavor,
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
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, $"{compute}/servers", token, body, ct);
        return doc!.RootElement.GetProperty("server").GetProperty("id").GetString()!;
    }

    private async Task WaitForServerActiveAsync(string compute, string token, string serverId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{compute}/servers/{serverId}", token, null, ct);
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
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{image}/v2/images?name={Uri.EscapeDataString(imageName)}", session.Token, null, ct);
        foreach (JsonElement img in doc!.RootElement.GetProperty("images").EnumerateArray())
        {
            return img.GetProperty("id").GetString()!;
        }
        throw new InvalidOperationException($"OpenStack image '{imageName}' not found in the project.");
    }

    // ──────── Neutron ────────

    private async Task<string> CreateSecurityGroupAsync(string network, string token, string name, CancellationToken ct)
    {
        object body = new { security_group = new { name, description = "EntKube bootstrap VM (ephemeral)" } };
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, $"{network}/v2.0/security-groups", token, body, ct);
        return doc!.RootElement.GetProperty("security_group").GetProperty("id").GetString()!;
    }

    private async Task AddIngressRuleAsync(string network, string token, string sgId, int port, CancellationToken ct)
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
        await SendAsync(HttpMethod.Post, $"{network}/v2.0/security-group-rules", token, body, ct);
    }

    private async Task<string> GetServerPortIdAsync(string network, string token, string serverId, string networkId, CancellationToken ct)
    {
        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, $"{network}/v2.0/ports?device_id={serverId}", token, null, ct);
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

    private async Task AssociateFloatingIpAsync(string network, string token, string floatingIpId, string portId, CancellationToken ct)
    {
        object body = new { floatingip = new { port_id = portId } };
        await SendAsync(HttpMethod.Put, $"{network}/v2.0/floatingips/{floatingIpId}", token, body, ct);
    }

    // ──────── HTTP plumbing ────────

    /// <summary>As <see cref="SendAsync"/>, but for calls whose JSON response body we read — throws if empty.</summary>
    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string token, object? body, CancellationToken ct)
        => await SendAsync(method, url, token, body, ct)
           ?? throw new InvalidOperationException($"OpenStack {method} {url} returned an empty response body.");

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        using HttpClient client = httpClientFactory.CreateClient();
        using HttpRequestMessage request = new(method, url);
        request.Headers.Add("X-Auth-Token", token);
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
