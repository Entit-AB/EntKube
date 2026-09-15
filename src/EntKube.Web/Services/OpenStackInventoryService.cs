using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>A bootable Glance image, as offered in the provisioning form's image pickers.</summary>
public sealed record OpenStackImage(string Id, string Name, int? MinDiskGb, int? MinRamMb)
{
    /// <summary>Label for a picker: the name plus the minimum flavor it needs, when the image declares one.</summary>
    public string Display => (MinDiskGb, MinRamMb) switch
    {
        ( > 0, > 0) => $"{Name} — needs ≥{MinDiskGb} GB disk, ≥{MinRamMb} MB RAM",
        ( > 0, _) => $"{Name} — needs ≥{MinDiskGb} GB disk",
        (_, > 0) => $"{Name} — needs ≥{MinRamMb} MB RAM",
        _ => Name
    };
}

/// <summary>A Nova flavor with the sizing that makes it recognizable in a picker.</summary>
public sealed record OpenStackFlavor(string Id, string Name, int Vcpus, int RamMb, int DiskGb)
{
    public string Display => $"{Name} — {Vcpus} vCPU, {FormatRam(RamMb)}, {DiskGb} GB disk";

    private static string FormatRam(int ramMb) =>
        ramMb >= 1024 ? $"{ramMb / 1024.0:0.#} GiB RAM" : $"{ramMb} MB RAM";
}

/// <summary>A Neutron network. <paramref name="External"/> decides which picker it belongs in.</summary>
public sealed record OpenStackNetwork(string Id, string Name, bool External, bool Shared, int SubnetCount)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name}{(Shared ? " (shared)" : "")}";
}

/// <summary>
/// Everything the provisioning form can offer as a choice for one OpenStack
/// connection, fetched in a single pass. <see cref="Error"/> is set instead of
/// throwing when the cloud could not be reached: the form falls back to free-text
/// entry, so a discovery failure must degrade the wizard rather than block it.
/// </summary>
public sealed record OpenStackInventory
{
    public IReadOnlyList<OpenStackImage> Images { get; init; } = [];
    public IReadOnlyList<OpenStackFlavor> Flavors { get; init; } = [];
    public IReadOnlyList<OpenStackNetwork> Networks { get; init; } = [];
    public IReadOnlyList<string> AvailabilityZones { get; init; } = [];

    /// <summary>Why discovery failed, or null when it succeeded.</summary>
    public string? Error { get; init; }

    /// <summary>Networks floating IPs and the API load balancer can come from.</summary>
    public IReadOnlyList<OpenStackNetwork> ExternalNetworks => [.. Networks.Where(n => n.External)];

    /// <summary>Project networks nodes and the bootstrap VM can attach to.</summary>
    public IReadOnlyList<OpenStackNetwork> TenantNetworks => [.. Networks.Where(n => !n.External)];

    public static OpenStackInventory Failed(string error) => new() { Error = error };
}

/// <summary>
/// Reads the choosable inventory of an OpenStack project — images, flavors,
/// networks and availability zones — so the provisioning form can offer them
/// instead of asking an operator to type a flavor name or paste a network UUID
/// they have to go and look up in Horizon.
///
/// Deliberately read-only and best-effort: every failure is returned as a message
/// on the inventory, because a cloud that will not answer a list call is still a
/// cloud you may want to provision into by hand.
/// </summary>
public class OpenStackInventoryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    OpenStackKeystoneClient keystone,
    OpenStackComputeService compute,
    ILogger<OpenStackInventoryService> logger)
{
    /// <summary>
    /// Authenticates the connection and lists everything the form offers. The four
    /// list calls run together — they are independent reads against different
    /// services, and serialising them makes the wizard wait for no reason.
    /// </summary>
    public async Task<OpenStackInventory> GetInventoryAsync(
        Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        // Everything is inside the try, the vault and database reads included: this runs
        // from a form that must stay usable, so no failure here may escape as an exception
        // and tear down the wizard around it.
        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            OpenStackConnection? connection = await db.OpenStackConnections
                .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == tenantId, ct);

            if (connection is null)
            {
                return OpenStackInventory.Failed("OpenStack connection not found.");
            }

            string? password = await vaultService.GetOpenStackSecretValueAsync(
                tenantId, connectionId, "OS_PASSWORD", ct);

            if (string.IsNullOrWhiteSpace(password))
            {
                return OpenStackInventory.Failed("No password stored in the vault for this connection.");
            }

            KeystoneSession session = await keystone.AuthenticateAsync(connection, password, ct);

            Task<IReadOnlyList<OpenStackImage>> images = compute.ListImagesAsync(session, ct);
            Task<IReadOnlyList<OpenStackFlavor>> flavors = compute.ListFlavorsAsync(session, ct);
            Task<IReadOnlyList<OpenStackNetwork>> networks = compute.ListNetworksAsync(session, ct);
            Task<IReadOnlyList<string>> zones = compute.ListAvailabilityZonesAsync(session, ct);

            await Task.WhenAll(images, flavors, networks, zones);

            return new OpenStackInventory
            {
                Images = [.. images.Result.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
                Flavors = [.. flavors.Result.OrderBy(f => f.Vcpus).ThenBy(f => f.RamMb).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)],
                Networks = [.. networks.Result.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)],
                AvailabilityZones = [.. zones.Result.OrderBy(z => z, StringComparer.OrdinalIgnoreCase)]
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenStack inventory discovery failed for connection {ConnectionId}", connectionId);
            return OpenStackInventory.Failed(ex.Message);
        }
    }
}
