using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the envelope encryption service that powers the secrets vault.
/// The service uses AES-256-GCM at both layers: root key → DEK, DEK → secret values.
/// </summary>
public class VaultEncryptionServiceTests
{
    // A stable 32-byte root key for testing purposes.
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly VaultEncryptionService sut = new(TestRootKey);

    // --- Data Key Generation ---

    [Fact]
    public void GenerateDataKey_ReturnsThirtyTwoBytes()
    {
        // A data encryption key should be 256 bits (32 bytes) for AES-256.

        byte[] dataKey = sut.GenerateDataKey();

        dataKey.Should().HaveCount(32);
    }

    [Fact]
    public void GenerateDataKey_ProducesUniqueKeysEachCall()
    {
        // Every call should produce a cryptographically random key.

        byte[] key1 = sut.GenerateDataKey();
        byte[] key2 = sut.GenerateDataKey();

        key1.Should().NotEqual(key2);
    }

    // --- Sealing and Unsealing Data Keys ---

    [Fact]
    public void SealDataKey_ProducesNonEmptyCiphertextAndNonce()
    {
        // Sealing a DEK with the root key should produce ciphertext + nonce.

        byte[] dataKey = sut.GenerateDataKey();

        (byte[] encryptedKey, byte[] nonce) = sut.SealDataKey(dataKey);

        encryptedKey.Should().NotBeEmpty();
        nonce.Should().NotBeEmpty();
        encryptedKey.Should().NotEqual(dataKey);
    }

    [Fact]
    public void UnsealDataKey_RecoversOriginalKey()
    {
        // Unsealing a previously sealed DEK should return the exact same key.

        byte[] originalKey = sut.GenerateDataKey();

        (byte[] encryptedKey, byte[] nonce) = sut.SealDataKey(originalKey);
        byte[] recovered = sut.UnsealDataKey(encryptedKey, nonce);

        recovered.Should().Equal(originalKey);
    }

    [Fact]
    public void UnsealDataKey_WithWrongRootKey_Throws()
    {
        // If the root key is different, unsealing must fail.

        byte[] dataKey = sut.GenerateDataKey();
        (byte[] encryptedKey, byte[] nonce) = sut.SealDataKey(dataKey);

        // Create a service with a different root key.
        byte[] wrongRoot = new byte[32];
        Array.Fill(wrongRoot, (byte)0xFF);
        VaultEncryptionService wrongService = new(wrongRoot);

        Action act = () => wrongService.UnsealDataKey(encryptedKey, nonce);

        act.Should().Throw<Exception>();
    }

    // --- Encrypting and Decrypting Secret Values ---

    [Fact]
    public void Encrypt_ProducesCiphertextDifferentFromPlaintext()
    {
        // Encrypting a secret value should produce unreadable ciphertext.

        byte[] dataKey = sut.GenerateDataKey();
        string plaintext = "super-secret-database-password";

        (byte[] ciphertext, byte[] nonce) = sut.Encrypt(dataKey, plaintext);

        ciphertext.Should().NotBeEmpty();
        nonce.Should().NotBeEmpty();
        System.Text.Encoding.UTF8.GetString(ciphertext).Should().NotBe(plaintext);
    }

    [Fact]
    public void Decrypt_RecoversOriginalPlaintext()
    {
        // Decrypting should return the exact original secret value.

        byte[] dataKey = sut.GenerateDataKey();
        string original = "my-api-key-12345!@#$%";

        (byte[] ciphertext, byte[] nonce) = sut.Encrypt(dataKey, original);
        string recovered = sut.Decrypt(dataKey, ciphertext, nonce);

        recovered.Should().Be(original);
    }

    [Fact]
    public void Decrypt_WithWrongDataKey_Throws()
    {
        // Using the wrong DEK to decrypt must fail — tenant isolation.

        byte[] dataKey1 = sut.GenerateDataKey();
        byte[] dataKey2 = sut.GenerateDataKey();
        string plaintext = "tenant-a-secret";

        (byte[] ciphertext, byte[] nonce) = sut.Encrypt(dataKey1, plaintext);

        Action act = () => sut.Decrypt(dataKey2, ciphertext, nonce);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Encrypt_SamePlaintext_ProducesDifferentCiphertext()
    {
        // Each encryption uses a unique nonce so identical plaintexts
        // produce different ciphertexts — preventing pattern analysis.

        byte[] dataKey = sut.GenerateDataKey();
        string plaintext = "repeated-secret";

        (byte[] ciphertext1, byte[] nonce1) = sut.Encrypt(dataKey, plaintext);
        (byte[] ciphertext2, byte[] nonce2) = sut.Encrypt(dataKey, plaintext);

        ciphertext1.Should().NotEqual(ciphertext2);
        nonce1.Should().NotEqual(nonce2);
    }

    [Fact]
    public void Encrypt_EmptyString_CanBeDecrypted()
    {
        // Edge case: empty secrets should encrypt/decrypt cleanly.

        byte[] dataKey = sut.GenerateDataKey();

        (byte[] ciphertext, byte[] nonce) = sut.Encrypt(dataKey, "");
        string recovered = sut.Decrypt(dataKey, ciphertext, nonce);

        recovered.Should().BeEmpty();
    }

    [Fact]
    public void Encrypt_LargeValue_CanBeDecrypted()
    {
        // Secrets like certificates or multi-line configs can be large.

        byte[] dataKey = sut.GenerateDataKey();
        string large = new string('X', 100_000);

        (byte[] ciphertext, byte[] nonce) = sut.Encrypt(dataKey, large);
        string recovered = sut.Decrypt(dataKey, ciphertext, nonce);

        recovered.Should().Be(large);
    }
}
