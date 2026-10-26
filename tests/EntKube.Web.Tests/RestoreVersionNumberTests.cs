using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// What a restore does with a bundle whose version numbers collide.
///
/// <para><b>Why this exists.</b> <c>ArchiveVersionAsync</c> allocated version numbers as
/// <c>MAX + 1</c> with nothing enforcing uniqueness, so any installation where two writers
/// archived one secret at once recorded a single change as two "version 4" rows. The unique index
/// being added alongside this repairs that and renumbers what is already stored — but a BUNDLE
/// captured BEFORE the repair still carries the duplicates, and <c>RestoreAsync</c> wrote
/// <c>VersionNumber</c> straight through.</para>
///
/// <para>⚠️ That would have failed at <c>SaveChangesAsync</c> on exactly the installations the
/// repair exists for: the ones that had duplicates. A restore is also the one operation an
/// operator reaches for when things are already bad.</para>
///
/// <para>⚠️ <b>These tests assert the RENUMBERING, not the absence of an exception.</b> The unique
/// index arrives in a separate change, so on this branch a duplicate would insert happily and a
/// "does not throw" test would pass while proving nothing. What makes the index safe is that the
/// numbers coming out are distinct and in the right order, so that is what is asserted.</para>
/// </summary>
public sealed class RestoreVersionNumberTests : IDisposable
{
    private readonly SqliteConnection source = new("DataSource=:memory:");
    private readonly SqliteConnection destination = new("DataSource=:memory:");

    private readonly VaultEncryptionService encryption = new(new byte[32]);
    private readonly BackupService exporter;
    private readonly BackupService importer;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid vaultId = Guid.NewGuid();
    private readonly byte[] dek = new byte[32];

    public RestoreVersionNumberTests()
    {
        source.Open();
        destination.Open();

        foreach (SqliteConnection connection in (SqliteConnection[])[source, destination])
        {
            using ApplicationDbContext db = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
        }

        exporter = new BackupService(
            new TestDbContextFactory(source), encryption, NullLogger<BackupService>.Instance);
        importer = new BackupService(
            new TestDbContextFactory(destination), encryption, NullLogger<BackupService>.Instance);

        Random.Shared.NextBytes(dek);

        using ApplicationDbContext seed = Source();
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        (byte[] sealedDek, byte[] dekNonce) = encryption.SealDataKey(dek);
        seed.SecretVaults.Add(new SecretVault
        {
            Id = vaultId, TenantId = tenantId,
            EncryptedDataKey = sealedDek, Nonce = dekNonce, CreatedAt = DateTime.UtcNow,
        });

        seed.SaveChanges();
    }

    private ApplicationDbContext Source() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(source).Options);

    private ApplicationDbContext Destination() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(destination).Options);

    /// <summary>Seeds one secret and the version rows given, with the numbers given verbatim.</summary>
    private Guid SeedSecret(string name, params (int Number, string Value, int MinutesAgo)[] versions)
    {
        using ApplicationDbContext db = Source();

        Guid secretId = Guid.NewGuid();
        (byte[] ct, byte[] nonce) = encryption.Encrypt(dek, "current");

        db.VaultSecrets.Add(new VaultSecret
        {
            Id = secretId, VaultId = vaultId, Name = name,
            SecretType = VaultSecretType.Opaque,
            EncryptedValue = ct, Nonce = nonce,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        foreach ((int number, string value, int minutesAgo) in versions)
        {
            (byte[] vct, byte[] vnonce) = encryption.Encrypt(dek, value);
            db.VaultSecretVersions.Add(new VaultSecretVersion
            {
                Id = Guid.NewGuid(), SecretId = secretId, VersionNumber = number,
                EncryptedValue = vct, Nonce = vnonce,
                CreatedBy = "nils", CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
            });
        }

        db.SaveChanges();
        return secretId;
    }

    /// <summary>Exports from the source and imports into the destination, as the round-trip tests do.</summary>
    private async Task RoundTripAsync()
    {
        byte[] bundle = await exporter.ExportAsync("nils");
        using MemoryStream stream = new(bundle);
        await importer.ImportAsync(stream, wipeExisting: false);
    }

    private List<VaultSecretVersion> RestoredVersions(Guid secretId)
    {
        using ApplicationDbContext db = Destination();
        return [.. db.VaultSecretVersions.AsNoTracking()
            .Where(v => v.SecretId == secretId)
            .OrderBy(v => v.CreatedAt)];
    }

    /// <summary>
    /// ⭐ Two rows numbered 4 come back as 4 and 5 — distinct, and in the order they happened.
    /// </summary>
    [Fact]
    public async Task A_bundle_with_duplicate_numbers_restores_with_distinct_ones()
    {
        Guid secretId = SeedSecret("api-key",
            (4, "older", 20),
            (4, "newer", 10));

        await RoundTripAsync();

        List<VaultSecretVersion> restored = RestoredVersions(secretId);

        restored.Should().HaveCount(2, "no version may be dropped to make the numbers fit");

        restored.Select(v => v.VersionNumber).Should().OnlyHaveUniqueItems(
            "two rows sharing a number is the state the unique index refuses");

        restored.Select(v => v.VersionNumber).Should().BeInAscendingOrder(
            "renumbering follows creation order, so the history still reads in the order it "
            + "happened");
    }

    /// <summary>
    /// The values survive the renumbering — a history whose numbers are tidy but whose contents
    /// moved would be worse than the duplicate.
    /// </summary>
    [Fact]
    public async Task Renumbering_does_not_move_the_values()
    {
        Guid secretId = SeedSecret("api-key",
            (4, "older", 20),
            (4, "newer", 10));

        await RoundTripAsync();

        List<VaultSecretVersion> restored = RestoredVersions(secretId);

        byte[] destinationDek;
        using (ApplicationDbContext db = Destination())
        {
            SecretVault v = db.SecretVaults.AsNoTracking().First();
            destinationDek = encryption.UnsealDataKey(v.EncryptedDataKey, v.Nonce);
        }

        restored.Select(v => encryption.Decrypt(destinationDek, v.EncryptedValue, v.Nonce))
            .Should().Equal(["older", "newer"],
                "oldest first — the renumbering orders by CreatedAt, and the values must follow "
                + "the same order rather than being paired with the wrong number");
    }

    /// <summary>
    /// ⚠️ A pruned history is legitimately NOT dense, and must be left alone.
    ///
    /// <para>Ten versions are retained per secret, so an old secret's surviving rows are numbered
    /// 7 and 8 rather than 1 and 2. Those numbers are correct and an operator may have quoted
    /// one. A renumber-everything restore would rewrite them to 1 and 2 to look tidier, which is
    /// the error the migration beside this one also avoids.</para>
    /// </summary>
    [Fact]
    public async Task A_pruned_but_healthy_history_keeps_its_numbers()
    {
        Guid secretId = SeedSecret("db-password",
            (7, "seventh", 20),
            (8, "eighth", 10));

        await RoundTripAsync();

        RestoredVersions(secretId).Select(v => v.VersionNumber).Should().Equal([7, 8],
            "nothing collided, so nothing should have been touched");
    }

    /// <summary>
    /// Only the colliding secret is renumbered. ⚠️ Without this, "renumbers duplicates" would
    /// also be satisfied by a restore that renumbered every secret in the bundle whenever any one
    /// of them collided.
    /// </summary>
    [Fact]
    public async Task One_colliding_secret_does_not_renumber_its_neighbour()
    {
        Guid colliding = SeedSecret("api-key", (4, "a", 20), (4, "b", 10));
        Guid healthy = SeedSecret("db-password", (7, "seventh", 20), (8, "eighth", 10));

        await RoundTripAsync();

        RestoredVersions(colliding).Select(v => v.VersionNumber).Should().OnlyHaveUniqueItems();
        RestoredVersions(healthy).Select(v => v.VersionNumber).Should().Equal([7, 8]);
    }

    public void Dispose()
    {
        source.Dispose();
        destination.Dispose();
        GC.SuppressFinalize(this);
    }
}
