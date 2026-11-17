using System.Net;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// A GitHub or GitLab issue address, taken apart. Only issue addresses parse: a pull request,
/// a merge request, a repository page or anything not https is refused, so what is linked is
/// always something the API can read as an issue.
/// </summary>
public sealed record IssueReference(IssueTrackerKind Kind, string Host, string Project, int Number)
{
    /// <summary>What a person would quote: <c>acme/portal#12</c>.</summary>
    public string Reference => $"{Project}#{Number}";

    /// <summary>The canonical web address, whatever was pasted (a comment anchor, a trailing slash).</summary>
    public string Url => Kind == IssueTrackerKind.GitHub
        ? $"https://{Host}/{Project}/issues/{Number}"
        : $"https://{Host}/{Project}/-/issues/{Number}";

    /// <summary>The API address of the issue. GitHub.com's API has its own host; Enterprise serves it under /api/v3.</summary>
    public string ApiUrl => Kind == IssueTrackerKind.GitHub
        ? (Host == "github.com" ? "https://api.github.com" : $"https://{Host}/api/v3") + $"/repos/{Project}/issues/{Number}"
        : $"https://{Host}/api/v4/projects/{Uri.EscapeDataString(Project)}/issues/{Number}";

    public static IssueReference? Parse(string? text)
    {
        if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
        {
            return null;
        }

        string host = uri.IdnHost.ToLowerInvariant();
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // GitLab: group[/subgroup…]/project/-/issues/12 (or /-/work_items/12 on newer GitLab).
        int dash = Array.IndexOf(parts, "-");
        if (dash >= 2 && parts.Length == dash + 3 && parts[dash + 1] is "issues" or "work_items"
            && int.TryParse(parts[dash + 2], out int glNumber) && glNumber > 0)
        {
            return new IssueReference(IssueTrackerKind.GitLab, host, string.Join('/', parts[..dash]), glNumber);
        }

        // GitHub: owner/repo/issues/12.
        if (dash < 0 && parts.Length == 4 && parts[2] == "issues" && int.TryParse(parts[3], out int ghNumber) && ghNumber > 0)
        {
            return new IssueReference(IssueTrackerKind.GitHub, host, $"{parts[0]}/{parts[1]}", ghNumber);
        }

        return null;
    }

    /// <summary>
    /// A host as an admin might type it — <c>https://GitLab.Example.se/</c> — as the bare lower-case
    /// name a tracker is stored under, or null when it is not a DNS name. IP addresses are refused:
    /// a tracker is somebody's named service, and an address is how a request gets aimed inward.
    /// </summary>
    public static string? NormaliseHost(string? text)
    {
        string t = (text ?? "").Trim();

        if (!t.Contains("://", StringComparison.Ordinal))
        {
            t = "https://" + t;
        }

        if (!Uri.TryCreate(t, UriKind.Absolute, out Uri? uri) || uri.HostNameType != UriHostNameType.Dns
            || !uri.IsDefaultPort || uri.AbsolutePath is not ("/" or "") || !uri.IdnHost.Contains('.'))
        {
            return null;
        }

        return uri.IdnHost.ToLowerInvariant();
    }
}

/// <summary>What is shown about a linked issue, read live. <see cref="Problem"/> says why the rest is missing.</summary>
public sealed record IssueView(
    Guid LinkId, IssueTrackerKind Kind, string Url, string Reference,
    string? Title, bool? IsOpen, IReadOnlyList<string> Labels, IReadOnlyList<string> Assignees,
    string? Milestone, string? Problem);

/// <summary>Where an issue tracker's token is kept. The vault in production; a dictionary in tests.</summary>
public interface IIssueTrackerTokens
{
    Task<string?> GetAsync(Guid tenantId, Guid trackerId, CancellationToken ct);

    Task<bool> HasAsync(Guid tenantId, Guid trackerId, CancellationToken ct);

    Task SetAsync(Guid tenantId, Guid trackerId, string? token, CancellationToken ct);
}

/// <summary>The tokens in the tenant's vault, owned by the tracker row.</summary>
public sealed class VaultIssueTrackerTokens(VaultService vault) : IIssueTrackerTokens
{
    public Task<string?> GetAsync(Guid tenantId, Guid trackerId, CancellationToken ct) =>
        vault.GetIssueTrackerTokenAsync(tenantId, trackerId, ct);

    public Task<bool> HasAsync(Guid tenantId, Guid trackerId, CancellationToken ct) =>
        vault.HasIssueTrackerTokenAsync(tenantId, trackerId, ct);

    public Task SetAsync(Guid tenantId, Guid trackerId, string? token, CancellationToken ct) =>
        string.IsNullOrEmpty(token)
            ? vault.DeleteIssueTrackerTokenAsync(tenantId, trackerId, ct)
            : vault.SetIssueTrackerTokenAsync(tenantId, trackerId, token, ct);
}

/// <summary>
/// GitHub and GitLab issues linked to tickets (docs/helpdesk.md, phase 6) — Zammad's GitHub and
/// GitLab integrations, read-only.
///
/// <para><b>Only configured hosts are ever called.</b> Linking checks the address against this
/// tenant's trackers before anything is stored, and reading checks again, so a link whose tracker
/// was since removed is shown as a link and nothing more.</para>
///
/// <para><b>Read live, briefly remembered.</b> An issue is read when the ticket is opened, and the
/// answer kept for a minute so a ticket re-rendering does not re-ask GitHub. The memory is keyed by
/// tenant as well as address: two tenants linking the same public issue use their own tokens, and
/// one tenant's answer is never served to the other.</para>
/// </summary>
public class IssueLinkService(
    IDbContextFactory<SupportDbContext> dbFactory,
    IIssueTrackerTokens tokens,
    IHttpClientFactory httpFactory,
    IMemoryCache cache)
{
    public const string HttpClientName = "IssueTrackers";

    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(1);

    // ───────────────────────────── Trackers ─────────────────────────────

    public async Task<List<IssueTracker>> TrackersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IssueTrackers.AsNoTracking().Where(t => t.TenantId == tenantId).OrderBy(t => t.Host).ToListAsync(ct);
    }

    public Task<bool> HasTokenAsync(Guid tenantId, Guid trackerId, CancellationToken ct = default) =>
        tokens.HasAsync(tenantId, trackerId, ct);

    /// <summary>
    /// Adds or changes a tracker. <paramref name="token"/> null keeps the stored token, empty
    /// removes it (a public tracker), anything else replaces it.
    /// </summary>
    public async Task<IssueTracker> SaveTrackerAsync(
        Guid tenantId, Guid? id, IssueTrackerKind kind, string host, bool enabled, string? token, string actor,
        CancellationToken ct = default)
    {
        string normal = IssueReference.NormaliseHost(host)
            ?? throw new ArgumentException("The host is a name such as github.com or gitlab.example.se — no path, port or IP address.");

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.IssueTrackers.AnyAsync(t => t.TenantId == tenantId && t.Host == normal && t.Id != id, ct))
        {
            throw new InvalidOperationException($"{normal} is already set up.");
        }

        IssueTracker? row = id is Guid existing
            ? await db.IssueTrackers.FirstOrDefaultAsync(t => t.Id == existing && t.TenantId == tenantId, ct)
              ?? throw new InvalidOperationException("There is no such tracker here.")
            : null;

        if (row is null)
        {
            row = new IssueTracker { Id = Guid.NewGuid(), TenantId = tenantId, Host = normal };
            db.IssueTrackers.Add(row);
        }

        row.Kind = kind;
        row.Host = normal;
        row.IsEnabled = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;
        await db.SaveChangesAsync(ct);

        if (token is not null)
        {
            await tokens.SetAsync(tenantId, row.Id, token.Trim(), ct);
        }

        return row;
    }

    /// <summary>Removes a tracker and its token. Links on its host stay, shown as plain links.</summary>
    public async Task<bool> DeleteTrackerAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IssueTrackers.Where(t => t.Id == id && t.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    // ───────────────────────────── Links ─────────────────────────────

    public async Task<List<TicketIssueLink>> LinksAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketIssueLinks.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.TicketId == ticketId)
            .OrderBy(l => l.AddedAt).ToListAsync(ct);
    }

    /// <summary>
    /// Links an issue to a ticket. The address must be an issue on a host this tenant has set up
    /// for that kind of tracker; linking the same issue twice returns the existing link.
    /// </summary>
    public async Task<TicketIssueLink> LinkAsync(Guid tenantId, Guid ticketId, string url, string actor, CancellationToken ct = default)
    {
        IssueReference issue = IssueReference.Parse(url)
            ?? throw new ArgumentException("That is not a GitHub or GitLab issue address (https://…/issues/12 or …/-/issues/12).");

        if (issue.Url.Length > 500)
        {
            throw new ArgumentException("That issue address is too long to keep.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("There is no such ticket here.");
        }

        IssueTracker? tracker = await db.IssueTrackers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Host == issue.Host, ct);

        if (tracker is null || !tracker.IsEnabled || tracker.Kind != issue.Kind)
        {
            throw new InvalidOperationException(
                $"{issue.Host} is not set up as a {issue.Kind} tracker here. An administrator adds it under Support › Issue trackers.");
        }

        TicketIssueLink? existing = await db.TicketIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.TicketId == ticketId && l.Url == issue.Url, ct);

        if (existing is not null)
        {
            return existing;
        }

        TicketIssueLink link = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, TicketId = ticketId, Kind = issue.Kind,
            Url = issue.Url, Reference = issue.Reference, AddedBy = actor,
        };

        db.TicketIssueLinks.Add(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    public async Task<bool> UnlinkAsync(Guid tenantId, Guid linkId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketIssueLinks.Where(l => l.Id == linkId && l.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    // ───────────────────────────── Reading ─────────────────────────────

    /// <summary>Every issue linked to the ticket, each read live from its tracker (or remembered for a minute).</summary>
    public async Task<List<IssueView>> ViewsAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        List<TicketIssueLink> links = await LinksAsync(tenantId, ticketId, ct);

        if (links.Count == 0)
        {
            return [];
        }

        Dictionary<string, IssueTracker> trackers = (await TrackersAsync(tenantId, ct)).ToDictionary(t => t.Host);

        return [.. await Task.WhenAll(links.Select(l => ViewAsync(tenantId, l, trackers, ct)))];
    }

    private async Task<IssueView> ViewAsync(
        Guid tenantId, TicketIssueLink link, Dictionary<string, IssueTracker> trackers, CancellationToken ct)
    {
        IssueView Plain(string problem) => new(link.Id, link.Kind, link.Url, link.Reference, null, null, [], [], null, problem);

        IssueReference? issue = IssueReference.Parse(link.Url);

        if (issue is null || !trackers.TryGetValue(issue.Host, out IssueTracker? tracker) || tracker.Kind != issue.Kind)
        {
            return Plain($"{issue?.Host ?? "This host"} is no longer set up as a tracker, so the issue is not read.");
        }

        if (!tracker.IsEnabled)
        {
            return Plain($"{tracker.Host} is switched off, so the issue is not read.");
        }

        (Guid, string) key = (tenantId, issue.Url);

        if (cache.TryGetValue(key, out IssueView? remembered) && remembered is not null)
        {
            return remembered with { LinkId = link.Id };
        }

        IssueView view = await FetchAsync(tenantId, tracker, issue, link, ct);
        cache.Set(key, view, Remember);
        return view;
    }

    private async Task<IssueView> FetchAsync(
        Guid tenantId, IssueTracker tracker, IssueReference issue, TicketIssueLink link, CancellationToken ct)
    {
        IssueView Plain(string problem) => new(link.Id, link.Kind, link.Url, link.Reference, null, null, [], [], null, problem);

        try
        {
            string? token = await tokens.GetAsync(tenantId, tracker.Id, ct);

            using HttpRequestMessage request = new(HttpMethod.Get, issue.ApiUrl);

            if (issue.Kind == IssueTrackerKind.GitHub)
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.UserAgent.ParseAdd("EntKube");

                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new("Bearer", token);
                }
            }
            else if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Add("PRIVATE-TOKEN", token);
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            HttpClient http = httpFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Plain($"{tracker.Host} refused the token (HTTP {(int)response.StatusCode}).");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Plain("Not found — or the token cannot see it.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Plain($"{tracker.Host} answered HTTP {(int)response.StatusCode}.");
            }

            using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            JsonElement root = json.RootElement;

            string? title = Text(root, "title");
            string? state = Text(root, "state");

            // GitHub labels are objects with a name; GitLab's are plain strings.
            List<string> labels = root.TryGetProperty("labels", out JsonElement l) && l.ValueKind == JsonValueKind.Array
                ? [.. l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Text(x, "name")).OfType<string>()]
                : [];

            List<string> assignees = root.TryGetProperty("assignees", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                ? [.. a.EnumerateArray().Select(x => Text(x, issue.Kind == IssueTrackerKind.GitHub ? "login" : "username")).OfType<string>()]
                : [];

            string? milestone = root.TryGetProperty("milestone", out JsonElement m) && m.ValueKind == JsonValueKind.Object ? Text(m, "title") : null;

            // GitHub says "open"; GitLab says "opened". Both say "closed".
            bool? open = state switch { "open" or "opened" => true, "closed" => false, _ => null };

            return new IssueView(link.Id, link.Kind, link.Url, link.Reference, title, open, labels, assignees, milestone, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Plain($"{tracker.Host} did not answer in time.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return Plain($"{tracker.Host} could not be read: {e.Message}");
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
