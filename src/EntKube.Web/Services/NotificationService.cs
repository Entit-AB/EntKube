using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

public class NotificationService(
    IHttpClientFactory httpClientFactory,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<NotificationService> logger)
{
    public async Task SendAsync(
        AlertIncident incident,
        NotificationChannel channel,
        bool isFiring,
        CancellationToken ct = default)
    {
        if (!channel.IsEnabled) return;
        if (!MeetsFilters(channel, incident.Severity, isFiring, incident.Status)) return;

        NotificationMessage message = FromIncident(incident, isFiring);

        bool success = false;
        string? error = null;

        try
        {
            success = await DispatchToChannelAsync(message, channel, isFiring, ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogWarning(ex, "Notification failed for channel {Channel} ({Type})", channel.Name, channel.Type);
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            IncidentId = incident.Id,
            ChannelId = channel.Id,
            IsFiring = isFiring,
            Success = success,
            Error = Truncate(error)
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sends an expiring-secret notification. For the tenant/ops scope
    /// (<paramref name="customerId"/> null) it targets tenant-level channels and honors
    /// alert routing rules (a rule matching by name/severity routes or suppresses the
    /// notice; cluster/namespace/label-scoped rules never match a secret event). For a
    /// customer scope it targets that customer's own channels and ignores tenant routing
    /// rules (customers don't manage them). Returns the number of channels notified plus
    /// an aggregate success/error for the caller to record. Writes no NotificationDelivery
    /// — the SecretExpiryNotification row is the record of this send.
    /// </summary>
    public async Task<(int Notified, bool Success, string? Error)> DispatchSecretExpiryAsync(
        Guid tenantId, ExpiringSecretInfo secret, int thresholdDays, Guid? customerId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<NotificationChannel> channels = await db.NotificationChannels
            .Where(c => c.TenantId == tenantId && c.CustomerId == customerId && c.IsEnabled)
            .ToListAsync(ct);
        if (channels.Count == 0)
            return (0, false, customerId is null
                ? "No enabled notification channels are configured for this tenant"
                : "No enabled notification channels are configured");

        NotificationMessage message = BuildSecretExpiryMessage(secret, thresholdDays);

        List<NotificationChannel> targets;
        if (customerId is null)
        {
            List<AlertRoutingRule> routingRules = await db.AlertRoutingRules
                .Include(r => r.Channel)
                .Where(r => r.TenantId == tenantId && r.IsEnabled)
                .OrderBy(r => r.Priority)
                .ToListAsync(ct);
            targets = ResolveSecretExpiryTargets(channels, routingRules, message.Title, message.Severity);
            if (targets.Count == 0)
                return (0, false, "Suppressed by an alert routing rule");
        }
        else
        {
            // Customer-owned channels: no tenant routing rules apply.
            targets = channels;
        }

        int notified = 0;
        List<string> errors = [];

        foreach (NotificationChannel channel in targets)
        {
            if (!MeetsFilters(channel, message.Severity, isFiring: true, IncidentStatus.Active))
                continue;

            try
            {
                if (await DispatchToChannelAsync(message, channel, isFiring: true, ct))
                    notified++;
                else
                    errors.Add($"{channel.Name}: delivery rejected");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Secret-expiry notification failed for channel {Channel} ({Type})", channel.Name, channel.Type);
                errors.Add($"{channel.Name}: {ex.Message}");
            }
        }

        bool success = notified > 0;
        string? error = errors.Count == 0 ? null : string.Join("; ", errors);
        return (notified, success, Truncate(error));
    }

    /// <summary>Dispatches a prepared message to a single channel's transport. May throw; returns transport success.</summary>
    private async Task<bool> DispatchToChannelAsync(
        NotificationMessage message, NotificationChannel channel, bool isFiring, CancellationToken ct)
        => channel.Type switch
        {
            NotificationChannelType.Slack   => await SendSlackAsync(message, channel, isFiring, ct),
            NotificationChannelType.Teams   => await SendTeamsAsync(message, channel, isFiring, ct),
            NotificationChannelType.Email   => await SendEmailAsync(message, channel, isFiring),
            NotificationChannelType.Webhook => await SendWebhookAsync(message, channel, isFiring, ct),
            _ => false
        };

    private static NotificationMessage FromIncident(AlertIncident incident, bool isFiring) => new()
    {
        Title = incident.AlertName,
        Severity = incident.Severity,
        ScopeFieldName = "Cluster",
        ScopeLabel = incident.Cluster?.Name ?? incident.ClusterId.ToString(),
        Timestamp = incident.StartsAt,
        EndsAt = incident.EndsAt,
        Summary = incident.Summary,
        Description = incident.Description,
        StatusLabel = incident.Status.ToString(),
        Status = incident.Status
    };

    private static NotificationMessage BuildSecretExpiryMessage(ExpiringSecretInfo secret, int thresholdDays)
    {
        int? days = secret.DaysUntilExpiry;
        bool critical = secret.IsExpired || (days.HasValue && days.Value <= 7);
        string when = secret.ExpiresAt.HasValue ? secret.ExpiresAt.Value.ToString("u") : "an unknown date";
        string title = secret.IsExpired ? $"Secret expired: {secret.Name}" : $"Secret expiring: {secret.Name}";
        string summary = secret.IsExpired
            ? $"{secret.TypeLabel} \"{secret.Name}\" ({secret.ScopeLabel}) expired on {when}."
            : $"{secret.TypeLabel} \"{secret.Name}\" ({secret.ScopeLabel}) expires on {when}"
              + (days.HasValue ? $" — in {days.Value} day(s)." : ".");

        return new NotificationMessage
        {
            Title = title,
            Severity = critical ? "critical" : "warning",
            ScopeFieldName = "Scope",
            ScopeLabel = secret.ScopeLabel,
            Timestamp = DateTime.UtcNow,
            Summary = summary,
            Description = secret.Detail ?? "",
            StatusLabel = "Active",
            Status = IncidentStatus.Active
        };
    }

    /// <summary>
    /// Resolves the channels a secret-expiry notice should reach, applying routing
    /// rules with the same precedence as alert dispatch: the first matching rule wins
    /// (suppress → none; channel → just that channel); no match → all enabled channels.
    /// </summary>
    private static List<NotificationChannel> ResolveSecretExpiryTargets(
        List<NotificationChannel> channels, List<AlertRoutingRule> rules, string title, string severity)
    {
        Dictionary<Guid, NotificationChannel> byId = channels.ToDictionary(c => c.Id);

        foreach (AlertRoutingRule rule in rules)
        {
            // Rules scoped to a cluster/namespace/label can never match a secret event.
            if (rule.MatchClusterId.HasValue) continue;
            if (!string.IsNullOrEmpty(rule.MatchNamespace)) continue;
            if (!string.IsNullOrEmpty(rule.MatchLabelKey)) continue;
            if (!string.IsNullOrEmpty(rule.MatchAlertName)
                && !title.Contains(rule.MatchAlertName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(rule.MatchSeverity)
                && !string.Equals(severity, rule.MatchSeverity, StringComparison.OrdinalIgnoreCase))
                continue;

            if (rule.SuppressIncident) return [];

            if (rule.ChannelId.HasValue)
            {
                NotificationChannel? ruleChannel = byId.GetValueOrDefault(rule.ChannelId.Value) ?? rule.Channel;
                if (ruleChannel is not null) return [ruleChannel];
            }
            // Matched but has no channel and isn't a suppress rule — fall through.
        }

        return channels;
    }

    private static string? Truncate(string? value) => value is { Length: > 1000 } ? value[..1000] : value;

    private static bool MeetsFilters(NotificationChannel channel, string severity, bool isFiring, IncidentStatus status)
        => channel.IsEnabled
           && MeetsSeverityFilter(severity, channel.SeverityFilter)
           && MeetsFiringFilter(isFiring, channel.FiringFilter)
           && MeetsAcknowledgeFilter(status, channel.AcknowledgeFilter);

    /// <summary>
    /// Re-dispatches an existing incident to channels, respecting routing rules.
    /// Returns the number of channels notified.
    /// </summary>
    public async Task<int> ReNotifyAsync(Guid incidentId, Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AlertIncident? incident = await db.AlertIncidents
            .Include(i => i.Cluster)
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);
        if (incident is null) return 0;

        List<NotificationChannel> channels = await db.NotificationChannels
            .Where(c => c.TenantId == tenantId && c.CustomerId == null && c.IsEnabled)
            .ToListAsync(ct);
        if (channels.Count == 0) return 0;

        List<AlertRoutingRule> routingRules = await db.AlertRoutingRules
            .Include(r => r.Channel)
            .Where(r => r.TenantId == tenantId && r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        string? ns = null;
        Dictionary<string, string>? labels = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(incident.LabelsJson);
            labels = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            labels.TryGetValue("namespace", out ns);
        }
        catch { }

        Dictionary<Guid, NotificationChannel> channelsById = channels.ToDictionary(c => c.Id);
        int sent = 0;

        foreach (AlertRoutingRule rule in routingRules.Where(r => r.IsEnabled))
        {
            if (rule.SuppressIncident) continue;
            if (rule.MatchClusterId.HasValue && rule.MatchClusterId != incident.ClusterId) continue;

            if (!string.IsNullOrEmpty(rule.MatchAlertName)
                && !incident.AlertName.Contains(rule.MatchAlertName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(rule.MatchSeverity)
                && !string.Equals(incident.Severity, rule.MatchSeverity, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(rule.MatchNamespace)
                && !string.Equals(ns, rule.MatchNamespace, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(rule.MatchLabelKey) && labels is not null)
            {
                if (!labels.TryGetValue(rule.MatchLabelKey, out string? labelVal)) continue;
                if (!string.IsNullOrEmpty(rule.MatchLabelValue)
                    && !string.Equals(labelVal, rule.MatchLabelValue, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (rule.ChannelId.HasValue)
            {
                NotificationChannel? ruleChannel = channelsById.GetValueOrDefault(rule.ChannelId.Value) ?? rule.Channel;
                await SendAsync(incident, ruleChannel, isFiring: true, ct);
            }
            return 1;
        }

        foreach (NotificationChannel channel in channels)
        {
            await SendAsync(incident, channel, isFiring: true, ct);
            sent++;
        }
        return sent;
    }

    public async Task SendTestAsync(NotificationChannel channel, CancellationToken ct = default)
    {
        NotificationMessage message = new()
        {
            Title = "TestAlert",
            Severity = "info",
            ScopeFieldName = "Cluster",
            ScopeLabel = "test-cluster",
            Timestamp = DateTime.UtcNow,
            Summary = "This is a test notification from EntKube.",
            StatusLabel = "Active",
            Status = IncidentStatus.Active
        };

        bool success = false;
        string? error = null;
        try
        {
            success = await DispatchToChannelAsync(message, channel, isFiring: true, ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!success)
            throw new InvalidOperationException(error ?? "Test notification failed");
    }

    private async Task<bool> SendSlackAsync(
        NotificationMessage message, NotificationChannel channel, bool isFiring, CancellationToken ct)
    {
        using JsonDocument config = JsonDocument.Parse(channel.ConfigurationJson);
        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Slack webhookUrl missing");

        string emoji = message.Severity switch
        {
            "critical" => ":red_circle:",
            "warning"  => ":warning:",
            _          => ":information_source:"
        };
        string state = isFiring ? "FIRING" : "RESOLVED";
        string text = $"{emoji} *[{state}] {message.Title}*\n" +
                      $"Severity: {message.Severity} | {message.ScopeFieldName}: {message.ScopeLabel}\n" +
                      $"Time: {message.Timestamp:u}\n" +
                      $"{(string.IsNullOrEmpty(message.Summary) ? "" : message.Summary)}";

        string body = JsonSerializer.Serialize(new { text });
        using HttpClient http = httpClientFactory.CreateClient("Notifications");
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(webhookUrl, content, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> SendTeamsAsync(
        NotificationMessage message, NotificationChannel channel, bool isFiring, CancellationToken ct)
    {
        using JsonDocument config = JsonDocument.Parse(channel.ConfigurationJson);

        if (config.RootElement.TryGetProperty("teamId", out JsonElement teamIdEl)
            && config.RootElement.TryGetProperty("channelId", out JsonElement channelIdEl))
        {
            string teamId = teamIdEl.GetString() ?? throw new InvalidOperationException("teamId is empty");
            string channelId = channelIdEl.GetString() ?? throw new InvalidOperationException("channelId is empty");
            return await SendTeamsViaGraphAsync(message, teamId, channelId, isFiring, ct);
        }

        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Teams webhookUrl missing");
        return await SendTeamsViaWebhookAsync(message, webhookUrl, isFiring, ct);
    }

    private async Task<bool> SendTeamsViaGraphAsync(
        NotificationMessage message, string teamId, string channelId, bool isFiring, CancellationToken ct)
    {
        NotificationProviderConfig? providerConfig;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            providerConfig = await db.NotificationProviderConfigs
                .FirstOrDefaultAsync(c => c.ProviderType == NotificationProviderType.MsTeamsGraph, ct);
        }

        if (providerConfig is null || !providerConfig.IsEnabled)
            throw new InvalidOperationException("MS Teams Graph provider is not configured or disabled");

        using JsonDocument graphConfig = JsonDocument.Parse(providerConfig.ConfigurationJson);
        string tenantId = graphConfig.RootElement.GetProperty("tenantId").GetString()
            ?? throw new InvalidOperationException("tenantId missing in Teams Graph config");
        string clientId = graphConfig.RootElement.GetProperty("clientId").GetString()
            ?? throw new InvalidOperationException("clientId missing in Teams Graph config");
        string clientSecret = graphConfig.RootElement.GetProperty("clientSecret").GetString()
            ?? throw new InvalidOperationException("clientSecret missing in Teams Graph config");

        string token = await NotificationProviderConfigService.AcquireGraphTokenAsync(tenantId, clientId, clientSecret, ct);

        string state = isFiring ? "FIRING" : "RESOLVED";
        string color = message.Severity switch
        {
            "critical" => "#FF0000",
            "warning"  => "#FFA500",
            _          => "#0078D4"
        };

        object messageBody = new
        {
            body = new
            {
                contentType = "html",
                content = $"<h3 style=\"color:{color}\">[{state}] {message.Title}</h3>" +
                          $"<p><b>Severity:</b> {message.Severity} | <b>{message.ScopeFieldName}:</b> {message.ScopeLabel}</p>" +
                          $"<p><b>Time:</b> {message.Timestamp:u}</p>" +
                          (string.IsNullOrEmpty(message.Summary) ? "" : $"<p>{message.Summary}</p>")
            }
        };

        using HttpClient http = httpClientFactory.CreateClient("Notifications");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        string url = $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels/{channelId}/messages";
        using StringContent content = new(JsonSerializer.Serialize(messageBody), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, content, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> SendTeamsViaWebhookAsync(
        NotificationMessage message, string webhookUrl, bool isFiring, CancellationToken ct)
    {
        string color = message.Severity switch
        {
            "critical" => "attention",
            "warning"  => "warning",
            _          => "accent"
        };
        string state = isFiring ? "FIRING" : "RESOLVED";

        object card = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", text = $"[{state}] {message.Title}", weight = "Bolder", size = "Medium", color },
                            new { type = "FactSet", facts = new[]
                            {
                                new { title = "Severity", value = message.Severity },
                                new { title = message.ScopeFieldName, value = message.ScopeLabel },
                                new { title = "Time", value = message.Timestamp.ToString("u") },
                                new { title = "Summary", value = string.IsNullOrEmpty(message.Summary) ? "-" : message.Summary }
                            }}
                        }
                    }
                }
            }
        };

        string body = JsonSerializer.Serialize(card);
        using HttpClient http = httpClientFactory.CreateClient("Notifications");
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(webhookUrl, content, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> SendEmailAsync(NotificationMessage message, NotificationChannel channel, bool isFiring)
    {
        using JsonDocument config = JsonDocument.Parse(channel.ConfigurationJson);
        string to = config.RootElement.GetProperty("to").GetString()
            ?? throw new InvalidOperationException("Email 'to' missing");

        string? smtpHost;
        int smtpPort;
        string from;
        string? smtpUser;
        string? smtpPass;
        bool enableSsl;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            NotificationProviderConfig? providerConfig = await db.NotificationProviderConfigs
                .FirstOrDefaultAsync(c => c.ProviderType == NotificationProviderType.Smtp);

            if (providerConfig?.IsEnabled == true)
            {
                using JsonDocument smtpDoc = JsonDocument.Parse(providerConfig.ConfigurationJson);
                JsonElement smtpRoot = smtpDoc.RootElement;
                smtpHost = smtpRoot.TryGetProperty("host", out JsonElement h) ? h.GetString() : null;
                smtpPort = smtpRoot.TryGetProperty("port", out JsonElement p) ? p.GetInt32() : 587;
                from = smtpRoot.TryGetProperty("from", out JsonElement f) ? (f.GetString() ?? "alerts@entkube.io") : "alerts@entkube.io";
                smtpUser = smtpRoot.TryGetProperty("username", out JsonElement u) ? u.GetString() : null;
                smtpPass = smtpRoot.TryGetProperty("password", out JsonElement pw) ? pw.GetString() : null;
                enableSsl = !smtpRoot.TryGetProperty("enableSsl", out JsonElement ssl) || ssl.GetBoolean();
            }
            else
            {
                smtpHost = configuration["Smtp:Host"];
                smtpPort = configuration.GetValue<int>("Smtp:Port", 587);
                from = configuration["Smtp:FromAddress"] ?? "alerts@entkube.io";
                smtpUser = configuration["Smtp:Username"];
                smtpPass = configuration["Smtp:Password"];
                enableSsl = true;
            }
        }

        if (string.IsNullOrEmpty(smtpHost))
        {
            logger.LogWarning("SMTP not configured — skipping email notification for channel {Channel}", channel.Name);
            return false;
        }

        string state = isFiring ? "FIRING" : "RESOLVED";
        string subject = $"[EntKube] [{state}] {message.Title} ({message.Severity})";
        string bodyText = $"Alert: {message.Title}\n" +
                          $"Status: {state}\n" +
                          $"Severity: {message.Severity}\n" +
                          $"{message.ScopeFieldName}: {message.ScopeLabel}\n" +
                          $"Time: {message.Timestamp:u}\n" +
                          $"Summary: {message.Summary}\n" +
                          $"Description: {message.Description}";

        MimeMessage mail = new();
        mail.From.Add(MailboxAddress.Parse(from));
        mail.To.Add(MailboxAddress.Parse(to));
        mail.Subject = subject;
        mail.Body = new TextPart("plain") { Text = bodyText };

        using SmtpClient smtp = new();
        SecureSocketOptions tls = enableSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
        await smtp.ConnectAsync(smtpHost, smtpPort, tls);
        if (!string.IsNullOrEmpty(smtpUser))
            await smtp.AuthenticateAsync(smtpUser, smtpPass ?? "");
        await smtp.SendAsync(mail);
        await smtp.DisconnectAsync(true);
        return true;
    }

    private async Task<bool> SendWebhookAsync(
        NotificationMessage message, NotificationChannel channel, bool isFiring, CancellationToken ct)
    {
        using JsonDocument config = JsonDocument.Parse(channel.ConfigurationJson);
        string url = config.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Webhook url missing");

        string? token = config.RootElement.TryGetProperty("token", out JsonElement t)
            ? t.GetString()
            : null;

        object payload = new
        {
            firing = isFiring,
            alertName = message.Title,
            severity = message.Severity,
            summary = message.Summary,
            description = message.Description,
            clusterName = message.ScopeLabel,
            startsAt = message.Timestamp,
            endsAt = message.EndsAt,
            status = message.StatusLabel
        };

        using HttpClient http = httpClientFactory.CreateClient("Notifications");
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, content, ct);
        return response.IsSuccessStatusCode;
    }

    private static bool MeetsSeverityFilter(string severity, AlertSeverityFilter filter) => filter switch
    {
        AlertSeverityFilter.CriticalOnly    => severity == "critical",
        AlertSeverityFilter.WarningAndAbove => severity is "critical" or "warning",
        _ => true
    };

    private static bool MeetsFiringFilter(bool isFiring, AlertFiringFilter filter) => filter switch
    {
        AlertFiringFilter.FiringOnly   => isFiring,
        AlertFiringFilter.ResolvedOnly => !isFiring,
        _ => true
    };

    private static bool MeetsAcknowledgeFilter(IncidentStatus status, AlertAcknowledgeFilter filter) => filter switch
    {
        AlertAcknowledgeFilter.UnacknowledgedOnly => status != IncidentStatus.Acknowledged,
        AlertAcknowledgeFilter.AcknowledgedOnly   => status == IncidentStatus.Acknowledged,
        _ => true
    };
}

/// <summary>
/// A transport-agnostic notification payload. Decouples the per-channel senders
/// (Slack/Teams/Email/Webhook) from their source so the same transports serve both
/// alert incidents and expiring-secret notices. <see cref="ScopeFieldName"/> labels
/// the context field (e.g. "Cluster" for alerts, "Scope" for secrets).
/// </summary>
public sealed record NotificationMessage
{
    public required string Title { get; init; }
    public required string Severity { get; init; }
    public string ScopeFieldName { get; init; } = "Cluster";
    public string ScopeLabel { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public DateTime? EndsAt { get; init; }
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string StatusLabel { get; init; } = "Active";
    public IncidentStatus Status { get; init; } = IncidentStatus.Active;
}
