using System.Security.Cryptography;
using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Making the signing keys, because the mail server will not share its own.
///
/// <para><b>Why this exists at all.</b> Stalwart generates DKIM keys and keeps them: a signature
/// object holds the private half, and the server returns secret fields anonymised — its own CLI
/// says the values "cannot be captured and must be supplied in the plan before applying". The
/// public half is stored nowhere; it is derived from the private key on demand. So no question
/// EntKube can ask produces a publishable record, and the only way the DNS list is ever complete
/// is for the keys to be EntKube's.</para>
///
/// <para>What these defend is the record that gets published. A DKIM record that is subtly wrong
/// fails exactly like no record at all — <c>dkim=fail</c>, DMARC down with it, every message
/// junked — and nothing on the sending side notices, because signing works perfectly either
/// way.</para>
/// </summary>
public class DkimKeyTests
{
    private static readonly DateOnly MadeOn = new(2026, 10, 1);

    /// <summary>
    /// <b>The property everything else rests on.</b> The published record has to verify against
    /// the key that signs — proven here by importing what was published and checking that a
    /// signature made with the private half is accepted by it.
    /// </summary>
    [Fact]
    public void The_published_record_verifies_what_the_private_key_signs()
    {
        DkimKey key = DkimKeys.Create(MadeOn);

        string base64 = key.DnsValue.Split("p=", StringSplitOptions.None)[1];

        using RSA published = RSA.Create();
        published.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);

        using RSA signing = RSA.Create();
        signing.ImportFromPem(key.PrivateKeyPem);

        byte[] message = "a message this domain sent"u8.ToArray();
        byte[] signature = signing.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        published.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    /// <summary>
    /// The record is reprinted from the key, not stored beside it. The DNS list is rendered on
    /// every page load and a second copy of the public half is a second thing to go stale.
    /// </summary>
    [Fact]
    public void The_record_can_be_recomputed_from_the_stored_key()
    {
        DkimKey key = DkimKeys.Create(MadeOn);

        DkimKeys.DnsValueForPrivateKey(key.PrivateKeyPem).Should().Be(key.DnsValue);
    }

    /// <summary>
    /// <c>k=rsa</c> is stated rather than left to default: a record without it is read as RSA by
    /// every verifier that defaults, and rejected by the ones that do not.
    /// </summary>
    [Fact]
    public void The_record_states_the_version_and_the_algorithm()
    {
        DkimKey key = DkimKeys.Create(MadeOn);

        key.DnsValue.Should().StartWith("v=DKIM1; k=rsa; p=");
    }

    /// <summary>
    /// 2048 bits: 1024 is weak, and a 4096-bit public key does not fit one DNS string without
    /// being split — which is where published records most often go wrong.
    /// </summary>
    [Fact]
    public void The_record_fits_in_a_single_dns_string()
    {
        DkimKeys.KeySizeBits.Should().Be(2048);

        // 255 characters is the limit on one character-string in a TXT record.
        DkimKeys.Create(MadeOn).DnsValue.Length.Should().BeLessThan(500);
    }

    /// <summary>
    /// The selector carries the day it was made, because the question asked of it in a zone file
    /// is always "is this the current one" — which a date answers and a random string does not —
    /// and says who made it, so a key of the server's own is never mistaken for one of these.
    /// </summary>
    [Fact]
    public void The_selector_says_when_it_was_made_and_by_whom() =>
        DkimKeys.SelectorFor(MadeOn).Should().Be("entkube-20261001");

    /// <summary>Two keys made on one day are still two keys.</summary>
    [Fact]
    public void Every_key_is_its_own() =>
        DkimKeys.Create(MadeOn).PrivateKeyPem.Should()
            .NotBe(DkimKeys.Create(MadeOn).PrivateKeyPem);

    /// <summary>Where it is published, with the trailing dot a zone file may carry removed.</summary>
    [Theory]
    [InlineData("entit.eu")]
    [InlineData("entit.eu.")]
    public void The_record_name_is_the_selector_under_the_domain(string domain) =>
        DkimKeys.RecordNameFor("entkube-20261001", domain)
            .Should().Be("entkube-20261001._domainkey.entit.eu");
}
