using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

public class NotificationProviderConfigService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<NotificationProviderConfig?> GetAsync(NotificationProviderType type, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.NotificationProviderConfigs
            .FirstOrDefaultAsync(c => c.ProviderType == type, ct);
    }

    public async Task<List<NotificationProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.NotificationProviderConfigs.ToListAsync(ct);
    }

    public async Task SaveAsync(NotificationProviderType type, string configJson, bool isEnabled, string? userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        NotificationProviderConfig? existing = await db.NotificationProviderConfigs
            .FirstOrDefaultAsync(c => c.ProviderType == type, ct);

        if (existing is null)
        {
            db.NotificationProviderConfigs.Add(new NotificationProviderConfig
            {
                ProviderType = type,
                ConfigurationJson = configJson,
                IsEnabled = isEnabled,
                UpdatedAt = DateTime.UtcNow,
                UpdatedByUserId = userId
            });
        }
        else
        {
            existing.ConfigurationJson = configJson;
            existing.IsEnabled = isEnabled;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedByUserId = userId;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(NotificationProviderType type, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        NotificationProviderConfig? config = await db.NotificationProviderConfigs
            .FirstOrDefaultAsync(c => c.ProviderType == type, ct);
        if (config is not null)
        {
            db.NotificationProviderConfigs.Remove(config);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task TestSmtpAsync(string configJson, string testRecipient, CancellationToken ct = default)
    {
        using JsonDocument doc = JsonDocument.Parse(configJson);
        JsonElement root = doc.RootElement;

        string host = root.GetProperty("host").GetString() ?? throw new InvalidOperationException("SMTP host missing");
        int port = root.TryGetProperty("port", out JsonElement portEl) ? portEl.GetInt32() : 587;
        string from = root.TryGetProperty("from", out JsonElement fromEl) ? (fromEl.GetString() ?? "alerts@entkube.io") : "alerts@entkube.io";
        string? username = root.TryGetProperty("username", out JsonElement userEl) ? userEl.GetString() : null;
        string? password = root.TryGetProperty("password", out JsonElement passEl) ? passEl.GetString() : null;
        bool enableSsl = !root.TryGetProperty("enableSsl", out JsonElement sslEl) || sslEl.GetBoolean();

        MimeMessage mail = new();
        mail.From.Add(MailboxAddress.Parse(from));
        mail.To.Add(MailboxAddress.Parse(testRecipient));
        mail.Subject = "[EntKube] SMTP Test";
        mail.Body = new TextPart("plain") { Text = "This is a test email from EntKube notification settings." };

        using SmtpClient smtp = new();
        SecureSocketOptions tls = enableSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
        await smtp.ConnectAsync(host, port, tls, ct);
        if (!string.IsNullOrEmpty(username))
            await smtp.AuthenticateAsync(username, password ?? "", ct);
        await smtp.SendAsync(mail, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    public async Task TestMsTeamsGraphAsync(string configJson, CancellationToken ct = default)
    {
        using JsonDocument doc = JsonDocument.Parse(configJson);
        JsonElement root = doc.RootElement;

        string tenantId = root.GetProperty("tenantId").GetString() ?? throw new InvalidOperationException("tenantId missing");
        string clientId = root.GetProperty("clientId").GetString() ?? throw new InvalidOperationException("clientId missing");
        string clientSecret = root.GetProperty("clientSecret").GetString() ?? throw new InvalidOperationException("clientSecret missing");

        string token = await AcquireGraphTokenAsync(tenantId, clientId, clientSecret, ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Failed to acquire access token from Azure AD");
    }

    internal static async Task<string> AcquireGraphTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct)
    {
        using HttpClient http = new();
        string url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://graph.microsoft.com/.default"
        });

        HttpResponseMessage response = await http.PostAsync(url, body, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure AD token request failed: {response.StatusCode} — {responseBody}");

        using JsonDocument tokenDoc = JsonDocument.Parse(responseBody);
        return tokenDoc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("access_token missing from response");
    }
}
