using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose outbound mTLS credential a method acts on.
///
/// <para>A credential names the partner endpoint an app presents a client certificate to, and
/// which certificate that is. <c>OutboundMtlsCredential</c> carries a TenantId of its own, so
/// these are direct comparisons — four methods simply never made them.</para>
/// </summary>
public class OutboundMtlsTenancyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly OutboundMtlsService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirAppId = Guid.NewGuid();
    private readonly Guid theirCredentialId = Guid.NewGuid();

    public OutboundMtlsTenancyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });
        db.Apps.Add(new App { Id = theirAppId, CustomerId = customerId, Name = "journal" });

        db.Set<OutboundMtlsCredential>().Add(new OutboundMtlsCredential
        {
            Id = theirCredentialId,
            TenantId = otherTenantId,
            AppId = theirAppId,
            Name = "partner-api",
            Host = "api.partner.se",
            Port = 443,
            VaultSecretId = Guid.NewGuid(),
            Mode = OutboundMtlsMode.SecretOnly,
        });
        db.SaveChanges();

        sut = new OutboundMtlsService(
            testDb.Factory, vault, NullLogger<OutboundMtlsService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private OutboundMtlsCredential Ours(string name = "ours-api")
    {
        OutboundMtlsCredential c = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppId = appId,
            Name = name,
            Host = "api.ours.se",
            Port = 443,
            VaultSecretId = Guid.NewGuid(),
            Mode = OutboundMtlsMode.SecretOnly,
        };
        db.Set<OutboundMtlsCredential>().Add(c);
        db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task Another_tenants_credentials_are_not_listable_or_readable()
    {
        OutboundMtlsCredential ours = Ours();

        // A positive first, so the refusals below are about the tenant and not an empty table.
        (await sut.GetForAppAsync(tenantId, appId)).Should().ContainSingle();
        (await sut.GetAsync(tenantId, ours.Id)).Should().NotBeNull();

        (await sut.GetForAppAsync(tenantId, theirAppId)).Should().BeEmpty();
        (await sut.GetAsync(tenantId, theirCredentialId)).Should().BeNull();
    }

    /// <summary>
    /// <b>Host is the partner endpoint the certificate is presented to</b>, and VaultSecretId is
    /// which certificate that is. By id alone this repointed somebody else's outbound mTLS.
    /// </summary>
    [Fact]
    public async Task Another_tenants_credential_cannot_be_repointed()
    {
        Func<Task> act = () => sut.UpdateAsync(tenantId, new OutboundMtlsCredential
        {
            Id = theirCredentialId,
            TenantId = otherTenantId,
            AppId = theirAppId,
            Name = "partner-api",
            Host = "api.attacker.example",
            Port = 443,
            VaultSecretId = Guid.NewGuid(),
            Mode = OutboundMtlsMode.SecretOnly,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Credential not found, or it is not this tenant's.");

        OutboundMtlsCredential stored = await db.Set<OutboundMtlsCredential>().AsNoTracking()
            .FirstAsync(c => c.Id == theirCredentialId);

        stored.Host.Should().Be("api.partner.se");
    }

    /// <summary>
    /// Removing a credential takes the client certificate off the next apply, and a partner
    /// that requires mTLS refuses the connection without it.
    /// </summary>
    [Fact]
    public async Task Another_tenants_credential_cannot_be_deleted()
    {
        OutboundMtlsCredential ours = Ours();

        await sut.DeleteAsync(tenantId, theirCredentialId);
        (await db.Set<OutboundMtlsCredential>().AsNoTracking()
            .AnyAsync(c => c.Id == theirCredentialId)).Should().BeTrue();

        // Ours does go, or the survival above proves nothing.
        await sut.DeleteAsync(tenantId, ours.Id);
        (await db.Set<OutboundMtlsCredential>().AsNoTracking()
            .AnyAsync(c => c.Id == ours.Id)).Should().BeFalse();
    }

    /// <summary>
    /// The create path has no row to compare against, only a value to normalise — and a
    /// TenantId on a caller-supplied entity is a claim, not an authorization. Without the
    /// stamp the credential is filed under whatever tenant the entity names.
    ///
    /// <para>The vault read was never the hole: GetCertificateBundleByIdAsync is itself
    /// tenant-scoped, so the certificate has to be ours either way. What the stamp decides is
    /// whose list the row lands in.</para>
    /// </summary>
    [Fact]
    public async Task A_new_credential_cannot_be_filed_under_another_tenant()
    {
        await vault.InitializeVaultAsync(tenantId);

        using RSA rsa = RSA.Create(2048);
        CertificateRequest req = new("CN=client.ours.se", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert =
            req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        string combined = rsa.ExportPkcs8PrivateKeyPem().Trim() + "\n" + cert.ExportCertificatePem().Trim();
        (bool ok, _, CertificateBundle? bundle) = CertificateImporter.Import(
            System.Text.Encoding.UTF8.GetBytes(combined), "client.pem", null);
        ok.Should().BeTrue();

        (bool stored, string? error, Guid? secretId) =
            await vault.SetTenantCertificateAsync(tenantId, "client", bundle!);
        stored.Should().BeTrue(error);

        OutboundMtlsCredential created = await sut.CreateAsync(tenantId, new OutboundMtlsCredential
        {
            TenantId = otherTenantId,
            AppId = appId,
            Name = "planted",
            Host = "api.partner.se",
            Port = 443,
            VaultSecretId = secretId!.Value,
            Mode = OutboundMtlsMode.SecretOnly,
        });

        created.TenantId.Should().Be(tenantId);

        OutboundMtlsCredential row = await db.Set<OutboundMtlsCredential>().AsNoTracking()
            .FirstAsync(c => c.Id == created.Id);

        row.TenantId.Should().Be(tenantId);
        (await sut.GetForAppAsync(otherTenantId, appId)).Should().BeEmpty();
    }
}
