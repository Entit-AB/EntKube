using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EntKube.Web.Tests;

/// <summary>
/// Who may change a client-certificate trust anchor.
///
/// <para>⚠️ <b>The sharpest scoping bug this programme has turned up.</b> A
/// <c>ClientCaBundle</c> is the set of CAs whose client certificates the gateway will accept on
/// a listener port. <c>AddCertificateAsync</c> checked that the bundle existed with
/// <c>AnyAsync(b =&gt; b.Id == bundleId)</c> — no tenant anywhere — and then added the CA. So one
/// tenant could add their own CA to another tenant's anchor and afterwards issue themselves a
/// client certificate that authenticates to that tenant's applications. Not a disclosure: an
/// authentication bypass, arranged with one id.</para>
///
/// <para>Every other by-id method on the service was unscoped the same way, including
/// <c>UpdateBundleAsync</c>, which can move the bundle's <c>ListenerPort</c>. Client-certificate
/// validation applies per port rather than per hostname — the service's own comment says 443
/// must stay free of it — so moving somebody else's anchor onto 443 would start demanding client
/// certificates from every host sharing that listener.</para>
///
/// <para><c>ClientCaBundle</c> carries a <c>TenantId</c>; <c>ClientCaCertificate</c> does not, so
/// certificates scope through their bundle.</para>
/// </summary>
public sealed class MtlsAnchorTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory dbFactory;
    private readonly MtlsService sut;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();

    public MtlsAnchorTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        dbFactory = new TestDbContextFactory(connection);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Tenants.AddRange(
            new Tenant { Id = ourTenant, Name = "Us", Slug = $"us-{ourTenant:N}" },
            new Tenant { Id = theirTenant, Name = "Them", Slug = $"them-{theirTenant:N}" });
        db.SaveChanges();

        sut = new MtlsService(dbFactory, NullLogger<MtlsService>.Instance);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ⚠️ A real CA, not the self-signed leaf TrustBundleServiceTests uses. MtlsService refuses
    /// anything without <c>basicConstraints CA:TRUE</c> — "upload the CA that signs your client
    /// certificates, not a client certificate itself" — which is a good check and the reason the
    /// first draft of this test failed on the fixture rather than on the thing under test.
    /// </summary>
    private static string NewCaPem()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest req = new(
            "CN=Test Client CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0,
            critical: true));
        using X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return cert.ExportCertificatePem();
    }

    [Fact]
    public async Task A_client_ca_cannot_be_added_to_another_tenants_trust_anchor()
    {
        ClientCaBundle theirs = await sut.CreateBundleAsync(
            theirTenant, "Partner mTLS", "their anchor", 8443);
        ClientCaCertificate theirCert = await sut.AddCertificateAsync(
            theirTenant, theirs.Id, "Their Partner CA", NewCaPem());

        // ⚠️ The bypass: our CA into their anchor.
        Func<Task> plant = () => sut.AddCertificateAsync(
            ourTenant, theirs.Id, "Our CA", NewCaPem());
        await plant.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = dbFactory.CreateDbContext();
        after.Set<ClientCaCertificate>().Count(c => c.BundleId == theirs.Id).Should().Be(1,
            "a CA in their anchor is a client certificate we could mint and present to their apps");
        after.Set<ClientCaCertificate>().Single(c => c.BundleId == theirs.Id)
            .Name.Should().Be("Their Partner CA");

        // We cannot read it, move its port, remove their CA, or delete it.
        (await sut.GetBundleAsync(ourTenant, theirs.Id)).Should().BeNull(
            "the anchor exists, so this is a refusal");
        (await sut.GetBundleAsync(theirTenant, theirs.Id)).Should().NotBeNull();

        Func<Task> repoint = () => sut.UpdateBundleAsync(ourTenant, theirs.Id, "hijacked", null, 443);
        Func<Task> drop = () => sut.DeleteBundleAsync(ourTenant, theirs.Id);
        await repoint.Should().ThrowAsync<InvalidOperationException>();
        await drop.Should().ThrowAsync<InvalidOperationException>();

        await sut.RemoveCertificateAsync(ourTenant, theirCert.Id);

        using ApplicationDbContext intact = dbFactory.CreateDbContext();
        ClientCaBundle bundle = intact.Set<ClientCaBundle>().Single(b => b.Id == theirs.Id);
        bundle.Name.Should().Be("Partner mTLS");
        bundle.ListenerPort.Should().Be(8443,
            "moving their anchor to 443 would demand client certificates from every host there");
        intact.Set<ClientCaCertificate>().Any(c => c.Id == theirCert.Id).Should().BeTrue(
            "removing their CA would break the mTLS their partners depend on");

        // The owner can still do all of it, so each refusal above is about the crossing.
        await sut.UpdateBundleAsync(theirTenant, theirs.Id, "renamed", null, 9443);
        (await sut.GetBundleAsync(theirTenant, theirs.Id))!.ListenerPort.Should().Be(9443);
        await sut.RemoveCertificateAsync(theirTenant, theirCert.Id);
        await sut.DeleteBundleAsync(theirTenant, theirs.Id);
        (await sut.GetBundleAsync(theirTenant, theirs.Id)).Should().BeNull();
    }
}
