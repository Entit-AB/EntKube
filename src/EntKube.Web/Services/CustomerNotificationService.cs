using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Customer-scoped management of notification channels owned via the portal. Customers
/// may manage Slack, Teams (incoming webhook), generic Webhook, and Email (recipient
/// only) channels — never the SMTP server or sender address, which stay system-managed.
/// Teams is always webhook mode here; the admin Microsoft Graph integration is not
/// exposed to customers. Every operation is scoped to (tenant, customer) so a customer
/// can only see and mutate their own channels.
/// </summary>
public class CustomerNotificationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    NotificationService notifications)
{
    /// <summary>The channel types a customer is allowed to create/manage.</summary>
    public static readonly IReadOnlyList<NotificationChannelType> AllowedTypes =
    [
        NotificationChannelType.Slack,
        NotificationChannelType.Teams,
        NotificationChannelType.Webhook,
        NotificationChannelType.Email
    ];

    public async Task<List<NotificationChannel>> GetChannelsAsync(
        Guid tenantId, Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.NotificationChannels
            .Where(c => c.TenantId == tenantId && c.CustomerId == customerId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a new customer channel, or updates an existing one when
    /// <paramref name="channelId"/> is supplied. Validates the type is allowed and the
    /// transport config is complete. Returns an error message on failure.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SaveChannelAsync(
        Guid tenantId, Guid customerId, Guid? channelId,
        string name, NotificationChannelType type,
        string? webhookUrl, string? email, string? bearerToken,
        AlertSeverityFilter severity, CancellationToken ct = default)
    {
        if (!AllowedTypes.Contains(type))
            return (false, "That channel type cannot be managed here.");
        if (string.IsNullOrWhiteSpace(name))
            return (false, "A name is required.");

        string configJson;
        try
        {
            configJson = BuildConfigJson(type, webhookUrl, email, bearerToken);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (channelId is Guid id)
        {
            NotificationChannel? existing = await db.NotificationChannels
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId && c.CustomerId == customerId, ct);
            if (existing is null)
                return (false, "Channel not found.");

            existing.Name = name.Trim();
            existing.Type = type;
            existing.ConfigurationJson = configJson;
            existing.SeverityFilter = severity;
        }
        else
        {
            db.NotificationChannels.Add(new NotificationChannel
            {
                TenantId = tenantId,
                CustomerId = customerId,
                Name = name.Trim(),
                Type = type,
                ConfigurationJson = configJson,
                SeverityFilter = severity
            });
        }

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task ToggleEnabledAsync(Guid tenantId, Guid customerId, Guid channelId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        NotificationChannel? ch = await db.NotificationChannels
            .FirstOrDefaultAsync(c => c.Id == channelId && c.TenantId == tenantId && c.CustomerId == customerId, ct);
        if (ch is null) return;
        ch.IsEnabled = !ch.IsEnabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteChannelAsync(Guid tenantId, Guid customerId, Guid channelId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        NotificationChannel? ch = await db.NotificationChannels
            .FirstOrDefaultAsync(c => c.Id == channelId && c.TenantId == tenantId && c.CustomerId == customerId, ct);
        if (ch is null) return;
        db.NotificationChannels.Remove(ch);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a test notification through a customer-owned channel. Throws on failure.</summary>
    public async Task TestChannelAsync(Guid tenantId, Guid customerId, Guid channelId, CancellationToken ct = default)
    {
        NotificationChannel? ch;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            ch = await db.NotificationChannels
                .FirstOrDefaultAsync(c => c.Id == channelId && c.TenantId == tenantId && c.CustomerId == customerId, ct);
        }
        if (ch is null)
            throw new InvalidOperationException("Channel not found.");
        await notifications.SendTestAsync(ch, ct);
    }

    private static string BuildConfigJson(NotificationChannelType type, string? webhookUrl, string? email, string? bearerToken)
        => type switch
        {
            NotificationChannelType.Slack =>
                !string.IsNullOrWhiteSpace(webhookUrl)
                    ? JsonSerializer.Serialize(new { webhookUrl = webhookUrl.Trim() })
                    : throw new InvalidOperationException("A Slack webhook URL is required."),

            // Customers always use Teams incoming webhooks (not the admin Graph integration).
            NotificationChannelType.Teams =>
                !string.IsNullOrWhiteSpace(webhookUrl)
                    ? JsonSerializer.Serialize(new { webhookUrl = webhookUrl.Trim() })
                    : throw new InvalidOperationException("A Teams webhook URL is required."),

            NotificationChannelType.Webhook =>
                !string.IsNullOrWhiteSpace(webhookUrl)
                    ? JsonSerializer.Serialize(new
                    {
                        url = webhookUrl.Trim(),
                        token = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken.Trim()
                    })
                    : throw new InvalidOperationException("A URL is required."),

            NotificationChannelType.Email =>
                !string.IsNullOrWhiteSpace(email)
                    ? JsonSerializer.Serialize(new { to = email.Trim() })
                    : throw new InvalidOperationException("A recipient email address is required."),

            _ => throw new InvalidOperationException("Unsupported channel type.")
        };
}
