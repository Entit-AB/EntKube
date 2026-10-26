using System.Data.Common;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EntKube.Web.Tests;

/// <summary>
/// One secret cannot have two version 4s.
///
/// <para><b>The defect.</b> <c>VaultService.ArchiveVersionAsync</c> allocates a version number
/// by reading <c>MAX(VersionNumber) + 1</c> and letting the caller's SaveChangesAsync insert it.
/// That is a read-then-write across ~30 call sites, and the index on
/// (SecretId, VersionNumber) was not unique — so two writers that both read MAX=3 both inserted
/// a row numbered 4 and the database took both. Nothing threw and nothing logged; the version
/// list simply showed two rows called v4, and which one was the fourth value became undecidable
/// after the fact. The sibling of this bug on the observed-secret refresh path was fixed with a
/// claim (ObservedSecretRefreshClaimTests); this one is everywhere else.</para>
///
/// <para>⭐ <b>What is being tested is a database constraint, not a code path.</b> The fix is
/// <c>IsUnique()</c> in SecretsModel.cs, because an allocation convention is only as good as the
/// next caller and there are thirty of them. So the tests here drive the real VaultService and
/// let the real SQLite schema adjudicate.</para>
///
/// <para><b>Why the absence assertions are not vacuous.</b> "No duplicate version number" would
/// pass against a vault that had stopped recording versions altogether, so every such assertion
/// is paired: <see cref="Sequential_writes_number_the_versions_one_two_three"/> proves the
/// uncontested path still numbers and archives, and
/// <see cref="A_healthy_secret_is_not_renumbered_by_the_migration"/> proves the repair migration
/// leaves a correct history exactly as it found it.</para>
/// </summary>
public class SecretVersionNumberUniquenessTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private const string SecretName = "DB_PASSWORD";

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;

    /// <summary>The ordinary writer — no interceptor, nothing special about it.</summary>
    private readonly VaultService vault;

    /// <summary>
    /// A second VaultService over the SAME database whose contexts carry
    /// <see cref="racer"/>. It is the real production service; only its connection is hooked.
    /// </summary>
    private readonly VaultService racingVault;

    private readonly CompetingWriterInterceptor racer = new();

    private Guid tenantId, appId, secretId;

    public SecretVersionNumberUniquenessTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();
        racingVault = new VaultService(
            new RacingSecretsFactory(testDb.ConnectionString, racer),
            testDb.Encryption,
            TestServices.NoClusterAccess);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ════════════════════════════════════════════════════════════════
    //  The positive half: versions are numbered, and numbered in order
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Three sequential writes leave versions 1, 2, 3 holding the three superseded values.
    /// Without this, every "no duplicate" assertion below would pass against a vault that had
    /// stopped writing version rows at all.
    /// </summary>
    [Fact]
    public async Task Sequential_writes_number_the_versions_one_two_three()
    {
        await SeedSecretAsync("v1");

        await vault.UpdateSecretValueAsync(tenantId, secretId, "v2");
        await vault.UpdateSecretValueAsync(tenantId, secretId, "v3");
        await vault.UpdateSecretValueAsync(tenantId, secretId, "v4");

        VersionNumbers().Should().Equal(1, 2, 3);
        ArchivedValues().Should().Equal("v1", "v2", "v3");
        (await vault.GetAppSecretValueAsync(tenantId, appId, SecretName)).Should().Be("v4");
    }

    // ════════════════════════════════════════════════════════════════
    //  The defect: two writers, one version number
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⭐ The race itself, made deterministic.
    ///
    /// <para>Running two updates in sequence proves nothing — the second reads the first's MAX
    /// and allocates the next number correctly. The duplicate only exists when both writers read
    /// MAX <em>before</em> either inserts. So the competing instance is run from inside the
    /// MAX query's own interceptor — the exact window between "this call read MAX=1" and "this
    /// call inserts 2" — and it is the real production method on the other side, not a
    /// stand-in.</para>
    ///
    /// <para>Before <c>IsUnique()</c> this test ended with two rows numbered 2 and no error
    /// anywhere. Now the loser's SaveChangesAsync throws and its whole write rolls back.</para>
    /// </summary>
    [Fact]
    public async Task Two_writers_that_both_allocated_version_two_cannot_both_land()
    {
        await SeedSecretAsync("v1");
        await vault.UpdateSecretValueAsync(tenantId, secretId, "v2");   // version 1 = "v1"

        // Fires once, after the racing writer has read MAX=1 and before it inserts.
        racer.OnNextVersionRead = () => vault.UpdateSecretValueAsync(tenantId, secretId, "v3");

        Func<Task> loser = () => racingVault.UpdateSecretValueAsync(tenantId, secretId, "v-loser");

        await loser.Should().ThrowAsync<DbUpdateException>(
            "the unique index is what makes the number unique — nothing in the service checks");

        racer.Fired.Should().BeTrue("the race window must actually have been entered");

        // Exactly one version 2, and it belongs to the writer that got there first.
        VersionNumbers().Should().Equal(1, 2);
        ArchivedValues().Should().Equal("v1", "v2");

        // ⚠️ The loser lost its value write too, not just its version row: the insert and the
        // new ciphertext are one SaveChangesAsync, so the secret still holds the winner's value.
        // A partial write here would be worse than the duplicate it replaced.
        (await vault.GetAppSecretValueAsync(tenantId, appId, SecretName)).Should().Be("v3");
    }

    /// <summary>
    /// The loser can simply try again, and the retry allocates 3 and succeeds. The constraint
    /// turns a corrupt history into a failed call, which is only an improvement if the failed
    /// call is recoverable — so that is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task The_loser_of_the_race_succeeds_on_a_retry()
    {
        await SeedSecretAsync("v1");
        await vault.UpdateSecretValueAsync(tenantId, secretId, "v2");

        racer.OnNextVersionRead = () => vault.UpdateSecretValueAsync(tenantId, secretId, "v3");
        Func<Task> loser = () => racingVault.UpdateSecretValueAsync(tenantId, secretId, "v-loser");
        await loser.Should().ThrowAsync<DbUpdateException>();

        // The hook has disarmed itself, so this is an uncontested write.
        (await racingVault.UpdateSecretValueAsync(tenantId, secretId, "v-retry")).Should().BeTrue();

        VersionNumbers().Should().Equal(1, 2, 3);
        ArchivedValues().Should().Equal("v1", "v2", "v3");
        (await vault.GetAppSecretValueAsync(tenantId, appId, SecretName)).Should().Be("v-retry");
    }

    // ════════════════════════════════════════════════════════════════
    //  The migration: an installation that ALREADY has duplicates
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ The dangerous half. <c>CREATE UNIQUE INDEX</c> fails on a table that already contains
    /// duplicates, and this application migrates itself at boot with no database console behind
    /// it in production — so that failure is an installation that will not start. The migration
    /// renumbers first; this proves it, against a database built by running the real migration
    /// chain up to the one before, not against a hand-made schema.
    /// </summary>
    [Fact]
    public void A_database_that_already_has_duplicates_migrates_and_is_renumbered()
    {
        using MigrationHarness h = MigrationHarness.UpToJustBeforeTheFix();

        Guid secret = h.SeedSecret();
        // The shape the defect leaves behind: two writers both read MAX=2 and both wrote 3.
        h.AddVersion(secret, number: 1, createdAt: At(1), value: "oldest");
        h.AddVersion(secret, number: 2, createdAt: At(2), value: "second");
        h.AddVersion(secret, number: 3, createdAt: At(3), value: "instance-a");
        h.AddVersion(secret, number: 3, createdAt: At(4), value: "instance-b");

        h.ApplyTheFix();

        // Renumbered 1..4 in creation order — the two version 3s become 3 and 4, and the
        // chronology the list is read in is preserved.
        h.VersionsOf(secret).Should().Equal(
            (1, "oldest"), (2, "second"), (3, "instance-a"), (4, "instance-b"));
    }

    /// <summary>
    /// The pairing for the test above: a secret whose history is already correct comes out of
    /// the migration byte for byte unchanged. Without this, "renumbered correctly" would pass
    /// against a migration that renumbered every secret in the installation.
    /// </summary>
    [Fact]
    public void A_healthy_secret_is_not_renumbered_by_the_migration()
    {
        using MigrationHarness h = MigrationHarness.UpToJustBeforeTheFix();

        Guid healthy = h.SeedSecret();
        // Deliberately NOT dense and NOT starting at 1 — a real history that has been pruned
        // down to its last two versions. A renumber-everything migration would rewrite these to
        // 1 and 2 and this assertion would catch it.
        h.AddVersion(healthy, number: 7, createdAt: At(1), value: "seventh");
        h.AddVersion(healthy, number: 8, createdAt: At(2), value: "eighth");

        Guid broken = h.SeedSecret();
        h.AddVersion(broken, number: 1, createdAt: At(1), value: "a");
        h.AddVersion(broken, number: 1, createdAt: At(2), value: "b");

        h.ApplyTheFix();

        h.VersionsOf(healthy).Should().Equal((7, "seventh"), (8, "eighth"));
        h.VersionsOf(broken).Should().Equal((1, "a"), (2, "b"));
    }

    /// <summary>
    /// And after the migration the constraint is really there — on a database that was repaired
    /// rather than created fresh. Asserting the renumber without this would leave the repaired
    /// installation free to grow the same duplicates again tomorrow.
    /// </summary>
    [Fact]
    public void The_migrated_database_refuses_a_duplicate_afterwards()
    {
        using MigrationHarness h = MigrationHarness.UpToJustBeforeTheFix();

        Guid secret = h.SeedSecret();
        h.AddVersion(secret, number: 1, createdAt: At(1), value: "a");
        h.AddVersion(secret, number: 1, createdAt: At(2), value: "b");

        h.ApplyTheFix();

        // 3 is free, 2 is not.
        h.Invoking(x => x.AddVersion(secret, number: 3, createdAt: At(3), value: "c"))
            .Should().NotThrow();
        h.Invoking(x => x.AddVersion(secret, number: 2, createdAt: At(4), value: "clash"))
            .Should().Throw<DbUpdateException>();
    }

    private static DateTime At(int minute) => new(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc);

    // ════════════════════════════════════════════════════════════════
    //  Fixtures
    // ════════════════════════════════════════════════════════════════

    private async Task SeedSecretAsync(string initialValue)
    {
        tenantId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "acme", Slug = $"acme-{tenantId:N}" });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "acme customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "acme-app" });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        secretId = (await vault.SetAppSecretAsync(tenantId, appId, SecretName, initialValue)).Id;

        // A freshly created secret has no history — every version row counted is one a write
        // under test appended.
        VersionNumbers().Should().BeEmpty();
    }

    private List<int> VersionNumbers()
    {
        using ApplicationDbContext read = testDb.CreateContext();
        return [.. read.Set<VaultSecretVersion>()
            .Where(v => v.SecretId == secretId)
            .OrderBy(v => v.VersionNumber)
            .Select(v => v.VersionNumber)];
    }

    private List<string> ArchivedValues()
    {
        using ApplicationDbContext read = testDb.CreateContext();
        SecretVault v = read.Set<SecretVault>().First(x => x.TenantId == tenantId);
        byte[] dek = testDb.Encryption.UnsealDataKey(v.EncryptedDataKey, v.Nonce);

        return [.. read.Set<VaultSecretVersion>()
            .Where(x => x.SecretId == secretId)
            .OrderBy(x => x.VersionNumber)
            .AsEnumerable()
            .Select(x => testDb.Encryption.Decrypt(dek, x.EncryptedValue, x.Nonce))];
    }

    /// <summary>
    /// Runs a callback once, in the window that matters: after this writer has read
    /// <c>MAX(VersionNumber)</c> and chosen its number, and before it inserts the row.
    ///
    /// <para>⚠️ It hangs off SaveChanges rather than off the MAX query itself, and that is not
    /// cosmetic. An interceptor on the MAX command fires while its reader is still open, so the
    /// competing write hits a SQLite read lock and fails — the test then "passes" on a locking
    /// error that has nothing to do with version numbers. By SavingChangesAsync the reader is
    /// closed, no transaction is open yet, and the competitor writes exactly as a second
    /// instance would.</para>
    ///
    /// <para>⚠️ It disarms itself before invoking the callback. The callback is another real
    /// vault write — left armed, the two instances would recurse into each other forever.</para>
    /// </summary>
    private sealed class CompetingWriterInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? OnNextVersionRead { get; set; }

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool archiving = eventData.Context?.ChangeTracker.Entries<VaultSecretVersion>()
                .Any(e => e.State == EntityState.Added) == true;

            if (OnNextVersionRead is not null && archiving)
            {
                Func<Task> competitor = OnNextVersionRead;
                OnNextVersionRead = null;
                Fired = true;
                await competitor();
            }

            return result;
        }
    }

    /// <summary>
    /// Hands <see cref="VaultService"/> contexts on the shared test database that carry the
    /// racing interceptor. Deliberately minimal: VaultService only ever asks for
    /// <see cref="SecretsDbContext"/>.
    /// </summary>
    private sealed class RacingSecretsFactory(string connectionString, ISaveChangesInterceptor interceptor)
        : IDbContextFactory<SecretsDbContext>
    {
        public SecretsDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<SecretsDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options);
    }

    /// <summary>
    /// A SQLite database built by running the real migration chain, stopped one migration short
    /// of the fix so duplicates can be planted the way a production database acquired them.
    ///
    /// <para>⚠️ Built with migrations, not <c>EnsureCreated</c>. EnsureCreated builds the schema
    /// from the current model, which already carries the unique index — the duplicates could
    /// never be planted, and the migration under test would never run.</para>
    /// </summary>
    private sealed class MigrationHarness : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ApplicationDbContext ctx;
        private readonly Guid vaultId = Guid.NewGuid();

        private MigrationHarness()
        {
            connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            ctx = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        }

        public static MigrationHarness UpToJustBeforeTheFix()
        {
            MigrationHarness h = new();

            List<string> all = [.. h.ctx.Database.GetMigrations()];
            int fix = all.FindIndex(m => m.EndsWith(FixName, StringComparison.Ordinal));
            fix.Should().BeGreaterThan(0, $"the SQLite provider must carry {FixName}");

            h.ctx.Database.GetService<IMigrator>().Migrate(all[fix - 1]);

            Guid tenant = Guid.NewGuid();
            h.ctx.Set<Tenant>().Add(new Tenant
            {
                Id = tenant, Name = "acme", Slug = $"acme-{tenant:N}",
            });
            h.ctx.Set<SecretVault>().Add(new SecretVault
            {
                Id = h.vaultId,
                TenantId = tenant,
                EncryptedDataKey = [1, 2, 3],
                Nonce = [4, 5, 6],
            });
            h.ctx.SaveChanges();
            return h;
        }

        private const string FixName = "UniqueSecretVersionNumber";

        public void ApplyTheFix() => ctx.Database.Migrate();

        public Guid SeedSecret()
        {
            Guid id = Guid.NewGuid();
            ctx.Set<VaultSecret>().Add(new VaultSecret
            {
                Id = id,
                VaultId = vaultId,
                Name = $"SECRET_{id:N}",
                EncryptedValue = [9],
                Nonce = [8],
            });
            ctx.SaveChanges();
            return id;
        }

        /// <summary>
        /// Inserts a version row with an explicit number. ⚠️ Before the fix this happily accepts
        /// a number the secret already has — which is the defect, expressed as a fixture.
        /// </summary>
        public void AddVersion(Guid secretId, int number, DateTime createdAt, string value)
        {
            ctx.Set<VaultSecretVersion>().Add(new VaultSecretVersion
            {
                Id = Guid.NewGuid(),
                SecretId = secretId,
                VersionNumber = number,
                CreatedAt = createdAt,
                EncryptedValue = System.Text.Encoding.UTF8.GetBytes(value),
                Nonce = [7],
            });
            ctx.SaveChanges();
        }

        /// <summary>The secret's versions as (number, value), read fresh and ordered by number.</summary>
        public List<(int, string)> VersionsOf(Guid secretId)
        {
            using ApplicationDbContext read = new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);

            return [.. read.Set<VaultSecretVersion>()
                .Where(v => v.SecretId == secretId)
                .OrderBy(v => v.VersionNumber)
                .AsEnumerable()
                .Select(v => (v.VersionNumber, System.Text.Encoding.UTF8.GetString(v.EncryptedValue)))];
        }

        public void Dispose()
        {
            ctx.Dispose();
            connection.Dispose();
        }
    }
}
