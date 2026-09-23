using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>Where outbound mail goes and who it comes from.</summary>
/// <param name="Host">Null when nothing is configured, which is not an error — it means no mail.</param>
/// <param name="Port">587 unless told otherwise.</param>
/// <param name="From">The default sender, used when a caller has no better address.</param>
/// <param name="Username">Null for a relay that does not authenticate.</param>
/// <param name="Password">Null likewise.</param>
/// <param name="UseSsl">STARTTLS or implicit TLS as the port implies; never plaintext by choice.</param>
public readonly record struct SmtpSettings(
    string? Host,
    int Port,
    string From,
    string? Username,
    string? Password,
    bool UseSsl)
{
    /// <summary>Whether there is anywhere to send.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Reads the one SMTP configuration the installation has.
///
/// <para><b>Why this is its own class.</b> The same fifteen lines of precedence — the
/// provider row if it is enabled, otherwise the <c>Smtp:</c> configuration section — were
/// about to exist in two places, one for alert mail and one for support mail. Two copies
/// of a credential lookup is how one of them ends up reading a setting the other does
/// not, and the symptom is mail that works for alerts and silently does not for
/// customers.</para>
/// </summary>
public class SmtpSettingsResolver(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration)
{
    /// <summary>The address used when nothing names a better one.</summary>
    public const string DefaultFrom = "alerts@entkube.io";

    public async Task<SmtpSettings> ResolveAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        NotificationProviderConfig? provider = await db.NotificationProviderConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProviderType == NotificationProviderType.Smtp, ct);

        if (provider?.IsEnabled == true)
        {
            using JsonDocument doc = JsonDocument.Parse(provider.ConfigurationJson);
            JsonElement root = doc.RootElement;

            return new SmtpSettings(
                root.TryGetProperty("host", out JsonElement h) ? h.GetString() : null,
                root.TryGetProperty("port", out JsonElement p) ? p.GetInt32() : 587,
                root.TryGetProperty("from", out JsonElement f) ? f.GetString() ?? DefaultFrom : DefaultFrom,
                root.TryGetProperty("username", out JsonElement u) ? u.GetString() : null,
                root.TryGetProperty("password", out JsonElement pw) ? pw.GetString() : null,
                !root.TryGetProperty("enableSsl", out JsonElement ssl) || ssl.GetBoolean());
        }

        return new SmtpSettings(
            configuration["Smtp:Host"],
            configuration.GetValue("Smtp:Port", 587),
            configuration["Smtp:FromAddress"] ?? DefaultFrom,
            configuration["Smtp:Username"],
            configuration["Smtp:Password"],
            true);
    }
}
