namespace EntKube.Contracts.DataServices;

/// <summary>Which provider hosts an object-storage link.</summary>
public enum StorageProviderKind
{
    Minio,
    AwsS3,
    AzureStorage,
    CleuraS3,
    CubeFs,
}

/// <summary>
/// A registered object-storage location, as everyone outside DataServices sees it.
///
/// <para><b>No credentials.</b> Access keys for a storage link live in the vault, not on the
/// row — so unlike <c>ClusterSummary</c> this record omits nothing that the table holds. That
/// is worth stating rather than leaving to be rediscovered: the reason this contract can hand
/// out the whole row is a property of the schema, and if a key ever lands on the entity it
/// stops being true.</para>
///
/// <para><paramref name="Endpoint"/> may be a cluster-internal Service URL (a MinIO or CubeFS
/// link discovered on a cluster), which is why callers normalise it before putting it in a
/// chart value rather than assuming it is publicly reachable.</para>
/// </summary>
/// <param name="Id">The link's id.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="EnvironmentId">Which environment it belongs to.</param>
/// <param name="Provider">Who hosts it.</param>
/// <param name="Name">Human-friendly name, for display.</param>
/// <param name="Endpoint">S3-compatible endpoint URL, if known.</param>
/// <param name="BucketName">Bucket or container name, if known.</param>
/// <param name="Region">Region, if the provider has one.</param>
public sealed record StorageLinkSummary(
    Guid Id,
    Guid TenantId,
    Guid EnvironmentId,
    StorageProviderKind Provider,
    string Name,
    string? Endpoint,
    string? BucketName,
    string? Region);

/// <summary>
/// What DataServices offers the rest of EntKube.
///
/// <para>Lookup only, and deliberately only what a caller outside the module already reads.
/// Four services in Telemetry — Loki, Mimir, Tempo and EntKube's own telemetry node — held
/// <c>ApplicationDbContext</c> for a single query each, and it was the <em>same</em> query:
/// "this tenant's storage link, by id", to turn into object-store chart values. That is the
/// one method here. Registering buckets, minting credentials and discovering MinIO stay
/// inside DataServices, because nothing outside it does those.</para>
///
/// <para>⚠️ The thing this contract is <b>not</b> is a general storage API. Four contracts
/// before it were written as complete-looking surfaces and then had no callers at all
/// (#138), so this one starts at exactly its consumers and grows when a consumer appears.
/// A method added here "for symmetry" is a method nothing measures.</para>
/// </summary>
public interface IDataServicesApi
{
    /// <summary>One storage link, or null when it is not this tenant's.</summary>
    Task<StorageLinkSummary?> GetStorageLinkAsync(
        Guid tenantId, Guid storageLinkId, CancellationToken ct = default);
}
