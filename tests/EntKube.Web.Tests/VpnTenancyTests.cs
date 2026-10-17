using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose VPN tunnel <see cref="VpnService"/> is willing to read, configure and peer.
///
/// <para>Thirteen of its methods took a tunnel or an endpoint id and no tenant, while
/// <c>GetTunnelsForTenantAsync</c>, <c>DetectClusterCidrsAsync</c> and
/// <c>ReconcileGatewayLinksAsync</c> beside them had always taken one. <c>VpnTunnel</c> carries a
/// <c>TenantId</c>; neither endpoint type does, so both travel endpoint → tunnel.</para>
///
/// <para>⚠️ Two of the thirteen already <em>took</em> a <c>tenantId</c> and used it only as the
/// vault's scope — <c>AddRemoteEndpointAsync</c> (seventh parameter) and <c>RotatePskAsync</c>
/// (third). A tenant that only ever reaches the vault authorizes nothing about the row, and the
/// first of those is the sharpest call in the service: a remote endpoint becomes a connection
/// block in the tunnel's <c>swanctl.conf</c>, with a PSK and a subnet list the caller chooses.
/// Adding one to a stranger's tunnel is a VPN peer into their network.</para>
/// </summary>
public class VpnTenancyTests : IDisposable
{
    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(RootKey);
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly VpnService sut;

    public VpnTenancyTests()
    {
        sut = new VpnService(
            testDb.Factory, testDb.CreateVaultService(),
            TestServices.BuildLifecycle(testDb, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object));
    }

    public void Dispose()
    {
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A tenant with an environment and a cluster of its own.</summary>
    private (Guid Tenant, Guid Cluster) Seed(string slug)
    {
        Guid tenant = Guid.NewGuid(), environment = Guid.NewGuid(), cluster = Guid.NewGuid();

        using ApplicationDbContext db = testDb.CreateContext();
        db.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
        db.Environments.Add(new Data.Environment
        {
            Id = environment, TenantId = tenant, Name = $"{slug}-prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster, TenantId = tenant, EnvironmentId = environment,
            Name = $"{slug}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.SaveChanges();

        // ⚠️ The PSK writes go through the vault, so a tenant with no vault cannot even add its
        // OWN remote endpoint — and a test where the owner's call fails too proves nothing about
        // scoping. Both tenants get one.
        testDb.CreateVaultService().InitializeVaultAsync(tenant).GetAwaiter().GetResult();

        return (tenant, cluster);
    }

    /// <summary>
    /// The tunnel itself: reading one returns both endpoint lists — public IPs, the subnets on
    /// each side, the peer identities — which is a map of the other tenant's network and of
    /// whoever they are peered with. The crypto settings are the other half: weakening somebody
    /// else's IKE or ESP proposal is an attack on the tunnel, not a config typo.
    /// </summary>
    [Fact]
    public async Task A_tunnel_is_not_readable_or_configurable_by_another_tenant()
    {
        var us = Seed("us");
        var them = Seed("them");

        VpnTunnel theirs = await sut.CreateTunnelAsync(
            them.Tenant, "their-s2s", VpnTunnelType.SiteToSite, "theirs");

        (await sut.GetTunnelWithDetailsAsync(us.Tenant, theirs.Id)).Should().BeNull(
            "the tunnel exists, so this is a refusal");
        (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id)).Should().NotBeNull(
            "the owner still sees it");
        (await sut.GetTunnelsForTenantAsync(us.Tenant)).Should().BeEmpty();

        Func<Task> weaken = () => sut.UpdateCryptoSettingsAsync(
            us.Tenant, theirs.Id, 1, "aes128-sha1-modp1024", "aes128-sha1", 5, 10);
        Func<Task> flipStatus = () => sut.UpdateTunnelStatusAsync(
            us.Tenant, theirs.Id, VpnTunnelStatus.Active);
        Func<Task> drop = () => sut.DeleteTunnelAsync(us.Tenant, theirs.Id);

        await weaken.Should().ThrowAsync<InvalidOperationException>();
        await flipStatus.Should().ThrowAsync<InvalidOperationException>();
        await drop.Should().ThrowAsync<InvalidOperationException>();

        VpnTunnel survived = (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id))!;
        survived.IkeProposal.Should().NotBe("aes128-sha1-modp1024",
            "their negotiated crypto must be untouched");
        survived.Status.Should().Be(VpnTunnelStatus.Draft);

        // And the owner can still do all three, so the refusals were about the crossing.
        await sut.UpdateCryptoSettingsAsync(
            them.Tenant, theirs.Id, 2, "aes256-sha256-modp2048", "aes256-sha256", 30, 120);
        await sut.UpdateTunnelStatusAsync(them.Tenant, theirs.Id, VpnTunnelStatus.Active);
        await sut.DeleteTunnelAsync(them.Tenant, theirs.Id);
        (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id)).Should().BeNull();
    }

    /// <summary>
    /// A local endpoint names a tunnel and a cluster, so it is a pair: the cluster is where the
    /// gateway config is then synced. Its subnets are what gets routed down the tunnel, so an
    /// edit changes which of the owner's networks the peer can reach.
    /// </summary>
    [Fact]
    public async Task A_local_endpoint_cannot_cross_a_tenant_on_either_side()
    {
        var us = Seed("us");
        var them = Seed("them");

        VpnTunnel ours = await sut.CreateTunnelAsync(us.Tenant, "our-mesh", VpnTunnelType.ClusterMesh);
        VpnTunnel theirs = await sut.CreateTunnelAsync(them.Tenant, "their-mesh", VpnTunnelType.ClusterMesh);

        // Their tunnel, our cluster.
        Func<Task> ontoTheirTunnel = () => sut.AddLocalEndpointAsync(
            us.Tenant, theirs.Id, us.Cluster, ["10.0.0.0/24"], VpnEndpointRole.Gateway);
        // Our tunnel, their cluster.
        Func<Task> ontoTheirCluster = () => sut.AddLocalEndpointAsync(
            us.Tenant, ours.Id, them.Cluster, ["10.0.0.0/24"], VpnEndpointRole.Gateway);

        await ontoTheirTunnel.Should().ThrowAsync<InvalidOperationException>();
        await ontoTheirCluster.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id))!
            .LocalEndpoints.Should().BeEmpty("nothing of ours was attached to their tunnel");

        // Their own endpoint, then every by-id call against it as us.
        VpnLocalEndpoint theirEndpoint = await sut.AddLocalEndpointAsync(
            them.Tenant, theirs.Id, them.Cluster, ["192.168.50.0/24"], VpnEndpointRole.Gateway);

        Func<Task> reroute = () => sut.UpdateLocalEndpointAsync(
            us.Tenant, theirEndpoint.Id, ["0.0.0.0/0"], "203.0.113.9");
        Func<Task> relink = () => sut.LinkComponentAsync(us.Tenant, theirEndpoint.Id, Guid.NewGuid());
        Func<Task> remove = () => sut.RemoveLocalEndpointAsync(us.Tenant, theirEndpoint.Id);
        Func<Task> sync = () => sut.SyncConfigToClusterAsync(theirEndpoint.Id, us.Tenant);

        await reroute.Should().ThrowAsync<InvalidOperationException>();
        await relink.Should().ThrowAsync<InvalidOperationException>();
        await remove.Should().ThrowAsync<InvalidOperationException>();
        await sync.Should().ThrowAsync<InvalidOperationException>();

        VpnLocalEndpoint intact = (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id))!
            .LocalEndpoints.Should().ContainSingle().Subject;
        intact.Subnets.Should().Contain("192.168.50.0/24",
            "the subnets are what gets routed down the tunnel");
        intact.PublicIp.Should().NotBe("203.0.113.9");
        intact.ComponentId.Should().BeNull();
    }

    /// <summary>
    /// ⚠️ The remote endpoint — the peer. This is the call that already took a <c>tenantId</c> in
    /// seventh position and filed the PSK under it while never checking the tunnel, so a peer with
    /// a chosen PSK and a chosen subnet list could be appended to any tunnel in the installation.
    /// </summary>
    [Fact]
    public async Task A_remote_peer_cannot_be_added_to_another_tenants_tunnel()
    {
        var us = Seed("us");
        var them = Seed("them");

        VpnTunnel theirs = await sut.CreateTunnelAsync(
            them.Tenant, "their-s2s", VpnTunnelType.SiteToSite);

        Func<Task> peerIn = () => sut.AddRemoteEndpointAsync(
            us.Tenant, theirs.Id, "attacker", "203.0.113.9", ["0.0.0.0/0"],
            VpnAuthMode.Psk, "chosen-psk");
        await peerIn.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id))!
            .RemoteEndpoints.Should().BeEmpty("a peer in their swanctl.conf is access to their network");

        // Their own peer, then the by-id calls against it.
        VpnRemoteEndpoint theirPeer = await sut.AddRemoteEndpointAsync(
            them.Tenant, theirs.Id, "partner", "198.51.100.7", ["172.16.0.0/16"],
            VpnAuthMode.Psk, "their-psk");

        Func<Task> repoint = () => sut.UpdateRemoteEndpointAsync(
            us.Tenant, theirPeer.Id, "203.0.113.9", ["0.0.0.0/0"]);
        Func<Task> rotate = () => sut.RotatePskAsync(us.Tenant, theirPeer.Id, "new-psk");
        Func<Task> remove = () => sut.RemoveRemoteEndpointAsync(us.Tenant, theirPeer.Id);

        await repoint.Should().ThrowAsync<InvalidOperationException>();
        await rotate.Should().ThrowAsync<InvalidOperationException>();
        await remove.Should().ThrowAsync<InvalidOperationException>();

        VpnRemoteEndpoint intact = (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id))!
            .RemoteEndpoints.Should().ContainSingle().Subject;
        intact.PublicIp.Should().Be("198.51.100.7",
            "repointing PublicIp aims an existing tunnel at a different peer");
        intact.Subnets.Should().Contain("172.16.0.0/16");

        // The owner can still rotate and remove.
        await sut.RotatePskAsync(them.Tenant, theirPeer.Id, "rotated");
        await sut.RemoveRemoteEndpointAsync(them.Tenant, theirPeer.Id);
        (await sut.GetTunnelWithDetailsAsync(them.Tenant, theirs.Id))!
            .RemoteEndpoints.Should().BeEmpty();
    }

    /// <summary>
    /// The generators. <c>GenerateSwanctlSecretsAsync</c> puts the PSKs in cleartext into the
    /// string it returns, and its tunnel read used to be unscoped while the vault was asked as a
    /// tenant — so the two halves could disagree about whose tunnel it was.
    /// </summary>
    [Fact]
    public async Task Swanctl_output_is_not_generated_for_another_tenants_tunnel()
    {
        var us = Seed("us");
        var them = Seed("them");

        VpnTunnel theirs = await sut.CreateTunnelAsync(
            them.Tenant, "their-s2s", VpnTunnelType.SiteToSite);
        await sut.AddLocalEndpointAsync(
            them.Tenant, theirs.Id, them.Cluster, ["192.168.50.0/24"], VpnEndpointRole.Gateway);
        await sut.AddRemoteEndpointAsync(
            them.Tenant, theirs.Id, "partner", "198.51.100.7", ["172.16.0.0/16"],
            VpnAuthMode.Psk, "their-psk");

        Func<Task> config = () => sut.GenerateSwanctlConfigAsync(us.Tenant, theirs.Id);
        Func<Task> secrets = () => sut.GenerateSwanctlSecretsAsync(us.Tenant, theirs.Id);

        await config.Should().ThrowAsync<InvalidOperationException>();
        await secrets.Should().ThrowAsync<InvalidOperationException>();

        // The owner's own generation still works and still carries the PSK.
        string theirConfig = await sut.GenerateSwanctlConfigAsync(them.Tenant, theirs.Id);
        theirConfig.Should().Contain("198.51.100.7");

        string theirSecrets = await sut.GenerateSwanctlSecretsAsync(them.Tenant, theirs.Id);
        theirSecrets.Should().Contain("their-psk",
            "the PSK has to be in the owner's own output, or the refusals above prove nothing");
    }
}
