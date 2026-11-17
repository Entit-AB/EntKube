using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// Fills <c>#{name}</c> placeholders in a text module. Pure: what goes in is the text and the
/// values, so what an agent is about to send can be checked without a database.
///
/// <para><b>A placeholder with no value becomes "-"</b>, as in Zammad, and so does one this
/// renderer does not know. Leaving <c>#{ticket.nubmer}</c> in place would put our template syntax
/// in front of the customer; the editor lists unknown placeholders instead
/// (<see cref="Unknown"/>), which is where a typo can be fixed.</para>
/// </summary>
public static partial class TextModuleRenderer
{
    /// <summary>What can be written, and what each one is, for the editor's help text.</summary>
    public static readonly IReadOnlyList<(string Name, string Meaning)> Known =
    [
        ("ticket.number", "the ticket's number, e.g. 1042"),
        ("ticket.reference", "the reference to keep in a mail subject, e.g. [EK-1042-…]"),
        ("ticket.title", "the ticket's title"),
        ("ticket.priority", "P1 to P4"),
        ("ticket.status", "the ticket's status"),
        ("ticket.reported_at", "when it was reported, Swedish time"),
        ("ticket.response_due", "when the first response is due, Swedish time"),
        ("customer.name", "the customer"),
        ("app.name", "the application, when the ticket has one"),
        ("requester.name", "who reported it"),
        ("requester.email", "their address"),
        ("user.name", "you — whoever inserts the module"),
    ];

    private static readonly HashSet<string> KnownNames = [.. Known.Select(k => k.Name)];

    /// <summary>The text with every placeholder replaced; unknown or empty ones by "-".</summary>
    public static string Render(string content, IReadOnlyDictionary<string, string?> values) =>
        Placeholder().Replace(content, m =>
            values.TryGetValue(m.Groups[1].Value.Trim(), out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "-");

    /// <summary>Placeholders in <paramref name="content"/> that will never have a value.</summary>
    public static IReadOnlyList<string> Unknown(string content) =>
        [.. Placeholder().Matches(content)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(name => !KnownNames.Contains(name))
            .Distinct()];

    /// <summary>The values for one ticket, as the agent inserting the module sees it.</summary>
    public static Dictionary<string, string?> ValuesFor(
        Ticket ticket, string? customerName, string? appName, string reference, string? userName, DateTime now)
    {
        DateTime? responseDue = TicketService.StatusOf(ticket, now).Response is { MetAt: null, Deadline: DateTime d } ? d : null;

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ticket.number"] = ticket.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ticket.reference"] = reference,
            ["ticket.title"] = ticket.Title,
            ["ticket.priority"] = ticket.Priority.ToString(),
            ["ticket.status"] = ticket.Status.ToString(),
            ["ticket.reported_at"] = BusinessCalendar.ToLocal(ticket.ReportedAt).ToString("yyyy-MM-dd HH:mm"),
            ["ticket.response_due"] = responseDue is DateTime due ? BusinessCalendar.ToLocal(due).ToString("yyyy-MM-dd HH:mm") : null,
            ["customer.name"] = customerName,
            ["app.name"] = appName,
            ["requester.name"] = ticket.RequestedBy,
            ["requester.email"] = ticket.RequestedByEmail,
            ["user.name"] = userName,
        };
    }

    [GeneratedRegex(@"#\{([^}]{1,80})\}")]
    private static partial Regex Placeholder();
}

/// <summary>Text modules: kept per tenant, offered per ticket.</summary>
public class TextModuleService(IDbContextFactory<SupportDbContext> dbFactory)
{
    /// <summary>Every module the tenant has, for the editor.</summary>
    public async Task<List<TextModule>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TextModules.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// What may be inserted on a ticket of <paramref name="customerId"/>: active modules for every
    /// customer, plus that customer's own, filtered by name or keyword when a query is given.
    /// </summary>
    public async Task<List<TextModule>> OfferedAsync(
        Guid tenantId, Guid customerId, string? query = null, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<TextModule> offered = await db.TextModules.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.IsActive && (m.CustomerId == null || m.CustomerId == customerId))
            .OrderBy(m => m.Name)
            .ToListAsync(ct);

        string q = (query ?? "").Trim();

        return q.Length == 0
            ? offered
            : [.. offered.Where(m =>
                m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || m.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Adds or changes a module. The customer, when given, must be this tenant's.</summary>
    public async Task<TextModule> SaveAsync(Guid tenantId, TextModule input, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("A text module needs a name.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (input.CustomerId is Guid customerId
            && !await db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That customer is not this tenant's.");
        }

        TextModule? row = input.Id == Guid.Empty
            ? null
            : await db.TextModules.FirstOrDefaultAsync(m => m.Id == input.Id && m.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new TextModule { Id = Guid.NewGuid(), TenantId = tenantId, Name = "" };
            db.TextModules.Add(row);
        }

        row.Name = input.Name.Trim();
        row.Keywords = (input.Keywords ?? "").Trim();
        row.Content = input.Content ?? "";
        row.CustomerId = input.CustomerId;
        row.IsActive = input.IsActive;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TextModules.Where(m => m.Id == id && m.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }
}
