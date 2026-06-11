using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages databases and credentials on vanilla (non-CNPG) PostgreSQL servers
/// that are registered with EntKube. SQL is executed via kubectl exec + psql
/// on the admin pod, using credentials stored in the vault.
/// </summary>
public class RegisteredPostgresService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IKubernetesClientFactory k8sFactory,
    StorageBrowserService storageBrowserService)
{
    // ──────── Instance Queries ────────

    /// <summary>
    /// Returns all registered Postgres instances for a tenant, with cluster and
    /// environment navigation properties loaded.
    /// </summary>
    public async Task<List<RegisteredPostgresInstance>> GetInstancesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RegisteredPostgresInstances
            .Include(i => i.KubernetesCluster)
                .ThenInclude(k => k.Environment)
            .Include(i => i.Databases)
            .Where(i => i.TenantId == tenantId)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);
    }

    // ──────── Instance Lifecycle ────────

    /// <summary>
    /// Registers a Postgres server with EntKube and stores the admin password in
    /// the vault. Optionally tests the connection before persisting.
    /// </summary>
    public async Task<RegisteredPostgresInstance> RegisterInstanceAsync(
        Guid tenantId,
        Guid kubernetesClusterId,
        string name,
        string ns,
        string serviceName,
        int port,
        string adminPodName,
        string adminUsername,
        string adminPassword,
        string? notes = null,
        bool testConnection = true,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster k8sCluster = await db.KubernetesClusters
            .FirstAsync(k => k.Id == kubernetesClusterId, ct);

        if (testConnection)
        {
            await TestConnectionCoreAsync(adminPodName, ns, adminUsername, adminPassword, k8sCluster.Kubeconfig!, ct);
        }

        RegisteredPostgresInstance instance = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = kubernetesClusterId,
            Name = name,
            Namespace = ns,
            ServiceName = serviceName,
            Port = port,
            AdminPodName = adminPodName,
            AdminUsername = adminUsername,
            Notes = notes
        };

        db.RegisteredPostgresInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetRegisteredPostgresAdminPasswordAsync(tenantId, instance.Id, adminPassword, ct);

        return instance;
    }

    /// <summary>
    /// Tests connectivity to the Postgres pod by running a trivial query.
    /// Throws InvalidOperationException on failure.
    /// </summary>
    public async Task TestConnectionAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        await TestConnectionCoreAsync(
            instance.AdminPodName, instance.Namespace, instance.AdminUsername,
            adminPassword, cluster.Kubeconfig!, ct);
    }

    /// <summary>
    /// Lists all databases on the Postgres server (from pg_database, excluding
    /// internal system databases like template0/template1).
    /// </summary>
    public async Task<List<string>> ListServerDatabasesAsync(
        Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        // Run SQL that outputs one database name per line, excluding system templates.
        const string sql = "SELECT datname FROM pg_database WHERE datname NOT IN ('template0','template1') ORDER BY datname;";

        string output = await k8sFactory.ExecuteSqlOnPodWithOutputAsync(
            instance.AdminPodName, instance.Namespace, sql, cluster.Kubeconfig!,
            instance.AdminUsername, adminPassword, ct);

        return ParsePsqlColumnOutput(output);
    }

    /// <summary>
    /// Updates the instance record. Does not re-verify the connection.
    /// To change the admin password call UpdateAdminPasswordAsync separately.
    /// </summary>
    public async Task UpdateInstanceAsync(
        Guid tenantId, Guid instanceId,
        string? name = null, string? notes = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance instance = await db.RegisteredPostgresInstances
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Registered Postgres instance not found.");

        if (name is not null) instance.Name = name;
        if (notes is not null) instance.Notes = notes;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Updates the admin password stored in the vault. Does not ALTER the role on the server.
    /// </summary>
    public async Task UpdateAdminPasswordAsync(
        Guid tenantId, Guid instanceId, string newPassword, CancellationToken ct = default)
    {
        await vaultService.SetRegisteredPostgresAdminPasswordAsync(tenantId, instanceId, newPassword, ct);
    }

    /// <summary>
    /// Removes the instance record and all managed databases and vault secrets.
    /// Does not drop databases from the Postgres server itself.
    /// </summary>
    public async Task DeleteInstanceAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance instance = await db.RegisteredPostgresInstances
            .Include(i => i.Databases)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Registered Postgres instance not found.");

        foreach (RegisteredPostgresDatabase database in instance.Databases)
        {
            await DeleteDatabaseSecretsAsync(tenantId, database.Id, ct);
        }

        db.RegisteredPostgresInstances.Remove(instance);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Database Management ────────

    /// <summary>
    /// Creates a new database on the server. Generates a random password, runs
    /// CREATE ROLE + CREATE DATABASE on the admin pod, then stores credentials
    /// in the vault tagged for Kubernetes sync.
    /// </summary>
    public async Task<RegisteredPostgresDatabase> CreateDatabaseAsync(
        Guid tenantId, Guid instanceId, string databaseName, CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        string owner = $"{databaseName}_owner";
        string password = GeneratePassword();

        RegisteredPostgresDatabase database = new()
        {
            Id = Guid.NewGuid(),
            RegisteredPostgresInstanceId = instance.Id,
            Name = databaseName,
            Owner = owner,
            Status = RegisteredPostgresDatabaseStatus.Creating
        };

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.RegisteredPostgresDatabases.Add(database);
        await db.SaveChangesAsync(ct);

        string sql = $"""
            CREATE ROLE "{owner}" WITH LOGIN PASSWORD '{password}';
            CREATE DATABASE "{databaseName}" OWNER "{owner}";
            """;

        await k8sFactory.ExecuteSqlOnPodAsync(
            instance.AdminPodName, instance.Namespace, sql, cluster.Kubeconfig!,
            instance.AdminUsername, adminPassword, ct);

        database.Status = RegisteredPostgresDatabaseStatus.Ready;
        await db.SaveChangesAsync(ct);

        string k8sSecretName = $"{instance.ServiceName}-{databaseName}-credentials";
        string host = $"{instance.ServiceName}.{instance.Namespace}.svc.cluster.local";

        await StoreDatabaseSecretAsync(tenantId, database.Id, "HOST", host, k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PORT", instance.Port.ToString(), k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "DATABASE", databaseName, k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "USERNAME", owner, k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PASSWORD", password, k8sSecretName, instance.Namespace, ct);

        return database;
    }

    /// <summary>
    /// Imports an existing database so EntKube can manage its credentials.
    /// The caller provides the existing owner role and its current password.
    /// No SQL is run on the server — only vault records are created.
    /// </summary>
    public async Task<RegisteredPostgresDatabase> ImportDatabaseAsync(
        Guid tenantId, Guid instanceId,
        string databaseName, string ownerRole, string ownerPassword,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance instance = await db.RegisteredPostgresInstances
            .Include(i => i.KubernetesCluster)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Registered Postgres instance not found.");

        RegisteredPostgresDatabase database = new()
        {
            Id = Guid.NewGuid(),
            RegisteredPostgresInstanceId = instance.Id,
            Name = databaseName,
            Owner = ownerRole,
            Status = RegisteredPostgresDatabaseStatus.Ready
        };

        db.RegisteredPostgresDatabases.Add(database);
        await db.SaveChangesAsync(ct);

        string k8sSecretName = $"{instance.ServiceName}-{databaseName}-credentials";
        string host = $"{instance.ServiceName}.{instance.Namespace}.svc.cluster.local";

        await StoreDatabaseSecretAsync(tenantId, database.Id, "HOST", host, k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PORT", instance.Port.ToString(), k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "DATABASE", databaseName, k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "USERNAME", ownerRole, k8sSecretName, instance.Namespace, ct);
        await StoreDatabaseSecretAsync(tenantId, database.Id, "PASSWORD", ownerPassword, k8sSecretName, instance.Namespace, ct);

        return database;
    }

    /// <summary>
    /// Drops a database and its owner role from the server, removes vault secrets,
    /// and deletes the database record.
    /// </summary>
    public async Task DropDatabaseAsync(
        Guid tenantId, Guid instanceId, Guid databaseId, CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresDatabase database = await db.RegisteredPostgresDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.RegisteredPostgresInstanceId == instance.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        try
        {
            string sql = $"""
                DROP DATABASE IF EXISTS "{database.Name}";
                DROP ROLE IF EXISTS "{database.Owner}";
                """;

            await k8sFactory.ExecuteSqlOnPodAsync(
                instance.AdminPodName, instance.Namespace, sql, cluster.Kubeconfig!,
                instance.AdminUsername, adminPassword, ct);
        }
        catch (InvalidOperationException)
        {
            // SQL failed (e.g. database doesn't exist on server, pod unreachable).
            // Continue with local cleanup so the user isn't stuck.
        }

        // Delete K8s secrets from the instance namespace and bound app namespaces.

        string primarySecretName = $"{instance.ServiceName}-{database.Name}-credentials";
        string kubeconfig = cluster.Kubeconfig!;

        try
        {
            await k8sFactory.DeleteManifestAsync("Secret", primarySecretName, instance.Namespace, kubeconfig, ct);
        }
        catch { }

        List<DatabaseBinding> bindings = await db.DatabaseBindings
            .Include(b => b.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(b => b.RegisteredPostgresDatabaseId == databaseId)
            .ToListAsync(ct);

        foreach (DatabaseBinding binding in bindings)
        {
            try
            {
                await k8sFactory.DeleteManifestAsync(
                    "Secret", binding.KubernetesSecretName,
                    binding.AppDeployment.Namespace,
                    binding.AppDeployment.Cluster.Kubeconfig!, ct);
            }
            catch { }
        }

        await DeleteDatabaseSecretsAsync(tenantId, databaseId, ct);

        db.RegisteredPostgresDatabases.Remove(database);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a database from EntKube without running any SQL on the server.
    /// Use when the database should be untracked (e.g. it was imported and the user
    /// only wants to remove it from EntKube, not drop it from the actual Postgres server).
    /// </summary>
    public async Task UnregisterDatabaseAsync(
        Guid tenantId, Guid instanceId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance instance = await db.RegisteredPostgresInstances
            .Include(i => i.KubernetesCluster)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Registered Postgres instance not found.");

        RegisteredPostgresDatabase database = await db.RegisteredPostgresDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.RegisteredPostgresInstanceId == instance.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        await DeleteDatabaseSecretsAsync(tenantId, databaseId, ct);

        db.RegisteredPostgresDatabases.Remove(database);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Credentials ────────

    /// <summary>
    /// Returns decrypted connection credentials for a database (HOST, PORT, DATABASE,
    /// USERNAME, PASSWORD).
    /// </summary>
    public async Task<Dictionary<string, string>> GetDatabaseCredentialsAsync(
        Guid tenantId, Guid databaseId, CancellationToken ct = default)
    {
        List<VaultSecret> secrets = await vaultService.GetRegisteredPostgresDatabaseSecretsAsync(tenantId, databaseId, ct);

        Dictionary<string, string> credentials = new();

        foreach (VaultSecret secret in secrets)
        {
            string? value = await vaultService.GetSecretValueByIdAsync(secret.Id, ct);
            if (value is not null) credentials[secret.Name] = value;
        }

        return credentials;
    }

    /// <summary>
    /// Syncs the database credentials to Kubernetes as an Opaque Secret in the
    /// instance namespace, and to every bound app deployment namespace.
    /// </summary>
    public async Task SyncCredentialsToK8sAsync(
        Guid tenantId, Guid instanceId, Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance instance = await db.RegisteredPostgresInstances
            .Include(i => i.KubernetesCluster)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Registered Postgres instance not found.");

        RegisteredPostgresDatabase database = await db.RegisteredPostgresDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.RegisteredPostgresInstanceId == instance.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        Dictionary<string, string> credentials = await GetDatabaseCredentialsAsync(tenantId, databaseId, ct);

        if (credentials.Count == 0)
            throw new InvalidOperationException("No credentials found in the vault for this database.");

        string primarySecretName = $"{instance.ServiceName}-{database.Name}-credentials";
        string kubeconfig = instance.KubernetesCluster.Kubeconfig!;

        await ApplyCredentialSecretAsync(credentials, primarySecretName, instance.Namespace, kubeconfig, ct);

        List<VaultSecret> vaultSecrets = await vaultService.GetRegisteredPostgresDatabaseSecretsAsync(tenantId, databaseId, ct);
        foreach (VaultSecret s in vaultSecrets)
        {
            await vaultService.ConfigureKubernetesSyncAsync(s.Id, true, primarySecretName, instance.Namespace, ct);
        }

        List<DatabaseBinding> bindings = await db.DatabaseBindings
            .Include(b => b.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(b => b.RegisteredPostgresDatabaseId == databaseId && b.SyncEnabled)
            .ToListAsync(ct);

        foreach (DatabaseBinding binding in bindings)
        {
            await k8sFactory.EnsureNamespaceAsync(binding.AppDeployment.Namespace, binding.AppDeployment.Cluster.Kubeconfig!, ct);
            await ApplyCredentialSecretAsync(
                credentials, binding.KubernetesSecretName,
                binding.AppDeployment.Namespace, binding.AppDeployment.Cluster.Kubeconfig!, ct);
            binding.LastSyncedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rotates the owner role password. Runs ALTER ROLE on the server, updates the
    /// vault, and syncs credentials to all bound namespaces.
    /// </summary>
    public async Task RotatePasswordAsync(
        Guid tenantId, Guid instanceId, Guid databaseId, CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresDatabase database = await db.RegisteredPostgresDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.RegisteredPostgresInstanceId == instance.Id, ct)
            ?? throw new InvalidOperationException("Database not found.");

        string newPassword = GeneratePassword();

        await k8sFactory.ExecuteSqlOnPodAsync(
            instance.AdminPodName, instance.Namespace,
            $"ALTER ROLE \"{database.Owner}\" PASSWORD '{newPassword}';",
            cluster.Kubeconfig!, instance.AdminUsername, adminPassword, ct);

        string k8sSecretName = $"{instance.ServiceName}-{database.Name}-credentials";

        await vaultService.SetRegisteredPostgresDatabaseSecretAsync(
            tenantId, databaseId, "PASSWORD", newPassword, k8sSecretName, instance.Namespace, ct);

        await SyncCredentialsToK8sAsync(tenantId, instanceId, databaseId, ct);
    }

    // ──────── Dump / Restore ────────

    /// <summary>
    /// Returns all pg_dump backups for a specific database, newest first.
    /// </summary>
    public async Task<List<RegisteredPostgresDump>> GetDumpsAsync(
        Guid databaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.RegisteredPostgresDumps
            .Include(d => d.StorageLink)
            .Where(d => d.RegisteredPostgresDatabaseId == databaseId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Runs pg_dump on the admin pod and uploads the plain-SQL result to S3.
    /// </summary>
    public async Task<RegisteredPostgresDump> CreateDumpAsync(
        Guid tenantId, Guid instanceId, Guid databaseId, Guid storageLinkId,
        CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresDatabase database = await db.RegisteredPostgresDatabases
            .FirstAsync(d => d.Id == databaseId, ct);

        StorageLink storageLink = await db.StorageLinks
            .FirstAsync(sl => sl.Id == storageLinkId, ct);

        string dumpSql = await k8sFactory.RunCommandOnPodAsync(
            instance.AdminPodName, instance.Namespace,
            ["pg_dump", "--format=plain", "-U", instance.AdminUsername, "-h", "127.0.0.1", "-d", database.Name],
            cluster.Kubeconfig!,
            new Dictionary<string, string> { ["PGPASSWORD"] = adminPassword },
            ct);

        byte[] bytes = Encoding.UTF8.GetBytes(dumpSql);
        string key = $"pg-dumps/{SanitizeS3Name(instance.Name)}/{SanitizeS3Name(database.Name)}/{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}Z.sql";

        using MemoryStream stream = new(bytes);
        await storageBrowserService.UploadAsync(tenantId, storageLink, key, stream, "text/plain", ct);

        RegisteredPostgresDump dump = new()
        {
            RegisteredPostgresDatabaseId = databaseId,
            StorageLinkId = storageLinkId,
            S3Key = key,
            SizeBytes = bytes.Length
        };

        db.RegisteredPostgresDumps.Add(dump);
        await db.SaveChangesAsync(ct);
        dump.StorageLink = storageLink;
        return dump;
    }

    /// <summary>
    /// Deletes the S3 object and the dump record.
    /// </summary>
    public async Task DeleteDumpAsync(Guid dumpId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresDump dump = await db.RegisteredPostgresDumps
            .Include(d => d.StorageLink)
            .Include(d => d.RegisteredPostgresDatabase)
                .ThenInclude(d => d.RegisteredPostgresInstance)
            .FirstAsync(d => d.Id == dumpId, ct);

        Guid tenantId = dump.RegisteredPostgresDatabase.RegisteredPostgresInstance.TenantId;

        try { await storageBrowserService.DeleteAsync(tenantId, dump.StorageLink, dump.S3Key, ct); }
        catch { }

        db.RegisteredPostgresDumps.Remove(dump);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Downloads a dump from S3 and restores it into a new database on a CNPG cluster.
    /// Creates the target database, then pipes the SQL via psql stdin.
    /// </summary>
    public async Task RestoreDumpToCnpgAsync(
        Guid dumpId, Guid cnpgClusterId, string newDatabaseName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresDump dump = await db.RegisteredPostgresDumps
            .Include(d => d.StorageLink)
            .Include(d => d.RegisteredPostgresDatabase)
                .ThenInclude(d => d.RegisteredPostgresInstance)
            .FirstAsync(d => d.Id == dumpId, ct);

        Guid tenantId = dump.RegisteredPostgresDatabase.RegisteredPostgresInstance.TenantId;

        byte[] dumpBytes = await storageBrowserService.DownloadAsync(tenantId, dump.StorageLink, dump.S3Key, ct);
        string dumpSql = Encoding.UTF8.GetString(dumpBytes);

        CnpgCluster cnpgCluster = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstAsync(c => c.Id == cnpgClusterId, ct);

        await RestoreSqlToCnpgAsync(tenantId, dumpSql, cnpgCluster, newDatabaseName, ct);
    }

    /// <summary>
    /// Runs pg_dump on the registered postgres pod and pipes the result
    /// directly into a new CNPG database — no S3 storage needed.
    /// --no-owner and --no-privileges ensure the dump is portable: owner roles
    /// from the source cluster won't exist on CNPG, so we skip those statements
    /// and let the postgres superuser own everything during restore.
    /// </summary>
    public async Task MigrateDirectlyToCnpgAsync(
        Guid tenantId, Guid instanceId, Guid databaseId,
        Guid cnpgClusterId, string newDatabaseName, CancellationToken ct = default)
    {
        (RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword) =
            await LoadInstanceAsync(tenantId, instanceId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresDatabase database = await db.RegisteredPostgresDatabases
            .FirstAsync(d => d.Id == databaseId, ct);

        string dumpSql = await k8sFactory.RunCommandOnPodAsync(
            instance.AdminPodName, instance.Namespace,
            [
                "pg_dump", "--format=plain", "--no-owner", "--no-privileges",
                "-U", instance.AdminUsername, "-h", "127.0.0.1", "-d", database.Name
            ],
            cluster.Kubeconfig!,
            new Dictionary<string, string> { ["PGPASSWORD"] = adminPassword },
            ct);

        CnpgCluster cnpgCluster = await db.CnpgClusters
            .Include(c => c.KubernetesCluster)
            .FirstAsync(c => c.Id == cnpgClusterId, ct);

        await RestoreSqlToCnpgAsync(tenantId, dumpSql, cnpgCluster, newDatabaseName, ct);
    }

    private async Task RestoreSqlToCnpgAsync(
        Guid tenantId, string dumpSql, CnpgCluster cnpgCluster, string newDatabaseName, CancellationToken ct)
    {
        string kubeconfig = cnpgCluster.KubernetesCluster.Kubeconfig!;
        string owner = $"{newDatabaseName}_owner";
        string password = GeneratePassword();

        // Create the owner role and the database on the CNPG cluster.
        await k8sFactory.ExecuteSqlAsync(
            cnpgCluster.Name, cnpgCluster.Namespace,
            $"""
            CREATE ROLE "{owner}" WITH LOGIN PASSWORD '{password}';
            CREATE DATABASE "{newDatabaseName}" OWNER "{owner}";
            """,
            kubeconfig, ct);

        // Restore the dump connected directly to the new database.
        // Using ExecuteSqlInCnpgDatabaseAsync (psql -d {db}) instead of \c avoids
        // the silent failure that occurs when \c cannot reconnect in stdin mode
        // (psql continues reading in the wrong database and exits with code 0).
        await k8sFactory.ExecuteSqlInCnpgDatabaseAsync(
            cnpgCluster.Name, cnpgCluster.Namespace,
            newDatabaseName, dumpSql, kubeconfig, ct);

        // Register the database in EntKube so it appears in the UI and credentials
        // can be managed and synced to Kubernetes Secrets.
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CnpgDatabase cnpgDb = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cnpgCluster.Id,
            Name = newDatabaseName,
            Owner = owner,
            Status = CnpgDatabaseStatus.Ready
        };

        db.CnpgDatabases.Add(cnpgDb);
        await db.SaveChangesAsync(ct);

        string k8sSecretName = $"{cnpgCluster.Name}-{newDatabaseName}-credentials";
        string host = $"{cnpgCluster.Name}-rw.{cnpgCluster.Namespace}.svc.cluster.local";

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetCnpgDatabaseSecretAsync(tenantId, cnpgDb.Id, "HOST", host, k8sSecretName, cnpgCluster.Namespace, ct);
        await vaultService.SetCnpgDatabaseSecretAsync(tenantId, cnpgDb.Id, "PORT", "5432", k8sSecretName, cnpgCluster.Namespace, ct);
        await vaultService.SetCnpgDatabaseSecretAsync(tenantId, cnpgDb.Id, "DATABASE", newDatabaseName, k8sSecretName, cnpgCluster.Namespace, ct);
        await vaultService.SetCnpgDatabaseSecretAsync(tenantId, cnpgDb.Id, "USERNAME", owner, k8sSecretName, cnpgCluster.Namespace, ct);
        await vaultService.SetCnpgDatabaseSecretAsync(tenantId, cnpgDb.Id, "PASSWORD", password, k8sSecretName, cnpgCluster.Namespace, ct);
    }

    // ──────── Private Helpers ────────

    private async Task<(RegisteredPostgresInstance instance, KubernetesCluster cluster, string adminPassword)>
        LoadInstanceAsync(Guid tenantId, Guid instanceId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance instance = await db.RegisteredPostgresInstances
            .Include(i => i.KubernetesCluster)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Registered Postgres instance not found.");

        string adminPassword = await vaultService.GetRegisteredPostgresAdminPasswordAsync(tenantId, instanceId, ct)
            ?? throw new InvalidOperationException("Admin password not found in vault.");

        return (instance, instance.KubernetesCluster, adminPassword);
    }

    private async Task TestConnectionCoreAsync(
        string podName, string ns, string username, string password,
        string kubeconfig, CancellationToken ct)
    {
        try
        {
            await k8sFactory.ExecuteSqlOnPodAsync(
                podName, ns, "SELECT 1;", kubeconfig, username, password, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not connect to Postgres on pod {podName}: {ex.Message}", ex);
        }
    }

    private Task StoreDatabaseSecretAsync(
        Guid tenantId, Guid databaseId, string name, string value,
        string k8sSecretName, string k8sNamespace, CancellationToken ct) =>
        vaultService.SetRegisteredPostgresDatabaseSecretAsync(
            tenantId, databaseId, name, value, k8sSecretName, k8sNamespace, ct);

    private async Task DeleteDatabaseSecretsAsync(Guid tenantId, Guid databaseId, CancellationToken ct)
    {
        List<VaultSecret> secrets = await vaultService.GetRegisteredPostgresDatabaseSecretsAsync(tenantId, databaseId, ct);
        foreach (VaultSecret s in secrets)
        {
            await vaultService.DeleteSecretAsync(s.Id, ct);
        }
    }

    private async Task ApplyCredentialSecretAsync(
        Dictionary<string, string> credentials, string secretName, string ns,
        string kubeconfig, CancellationToken ct)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");

        foreach (KeyValuePair<string, string> kvp in credentials)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(kvp.Value));
            sb.AppendLine($"  {kvp.Key}: {encoded}");
        }

        await k8sFactory.ApplyManifestAsync(sb.ToString(), kubeconfig, ct);
    }

    private static List<string> ParsePsqlColumnOutput(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "datname" && !l.StartsWith("---") && !l.StartsWith("("))
            .ToList();

    private static string SanitizeS3Name(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9\-]", "-");

    private static string GeneratePassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(24, chars, static (span, c) =>
        {
            Span<byte> bytes = stackalloc byte[24];
            RandomNumberGenerator.Fill(bytes);
            for (int i = 0; i < span.Length; i++)
                span[i] = c[bytes[i] % c.Length];
        });
    }

    /// <summary>
    /// Queries Kubernetes for all pods belonging to a registered instance.
    ///
    /// Primary strategy: Helm release labels (app.kubernetes.io/instance + name).
    /// Bitnami HA and similar charts split primary/read into separate StatefulSets
    /// that share the same Helm labels, so a label selector is the only way to get
    /// all pods across both StatefulSets in one query.
    ///
    /// Fallback: StatefulSet ordinal names ({stsName}-0, -1, …).
    /// Used for non-Helm StatefulSets where Helm labels are absent.
    /// </summary>
    public async Task<List<CnpgPodInfo>> GetPodsAsync(
        Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        RegisteredPostgresInstance? instance = await db.Set<RegisteredPostgresInstance>()
            .Include(i => i.KubernetesCluster)
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.TenantId == tenantId, ct);

        if (instance is null || string.IsNullOrWhiteSpace(instance.KubernetesCluster.Kubeconfig))
            return [];

        string kubeconfig = instance.KubernetesCluster.Kubeconfig;
        string adminPodJson;

        try
        {
            adminPodJson = await k8sFactory.GetJsonAsync(
                $"pod/{instance.AdminPodName}", instance.Namespace, kubeconfig, ct: ct);
        }
        catch
        {
            return [];
        }

        // ── Strategy 1: Helm release labels (covers Bitnami HA with separate primary/read StatefulSets) ──
        try
        {
            string? helmInstance = GetPodLabel(adminPodJson, "app.kubernetes.io/instance");
            string? helmName = GetPodLabel(adminPodJson, "app.kubernetes.io/name");

            if (helmInstance is not null && helmName is not null)
            {
                string selector = $"app.kubernetes.io/instance={helmInstance},app.kubernetes.io/name={helmName}";
                string podsJson = await k8sFactory.GetJsonAsync(
                    "pods", instance.Namespace, kubeconfig, selector, ct);
                List<CnpgPodInfo> helmPods = ParsePodList(podsJson, isList: true);
                if (helmPods.Count > 0)
                    return helmPods;
            }
        }
        catch { }

        // ── Strategy 2: StatefulSet ordinal names (non-Helm StatefulSets with replicas > 1) ──
        try
        {
            string? stsName = GetStatefulSetOwnerName(adminPodJson);
            if (stsName is not null)
            {
                string stsJson = await k8sFactory.GetJsonAsync(
                    $"statefulset/{stsName}", instance.Namespace, kubeconfig, ct: ct);
                int replicas = GetStatefulSetReplicas(stsJson);

                if (replicas > 1)
                {
                    Task<string>[] tasks = Enumerable.Range(0, replicas)
                        .Select(i => k8sFactory.GetJsonAsync(
                            $"pod/{stsName}-{i}", instance.Namespace, kubeconfig, ct: ct))
                        .ToArray();
                    string[] podJsons = await Task.WhenAll(tasks);

                    List<CnpgPodInfo> result = [];
                    foreach (string podJson in podJsons)
                        result.AddRange(ParsePodList(podJson, isList: false));
                    if (result.Count > 0)
                        return result;
                }
            }
        }
        catch { }

        return ParsePodList(adminPodJson, isList: false);
    }

    private static string? GetPodLabel(string podJson, string labelKey)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(podJson);
        if (doc.RootElement.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)
            && meta.TryGetProperty("labels", out System.Text.Json.JsonElement labels)
            && labels.TryGetProperty(labelKey, out System.Text.Json.JsonElement val))
            return val.GetString();
        return null;
    }

    private static string? GetStatefulSetOwnerName(string podJson)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(podJson);
        if (!doc.RootElement.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)
            || !meta.TryGetProperty("ownerReferences", out System.Text.Json.JsonElement owners))
            return null;

        foreach (System.Text.Json.JsonElement owner in owners.EnumerateArray())
        {
            if (owner.TryGetProperty("kind", out System.Text.Json.JsonElement kind)
                && kind.GetString() == "StatefulSet"
                && owner.TryGetProperty("name", out System.Text.Json.JsonElement name))
                return name.GetString();
        }
        return null;
    }

    private static int GetStatefulSetReplicas(string stsJson)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(stsJson);
        if (doc.RootElement.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
            && spec.TryGetProperty("replicas", out System.Text.Json.JsonElement replicas))
            return replicas.GetInt32();
        return 1;
    }

    private static List<CnpgPodInfo> ParsePodList(string json, bool isList)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        System.Collections.Generic.IEnumerable<System.Text.Json.JsonElement> items = isList
            && root.TryGetProperty("items", out System.Text.Json.JsonElement itemsEl)
            ? itemsEl.EnumerateArray()
            : [root];

        List<CnpgPodInfo> pods = [];
        foreach (System.Text.Json.JsonElement item in items)
        {
            string podName = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
            string podStatus = "Unknown";
            bool ready = false;
            string? node = null;
            DateTime? startTime = null;
            int restarts = 0;

            if (item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                && spec.TryGetProperty("nodeName", out System.Text.Json.JsonElement nodeName))
                node = nodeName.GetString();

            if (item.TryGetProperty("status", out System.Text.Json.JsonElement statusEl))
            {
                if (statusEl.TryGetProperty("phase", out System.Text.Json.JsonElement phaseEl))
                    podStatus = phaseEl.GetString() ?? "Unknown";

                if (statusEl.TryGetProperty("startTime", out System.Text.Json.JsonElement startEl)
                    && DateTime.TryParse(startEl.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                    startTime = parsed;

                if (statusEl.TryGetProperty("containerStatuses", out System.Text.Json.JsonElement containers))
                {
                    foreach (System.Text.Json.JsonElement container in containers.EnumerateArray())
                    {
                        if (container.TryGetProperty("restartCount", out System.Text.Json.JsonElement rc))
                            restarts += rc.GetInt32();
                        if (container.TryGetProperty("ready", out System.Text.Json.JsonElement readyEl))
                            ready = ready || readyEl.GetBoolean();
                    }
                }
            }

            pods.Add(new CnpgPodInfo
            {
                Name = podName,
                Role = "primary",
                Status = podStatus,
                Ready = ready,
                Node = node,
                StartTime = startTime,
                Restarts = restarts
            });
        }
        return pods;
    }
}
