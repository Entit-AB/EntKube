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

/// <summary>Which operator-run service a managed data service is.</summary>
public enum ManagedServiceKind
{
    /// <summary>A CloudNativePG cluster.</summary>
    Postgres,
    MongoDb,
    Redis,
    Kafka,
    RabbitMq,
    Elasticsearch,
}

/// <summary>
/// Where a managed data service is in its life.
///
/// <para>The union of the six kinds' own status enums, which do not have the same members: CNPG
/// and MongoDB upgrade and restore, Kafka and Elasticsearch update, Redis and RabbitMQ do neither.
/// Mapped <b>by name</b>, and <c>ModuleApiTests</c> holds the names to the six entity enums in both
/// directions, so a status added to one kind fails a test instead of being read as another.</para>
/// </summary>
public enum ManagedServiceStatus
{
    Creating,
    Running,
    Updating,
    Upgrading,
    Restoring,
    Failed,
    Deleting,
}

/// <summary>
/// One data service EntKube manages or has adopted, whatever kind it is: what it is called, where
/// it runs, and what is true of it that a caller cannot see from the cluster.
///
/// <para><b>No credentials</b>, as with <see cref="StorageLinkSummary"/>: they live in the vault.</para>
/// </summary>
/// <param name="Id">The row's id, unique within its kind.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="ClusterId">The Kubernetes cluster it is on.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Its resource name in the cluster.</param>
/// <param name="Namespace">Its namespace.</param>
/// <param name="Status">
/// Where it is in its life. ⚠️ <see cref="ManagedServiceStatus.Failed"/> means "the last thing
/// EntKube did to it failed", NOT "it is gone": a Redis whose resize was refused is Failed and
/// still serving. A caller deciding whether something exists asks for "not Deleting", never for
/// "Running".
/// </param>
/// <param name="IsOperatorManaged">
/// False only for a RabbitMQ that EntKube adopted from somebody else's StatefulSet: its Services,
/// labels and ports are whatever that chart made them.
/// </param>
/// <param name="IsInCluster">
/// False only for a MongoDB reached by an external connection string (Atlas and the like), which
/// may have nothing in this cluster at all.
/// </param>
/// <param name="HasExporter">
/// True only for an Elasticsearch whose Prometheus exporter EntKube has turned on.
/// </param>
public sealed record ManagedServiceSummary(
    Guid Id,
    Guid TenantId,
    Guid ClusterId,
    ManagedServiceKind Kind,
    string Name,
    string Namespace,
    ManagedServiceStatus Status,
    bool IsOperatorManaged,
    bool IsInCluster,
    bool HasExporter);

/// <summary>
/// What DataServices offers the rest of EntKube.
///
/// <para>Lookup only, and deliberately only what a caller outside the module already reads.
/// Four services in Telemetry — Loki, Mimir, Tempo and EntKube's own telemetry node — held
/// <c>ApplicationDbContext</c> for a single query each, and it was the <em>same</em> query:
/// "this tenant's storage link, by id", to turn into object-store chart values. That was the
/// one method here until the monitor reconcile needed to know which data services exist. Registering buckets, minting credentials and discovering MinIO stay
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

    /// <summary>
    /// Every data service this tenant has, of every kind, on every cluster.
    ///
    /// <para><b>The whole list or an exception — never part of it.</b> The consumer is the monitor
    /// reconcile, which deletes the Prometheus monitor of whatever is missing from this answer. A
    /// list that quietly lacked one kind, because that kind's query failed, would stop the scraping
    /// of running databases. So no kind's failure is swallowed here, and there is no parameter that
    /// narrows the answer to fewer kinds.</para>
    /// </summary>
    Task<IReadOnlyList<ManagedServiceSummary>> GetManagedServicesForTenantAsync(
        Guid tenantId, CancellationToken ct = default);
}
