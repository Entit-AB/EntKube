using System.Security.Cryptography;

namespace EntKube.Web.Services.Mail;

/// <summary>A signing key and the record that has to be published for it.</summary>
/// <param name="Selector">
/// The name the record is published under. Chosen by whoever makes the key, and the half of a
/// DKIM record that cannot be guessed — a perfectly good public key fails every check if it is
/// published against the wrong one.
/// </param>
/// <param name="PrivateKeyPem">
/// PKCS#8 PEM. Goes to the vault and into the mail server's configuration, and nowhere else —
/// never into a Job's output, a log line or a page.
/// </param>
/// <param name="DnsValue">
/// The TXT value to publish at <c>{selector}._domainkey.{domain}</c>.
/// </param>
public readonly record struct DkimKey(string Selector, string PrivateKeyPem, string DnsValue);

/// <summary>
/// Makes DKIM signing keys, and the DNS record that goes with each one.
///
/// <para><b>Why EntKube has to make them rather than read them.</b> Stalwart generates its own
/// keys and will not give the public half back: a signature object holds the private key, and
/// the server returns secret fields anonymised — its own CLI says the values "cannot be captured
/// and must be supplied in the plan before applying". The public half is not stored anywhere at
/// all; it is derived from the private key on demand. So there is no question to ask that
/// produces a publishable record, and the only way a DNS list can ever be complete is for the
/// keys to be EntKube's, held in the vault, with the public half computed here.</para>
///
/// <para><b>Why that is better anyway.</b> A key the server rotates takes its selector with it,
/// and the published record silently stops matching — the failure is total at the far end and
/// invisible from here. A key EntKube owns has a selector that only changes when somebody
/// decides it should.</para>
///
/// <para><b>RSA only, deliberately.</b> Ed25519 signatures are smaller and newer, and a verifier
/// that does not know them falls back to the RSA signature — so a domain signing with both gains
/// nothing it does not already have from RSA alone, while .NET has no Ed25519 of its own and
/// adding a cryptography dependency to sign mail is a poor trade. 2048 bits because 1024 is
/// weak and a 4096-bit public key does not fit one DNS string without splitting, which is where
/// published records most often go wrong.</para>
/// </summary>
public static class DkimKeys
{
    /// <summary>The key size. 2048 is the floor every verifier accepts and the ceiling one TXT string holds.</summary>
    public const int KeySizeBits = 2048;

    /// <summary>
    /// A new key for a domain, with the selector it is to be published under.
    /// </summary>
    /// <param name="on">
    /// The day the key was made, which the selector carries so two generations can be told
    /// apart at a glance in a zone file.
    /// </param>
    public static DkimKey Create(DateOnly on)
    {
        using RSA rsa = RSA.Create(KeySizeBits);

        return new DkimKey(
            SelectorFor(on),
            rsa.ExportPkcs8PrivateKeyPem(),
            DnsValueFor(rsa));
    }

    /// <summary>
    /// The selector, built from the date rather than randomly.
    ///
    /// <para>It appears in a zone file beside the key, and the question asked of it there is
    /// always "is this the current one". A date answers that; a random string does not. The
    /// <c>entkube</c> part says who made it, so a key of the server's own is never mistaken for
    /// one of these.</para>
    /// </summary>
    public static string SelectorFor(DateOnly on) =>
        $"entkube-{on:yyyyMMdd}";

    /// <summary>
    /// The TXT value for a public key: what a verifier fetches and checks a signature against.
    /// </summary>
    /// <remarks>
    /// <c>k=rsa</c> is stated rather than left to default, because a record without it is read
    /// as RSA by every verifier that defaults and rejected by the ones that do not.
    /// </remarks>
    public static string DnsValueFor(RSA rsa) =>
        $"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}";

    /// <summary>
    /// The public record for a key already held — so a record can be reprinted from the vault
    /// without making a new key, which is what happens every time the DNS list is rendered.
    /// </summary>
    public static string DnsValueForPrivateKey(string privateKeyPem)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        return DnsValueFor(rsa);
    }

    /// <summary>The full record name, as it is published.</summary>
    public static string RecordNameFor(string selector, string domain) =>
        $"{selector}._domainkey.{domain.Trim().TrimEnd('.')}";
}
