using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
        if (!MeetsSeverityFilter(incident.Severity, channel.SeverityFilter)) return;

        bool success = false;
        string? error = null;

        try
        {
            success = channel.Type switch
            {
                NotificationChannelType.Slack   => await SendSlackAsync(incident, channel, isFiring, ct),
                NotificationChannelType.Teams   => await SendTeamsAsync(incident, channel, isFiring, ct),
                NotificationChannelType.Email   => await SendEmailAsync(incident, channel, isFiring),
                NotificationChannelType.Webhook => await SendWebhookAsync(incident, channel, isFiring, ct),
                _ => false
            };
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
            Error = error?.Length > 1000 ? error[..1000] : error
        });
        await db.SaveChangesAsync(ct);
    }

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
            .Where(c => c.TenantId == tenantId && c.IsEnabled)
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
        AlertIncident fake = new()
        {
            Id = Guid.Empty,
            ClusterId = Guid.Empty,
            Fingerprint = "test",
            AlertName = "TestAlert",
            Severity = "info",
            Summary = "This is a test notification from EntKube.",
            StartsAt = DateTime.UtcNow,
            Status = IncidentStatus.Active,
            Cluster = new KubernetesCluster { Name = "test-cluster", ApiServerUrl = "" }
        };

        bool success = false;
        string? error = null;
        try
        {
            success = channel.Type switch
            {
                NotificationChannelType.Slack   => await SendSlackAsync(fake, channel, true, ct),
                NotificationChannelType.Teams   => await SendTeamsAsync(fake, channel, true, ct),
                NotificationChannelType.Email   => await SendEmailAsync(fake, channel, true),
                NotificationChannelType.Webhook => await SendWebhookAsync(fake, channel, true, ct),
                _ => false
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!success)
            throw new InvalidOperationException(error ?? "Test notification failed");
    }

    private async Task<bool> SendSlackAsync(
        AlertIncident incident, NotificationChannel channel, bool isFiring, CancellationToken ct)
    {
        using JsonDocument config = JsonDocument.Parse(channel.ConfigurationJson);
        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Slack webhookUrl missing");

        string emoji = incident.Severity switch
        {
            "critical" => ":red_circle:",
            "warning"  => ":warning:",
            _          => ":information_source:"
        };
        string state = isFiring ? "FIRING" : "RESOLVED";
        string text = $"{emoji} *[{state}] {incident.AlertName}*\n" +
                      $"Severity: {incident.Severity} | Cluster: {incident.Cluster?.Name ?? incident.ClusterId.ToString()}\n" +
                      $"Started: {incident.StartsAt:u}\n" +
                      $"{(string.IsNullOrEmpty(incident.Summary) ? "" : incident.Summary)}";

        string body = JsonSerializer.Serialize(new { text });
        using HttpClient http = httpClientFactory.CreateClient("Notifications");
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(webhookUrl, content, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> SendTeamsAsync(
        AlertIncident incident, NotificationChannel channel, bool isFiring, CancellationToken ct)
    {
        using JsonDocument config = JsonDocument.Parse(channel.ConfigurationJson);

        if (config.RootElement.TryGetProperty("teamId", out JsonElement teamIdEl)
            && config.RootElement.TryGetProperty("channelId", out JsonElement channelIdEl))
        {
            string teamId = teamIdEl.GetString() ?? throw new InvalidOperationException("teamId is empty");
            string channelId = channelIdEl.GetString() ?? throw new InvalidOperationException("channelId is empty");
            return await SendTeamsViaGraphAsync(incident, teamId, channelId, isFiring, ct);
        }

        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Teams webhookUrl missing");
        return await SendTeamsViaWebhookAsync(incident, webhookUrl, isFiring, ct);
    }

    private async Task<bool> SendTeamsViaGraphAsync(
        AlertIncident incident, string teamId, string channelId, bool isFiring, CancellationToken ct)
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
        string color = incident.Severity switch
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
                content = $"<h3 style=\"color:{color}\">[{state}] {incident.AlertName}</h3>" +
                          $"<p><b>Severity:</b> {incident.Severity} | <b>Cluster:</b> {incident.Cluster?.Name ?? incident.ClusterId.ToString()}</p>" +
                          $"<p><b>Started:</b> {incident.StartsAt:u}</p>" +
                          (string.IsNullOrEmpty(incident.Summary) ? "" : $"<p>{incident.Summary}</p>")
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
        AlertIncident incident, string webhookUrl, bool isFiring, CancellationToken ct)
    {
        string color = incident.Severity switch
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
                            new { type = "TextBlock", text = $"[{state}] {incident.AlertName}", weight = "Bolder", size = "Medium", color },
                            new { type = "FactSet", facts = new[]
                            {
                                new { title = "Severity", value = incident.Severity },
                                new { title = "Cluster", value = incident.Cluster?.Name ?? incident.ClusterId.ToString() },
                                new { title = "Started", value = incident.StartsAt.ToString("u") },
                                new { title = "Summary", value = string.IsNullOrEmpty(incident.Summary) ? "-" : incident.Summary }
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

    private async Task<bool> SendEmailAsync(AlertIncident incident, NotificationChannel channel, bool isFiring)
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
        string subject = $"[EntKube] [{state}] {incident.AlertName} ({incident.Severity})";
        string body = $"Alert: {incident.AlertName}\n" +
                      $"Status: {state}\n" +
                      $"Severity: {incident.Severity}\n" +
                      $"Cluster: {incident.Cluster?.Name ?? incident.ClusterId.ToString()}\n" +
                      $"Started: {incident.StartsAt:u}\n" +
                      $"Summary: {incident.Summary}\n" +
                      $"Description: {incident.Description}";

        using SmtpClient smtp = new(smtpHost, smtpPort);
        smtp.EnableSsl = enableSsl;
        smtp.Timeout = 15000;
        if (!string.IsNullOrEmpty(smtpUser))
            smtp.Credentials = new NetworkCredential(smtpUser, smtpPass);

        using MailMessage mail = new(from, to, subject, body);
        await smtp.SendMailAsync(mail);
        return true;
    }

    private async Task<bool> SendWebhookAsync(
        AlertIncident incident, NotificationChannel channel, bool isFiring, CancellationToken ct)
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
            alertName = incident.AlertName,
            severity = incident.Severity,
            summary = incident.Summary,
            description = incident.Description,
            clusterName = incident.Cluster?.Name ?? incident.ClusterId.ToString(),
            startsAt = incident.StartsAt,
            endsAt = incident.EndsAt,
            status = incident.Status.ToString()
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
        AlertSeverityFilter.CriticalOnly   => severity == "critical",
        AlertSeverityFilter.WarningAndAbove => severity is "critical" or "warning",
        _ => true
    };
}
