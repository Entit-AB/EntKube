using System.Security.Cryptography;
using System.Text;

namespace EntKube.Web.Services;

/// <summary>An SSH keypair: PKCS#8 PEM for us, authorized_keys line for the cloud.</summary>
public sealed record SshKeyPair(string PrivateKeyPem, string PublicKeyOpenSsh);

/// <summary>
/// Generates the throwaway SSH keys that let EntKube reach a machine it just booted — the
/// bootstrap cluster's node, and the temporary VM a machine image is baked on.
///
/// <para>Shared between those two callers because the OpenSSH public-key encoding is fiddly enough
/// to get subtly wrong twice: the wire format is length-prefixed fields with a sign-padding rule
/// for the big integers, and a key that is one byte off is rejected at boot with nothing useful in
/// the log.</para>
/// </summary>
public static class SshKeyFactory
{
    public static SshKeyPair Create(string comment)
    {
        using RSA rsa = RSA.Create(3072);
        return new SshKeyPair(rsa.ExportPkcs8PrivateKeyPem(), ToOpenSshPublicKey(rsa, comment));
    }

    /// <summary>Writes a private key where ssh will accept it — it refuses world-readable keys.</summary>
    public static async Task WritePrivateKeyFileAsync(string path, string pem, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(path, pem, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Encodes an RSA public key in OpenSSH authorized_keys ("ssh-rsa AAAA… comment") format.</summary>
    public static string ToOpenSshPublicKey(RSA rsa, string comment)
    {
        RSAParameters p = rsa.ExportParameters(false);
        using MemoryStream ms = new();

        void WriteBytes(byte[] b)
        {
            Span<byte> len = stackalloc byte[4];
            len[0] = (byte)(b.Length >> 24);
            len[1] = (byte)(b.Length >> 16);
            len[2] = (byte)(b.Length >> 8);
            len[3] = (byte)b.Length;
            ms.Write(len);
            ms.Write(b);
        }

        static byte[] ToMpint(byte[] b)
        {
            // SSH mpint: prepend a zero byte if the MSB is set, to keep it non-negative.
            if (b.Length > 0 && (b[0] & 0x80) != 0)
            {
                byte[] padded = new byte[b.Length + 1];
                Array.Copy(b, 0, padded, 1, b.Length);
                return padded;
            }
            return b;
        }

        WriteBytes(Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteBytes(ToMpint(p.Exponent!));
        WriteBytes(ToMpint(p.Modulus!));

        return $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} {comment}";
    }
}
