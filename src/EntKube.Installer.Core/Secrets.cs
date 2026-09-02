using System.Security.Cryptography;

namespace EntKube.Installer;

/// <summary>
/// Generates the values an operator should not have to think up.
///
/// Asking someone to invent a database password at an install prompt reliably produces a weak one,
/// and asking them to produce a 32-byte base64 key produces a support request. Both are generated
/// here from the system CSPRNG, and neither is ever regenerated for an install that already has one
/// — see <see cref="EnvFile"/> for why that matters.
/// </summary>
public static class Secrets
{
    /// <summary>
    /// The vault root key. Exactly 32 bytes, base64 — the app rejects any other length, so this is
    /// a fixed size rather than a configurable one.
    /// </summary>
    public static string VaultRootKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// A password for a service that only ever reads it from configuration, so it is optimised for
    /// entropy rather than for typing.
    ///
    /// The alphabet excludes '#', '$', quotes and backslashes on purpose: these land in a dotenv
    /// file, a compose interpolation and a Postgres connection string, and each of those treats at
    /// least one of them as syntax. The value is quoted on the way out anyway, but a password that
    /// cannot be pasted into psql by hand when something goes wrong is a bad password to have chosen
    /// for an operator.
    /// </summary>
    public static string Password(int length = 32)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_";
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
