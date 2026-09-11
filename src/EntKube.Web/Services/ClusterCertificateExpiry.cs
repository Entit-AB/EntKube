using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>How close a cluster's certificates are to the edge.</summary>
public enum CertificateUrgency
{
    /// <summary>More than 90 days left. Nothing to do.</summary>
    Fine,

    /// <summary>Inside 90 days. Worth planning a control-plane rollout.</summary>
    Soon,

    /// <summary>Inside 30 days. Schedule it now.</summary>
    Urgent,

    /// <summary>Already expired. The cluster's components can no longer talk to its API server.</summary>
    Expired
}

/// <summary>What the certificates say, and what it means.</summary>
public sealed record CertificateStatus(DateTime? NotAfter, int? DaysRemaining, CertificateUrgency Urgency, string Message);

/// <summary>
/// Watches the clock on a kubeadm cluster's certificates.
///
/// <para><b>Why this needs watching.</b> kubeadm issues client and serving certificates with a one
/// year life. A cluster that is never upgraded and never rolled reaches that date and stops:
/// kubelets cannot authenticate, controllers cannot reach the API server, and the failure arrives
/// all at once on a date nobody wrote down. CAPI renews them whenever the control plane rolls, so
/// a cluster that gets regular upgrades never notices — and a stable one, the kind nobody touches
/// precisely because it works, is exactly the one this happens to.</para>
///
/// <para>The remedy is a control-plane rollout, which is an operation that already exists. This
/// only has to say when.</para>
/// </summary>
public static class ClusterCertificateExpiry
{
    /// <summary>Inside this, it is worth planning. Comfortably more than a maintenance window away.</summary>
    public const int SoonDays = 90;

    /// <summary>Inside this, it should be scheduled rather than planned.</summary>
    public const int UrgentDays = 30;

    /// <summary>
    /// Reads the expiry from the admin kubeconfig's embedded client certificate. That certificate
    /// is issued by the same kubeadm CA and renewed by the same rollout as the rest, so it is a
    /// faithful proxy — and unlike the certificates on the nodes, it is something EntKube already
    /// holds without reaching into the cluster.
    /// </summary>
    public static CertificateStatus FromKubeconfig(string? kubeconfig, DateTime? now = null)
    {
        DateTime reference = now ?? DateTime.UtcNow;

        string? base64 = ExtractClientCertificate(kubeconfig);
        if (base64 is null)
        {
            return new CertificateStatus(null, null, CertificateUrgency.Fine,
                "This cluster's kubeconfig has no embedded client certificate, so its expiry cannot be "
                + "read here. A kubeconfig that authenticates with a token instead is renewed elsewhere.");
        }

        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
            DateTime notAfter = certificate.NotAfter.ToUniversalTime();
            int days = (int)Math.Floor((notAfter - reference).TotalDays);

            return new CertificateStatus(notAfter, days, UrgencyFor(days), MessageFor(days, notAfter));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return new CertificateStatus(null, null, CertificateUrgency.Fine,
                "The kubeconfig's client certificate could not be read, so its expiry is unknown.");
        }
    }

    public static CertificateUrgency UrgencyFor(int daysRemaining) => daysRemaining switch
    {
        < 0 => CertificateUrgency.Expired,
        <= UrgentDays => CertificateUrgency.Urgent,
        <= SoonDays => CertificateUrgency.Soon,
        _ => CertificateUrgency.Fine
    };

    private static string MessageFor(int days, DateTime notAfter) => days switch
    {
        < 0 =>
            $"The cluster's certificates expired on {notAfter:yyyy-MM-dd}. Components can no longer "
            + "authenticate to the API server; a control-plane rollout reissues them.",
        <= UrgentDays =>
            $"The cluster's certificates expire in {days} day(s), on {notAfter:yyyy-MM-dd}. Roll the "
            + "control plane before then — it reissues them, and there is no partial failure to warn you.",
        <= SoonDays =>
            $"The cluster's certificates expire in {days} days, on {notAfter:yyyy-MM-dd}. Any control-plane "
            + "rollout between now and then renews them, so an upgrade would cover it.",
        _ =>
            $"Certificates are valid until {notAfter:yyyy-MM-dd} ({days} days)."
    };

    /// <summary>
    /// Pulls client-certificate-data out of a kubeconfig. Parsed structurally rather than with a
    /// regular expression, because the field appears under whichever user the context names and a
    /// kubeconfig may carry several.
    /// </summary>
    private static string? ExtractClientCertificate(string? kubeconfig)
    {
        if (string.IsNullOrWhiteSpace(kubeconfig))
        {
            return null;
        }

        foreach (string line in kubeconfig.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("client-certificate-data:", StringComparison.Ordinal))
            {
                string value = trimmed["client-certificate-data:".Length..].Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }
}
