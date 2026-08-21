using System.Security.Cryptography;
using System.Text;

namespace EntKube.Web.Services.Outbound;

/// <summary>
/// Signs outbound webhook payloads so a receiver can verify they came from EntKube
/// and have not been altered.
///
/// Without a signature, a webhook receiver has no way to tell an EntKube notification
/// from anyone else who learned the URL — and webhook URLs leak, through logs, browser
/// history and copy-paste. A bearer token in a header proves the sender knew a secret
/// but says nothing about the body; an HMAC over the body proves both.
///
/// The timestamp is signed alongside the body so a captured delivery cannot be replayed
/// indefinitely: a receiver rejects anything outside its tolerance window.
///
/// Header format follows the convention GitHub established, because receivers and
/// off-the-shelf verification snippets already understand it.
/// </summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-EntKube-Signature-256";
    public const string TimestampHeader = "X-EntKube-Timestamp";
    public const string EventHeader = "X-EntKube-Event";

    /// <summary>
    /// Computes <c>sha256=&lt;hex&gt;</c> over "{timestamp}.{body}".
    ///
    /// The timestamp is inside the signed material rather than merely sent beside it —
    /// signing only the body would let anyone replay a captured delivery with a fresh
    /// timestamp and a still-valid signature.
    /// </summary>
    public static string Sign(string secret, long unixTimestamp, string body)
    {
        byte[] key = Encoding.UTF8.GetBytes(secret);
        byte[] payload = Encoding.UTF8.GetBytes($"{unixTimestamp}.{body}");

        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, payload));
    }

    /// <summary>
    /// Verifies a signature. Provided so the documented receiver-side check is exercised
    /// by our own tests rather than only described in prose.
    /// </summary>
    public static bool Verify(string secret, long unixTimestamp, string body, string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }

        string expected = Sign(secret, unixTimestamp, body);

        // Fixed-time comparison: a byte-by-byte early exit leaks how much of a guessed
        // signature was correct, which is enough to forge one a byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }
}
