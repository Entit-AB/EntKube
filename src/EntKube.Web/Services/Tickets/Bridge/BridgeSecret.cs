using System.Security.Cryptography;
using System.Text;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>
/// The shared secret a customer's system presents, and how it is kept.
///
/// <para><b>Hashed, not vaulted.</b> Every other credential in EntKube is encrypted in the
/// tenant's vault because something needs to read it back and present it. This one is only
/// ever <em>compared</em> — so storing anything reversible would be giving ourselves an
/// ability we have no use for, and a copy of a working credential to leak. It is shown once,
/// when it is issued, and after that nobody can recover it, including us.</para>
///
/// <para><b>And hashed plainly, on purpose.</b> A password gets a slow KDF because people
/// choose weak ones. This is 32 random bytes we generated; there is nothing to guess, and a
/// deliberately slow hash on the path every delivery takes would be a denial of service
/// somebody could aim at us for free. Salted SHA-256 with a constant-time comparison is the
/// right shape for a high-entropy machine credential.</para>
/// </summary>
public static class BridgeSecret
{
    /// <summary>How the stored form is recognised, so the scheme can be changed later.</summary>
    private const string Scheme = "s256";

    /// <summary>
    /// A new secret, for showing to whoever is configuring the sending system. URL-safe,
    /// because it will be pasted into a header field in somebody's ServiceNow.
    /// </summary>
    public static string Issue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>What goes in the database: the scheme, the salt and the hash.</summary>
    public static string Store(string secret)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return $"{Scheme}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(Hash(secret, salt))}";
    }

    /// <summary>
    /// Whether a presented secret is the stored one.
    ///
    /// <para>False for anything malformed or missing rather than throwing: this is reached
    /// by an unauthenticated request, and the difference between "wrong" and "unreadable"
    /// is not one to tell a caller about.</para>
    /// </summary>
    public static bool Matches(string? presented, string? stored)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        string[] parts = stored.Split(':');

        if (parts.Length != 3 || parts[0] != Scheme)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);

            // Constant time: a comparison that returns early leaks, one byte at a time, how
            // much of a guess was right.
            return CryptographicOperations.FixedTimeEquals(Hash(presented, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Hash(string secret, byte[] salt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        byte[] salted = new byte[salt.Length + bytes.Length];

        salt.CopyTo(salted, 0);
        bytes.CopyTo(salted, salt.Length);

        return SHA256.HashData(salted);
    }
}
