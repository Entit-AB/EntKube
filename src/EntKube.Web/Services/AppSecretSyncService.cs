using EntKube.Web.Data;
using EntKube.Web.Services.Clusters;

namespace EntKube.Web.Services;

/// <summary>Which kind of binding an outcome belongs to. Used for grouping, not for dispatch.</summary>
public enum SyncFamily
{
    Database,
    Cache,
    Messaging,
    Storage,
    Identity,
}

/// <summary>Which service owns a <see cref="DatabaseBinding"/>'s credentials.</summary>
public enum DatabaseBindingTarget
{
    /// <summary>
    /// ⚠️ A binding naming no database at all. The three ids are each <c>Guid?</c> and documented
    /// "mutually exclusive", which guarantees at most one — not at least one — and nothing in the
    /// schema requires one. The page this came from ran an <c>if / else if / else if</c> with no
    /// final branch and then reported success, so such a row was reported as synced while nothing
    /// was sent. Not a defect anyone has hit; it is a silent success, which is worse than a loud
    /// failure and costs one enum member to remove.
    /// </summary>
    None,
    Cnpg,
    Mongo,
    RegisteredPostgres,
}

/// <summary>One binding's result. <c>Error</c> is null when it synced.</summary>
public sealed record BindingSyncOutcome(SyncFamily Family, string SecretName, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>A whole family that could not even be listed, so none of its bindings were tried.</summary>
public sealed record FamilyLoadFailure(SyncFamily Family, string Error);

/// <summary>
/// What a whole-app sync did, as data rather than as display text.
///
/// <para>⚠️ The page this was extracted from accumulated <c>"✓ db/{name}"</c> and
/// <c>"✗ identity/{name}: {msg}"</c> strings as it went. That is what made the operation
/// uncallable rather than merely misplaced: any caller that is not a Razor page — the CLI, the
/// API, another service — would inherit a display format as its contract. Formatting is the
/// caller's job and stays in the page.</para>
/// </summary>
public sealed record AppSecretSyncReport(
    string? VaultOutput,
    IReadOnlyList<BindingSyncOutcome> Bindings,
    IReadOnlyList<FamilyLoadFailure> LoadFailures,
    string? FatalError)
{
    public bool AnythingFailed => FatalError is not null
        || LoadFailures.Count > 0
        || Bindings.Any(b => !b.Succeeded);
}

/// <summary>One binding, reduced to the two things the orchestration needs of it.</summary>
public sealed record BindingToSync(string SecretName, bool SyncEnabled, Func<CancellationToken, Task> SyncAsync);

/// <summary>
/// One family of bindings: how to list them, and (via <see cref="BindingToSync"/>) how to sync one.
///
/// <para>⚠️ THIS INDIRECTION IS WHY THE ORCHESTRATION IS TESTABLE. The nine services this operation
/// needs are concrete classes with non-virtual methods, and nothing in the suite mocks any of them,
/// so Moq cannot stand in for them. Expressed as delegates, the part worth testing — the filtering,
/// the two layers of failure isolation, the shape of the report — is exercised with lambdas, while
/// the nine services remain one line of wiring each in <see cref="AppSecretSyncService"/>.</para>
/// </summary>
public sealed record BindingFamily(SyncFamily Family, Func<CancellationToken, Task<IReadOnlyList<BindingToSync>>> LoadAsync);

/// <summary>
/// Re-sync every credential an app's deployments have bound into their clusters: the app's own
/// vault secrets, then each deployment's database, cache, messaging, storage and identity bindings.
///
/// <para><b>Why this exists as a service.</b> It was 126 lines inside
/// <c>PortalSecretsDetail.razor</c> with no render state in it at all, so the only way to run it
/// was to click a button. "Re-sync every bound credential for this app" is an operation the CLI and
/// the API both need; see docs/decomposition.md §6.2, which measured this as the highest-value
/// single extraction in Portal.</para>
///
/// <para>⚠️ FAILURE IS ISOLATED TWICE, and both layers are deliberate. A family that cannot be
/// listed must not stop the other families, and one binding that will not sync must not stop its
/// siblings — an operator pressing "sync everything" after rotating one secret should not be
/// stopped by an unrelated cluster being unreachable. Both are tested by making exactly one thing
/// throw and asserting the rest still ran.</para>
///
/// <para>⚠️ This sits in Secrets while depending on DataServices, Identity and Delivery services
/// directly. That is Phase 0: strictly better than a Razor page doing it, and exactly the edge
/// Phase 1's module contracts exist to replace. Recorded rather than hidden.</para>
/// </summary>
public sealed class AppSecretSyncService(
    VaultService vault,
    DeploymentService deployments,
    CnpgService cnpg,
    MongoService mongo,
    RegisteredPostgresService registeredPostgres,
    RedisService redis,
    RabbitMQService rabbit,
    StorageService storage,
    KeycloakService keycloak,
    ILogger<AppSecretSyncService> logger)
{
    /// <summary>
    /// Which service owns this binding's credentials. Pure, so the one piece of real dispatch in
    /// the operation can be tested without constructing anything.
    /// </summary>
    public static DatabaseBindingTarget ResolveTarget(DatabaseBinding binding)
    {
        if (binding.CnpgDatabaseId.HasValue) return DatabaseBindingTarget.Cnpg;
        if (binding.MongoDatabaseId.HasValue) return DatabaseBindingTarget.Mongo;
        if (binding.RegisteredPostgresDatabaseId.HasValue) return DatabaseBindingTarget.RegisteredPostgres;
        return DatabaseBindingTarget.None;
    }

    /// <summary>
    /// The orchestration, with the services behind delegates. Public so it can be tested directly:
    /// it is the part that carries the behaviour, and it is the part the collaborators' concrete
    /// classes would otherwise make untestable.
    /// </summary>
    public static async Task<(List<BindingSyncOutcome> Outcomes, List<FamilyLoadFailure> LoadFailures)>
        RunFamiliesAsync(IReadOnlyList<BindingFamily> families, CancellationToken ct = default)
    {
        List<BindingSyncOutcome> outcomes = [];
        List<FamilyLoadFailure> loadFailures = [];

        foreach (BindingFamily family in families)
        {
            IReadOnlyList<BindingToSync> bindings;
            try
            {
                bindings = await family.LoadAsync(ct);
            }
            catch (Exception ex)
            {
                // Outer isolation: this family is unlistable, the others are unaffected.
                loadFailures.Add(new FamilyLoadFailure(family.Family, ex.Message));
                continue;
            }

            foreach (BindingToSync binding in bindings.Where(b => b.SyncEnabled))
            {
                try
                {
                    await binding.SyncAsync(ct);
                    outcomes.Add(new BindingSyncOutcome(family.Family, binding.SecretName, null));
                }
                catch (Exception ex)
                {
                    // Inner isolation: one binding, not the rest of the family.
                    outcomes.Add(new BindingSyncOutcome(family.Family, binding.SecretName, ex.Message));
                }
            }
        }

        return (outcomes, loadFailures);
    }

    /// <summary>
    /// Sync one app: its vault secrets, then every binding of every deployment it has.
    /// <paramref name="environmentId"/> scopes the vault half when the caller is in an environment
    /// context, exactly as the page did.
    /// </summary>
    public async Task<AppSecretSyncReport> SyncAppAsync(
        Guid tenantId, Guid appId, Guid? environmentId = null, CancellationToken ct = default)
    {
        string? vaultOutput = null;
        List<BindingSyncOutcome> outcomes = [];
        List<FamilyLoadFailure> loadFailures = [];

        try
        {
            HelmExecutionResult vaultResult =
                await vault.SyncAppSecretsToKubernetesAsync(tenantId, appId, environmentId: environmentId);
            if (!string.IsNullOrEmpty(vaultResult.Output)) vaultOutput = vaultResult.Output;

            foreach (AppDeployment deployment in await deployments.GetDeploymentsAsync(tenantId, appId))
            {
                (List<BindingSyncOutcome> o, List<FamilyLoadFailure> f) =
                    await RunFamiliesAsync(FamiliesFor(tenantId, deployment.Id), ct);
                outcomes.AddRange(o);
                loadFailures.AddRange(f);
            }
        }
        catch (Exception ex)
        {
            // Only reached by something outside a family — the vault half, or listing deployments.
            logger.LogWarning(ex, "Secret sync for app {AppId} stopped early", appId);
            return new AppSecretSyncReport(vaultOutput, outcomes, loadFailures, ex.Message);
        }

        return new AppSecretSyncReport(vaultOutput, outcomes, loadFailures, null);
    }

    /// <summary>
    /// The wiring, and deliberately nothing else: one line per family, each naming how to list its
    /// bindings and how to sync one. Behaviour lives in <see cref="RunFamiliesAsync"/>, which is
    /// tested; this is the part that cannot be unit-tested and so is kept free of decisions.
    /// </summary>
    private IReadOnlyList<BindingFamily> FamiliesFor(Guid tenantId, Guid deploymentId) =>
    [
        new(SyncFamily.Database, async _ =>
            [.. (await cnpg.GetDatabaseBindingsAsync(tenantId, deploymentId))
                .Select(b => new BindingToSync(b.KubernetesSecretName, b.SyncEnabled,
                    _ => SyncDatabaseAsync(tenantId, b)))]),

        new(SyncFamily.Cache, async _ =>
            [.. (await redis.GetCacheBindingsForDeploymentAsync(tenantId, deploymentId))
                .Select(b => new BindingToSync(b.KubernetesSecretName, b.SyncEnabled,
                    _ => redis.SyncCacheBindingAsync(tenantId, b.Id)))]),

        new(SyncFamily.Messaging, async _ =>
            [.. (await rabbit.GetMessagingBindingsForDeploymentAsync(tenantId, deploymentId))
                .Select(b => new BindingToSync(b.KubernetesSecretName, b.SyncEnabled,
                    _ => rabbit.SyncMessagingBindingAsync(tenantId, b.Id)))]),

        new(SyncFamily.Storage, async _ =>
            [.. (await storage.GetBindingsForDeploymentAsync(tenantId, deploymentId))
                .Select(b => new BindingToSync(b.KubernetesSecretName, b.SyncEnabled,
                    _ => storage.SyncStorageBindingAsync(tenantId, b.Id)))]),

        new(SyncFamily.Identity, async _ =>
            [.. (await keycloak.GetIdentityBindingsForDeploymentAsync(tenantId, deploymentId))
                .Select(b => new BindingToSync(b.KubernetesSecretName, b.SyncEnabled,
                    _ => keycloak.SyncIdentityBindingAsync(tenantId, b.Id)))]),
    ];

    private Task SyncDatabaseAsync(Guid tenantId, DatabaseBinding binding) => ResolveTarget(binding) switch
    {
        DatabaseBindingTarget.Cnpg => cnpg.SyncDatabaseCredentialsToK8sAsync(
            tenantId, binding.CnpgDatabase!.CnpgClusterId, binding.CnpgDatabaseId!.Value),

        DatabaseBindingTarget.Mongo => mongo.SyncDatabaseCredentialsToK8sAsync(
            tenantId, binding.MongoDatabase!.MongoClusterId, binding.MongoDatabaseId!.Value),

        DatabaseBindingTarget.RegisteredPostgres => registeredPostgres.SyncCredentialsToK8sAsync(
            tenantId, binding.RegisteredPostgresDatabase!.RegisteredPostgresInstanceId,
            binding.RegisteredPostgresDatabaseId!.Value),

        // ⚠️ Loud rather than silent. The page fell through its if/else chain and reported success.
        _ => throw new InvalidOperationException(
            $"Binding '{binding.KubernetesSecretName}' names no database, so there is nothing to sync."),
    };
}
