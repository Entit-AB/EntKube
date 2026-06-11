using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntKube.Web.Data;
using EntKube.Web.Data.Backup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EntKube.Web.Services;

/// <summary>
/// Exports the full EntKube app state to a gzip-compressed JSON bundle and
/// restores it on a target installation. Secrets are decrypted during export
/// and re-encrypted with fresh DEKs on restore — the bundle does not carry
/// the root key, but it does carry plaintext secret values, so treat it as
/// sensitive material (encrypt at rest, restrict access).
/// </summary>
public class BackupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultEncryptionService encryption,
    ILogger<BackupService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    // ── Export ──────────────────────────────────────────────────────────────

    public async Task<byte[]> ExportAsync(string performedBy)
    {
        await using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Unseal every tenant's DEK so we can decrypt individual secrets.
        Dictionary<Guid, byte[]> dekByVaultId = [];
        List<VaultRecord> vaultRecords = [];

        foreach (SecretVault vault in await db.SecretVaults.AsNoTracking().ToListAsync())
        {
            byte[] dek = encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
            dekByVaultId[vault.Id] = dek;
            vaultRecords.Add(new VaultRecord { Id = vault.Id, TenantId = vault.TenantId, CreatedAt = vault.CreatedAt });
        }

        List<VaultSecretRecord> secretRecords = [];
        foreach (VaultSecret s in await db.VaultSecrets.AsNoTracking().ToListAsync())
        {
            if (!dekByVaultId.TryGetValue(s.VaultId, out byte[]? dek))
            {
                logger.LogWarning("VaultSecret {Id} references unknown vault {VaultId} — skipping.", s.Id, s.VaultId);
                continue;
            }
            string plaintext = encryption.Decrypt(dek, s.EncryptedValue, s.Nonce);
            secretRecords.Add(new VaultSecretRecord(
                s.Id, s.VaultId, s.Name, plaintext,
                s.AppId, s.ComponentId, s.StorageLinkId, s.OpenStackConnectionId,
                s.CnpgClusterId, s.CnpgDatabaseId, s.MongoDatabaseId, s.MongoClusterId,
                s.RegisteredPostgresDatabaseId, s.RabbitMQClusterId,
                s.RedisClusterId, s.VpnRemoteEndpointId, s.GitRepositoryId, s.CustomerGitCredentialId,
                s.SyncToKubernetes, s.KubernetesClusterId, s.KubernetesSecretName, s.KubernetesNamespace,
                s.CreatedAt, s.UpdatedAt));
        }

        List<DockerCredentialRecord> credRecords = [];
        foreach (DockerRegistryCredential c in await db.DockerRegistryCredentials.AsNoTracking().ToListAsync())
        {
            if (!dekByVaultId.TryGetValue(c.VaultId, out byte[]? dek))
            {
                logger.LogWarning("DockerCredential {Id} references unknown vault {VaultId} — skipping.", c.Id, c.VaultId);
                continue;
            }
            string plainPassword = encryption.Decrypt(dek, c.EncryptedPassword, c.PasswordNonce);
            credRecords.Add(new DockerCredentialRecord(
                c.Id, c.VaultId, c.Name, c.RegistryType, c.Server, c.Username, plainPassword,
                c.Email, c.AppId, c.KubernetesClusterId, c.KubernetesSecretName, c.KubernetesNamespace,
                c.CreatedAt, c.UpdatedAt));
        }

        List<UserRecord> userRecords = (await db.Users.AsNoTracking().ToListAsync())
            .Select(u => new UserRecord(
                u.Id, u.UserName, u.Email, u.PasswordHash, u.EmailConfirmed,
                u.NormalizedUserName, u.NormalizedEmail, u.SecurityStamp, u.ConcurrencyStamp,
                u.PhoneNumber, u.PhoneNumberConfirmed, u.TwoFactorEnabled,
                u.LockoutEnd, u.LockoutEnabled, u.AccessFailedCount))
            .ToList();

        List<RoleRecord> roleRecords = (await db.Roles.AsNoTracking().ToListAsync())
            .Select(r => new RoleRecord(r.Id, r.Name, r.NormalizedName, r.ConcurrencyStamp))
            .ToList();

        List<UserRoleRecord> userRoleRecords = (await db.Set<IdentityUserRole<string>>().AsNoTracking().ToListAsync())
            .Select(ur => new UserRoleRecord(ur.UserId, ur.RoleId))
            .ToList();

        BackupBundle bundle = new()
        {
            CreatedBy = performedBy,
            CreatedAt = DateTime.UtcNow,
            Users = userRecords,
            Roles = roleRecords,
            UserRoles = userRoleRecords,
            Tenants = await db.Tenants.AsNoTracking().ToListAsync(),
            TenantRoles = await db.TenantRoles.AsNoTracking().ToListAsync(),
            TenantMemberships = await db.TenantMemberships.AsNoTracking().ToListAsync(),
            Groups = await db.Groups.AsNoTracking().ToListAsync(),
            GroupMemberships = await db.GroupMemberships.AsNoTracking().ToListAsync(),
            Environments = await db.Environments.AsNoTracking().ToListAsync(),
            Customers = await db.Customers.AsNoTracking().ToListAsync(),
            CustomerAccesses = await db.CustomerAccesses.AsNoTracking().ToListAsync(),
            Apps = await db.Apps.AsNoTracking().ToListAsync(),
            AppEnvironments = await db.AppEnvironments.AsNoTracking().ToListAsync(),
            AppNetworkPolicies = await db.AppNetworkPolicies.AsNoTracking().ToListAsync(),
            AppQuotas = await db.AppQuotas.AsNoTracking().ToListAsync(),
            AppRbacPolicies = await db.AppRbacPolicies.AsNoTracking().ToListAsync(),
            AppRbacRules = await db.AppRbacRules.AsNoTracking().ToListAsync(),
            KubernetesClusters = await db.KubernetesClusters.AsNoTracking().ToListAsync(),
            ClusterComponents = await db.ClusterComponents.AsNoTracking().ToListAsync(),
            ExternalRoutes = await db.ExternalRoutes.AsNoTracking().ToListAsync(),
            OpenStackConnections = await db.OpenStackConnections.AsNoTracking().ToListAsync(),
            StorageLinks = await db.StorageLinks.AsNoTracking().ToListAsync(),
            AppDeployments = await db.AppDeployments.AsNoTracking().ToListAsync(),
            DeploymentManifests = await db.DeploymentManifests.AsNoTracking().ToListAsync(),
            StorageBindings = await db.StorageBindings.AsNoTracking().ToListAsync(),
            CnpgClusters = await db.CnpgClusters.AsNoTracking().ToListAsync(),
            CnpgDatabases = await db.CnpgDatabases.AsNoTracking().ToListAsync(),
            MongoClusters = await db.MongoClusters.AsNoTracking().ToListAsync(),
            MongoDatabases = await db.MongoDatabases.AsNoTracking().ToListAsync(),
            RabbitMQClusters = await db.RabbitMQClusters.AsNoTracking().ToListAsync(),
            RegisteredPostgresInstances = await db.RegisteredPostgresInstances.AsNoTracking().ToListAsync(),
            RegisteredPostgresDatabases = await db.RegisteredPostgresDatabases.AsNoTracking().ToListAsync(),
            DatabaseBindings = await db.DatabaseBindings.AsNoTracking().ToListAsync(),
            MessagingBindings = await db.MessagingBindings.AsNoTracking().ToListAsync(),
            GitRepositories = await db.GitRepositories.AsNoTracking().ToListAsync(),
            GitKnownHosts = await db.GitKnownHosts.AsNoTracking().ToListAsync(),
            CustomerGitCredentials = await db.CustomerGitCredentials.AsNoTracking().ToListAsync(),
            CustomerGitRepoPolicies = await db.CustomerGitRepoPolicies.AsNoTracking().ToListAsync(),
            RedisClusters = await db.RedisClusters.AsNoTracking().ToListAsync(),
            CacheBindings = await db.CacheBindings.AsNoTracking().ToListAsync(),
            VpnTunnels = await db.VpnTunnels.AsNoTracking().ToListAsync(),
            VpnLocalEndpoints = await db.VpnLocalEndpoints.AsNoTracking().ToListAsync(),
            VpnRemoteEndpoints = await db.VpnRemoteEndpoints.AsNoTracking().ToListAsync(),
            KeycloakComponentConfigs = await db.KeycloakComponentConfigs.AsNoTracking().ToListAsync(),
            KeycloakThemes = await db.KeycloakThemes.AsNoTracking().ToListAsync(),
            KeycloakRealms = await db.KeycloakRealms.AsNoTracking().ToListAsync(),
            HarborComponentConfigs = await db.HarborComponentConfigs.AsNoTracking().ToListAsync(),
            HarborProjects = await db.HarborProjects.AsNoTracking().ToListAsync(),
            NotificationChannels = await db.NotificationChannels.AsNoTracking().ToListAsync(),
            SlaTargets = await db.SlaTargets.AsNoTracking().ToListAsync(),
            MaintenanceWindows = await db.MaintenanceWindows.AsNoTracking().ToListAsync(),
            SecretVaults = vaultRecords,
            VaultSecrets = secretRecords,
            DockerCredentials = credRecords,
        };

        using MemoryStream ms = new();
        await using (GZipStream gz = new(ms, CompressionLevel.Optimal, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gz, bundle, JsonOptions);

        logger.LogInformation("Backup exported by {User}: {Tenants} tenants, {Secrets} secrets, {Creds} registry credentials.",
            performedBy, bundle.Tenants.Count, bundle.VaultSecrets.Count, bundle.DockerCredentials.Count);

        return ms.ToArray();
    }

    // ── Import ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a backup bundle. When <paramref name="wipeExisting"/> is true,
    /// ALL existing data is deleted before the import begins (the wipe and the
    /// insert are wrapped in a single transaction so a failed import rolls back
    /// the wipe too). Designed for PostgreSQL; the TRUNCATE CASCADE syntax is
    /// Postgres-specific.
    /// </summary>
    public async Task ImportAsync(Stream bundleStream, bool wipeExisting)
    {
        // Buffer the stream so we can inspect the magic bytes without consuming them.
        // (Blazor's IBrowserFile stream is not seekable.)
        using MemoryStream ms = new();
        await bundleStream.CopyToAsync(ms);
        ms.Position = 0;

        // Gzip magic: 0x1F 0x8B. Accept plain JSON too — macOS browsers/Archive Utility
        // sometimes auto-decompress .gz downloads, leaving the user with a plain .json file.
        bool isGzip = ms.Length >= 2 && ms.ReadByte() == 0x1F && ms.ReadByte() == 0x8B;
        ms.Position = 0;

        await using Stream jsonStream = isGzip ? new GZipStream(ms, CompressionMode.Decompress) : ms;
        BackupBundle? bundle = await JsonSerializer.DeserializeAsync<BackupBundle>(jsonStream, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize backup bundle.");

        if (bundle.Version != 1)
            throw new InvalidDataException($"Unsupported backup version: {bundle.Version}. Only version 1 is supported.");

        await using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (!wipeExisting && await db.Roles.AnyAsync())
            throw new InvalidOperationException(
                "The target database already contains data. " +
                "Enable \"Wipe all existing data\" to overwrite it, or restore to a fresh installation.");

        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync();

        try
        {
            if (wipeExisting)
            {
                logger.LogInformation("Wiping all existing data before restore.");
                // TRUNCATE ... CASCADE removes the listed tables and all tables that
                // transitively reference them via FK constraints, regardless of ON DELETE
                // behaviour. Three top-level tables cover the entire schema:
                //   Tenants  → every tenant-owned entity (clusters, apps, secrets, …)
                //   AspNetUsers → memberships, customer accesses, group memberships
                //   AspNetRoles → role assignments
                await db.Database.ExecuteSqlRawAsync(@"TRUNCATE ""Tenants"" CASCADE");
                await db.Database.ExecuteSqlRawAsync(@"TRUNCATE ""AspNetUsers"" CASCADE");
                await db.Database.ExecuteSqlRawAsync(@"TRUNCATE ""AspNetRoles"" CASCADE");
                db.ChangeTracker.Clear();
            }

            // Identity — insert roles and users first so FK references from
            // TenantMembership, CustomerAccess, etc. can resolve.
            await InsertBatch(db, bundle.Roles.Select(r => new IdentityRole
            {
                Id = r.Id, Name = r.Name, NormalizedName = r.NormalizedName, ConcurrencyStamp = r.ConcurrencyStamp,
            }));

            await InsertBatch(db, bundle.Users.Select(u => new ApplicationUser
            {
                Id = u.Id, UserName = u.UserName, Email = u.Email, PasswordHash = u.PasswordHash,
                EmailConfirmed = u.EmailConfirmed, NormalizedUserName = u.NormalizedUserName,
                NormalizedEmail = u.NormalizedEmail, SecurityStamp = u.SecurityStamp,
                ConcurrencyStamp = u.ConcurrencyStamp, PhoneNumber = u.PhoneNumber,
                PhoneNumberConfirmed = u.PhoneNumberConfirmed, TwoFactorEnabled = u.TwoFactorEnabled,
                LockoutEnd = u.LockoutEnd, LockoutEnabled = u.LockoutEnabled, AccessFailedCount = u.AccessFailedCount,
            }));

            await InsertBatch(db, bundle.UserRoles.Select(ur =>
                new IdentityUserRole<string> { UserId = ur.UserId, RoleId = ur.RoleId }));

            // Tenant structure — parents before children.
            await InsertEntities(db, db.Tenants, bundle.Tenants);
            await InsertEntities(db, db.TenantRoles, bundle.TenantRoles);
            await InsertEntities(db, db.TenantMemberships, bundle.TenantMemberships);
            await InsertEntities(db, db.Groups, bundle.Groups);
            await InsertEntities(db, db.GroupMemberships, bundle.GroupMemberships);
            await InsertEntities(db, db.Environments, bundle.Environments);
            await InsertEntities(db, db.Customers, bundle.Customers);
            await InsertEntities(db, db.CustomerAccesses, bundle.CustomerAccesses);

            // Customer git credentials must exist before GitRepositories and VaultSecrets reference them.
            await InsertEntities(db, db.CustomerGitCredentials, bundle.CustomerGitCredentials);
            await InsertEntities(db, db.CustomerGitRepoPolicies, bundle.CustomerGitRepoPolicies);

            await InsertEntities(db, db.Apps, bundle.Apps);
            await InsertEntities(db, db.AppEnvironments, bundle.AppEnvironments);

            // App governance — after Apps (AppId FK).
            await InsertEntities(db, db.AppNetworkPolicies, bundle.AppNetworkPolicies);
            await InsertEntities(db, db.AppQuotas, bundle.AppQuotas);
            await InsertEntities(db, db.AppRbacPolicies, bundle.AppRbacPolicies);
            await InsertEntities(db, db.AppRbacRules, bundle.AppRbacRules);

            // Infrastructure — OpenStack and K8s clusters before storage links
            // (StorageLink.OpenStackConnectionId and StorageLink.ComponentId are optional
            // FKs that must exist when non-null).
            await InsertEntities(db, db.OpenStackConnections, bundle.OpenStackConnections);
            await InsertEntities(db, db.KubernetesClusters, bundle.KubernetesClusters);
            await InsertEntities(db, db.ClusterComponents, bundle.ClusterComponents);

            // Git repos, redis, and VPN depend on clusters/credentials already inserted above.
            await InsertEntities(db, db.GitKnownHosts, bundle.GitKnownHosts);

            // Null out CustomerGitCredentialId on repos not covered by this bundle (older exports).
            var bundledCredIds = bundle.CustomerGitCredentials.Select(c => c.Id).ToHashSet();
            foreach (var r in bundle.GitRepositories.Where(r => r.CustomerGitCredentialId.HasValue && !bundledCredIds.Contains(r.CustomerGitCredentialId!.Value)))
                r.CustomerGitCredentialId = null;
            await InsertEntities(db, db.GitRepositories, bundle.GitRepositories);

            await InsertEntities(db, db.RedisClusters, bundle.RedisClusters);
            await InsertEntities(db, db.VpnTunnels, bundle.VpnTunnels);
            await InsertEntities(db, db.VpnLocalEndpoints, bundle.VpnLocalEndpoints);
            await InsertEntities(db, db.VpnRemoteEndpoints, bundle.VpnRemoteEndpoints);

            await InsertEntities(db, db.ExternalRoutes, bundle.ExternalRoutes);
            await InsertEntities(db, db.StorageLinks, bundle.StorageLinks);

            // Build sets of IDs that are actually present in this bundle so we can null out
            // dangling FK references that arise from older bundles or deleted source records.
            var bundledRepoIds       = bundle.GitRepositories.Select(r => r.Id).ToHashSet();
            var bundledRedisIds      = bundle.RedisClusters.Select(r => r.Id).ToHashSet();
            var bundledVpnEndIds     = bundle.VpnRemoteEndpoints.Select(e => e.Id).ToHashSet();

            foreach (var d in bundle.AppDeployments)
            {
                if (d.GitRepositoryId.HasValue && !bundledRepoIds.Contains(d.GitRepositoryId.Value))
                    d.GitRepositoryId = null;
            }

            // Deployments — apps and clusters must exist first (RESTRICT FKs).
            await InsertEntities(db, db.AppDeployments, bundle.AppDeployments);
            await InsertEntities(db, db.DeploymentManifests, bundle.DeploymentManifests);
            await InsertEntities(db, db.StorageBindings, bundle.StorageBindings);

            // Cache bindings — after AppDeployments and RedisClusters (both non-null FKs).
            await InsertEntities(db, db.CacheBindings, bundle.CacheBindings);

            // Databases — after storage links and k8s clusters.
            await InsertEntities(db, db.RegisteredPostgresInstances, bundle.RegisteredPostgresInstances);
            await InsertEntities(db, db.RegisteredPostgresDatabases, bundle.RegisteredPostgresDatabases);
            await InsertEntities(db, db.CnpgClusters, bundle.CnpgClusters);
            await InsertEntities(db, db.CnpgDatabases, bundle.CnpgDatabases);
            await InsertEntities(db, db.MongoClusters, bundle.MongoClusters);
            await InsertEntities(db, db.MongoDatabases, bundle.MongoDatabases);
            await InsertEntities(db, db.RabbitMQClusters, bundle.RabbitMQClusters);
            await InsertEntities(db, db.DatabaseBindings, bundle.DatabaseBindings);
            await InsertEntities(db, db.MessagingBindings, bundle.MessagingBindings);

            // Identity/Auth providers — after cluster components and databases.
            await InsertEntities(db, db.KeycloakComponentConfigs, bundle.KeycloakComponentConfigs);
            await InsertEntities(db, db.KeycloakThemes, bundle.KeycloakThemes);
            await InsertEntities(db, db.KeycloakRealms, bundle.KeycloakRealms);

            // Container registry.
            await InsertEntities(db, db.HarborComponentConfigs, bundle.HarborComponentConfigs);
            await InsertEntities(db, db.HarborProjects, bundle.HarborProjects);

            // Operations.
            await InsertEntities(db, db.NotificationChannels, bundle.NotificationChannels);
            await InsertEntities(db, db.SlaTargets, bundle.SlaTargets);
            await InsertEntities(db, db.MaintenanceWindows, bundle.MaintenanceWindows);

            // Null out VaultSecret FKs that point to entities not present in this bundle
            // (backwards-compatible with bundles exported before these entity types were added).
            for (int i = 0; i < bundle.VaultSecrets.Count; i++)
            {
                VaultSecretRecord sr = bundle.VaultSecrets[i];
                if (sr.GitRepositoryId.HasValue       && !bundledRepoIds.Contains(sr.GitRepositoryId.Value))
                    sr = sr with { GitRepositoryId = null };
                if (sr.RedisClusterId.HasValue         && !bundledRedisIds.Contains(sr.RedisClusterId.Value))
                    sr = sr with { RedisClusterId = null };
                if (sr.VpnRemoteEndpointId.HasValue    && !bundledVpnEndIds.Contains(sr.VpnRemoteEndpointId.Value))
                    sr = sr with { VpnRemoteEndpointId = null };
                if (sr.CustomerGitCredentialId.HasValue && !bundledCredIds.Contains(sr.CustomerGitCredentialId.Value))
                    sr = sr with { CustomerGitCredentialId = null };
                bundle.VaultSecrets[i] = sr;
            }

            // Secrets — generate a fresh DEK for every vault and re-encrypt all
            // secrets with it. The new DEK is sealed with the new server's root key.
            Dictionary<Guid, byte[]> newDekByVaultId = [];

            foreach (VaultRecord vr in bundle.SecretVaults)
            {
                byte[] dek = encryption.GenerateDataKey();
                (byte[] encKey, byte[] nonce) = encryption.SealDataKey(dek);
                newDekByVaultId[vr.Id] = dek;
                db.SecretVaults.Add(new SecretVault
                {
                    Id = vr.Id, TenantId = vr.TenantId,
                    EncryptedDataKey = encKey, Nonce = nonce,
                    CreatedAt = vr.CreatedAt,
                });
            }
            if (bundle.SecretVaults.Count > 0)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }

            foreach (VaultSecretRecord sr in bundle.VaultSecrets)
            {
                if (!newDekByVaultId.TryGetValue(sr.VaultId, out byte[]? dek))
                {
                    logger.LogWarning("VaultSecret {Id} vault {VaultId} not in bundle — skipping.", sr.Id, sr.VaultId);
                    continue;
                }
                (byte[] ciphertext, byte[] sNonce) = encryption.Encrypt(dek, sr.PlaintextValue);
                db.VaultSecrets.Add(new VaultSecret
                {
                    Id = sr.Id, VaultId = sr.VaultId, Name = sr.Name,
                    EncryptedValue = ciphertext, Nonce = sNonce,
                    AppId = sr.AppId, ComponentId = sr.ComponentId, StorageLinkId = sr.StorageLinkId,
                    OpenStackConnectionId = sr.OpenStackConnectionId, CnpgClusterId = sr.CnpgClusterId,
                    CnpgDatabaseId = sr.CnpgDatabaseId, MongoDatabaseId = sr.MongoDatabaseId,
                    MongoClusterId = sr.MongoClusterId, RegisteredPostgresDatabaseId = sr.RegisteredPostgresDatabaseId,
                    RabbitMQClusterId = sr.RabbitMQClusterId,
                    RedisClusterId = sr.RedisClusterId,
                    VpnRemoteEndpointId = sr.VpnRemoteEndpointId,
                    GitRepositoryId = sr.GitRepositoryId,
                    CustomerGitCredentialId = sr.CustomerGitCredentialId,
                    SyncToKubernetes = sr.SyncToKubernetes, KubernetesClusterId = sr.KubernetesClusterId,
                    KubernetesSecretName = sr.KubernetesSecretName, KubernetesNamespace = sr.KubernetesNamespace,
                    CreatedAt = sr.CreatedAt, UpdatedAt = sr.UpdatedAt,
                });
            }
            if (bundle.VaultSecrets.Count > 0)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }

            foreach (DockerCredentialRecord cr in bundle.DockerCredentials)
            {
                if (!newDekByVaultId.TryGetValue(cr.VaultId, out byte[]? dek))
                {
                    logger.LogWarning("DockerCredential {Id} vault {VaultId} not in bundle — skipping.", cr.Id, cr.VaultId);
                    continue;
                }
                (byte[] encPassword, byte[] pwNonce) = encryption.Encrypt(dek, cr.PlaintextPassword);
                db.DockerRegistryCredentials.Add(new DockerRegistryCredential
                {
                    Id = cr.Id, VaultId = cr.VaultId, Name = cr.Name, RegistryType = cr.RegistryType,
                    Server = cr.Server, Username = cr.Username,
                    EncryptedPassword = encPassword, PasswordNonce = pwNonce,
                    Email = cr.Email, AppId = cr.AppId, KubernetesClusterId = cr.KubernetesClusterId,
                    KubernetesSecretName = cr.KubernetesSecretName, KubernetesNamespace = cr.KubernetesNamespace,
                    CreatedAt = cr.CreatedAt, UpdatedAt = cr.UpdatedAt,
                });
            }
            if (bundle.DockerCredentials.Count > 0)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }

            await tx.CommitAsync();

            logger.LogInformation(
                "Restore completed: {Tenants} tenants, {Apps} apps, {Secrets} secrets, {Clusters} clusters.",
                bundle.Tenants.Count, bundle.Apps.Count, bundle.VaultSecrets.Count, bundle.KubernetesClusters.Count);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task InsertEntities<T>(ApplicationDbContext db, DbSet<T> set, List<T> items)
        where T : class
    {
        if (items.Count == 0) return;
        // Clear navigations so EF Core only inserts the scalar/FK columns.
        // Navigations are null or empty after deserialization (no Include was used
        // on export), so this mainly prevents EF from trying to chase them.
        foreach (T item in items)
            set.Add(item);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private async Task InsertBatch<T>(ApplicationDbContext db, IEnumerable<T> items)
        where T : class
    {
        List<T> list = items.ToList();
        if (list.Count == 0) return;
        db.Set<T>().AddRange(list);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
