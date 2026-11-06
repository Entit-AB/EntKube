using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>What became of asking for a ticket: the ticket's id, or why there is not one.</summary>
public sealed record FeedbackTicketResult(Guid? TicketId, string? Refusal);

/// <summary>
/// Turns one piece of feedback into a ticket, at a person's request.
///
/// <para><b>The reporter's address stays out of the ticket's fields.</b> <c>TicketNotifier</c> mails
/// <c>RequestedByEmail</c>, and this one was typed into a public form: passing it through would let
/// anyone point our ticket mail at an address of their choosing. It goes in the description, marked
/// as unverified, for a person to act on.</para>
///
/// <para><b>Only feedback with an application behind it.</b> A ticket belongs to a customer, and a
/// site with no app owner has none — the same rule <see cref="AlertTicketBridge"/> applies.</para>
/// </summary>
public class FeedbackTicketBridge(
    IDbContextFactory<SupportDbContext> dbFactory,
    FeedbackService feedback,
    TicketService tickets)
{
    public async Task<FeedbackTicketResult> OpenAsync(
        Guid tenantId, Guid feedbackId, string? actor, CancellationToken ct = default)
    {
        FeedbackItem? item = await feedback.GetAsync(tenantId, feedbackId, ct);

        if (item is null) return new FeedbackTicketResult(null, "That feedback no longer exists.");
        if (item.TicketId is { } existing) return new FeedbackTicketResult(existing, null);

        if (item.AppId is null)
        {
            return new FeedbackTicketResult(null,
                "This site has no app owner, so there is no customer to raise a ticket for.");
        }

        await using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Guid appId = item.AppId.Value;
        Guid? customerId = await db.Apps
            .Where(a => a.Id == appId && db.Customers.Any(c => c.Id == a.CustomerId && c.TenantId == tenantId))
            .Select(a => (Guid?)a.CustomerId)
            .FirstOrDefaultAsync(ct);

        if (customerId is null)
        {
            return new FeedbackTicketResult(null, "The application this feedback belonged to no longer exists.");
        }

        Ticket ticket = await tickets.CreateAsync(
            tenantId,
            customerId.Value,
            appId,
            Title(item),
            Describe(item),
            TicketChannel.Feedback,
            proposedPriority: null,
            reportedAt: DateTime.UtcNow,
            requestedBy: actor,
            ct: ct);

        await feedback.LinkTicketAsync(tenantId, feedbackId, ticket.Id, actor, ct);

        return new FeedbackTicketResult(ticket.Id, null);
    }

    public static string Title(FeedbackItem item)
    {
        string firstLine = item.Message.Split('\n', 2)[0].Trim();

        if (firstLine.Length == 0) return $"{item.Kind} feedback from {item.SiteName}";

        return firstLine.Length <= 80 ? $"{item.Kind}: {firstLine}" : $"{item.Kind}: {firstLine[..77]}...";
    }

    public static string Describe(FeedbackItem item)
    {
        StringBuilder text = new();

        if (item.Message.Length > 0) text.AppendLine(item.Message).AppendLine();

        text.AppendLine($"Reported through the feedback widget on {item.SiteName}, {item.CreatedAt:yyyy-MM-dd HH:mm} UTC.");
        text.AppendLine($"Page: {item.PagePath}");

        if (item.Rating is { } rating) text.AppendLine($"Rating: {rating} of 5");

        string client = string.Join(", ", new[] { item.Browser, item.Os, item.Device }.Where(s => !string.IsNullOrEmpty(s)));
        if (client.Length > 0) text.AppendLine($"Browser: {client}");

        if (item.ViewportWidth is { } w && item.ViewportHeight is { } h) text.AppendLine($"Viewport: {w}x{h}");
        if (item.RumSessionId is { } session) text.AppendLine($"RUM session: {session}");

        if (item.ContactEmail is { } email)
        {
            text.AppendLine($"Address given by the reporter (unverified): {email}");
        }

        if (item.Errors.Count > 0)
        {
            text.AppendLine().AppendLine("Recent errors on the page:");
            foreach (string error in item.Errors) text.AppendLine($"  {error}");
        }

        return text.ToString().TrimEnd();
    }
}
