using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EntKube.Web.Services;

/// <summary>
/// The component parts of a TLS certificate secret. Stored encrypted as a JSON
/// document inside <see cref="EntKube.Web.Data.VaultSecret.EncryptedValue"/> when
/// the secret's type is <see cref="EntKube.Web.Data.VaultSecretType.Certificate"/>.
///
/// Only <see cref="Certificate"/> is required. The certificate may be a single
/// leaf certificate or a full PEM chain; intermediates can alternatively be supplied
/// separately in <see cref="Chain"/>. A private key and/or a CA certificate are
/// optional.
/// </summary>
public sealed class CertificateBundle
{
    /// <summary>The (leaf) certificate in PEM format. May already include the chain.</summary>
    public string? Certificate { get; set; }

    /// <summary>The private key in PEM format, when the secret carries one.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>A separate CA (root) certificate in PEM format, when supplied.</summary>
    public string? CaCertificate { get; set; }

    /// <summary>
    /// Intermediate certificate chain in PEM format, kept separately from the leaf.
    /// When syncing, this is appended to the leaf to form <c>tls.crt</c>.
    /// </summary>
    public string? Chain { get; set; }

    public bool HasCertificate => !string.IsNullOrWhiteSpace(Certificate);
    public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKey);
    public bool HasCaCertificate => !string.IsNullOrWhiteSpace(CaCertificate);
    public bool HasChain => !string.IsNullOrWhiteSpace(Chain);

    /// <summary>
    /// The combined <c>tls.crt</c> payload: the leaf certificate followed by any
    /// separately-supplied intermediate chain.
    /// </summary>
    public string CombinedCertificateChain => ConcatPem(Certificate, Chain);

    /// <summary>
    /// The full certificate bundle — leaf certificate + intermediate chain + CA
    /// certificate, concatenated — i.e. everything except the private key. Published
    /// as the <c>fullchain.crt</c> key when syncing. (The private key is, by
    /// convention, never part of a "fullchain".)
    /// </summary>
    public string FullChain => ConcatPem(Certificate, Chain, CaCertificate);

    /// <summary>Concatenates the non-empty PEM parts, each trimmed, with a trailing newline.</summary>
    private static string ConcatPem(params string?[] parts)
    {
        IEnumerable<string> present = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        string joined = string.Join("\n", present);
        return joined.Length > 0 ? joined + "\n" : joined;
    }
}

/// <summary>
/// Parsed, human-readable metadata extracted from a certificate's leaf cert.
/// Used to show subject/issuer/SANs/validity in the UI and to warn on expiry.
/// </summary>
public sealed class CertificateInfo
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required DateTime NotBefore { get; init; }
    public required DateTime NotAfter { get; init; }
    public required string SerialNumber { get; init; }
    public required string Thumbprint { get; init; }
    public required IReadOnlyList<string> SubjectAlternativeNames { get; init; }
    public bool IsSelfSigned { get; init; }

    public bool IsExpired => DateTime.UtcNow > NotAfter;
    public bool NotYetValid => DateTime.UtcNow < NotBefore;
    public int DaysUntilExpiry => (int)Math.Floor((NotAfter - DateTime.UtcNow).TotalDays);

    /// <summary>True when the cert is valid today but expires within 30 days.</summary>
    public bool ExpiringSoon => !IsExpired && !NotYetValid && DaysUntilExpiry <= 30;
}

/// <summary>
/// Helpers for parsing and validating PEM certificate material. All methods are
/// best-effort and never throw on malformed input — they return null or an error
/// string so callers can surface a friendly message.
/// </summary>
public static class CertificateParser
{
    /// <summary>
    /// Parses the first certificate in a PEM blob and returns its metadata, or
    /// null when the input is empty or not a parseable certificate.
    /// </summary>
    public static CertificateInfo? TryParse(string? certificatePem)
    {
        if (string.IsNullOrWhiteSpace(certificatePem)) return null;

        try
        {
            using X509Certificate2 cert = X509Certificate2.CreateFromPem(certificatePem);

            List<string> sans = [];
            foreach (X509Extension ext in cert.Extensions)
            {
                if (ext.Oid?.Value == "2.5.29.17" && ext is X509SubjectAlternativeNameExtension san)
                {
                    sans.AddRange(san.EnumerateDnsNames());
                }
            }

            return new CertificateInfo
            {
                Subject = cert.Subject,
                Issuer = cert.Issuer,
                NotBefore = cert.NotBefore.ToUniversalTime(),
                NotAfter = cert.NotAfter.ToUniversalTime(),
                SerialNumber = cert.SerialNumber,
                Thumbprint = cert.Thumbprint,
                SubjectAlternativeNames = sans,
                IsSelfSigned = cert.Subject == cert.Issuer,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a bundle before it is stored. Ensures a certificate is present and
    /// parseable, that any CA/chain material parses, and — when a private key is
    /// supplied — that it actually matches the certificate.
    /// </summary>
    public static (bool Ok, string? Error) Validate(CertificateBundle bundle)
    {
        if (!bundle.HasCertificate)
        {
            return (false, "A certificate (PEM) is required.");
        }

        CertificateInfo? info = TryParse(bundle.Certificate);
        if (info is null)
        {
            return (false, "The certificate is not valid PEM. Paste the full -----BEGIN CERTIFICATE----- block.");
        }

        if (bundle.HasCaCertificate && TryParse(bundle.CaCertificate) is null)
        {
            return (false, "The CA certificate is not valid PEM.");
        }

        if (bundle.HasChain && TryParse(bundle.Chain) is null)
        {
            return (false, "The certificate chain is not valid PEM.");
        }

        if (bundle.HasPrivateKey)
        {
            try
            {
                // Throws if the key does not correspond to the certificate.
                using X509Certificate2 _ = X509Certificate2.CreateFromPem(bundle.Certificate, bundle.PrivateKey);
            }
            catch
            {
                return (false, "The private key could not be loaded or does not match the certificate.");
            }
        }

        return (true, null);
    }
}

/// <summary>
/// Imports certificate material from an uploaded file into a <see cref="CertificateBundle"/>.
/// Supports PEM (<c>.pem</c>/<c>.crt</c>/<c>.cer</c>, certificate with or without a key and
/// optional chain), DER-encoded single certificates (<c>.cer</c>/<c>.crt</c>/<c>.der</c>),
/// and password-protected PKCS#12 (<c>.pfx</c>/<c>.p12</c>, which carries the certificate,
/// private key, and chain). Never throws — returns a friendly error instead.
/// </summary>
public static class CertificateImporter
{
    public static (bool Ok, string? Error, CertificateBundle? Bundle) Import(
        byte[] data, string fileName, string? password)
    {
        if (data is null || data.Length == 0)
        {
            return (false, "The file is empty.", null);
        }

        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        string text = Encoding.UTF8.GetString(data);
        bool looksPem = text.Contains("-----BEGIN", StringComparison.Ordinal);

        if (ext is ".pfx" or ".p12")
        {
            return ImportPkcs12(data, password);
        }

        if (looksPem)
        {
            return ImportPem(text, password);
        }

        // Binary, non-PEM: try a DER certificate, then fall back to PKCS#12
        // (covers a .pfx that was given a .crt/.cer extension).
        (bool ok, string? err, CertificateBundle? bundle) = ImportDer(data);
        if (ok)
        {
            return (ok, err, bundle);
        }

        return ImportPkcs12(data, password);
    }

    private static (bool, string?, CertificateBundle?) ImportPem(string text, string? password)
    {
        List<(string Label, string Pem)> blocks = FindPemBlocks(text);
        List<string> certs = blocks.Where(b => b.Label == "CERTIFICATE").Select(b => b.Pem).ToList();

        if (certs.Count == 0)
        {
            return (false, "No certificate was found in the file.", null);
        }

        (string Label, string Pem) keyBlock = blocks.FirstOrDefault(b => b.Label.EndsWith("PRIVATE KEY", StringComparison.Ordinal));
        string? key = keyBlock.Pem; // null when no key block was present

        // A legacy OpenSSL "traditional" encrypted key (PKCS#1 with Proc-Type/DEK-Info
        // headers) isn't valid RFC 7468 PEM, so the strict reader skips it entirely.
        // Detect that case and tell the user rather than silently dropping the key.
        if (key is null
            && (text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal) || text.Contains("DEK-Info", StringComparison.Ordinal)))
        {
            return (false, "This file contains a legacy encrypted (PKCS#1 / DEK-Info) private key, which isn't supported. Re-export it as PKCS#8 (openssl pkcs8 -topk8) or upload a .pfx.", null);
        }

        // An encrypted PEM key must be decrypted with the supplied password and stored
        // unencrypted — otherwise it fails validation and would be synced unusable.
        if (key is not null && IsEncryptedKey(keyBlock.Label, key))
        {
            string? decrypted = DecryptPrivateKeyPem(key, password ?? string.Empty);
            if (decrypted is null)
            {
                return (false, "The private key is encrypted — the password is missing, incorrect, or in an unsupported (legacy) format. Re-export it as a PKCS#8 key or a .pfx.", null);
            }
            key = decrypted;
        }

        CertificateBundle bundle = new()
        {
            Certificate = certs[0],
            PrivateKey = key,
        };
        AssignChainAndCa(bundle, certs.Skip(1));
        return (true, null, bundle);
    }

    /// <summary>
    /// True when a PEM private-key block is password-encrypted: either a PKCS#8
    /// "ENCRYPTED PRIVATE KEY" block, or a legacy PKCS#1 key carrying the OpenSSL
    /// <c>Proc-Type/DEK-Info</c> encryption headers.
    /// </summary>
    private static bool IsEncryptedKey(string label, string pem) =>
        label == "ENCRYPTED PRIVATE KEY"
        || pem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal)
        || pem.Contains("DEK-Info", StringComparison.Ordinal);

    /// <summary>
    /// Decrypts a PKCS#8 "ENCRYPTED PRIVATE KEY" PEM with the given password and
    /// returns it as an unencrypted PKCS#8 PEM, trying each key algorithm. Returns
    /// null on a wrong password or an unsupported (e.g. legacy PKCS#1) encryption.
    /// </summary>
    private static string? DecryptPrivateKeyPem(string encryptedPem, string password)
    {
        foreach (Func<AsymmetricAlgorithm> create in new Func<AsymmetricAlgorithm>[]
                 { RSA.Create, ECDsa.Create, ECDiffieHellman.Create, DSA.Create })
        {
            AsymmetricAlgorithm alg = create();
            try
            {
                alg.ImportFromEncryptedPem(encryptedPem, password);
                return alg.ExportPkcs8PrivateKeyPem();
            }
            catch
            {
                // Wrong algorithm or wrong password — try the next algorithm.
            }
            finally
            {
                alg.Dispose();
            }
        }
        return null;
    }

    private static (bool, string?, CertificateBundle?) ImportDer(byte[] data)
    {
        try
        {
            using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(data);
            return (true, null, new CertificateBundle { Certificate = cert.ExportCertificatePem() });
        }
        catch
        {
            return (false, "Could not read the file as a certificate (PEM or DER).", null);
        }
    }

    private static (bool, string?, CertificateBundle?) ImportPkcs12(byte[] data, string? password)
    {
        X509Certificate2Collection collection = [];
        try
        {
            collection = LoadPkcs12(data, password ?? string.Empty);

            if (collection.Count == 0)
            {
                return (false, "The PFX/P12 file contained no certificates.", null);
            }

            // The leaf is the entry that carries the private key (fall back to the first).
            X509Certificate2 leaf = collection.FirstOrDefault(c => c.HasPrivateKey) ?? collection[0];

            CertificateBundle bundle = new()
            {
                Certificate = leaf.ExportCertificatePem(),
                PrivateKey = ExportPrivateKeyPem(leaf),
            };
            AssignChainAndCa(bundle, collection.Where(c => !ReferenceEquals(c, leaf)).Select(c => c.ExportCertificatePem()));
            return (true, null, bundle);
        }
        catch
        {
            return (false, "Could not open the PFX/P12 — wrong password or unsupported file.", null);
        }
        finally
        {
            foreach (X509Certificate2 c in collection)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>
    /// Loads a PKCS#12 collection. Prefers <see cref="X509KeyStorageFlags.EphemeralKeySet"/>
    /// so private keys stay in memory (Linux/servers); falls back to a plain exportable
    /// load on platforms that don't support ephemeral keys (e.g. macOS dev machines).
    /// </summary>
    private static X509Certificate2Collection LoadPkcs12(byte[] data, string password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12Collection(
                data, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (PlatformNotSupportedException)
        {
            return X509CertificateLoader.LoadPkcs12Collection(
                data, password, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>
    /// Splits the non-leaf certificates into a separate CA (the self-signed root, if
    /// any) and an intermediate chain.
    /// </summary>
    private static void AssignChainAndCa(CertificateBundle bundle, IEnumerable<string> nonLeafCerts)
    {
        List<string> chain = [];
        foreach (string pem in nonLeafCerts)
        {
            CertificateInfo? info = CertificateParser.TryParse(pem);
            if (info is not null && info.IsSelfSigned && bundle.CaCertificate is null)
            {
                bundle.CaCertificate = pem.Trim();
            }
            else
            {
                chain.Add(pem.Trim());
            }
        }

        if (chain.Count > 0)
        {
            bundle.Chain = string.Join("\n", chain);
        }
    }

    private static string? ExportPrivateKeyPem(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey)
        {
            return null;
        }

        AsymmetricAlgorithm? key =
            cert.GetRSAPrivateKey()
            ?? (AsymmetricAlgorithm?)cert.GetECDsaPrivateKey()
            ?? cert.GetDSAPrivateKey();

        try
        {
            return key?.ExportPkcs8PrivateKeyPem();
        }
        catch
        {
            return null;
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>Finds every PEM block in the text, returning (label, full PEM) pairs in order.</summary>
    private static List<(string Label, string Pem)> FindPemBlocks(string text)
    {
        List<(string, string)> blocks = [];
        int offset = 0;
        while (offset < text.Length)
        {
            ReadOnlySpan<char> span = text.AsSpan(offset);
            if (!PemEncoding.TryFind(span, out PemFields fields))
            {
                break;
            }

            string label = span[fields.Label].ToString();
            string pem = span[fields.Location].ToString();
            blocks.Add((label, pem));
            offset += fields.Location.End.GetOffset(span.Length);
        }
        return blocks;
    }
}
