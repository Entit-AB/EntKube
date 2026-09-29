using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>What came back when the server was asked what its domains need published.</summary>
/// <param name="Domain">The domain asked about.</param>
/// <param name="Records">
/// The DKIM records, as <c>selector value</c> lines — the form
/// <see cref="StalwartMailDomain.DkimDnsRecords"/> holds and the DNS list renders.
/// </param>
/// <param name="Error">
/// Why nothing came back, in words an operator can act on. Null on success. Never thrown: a
/// server that cannot be reached must leave the rest of the page working, and the records
/// already recorded standing.
/// </param>
public readonly record struct StalwartDnsFetch(string Domain, string? Records, string? Error);

/// <summary>
/// Asks the mail server what DNS its domains need, and keeps the answer.
///
/// <para><b>Why EntKube has to ask rather than know.</b> Stalwart generates the DKIM keys
/// itself and keeps the private half; the selector and the public half only exist once a
/// domain has been applied. EntKube could take that job over — generate the keys, hold them
/// in the vault, configure the server with them — and then it would never need to ask. That
/// is a change to the apply plan, and a plan operation with a shape the server does not
/// recognise aborts the run at that point and strands everything after it, so it is not a
/// thing to write from a reading of the documentation.</para>
///
/// <para><b>What it must not become is "go and look in the other admin UI".</b> A record
/// nobody published is invisible from every screen here and total at the far end: the server
/// signs every message, the receiver finds no key, and DMARC fails with it. Sending an
/// operator to a second interface to copy a value by hand is how that stays unnoticed for as
/// long as it did. So the value is fetched, written down beside the rest of the domain's DNS,
/// and shown in one list.</para>
///
/// <para><b>Reached inside the cluster, over plain HTTP.</b> The server's own Service name on
/// its HTTP listener: no public DNS, no certificate whose name cannot match, and nothing that
/// depends on the admin interface being published at all. The credential is the administrator
/// EntKube already applies configuration as.</para>
/// </summary>
public class StalwartDnsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    IHttpClientFactory httpFactory,
    ILogger<StalwartDnsService> logger)
{
    /// <summary>
    /// Long enough for a server that is busy, short enough that a page does not hang on one
    /// that will never answer — which is what a listener expecting a PROXY header does.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Fetches every domain's DKIM records and records them on the domains.
    ///
    /// <para>Written down rather than only displayed: the list of DNS a zone needs has to be
    /// readable when the mail server is down, being upgraded, or not yet reachable — those are
    /// exactly the moments somebody is looking at it.</para>
    /// </summary>
    public async Task<IReadOnlyList<StalwartDnsFetch>> RefreshAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        StalwartComponentConfig? config = await db.StalwartComponentConfigs
            .Include(c => c.Domains)
            .FirstOrDefaultAsync(c => c.ClusterComponentId == componentId && c.TenantId == tenantId, ct);

        ClusterComponent? component = await db.ClusterComponents.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        if (config is null || component is null)
        {
            return [new StalwartDnsFetch("", null, "That mail server is no longer configured.")];
        }

        string? password = await vault.GetComponentSecretValueAsync(
            tenantId, componentId, StalwartManifestBuilder.AdminPasswordSecretName, ct);

        if (string.IsNullOrEmpty(password))
        {
            return [new StalwartDnsFetch(
                "", null,
                "No administrator password is stored for this server, so its API cannot be asked. "
                + "Save the mail server's settings again and one is created.")];
        }

        string baseUrl =
            $"http://{component.ReleaseName ?? component.Name}."
            + $"{component.Namespace ?? StalwartService.DefaultNamespace}"
            + $".svc.cluster.local:{StalwartPlanBuilder.HttpPort}";

        using HttpClient http = httpFactory.CreateClient();
        http.Timeout = Timeout;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.AdminUsername}:{password}")));

        List<StalwartDnsFetch> results = [];

        foreach (StalwartMailDomain domain in config.Domains.OrderBy(d => d.Name))
        {
            StalwartDnsFetch fetched = await FetchAsync(http, baseUrl, domain.Name, ct);

            if (fetched.Records is not null)
            {
                domain.DkimDnsRecords = fetched.Records;
            }

            results.Add(fetched);
        }

        await db.SaveChangesAsync(ct);

        return results;
    }

    private async Task<StalwartDnsFetch> FetchAsync(
        HttpClient http, string baseUrl, string domain, CancellationToken ct)
    {
        string name = domain.Trim().ToLowerInvariant();
        string url = $"{baseUrl}/api/dns/records/{Uri.EscapeDataString(name)}";

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new StalwartDnsFetch(
                    name, null,
                    $"The mail server answered {(int)response.StatusCode} for {name}. "
                    + (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "The stored administrator credential was refused."
                        : Summarise(body)));
            }

            string? records = DkimLinesIn(body, name);

            return records is null
                ? new StalwartDnsFetch(
                    name, null,
                    $"The mail server listed no DKIM record for {name}. A domain signs only after "
                    + "it has been applied, so apply the configuration and ask again.")
                : new StalwartDnsFetch(name, records, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read DNS records for {Domain} from {Url}.", name, url);

            return new StalwartDnsFetch(
                name, null,
                $"Could not reach the mail server at {url}: {ex.Message}. It is asked on its own "
                + "Service name inside the cluster — a listener that is waiting for a PROXY header "
                + "from this address will never answer, and an address in the trusted networks is "
                + "what causes that.");
        }
    }

    /// <summary>
    /// The DKIM records in what the server returned, as <c>selector value</c> lines.
    ///
    /// <para><b>Parsed leniently on purpose.</b> This is the one place EntKube reads a shape it
    /// does not control, and the cost of being strict is a blank DNS list with no explanation
    /// on the day the server's response gains a field. So: the payload may be wrapped in
    /// <c>data</c> or bare, the value may be called <c>content</c> or <c>value</c>, and
    /// anything that is not a TXT record under <c>_domainkey</c> is simply not ours to care
    /// about here — MX, SPF and DMARC are already in the list from what EntKube itself knows,
    /// and its SPF is the better answer because it knows the address the server sends from.</para>
    /// </summary>
    public static string? DkimLinesIn(string json, string domain)
    {
        List<string> lines = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out JsonElement data))
            {
                root = data;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement record in root.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? type = Text(record, "type");
                string? recordName = Text(record, "name");
                string? value = Text(record, "content") ?? Text(record, "value");

                if (recordName is null
                    || value is null
                    || !recordName.Contains("_domainkey", StringComparison.OrdinalIgnoreCase)
                    || (type is not null && !type.Equals("TXT", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // The selector alone, so the stored form stays the one a person can read and
                // edit. The list rebuilds the full name from the domain.
                string selector = recordName.TrimEnd('.');
                int marker = selector.IndexOf("._domainkey", StringComparison.OrdinalIgnoreCase);

                if (marker > 0)
                {
                    selector = selector[..marker];
                }

                lines.Add($"{selector} {value.Trim().Trim('"')}");
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Enough of a failing response to recognise it, never the whole body in a toast.</summary>
    private static string Summarise(string body) =>
        string.IsNullOrWhiteSpace(body)
            ? "It said nothing further."
            : body.Length <= 200 ? body : body[..200] + "…";
}
