using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>What the agent's new-ticket form holds. Templates fill it; nothing else writes it.</summary>
public sealed class NewTicketDraft
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public TicketPriority? Priority { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? AppId { get; set; }
    public string Tags { get; set; } = "";
}

/// <summary>Ticket templates for the agent's new-ticket form.</summary>
public class TicketTemplateService(IDbContextFactory<SupportDbContext> dbFactory)
{
    /// <summary>
    /// Fills only what is still empty — Zammad's rule. A field the agent already set is never
    /// overwritten, however it was set, so applying a template twice, or after typing, is safe.
    /// An application from the template is taken only together with its own customer, or when
    /// the draft's customer is already that customer.
    /// </summary>
    public static void Apply(TicketTemplate template, NewTicketDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) && !string.IsNullOrWhiteSpace(template.Title))
        {
            draft.Title = template.Title;
        }

        if (string.IsNullOrWhiteSpace(draft.Description) && !string.IsNullOrWhiteSpace(template.Description))
        {
            draft.Description = template.Description;
        }

        draft.Priority ??= template.Priority;

        if (draft.CustomerId is null && template.CustomerId is not null)
        {
            draft.CustomerId = template.CustomerId;
        }

        if (draft.AppId is null && template.AppId is not null && draft.CustomerId == template.CustomerId)
        {
            draft.AppId = template.AppId;
        }

        if (string.IsNullOrWhiteSpace(draft.Tags))
        {
            draft.Tags = template.Tags;
        }
    }

    /// <summary>
    /// The tenant's customers, by name, for the new-ticket form. Read from the hub tables the
    /// Support context may read, so the form does not reach into Catalog's services.
    /// </summary>
    public async Task<List<(Guid Id, string Name)>> CustomersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Customers.Where(c => c.TenantId == tenantId).OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return [.. rows.Select(r => (r.Id, r.Name))];
    }

    /// <summary>One customer's applications, by name — empty for a customer that is not this tenant's.</summary>
    public async Task<List<(Guid Id, string Name)>> AppsAsync(Guid tenantId, Guid customerId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Apps.Where(a => a.CustomerId == customerId && a.Customer.TenantId == tenantId).OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        return [.. rows.Select(r => (r.Id, r.Name))];
    }

    public async Task<List<TicketTemplate>> ListAsync(Guid tenantId, bool activeOnly = false, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && (!activeOnly || t.IsActive))
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task<TicketTemplate> SaveAsync(Guid tenantId, TicketTemplate input, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("A template needs a name.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (input.CustomerId is Guid customerId
            && !await db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That customer is not this tenant's.");
        }

        if (input.AppId is Guid appId
            && (input.CustomerId is null || !await db.Apps.AnyAsync(a => a.Id == appId && a.CustomerId == input.CustomerId, ct)))
        {
            throw new InvalidOperationException("The application must belong to the template's customer.");
        }

        TicketTemplate? row = input.Id == Guid.Empty
            ? null
            : await db.TicketTemplates.FirstOrDefaultAsync(t => t.Id == input.Id && t.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new TicketTemplate { Id = Guid.NewGuid(), TenantId = tenantId, Name = "" };
            db.TicketTemplates.Add(row);
        }

        row.Name = input.Name.Trim();
        row.Title = Blank(input.Title);
        row.Description = Blank(input.Description);
        row.Priority = input.Priority;
        row.CustomerId = input.CustomerId;
        row.AppId = input.AppId;
        row.Tags = (input.Tags ?? "").Trim();
        row.IsActive = input.IsActive;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketTemplates.Where(t => t.Id == id && t.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>What running a macro did.</summary>
/// <param name="Applied">What changed, in words, for the confirmation.</param>
public readonly record struct MacroRun(IReadOnlyList<string> Applied, MacroAfterRun AfterRun);

/// <summary>Macros: defined per tenant, run by a person on one ticket or many.</summary>
public class MacroService(
    IDbContextFactory<SupportDbContext> dbFactory, TicketService tickets, TicketWorkService work)
{
    /// <summary>Splits "vpn, login  urgent" into its tags.</summary>
    public static IReadOnlyList<string> TagList(string? tags) =>
        [.. (tags ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Why a macro definition cannot be saved, if it cannot. The rules a macro's changes would hit
    /// at run time are checked when it is written, so a macro that can never run cannot be kept.
    /// </summary>
    public static string? WhyInvalid(Macro m)
    {
        if (string.IsNullOrWhiteSpace(m.Name))
        {
            return "A macro needs a name.";
        }

        if (m.Priority is not null && string.IsNullOrWhiteSpace(m.PriorityReason))
        {
            return "A macro that sets a priority must carry the written reason §14.3 requires.";
        }

        if (m.ReminderAfterHours is < 1 or > 24 * 365)
        {
            return "A reminder is between one hour and a year away.";
        }

        if (TagList(m.AddTags).Concat(TagList(m.RemoveTags)).Any(t => TicketWorkService.NormaliseTag(t) is null))
        {
            return "One of the tags cannot be a tag (too long, or carrying a comma).";
        }

        return null;
    }

    public async Task<List<Macro>> ListAsync(Guid tenantId, bool activeOnly = false, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Macros.AsNoTracking()
            .Where(m => m.TenantId == tenantId && (!activeOnly || m.IsActive))
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    public async Task<Macro> SaveAsync(Guid tenantId, Macro input, string actor, CancellationToken ct = default)
    {
        if (WhyInvalid(input) is string why)
        {
            throw new ArgumentException(why);
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Macro? row = input.Id == Guid.Empty
            ? null
            : await db.Macros.FirstOrDefaultAsync(m => m.Id == input.Id && m.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new Macro { Id = Guid.NewGuid(), TenantId = tenantId, Name = "" };
            db.Macros.Add(row);
        }

        row.Name = input.Name.Trim();
        row.IsActive = input.IsActive;
        row.Assign = input.Assign;
        row.AddTags = (input.AddTags ?? "").Trim();
        row.RemoveTags = (input.RemoveTags ?? "").Trim();
        row.ReminderAfterHours = input.ReminderAfterHours;
        row.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        row.NoteCustomerVisible = row.Note is not null && input.NoteCustomerVisible;
        row.Priority = input.Priority;
        row.PriorityReason = input.Priority is null ? null : input.PriorityReason?.Trim();
        row.Resolution = string.IsNullOrWhiteSpace(input.Resolution) ? null : input.Resolution.Trim();
        row.Close = input.Close;
        row.AfterRun = input.AfterRun;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Macros.Where(m => m.Id == id && m.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>
    /// Runs a macro on one ticket, as <paramref name="actor"/>.
    ///
    /// <para><b>Everything is checked before anything is changed</b> — Zammad's macros are
    /// all-or-nothing too. A closed ticket is refused whole; a macro that would close a ticket it
    /// does not resolve, on a ticket that is not resolved, is refused whole. What cannot be
    /// checked in advance is a write failing half-way, which these service calls do not do for
    /// any input that passed the checks.</para>
    ///
    /// <para>Every history entry it writes names the macro beside the person:
    /// <c>nils (macro "Close as duplicate")</c>.</para>
    /// </summary>
    /// <param name="assignee">Who "me" is: the runner's name, account and address.</param>
    /// <returns>What it did, or null when the ticket or the macro is not this tenant's.</returns>
    /// <exception cref="InvalidOperationException">The macro cannot run on this ticket, and why.</exception>
    public async Task<MacroRun?> RunAsync(
        Guid tenantId, Guid ticketId, Guid macroId, string actor,
        (string Name, string? UserId, string? Email) assignee, DateTime at, CancellationToken ct = default)
    {
        Macro? macro;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            macro = await db.Macros.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == macroId && m.TenantId == tenantId && m.IsActive, ct);
        }

        Ticket? ticket = macro is null ? null : await tickets.GetAsync(tenantId, ticketId, ct);

        if (macro is null || ticket is null)
        {
            return null;
        }

        // ── Check ──
        if (TicketWorkService.IsSettled(ticket.Status))
        {
            throw new InvalidOperationException($"#{ticket.Number} is {ticket.Status.ToString().ToLowerInvariant()}; macros run on open tickets.");
        }

        bool resolvedAfter = ticket.Status == TicketStatus.Resolved || macro.Resolution is not null;

        if (macro.Close && !resolvedAfter)
        {
            throw new InvalidOperationException(
                $"“{macro.Name}” closes the ticket, and #{ticket.Number} is not resolved. Nothing was changed.");
        }

        if (WhyInvalid(macro) is string why)
        {
            throw new InvalidOperationException($"“{macro.Name}” cannot run: {why}");
        }

        // ── Apply ──
        string by = $"{actor} (macro “{macro.Name}”)";
        List<string> applied = [];

        switch (macro.Assign)
        {
            case MacroAssign.Me:
                await tickets.AssignAsync(tenantId, ticketId, assignee.Name, by, at, assignee.UserId, assignee.Email, ct: ct);
                applied.Add($"assigned to {assignee.Name}");
                break;
            case MacroAssign.Nobody:
                await tickets.AssignAsync(tenantId, ticketId, null, by, at, ct: ct);
                applied.Add("put back in the queue");
                break;
        }

        foreach (string tag in TagList(macro.AddTags))
        {
            await work.AddTagAsync(tenantId, ticketId, tag, by, at, ct);
            applied.Add($"tagged {TicketWorkService.NormaliseTag(tag)}");
        }

        foreach (string tag in TagList(macro.RemoveTags))
        {
            await work.RemoveTagAsync(tenantId, ticketId, tag, by, at, ct);
            applied.Add($"untagged {TicketWorkService.NormaliseTag(tag)}");
        }

        if (macro.Priority is TicketPriority priority)
        {
            await tickets.ConfirmPriorityAsync(tenantId, ticketId, priority, macro.PriorityReason!, by, at, ct);
            applied.Add($"priority {priority}");
        }

        if (macro.Note is string note)
        {
            await tickets.AddEventAsync(tenantId, ticketId, TicketEventKind.Note, note, by, at, macro.NoteCustomerVisible, ct);
            applied.Add(macro.NoteCustomerVisible ? "note the customer sees" : "internal note");
        }

        if (macro.ReminderAfterHours is int hours && macro.Resolution is null && !macro.Close)
        {
            await work.SetReminderAsync(tenantId, ticketId, at.AddHours(hours), $"Set by macro “{macro.Name}”", by, at, ct);
            applied.Add($"reminder in {hours} h");
        }

        if (macro.Resolution is string resolution)
        {
            await tickets.ResolveAsync(tenantId, ticketId, resolution, by, at, ct);
            applied.Add("resolved");
        }

        if (macro.Close)
        {
            await tickets.CloseAsync(tenantId, ticketId, by, at, ct);
            applied.Add("closed");
        }

        return new MacroRun(applied, macro.AfterRun);
    }
}

/// <summary>An item as shown: whether it is done, and why.</summary>
/// <param name="ReferencedNumber">The ticket the item names, when it names one.</param>
/// <param name="Done">Ticked by hand, or — for a referenced ticket — that ticket is settled.</param>
public readonly record struct ChecklistLine(
    Guid Id, string Text, bool Done, int? ReferencedNumber, bool ReferenceFound, string? CheckedBy);

/// <summary>A ticket's checklist as shown.</summary>
public sealed record ChecklistView(string Title, IReadOnlyList<ChecklistLine> Lines)
{
    public int Open => Lines.Count(l => !l.Done);
}

/// <summary>Ticket checklists and the templates they start from.</summary>
public partial class TicketChecklistService(IDbContextFactory<SupportDbContext> dbFactory)
{
    /// <summary>The ticket an item's text names (<c>#1042</c>), if it names one.</summary>
    public static int? ReferencedNumber(string text) =>
        TicketRef().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) ? n : null;

    /// <summary>The ticket's checklist, or null when it has none.</summary>
    public async Task<ChecklistView?> GetAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketChecklist? list = await db.TicketChecklists.AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.TicketId == ticketId && c.Ticket.TenantId == tenantId, ct);

        if (list is null)
        {
            return null;
        }

        // A referenced ticket is looked up in THIS tenant only: an item naming another tenant's
        // number must not reveal whether that ticket is closed.
        List<int> numbers = [.. list.Items.Select(i => ReferencedNumber(i.Text)).OfType<int>().Distinct()];

        Dictionary<int, TicketStatus> statuses = numbers.Count == 0
            ? []
            : await db.Tickets.AsNoTracking()
                .Where(t => t.TenantId == tenantId && numbers.Contains(t.Number))
                .ToDictionaryAsync(t => t.Number, t => t.Status, ct);

        List<ChecklistLine> lines = [.. list.Items
            .OrderBy(i => i.SortOrder)
            .Select(i =>
            {
                int? number = ReferencedNumber(i.Text);

                if (number is int n)
                {
                    bool found = statuses.TryGetValue(n, out TicketStatus s);
                    return new ChecklistLine(i.Id, i.Text, found && TicketWorkService.IsSettled(s), n, found, null);
                }

                return new ChecklistLine(i.Id, i.Text, i.IsChecked, null, false, i.CheckedBy);
            })];

        return new ChecklistView(list.Title, lines);
    }

    /// <summary>
    /// Starts a ticket's checklist, empty or from a template. A ticket has at most one; asking for
    /// a second is refused rather than replacing the first.
    /// </summary>
    public async Task<bool> CreateAsync(
        Guid tenantId, Guid ticketId, Guid? templateId, string actor, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
        {
            return false;
        }

        if (await db.TicketChecklists.AnyAsync(c => c.TicketId == ticketId, ct))
        {
            throw new InvalidOperationException("This ticket already has a checklist.");
        }

        ChecklistTemplate? template = templateId is Guid id
            ? await db.ChecklistTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct)
              ?? throw new InvalidOperationException("There is no such checklist template.")
            : null;

        TicketChecklist list = new()
        {
            TicketId = ticketId,
            Title = template?.Name ?? "Checklist",
            CreatedBy = actor,
        };

        int order = 0;
        foreach (string line in (template?.Items ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            list.Items.Add(new TicketChecklistItem { Id = Guid.NewGuid(), TicketId = ticketId, Text = Clip(line), SortOrder = order++ });
        }

        db.TicketChecklists.Add(list);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RenameAsync(Guid tenantId, Guid ticketId, string title, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketChecklists
            .Where(c => c.TicketId == ticketId && c.Ticket.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Title, string.IsNullOrWhiteSpace(title) ? "Checklist" : Clip(title)), ct) > 0;
    }

    public async Task<bool> AddItemAsync(Guid tenantId, Guid ticketId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("An item needs some text.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.TicketChecklists.AnyAsync(c => c.TicketId == ticketId && c.Ticket.TenantId == tenantId, ct))
        {
            return false;
        }

        int next = (await db.TicketChecklistItems.Where(i => i.TicketId == ticketId).MaxAsync(i => (int?)i.SortOrder, ct) ?? -1) + 1;

        db.TicketChecklistItems.Add(new TicketChecklistItem { Id = Guid.NewGuid(), TicketId = ticketId, Text = Clip(text), SortOrder = next });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RenameItemAsync(Guid tenantId, Guid ticketId, Guid itemId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("An item needs some text.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await Items(db, tenantId, ticketId).Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Text, Clip(text)), ct) > 0;
    }

    /// <summary>
    /// Ticks or unticks an item. An item naming a ticket is refused: it is done when that ticket
    /// is, and a hand-ticked one would say so while the other ticket is still open.
    /// </summary>
    public async Task<bool> SetCheckedAsync(
        Guid tenantId, Guid ticketId, Guid itemId, bool done, string actor, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketChecklistItem? item = await Items(db, tenantId, ticketId).FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item is null)
        {
            return false;
        }

        if (ReferencedNumber(item.Text) is int n)
        {
            throw new InvalidOperationException($"This item is done when #{n} is closed; it is not ticked by hand.");
        }

        item.IsChecked = done;
        item.CheckedBy = done ? actor : null;
        item.CheckedAt = done ? DateTime.UtcNow : null;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveItemAsync(Guid tenantId, Guid ticketId, Guid itemId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await Items(db, tenantId, ticketId).Where(i => i.Id == itemId).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Moves an item one place up (<paramref name="by"/> = -1) or down (+1).</summary>
    public async Task<bool> MoveItemAsync(Guid tenantId, Guid ticketId, Guid itemId, int by, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<TicketChecklistItem> items = await Items(db, tenantId, ticketId).OrderBy(i => i.SortOrder).ToListAsync(ct);
        int at = items.FindIndex(i => i.Id == itemId);
        int to = at + Math.Sign(by);

        if (at < 0 || to < 0 || to >= items.Count)
        {
            return at >= 0;
        }

        (items[at], items[to]) = (items[to], items[at]);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketChecklists
            .Where(c => c.TicketId == ticketId && c.Ticket.TenantId == tenantId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    // ── Templates ──

    public async Task<List<ChecklistTemplate>> ListTemplatesAsync(Guid tenantId, bool activeOnly = false, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChecklistTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && (!activeOnly || t.IsActive))
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task<ChecklistTemplate> SaveTemplateAsync(Guid tenantId, ChecklistTemplate input, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("A checklist template needs a name.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ChecklistTemplate? row = input.Id == Guid.Empty
            ? null
            : await db.ChecklistTemplates.FirstOrDefaultAsync(t => t.Id == input.Id && t.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new ChecklistTemplate { Id = Guid.NewGuid(), TenantId = tenantId, Name = "" };
            db.ChecklistTemplates.Add(row);
        }

        row.Name = input.Name.Trim();
        row.Items = string.Join('\n', (input.Items ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        row.IsActive = input.IsActive;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteTemplateAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChecklistTemplates.Where(t => t.Id == id && t.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    private static IQueryable<TicketChecklistItem> Items(SupportDbContext db, Guid tenantId, Guid ticketId) =>
        db.TicketChecklistItems.Where(i => i.TicketId == ticketId && i.Checklist.Ticket.TenantId == tenantId);

    private static string Clip(string s)
    {
        string t = s.Trim();
        return t.Length > 500 ? t[..500] : t;
    }

    [GeneratedRegex(@"#(\d{1,9})\b")]
    private static partial Regex TicketRef();
}
