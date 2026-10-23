using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// How long one instance goes on serving a kubeconfig that another instance has replaced.
///
/// <para><b>The defect, and why it was invisible.</b> <c>KubeconfigResolver</c> cached the
/// DECRYPTED kubeconfig YAML with no expiry at all. Its only eviction is <c>Invalidate</c>,
/// called from the write path — in process, on whichever instance performed the write. Rotation
/// upserts the secret in place, so the cache key (the secret id) does not change. In a single
/// instance that is correct and fast. Behind a load balancer, an operator who rotates or revokes
/// a cluster credential on instance A leaves instances B and C serving the OLD plaintext <b>until
/// their process restarts</b> — across every call site that reaches <c>cluster.Kubeconfig</c>
/// through the materialization interceptor. A revoked credential that still works is the one
/// outcome a vault must not have.</para>
///
/// <para>⚠️ A TTL <b>bounds</b> this; it does not coordinate. Cross-instance invalidation needs a
/// bus and is its own step (docs/decomposition.md §9.5). What these tests pin is the bound.</para>
/// </summary>
public sealed class KubeconfigCacheFreshnessTests : IDisposable
{
    private const string ConnectionString = "DataSource=kubefresh;Mode=Memory;Cache=Shared";

    private static readonly byte[] TestRootKey = Enumerable.Repeat((byte)7, 32).ToArray();

    private readonly SqliteConnection keepAlive;
    private readonly ApplicationDbContext db;
    private readonly VaultEncryptionService encryption;
    private readonly ServiceProvider sp;
    private readonly Guid tenantId = Guid.NewGuid();

    private sealed class Factory(string connectionString)
        : IDbContextFactory<ApplicationDbContext>, IDbContextFactory<SecretsDbContext>
    {
        public ApplicationDbContext CreateDbContext() => Create<ApplicationDbContext>();

        SecretsDbContext IDbContextFactory<SecretsDbContext>.CreateDbContext()
            => Create<SecretsDbContext>();

        private TContext Create<TContext>() where TContext : DbContext
            => (TContext)Activator.CreateInstance(
                typeof(TContext),
                new DbContextOptionsBuilder<TContext>()
                    .UseSqlite(connectionString)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                    .Options)!;
    }

    public KubeconfigCacheFreshnessTests()
    {
        keepAlive = new SqliteConnection(ConnectionString);
        keepAlive.Open();

        Factory factory = new(ConnectionString);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        encryption = new VaultEncryptionService(TestRootKey);

        sp = new ServiceCollection()
            .AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory)
            .AddSingleton<IDbContextFactory<SecretsDbContext>>(factory)
            .BuildServiceProvider();
    }

    /// <summary>Seeds a vault with one kubeconfig secret and returns its id.</summary>
    private Guid SeedSecret(string yaml)
    {
        // SecretVault.TenantId is a real FK, so the tenant has to exist first — the first version
        // of this seeded only the vault and SQLite refused it.
        if (!db.Tenants.Any(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
            db.SaveChanges();
        }

        byte[] dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        (byte[] sealedDek, byte[] dekNonce) = encryption.SealDataKey(dek);

        SecretVault vault = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            EncryptedDataKey = sealedDek, Nonce = dekNonce, CreatedAt = DateTime.UtcNow,
        };
        db.SecretVaults.Add(vault);

        Guid secretId = Guid.NewGuid();
        Write(secretId, vault.Id, dek, yaml, add: true);
        db.SaveChanges();

        return secretId;
    }

    /// <summary>
    /// Writes the secret's ciphertext directly. ⭐ This is how the test models "another instance
    /// rotated it": the row changes without this process's <c>Invalidate</c> ever being called,
    /// which is exactly the situation a load balancer creates and the one the old cache could not
    /// recover from.
    /// </summary>
    private void Write(Guid secretId, Guid vaultId, byte[] dek, string yaml, bool add)
    {
        // ⚠️ The stored plaintext is a SERIALIZED BUNDLE, not raw YAML — Resolve does
        // `KubeconfigBundle.Deserialize(json).ConfigYaml`. The first version of this test
        // encrypted the YAML directly, so every Resolve returned null and the failure looked like
        // the cache rather than the test.
        (byte[] ct, byte[] nonce) = encryption.Encrypt(
            dek, new KubeconfigBundle { ConfigYaml = yaml, ContextName = "prod" }.Serialize());

        if (add)
        {
            db.VaultSecrets.Add(new VaultSecret
            {
                Id = secretId, VaultId = vaultId, Name = "kubeconfig",
                SecretType = VaultSecretType.Kubeconfig,
                EncryptedValue = ct, Nonce = nonce,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

            return;
        }

        VaultSecret existing = db.VaultSecrets.First(s => s.Id == secretId);
        existing.EncryptedValue = ct;
        existing.Nonce = nonce;
        existing.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    private byte[] DekOf(Guid secretId)
    {
        SecretVault vault = db.SecretVaults.AsNoTracking().First(v => v.TenantId == tenantId);
        return encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
    }

    /// <summary>
    /// ⭐ The defect: a rotation performed elsewhere is picked up, with nobody calling
    /// <c>Invalidate</c> on this instance.
    ///
    /// <para>The window is set to almost nothing so the test can finish. That is the only reason
    /// the duration is injectable — the property is "it expires at all", and a 30-second default
    /// cannot be exercised by a test that has to return.</para>
    /// </summary>
    [Fact]
    public async Task A_rotation_on_another_instance_is_picked_up_without_being_told()
    {
        Guid secretId = SeedSecret("kind: Config\nclusters: [old]\n");
        Guid vaultId = db.SecretVaults.AsNoTracking().First().Id;

        KubeconfigResolver resolver = new(sp, encryption, TimeSpan.FromMilliseconds(30));

        resolver.Resolve(tenantId, secretId).Should().Contain("old",
            "the positive case first — without this the assertion below could pass because "
            + "nothing resolves at all");

        // Another instance replaces the credential. This process is never told.
        Write(secretId, vaultId, DekOf(secretId), "kind: Config\nclusters: [rotated]\n", add: false);

        await Task.Delay(80);

        resolver.Resolve(tenantId, secretId).Should().Contain("rotated",
            "with no expiry this returned the revoked credential until the process restarted, "
            + "which behind a load balancer means every instance that did not perform the "
            + "rotation goes on using it");
    }

    /// <summary>
    /// ⚠️ The cache must still BE a cache. Inside the window the plaintext is reused rather than
    /// re-decrypted, which is the whole reason it exists — without this assertion, "expires
    /// correctly" would also be satisfied by a cache that never stores anything, and the fix
    /// would have quietly turned every cluster operation into a vault read.
    /// </summary>
    [Fact]
    public void Inside_the_window_the_plaintext_is_reused()
    {
        Guid secretId = SeedSecret("kind: Config\nclusters: [first]\n");
        Guid vaultId = db.SecretVaults.AsNoTracking().First().Id;

        KubeconfigResolver resolver = new(sp, encryption, TimeSpan.FromMinutes(5));

        resolver.Resolve(tenantId, secretId).Should().Contain("first");

        Write(secretId, vaultId, DekOf(secretId), "kind: Config\nclusters: [second]\n", add: false);

        resolver.Resolve(tenantId, secretId).Should().Contain("first",
            "a five-minute window must still be serving the cached value a moment later");
    }

    /// <summary>
    /// The in-process path keeps its immediacy: on the instance that performed the rotation the
    /// new value is visible at once, not a window late.
    /// </summary>
    [Fact]
    public void Invalidate_is_still_immediate_on_the_instance_that_rotated()
    {
        Guid secretId = SeedSecret("kind: Config\nclusters: [before]\n");
        Guid vaultId = db.SecretVaults.AsNoTracking().First().Id;

        KubeconfigResolver resolver = new(sp, encryption, TimeSpan.FromMinutes(5));
        resolver.Resolve(tenantId, secretId).Should().Contain("before");

        Write(secretId, vaultId, DekOf(secretId), "kind: Config\nclusters: [after]\n", add: false);
        resolver.Invalidate(secretId);

        resolver.Resolve(tenantId, secretId).Should().Contain("after",
            "the write path still calls this, and it must not have become a no-op");
    }

    /// <summary>
    /// ⚠️ The default is asserted because the test above deliberately runs with a window nothing
    /// ships with. A fix whose production value was left at zero — or at a day — would pass every
    /// test here.
    /// </summary>
    [Fact]
    public void The_shipped_window_is_short_but_not_zero()
    {
        KubeconfigResolver.DefaultFreshness.Should().BeGreaterThan(TimeSpan.Zero,
            "zero would make this a vault read per operation, across ~350 call sites");

        KubeconfigResolver.DefaultFreshness.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(1),
            "the window IS the time a revoked credential keeps working on an instance that did "
            + "not perform the revocation");
    }

    public void Dispose()
    {
        sp.Dispose();
        db.Dispose();
        keepAlive.Dispose();
    }
}
