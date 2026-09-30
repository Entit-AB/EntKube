using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>What came back when the mail server was asked what its domains need published.</summary>
/// <param name="Domain">The domain asked about, or empty when the attempt failed before any.</param>
/// <param name="Records">
/// DKIM records as <c>selector value</c> lines — the form
/// <see cref="StalwartMailDomain.DkimDnsRecords"/> holds and the DNS list renders. Null when
/// nothing recognisable came back, which leaves whatever was recorded before standing.
/// </param>
/// <param name="Error">Why, in words an operator can act on. Null on success.</param>
/// <param name="RawResponse">
/// What the server actually said, when it could not be read. Shown rather than swallowed: the
/// shape of this answer is the one thing here that has never been seen, and a failure that
/// carries the response is one paste away from being fixed, while a failure that hides it is
/// another round of guessing.
/// </param>
public readonly record struct StalwartDnsFetch(
    string Domain, string? Records, string? Error, string? RawResponse = null);

/// <summary>
/// Asks the mail server what DNS its domains need, and records the answer.
///
/// <para><b>How it reaches the server, and why not the obvious way.</b> EntKube runs outside the
/// clusters it manages, so the server's Service name resolves nowhere here. The API server's
/// proxy — what Prometheus, Loki and the telemetry querier are read through — cannot be used
/// either: it strips the caller's Authorization header before forwarding, which is the whole
/// point of it and exactly the header this needs. So this takes the path
/// <c>ElasticsearchService</c> already takes for the same reason: exec into one of the server's
/// own pods and ask over loopback. That also sidesteps a second obstacle — where
/// <c>proxyTrustedNetworks</c> reaches inside the cluster, Stalwart closes any pod-to-pod
/// connection that does not open with a PROXY header, while loopback is answered normally.</para>
///
/// <para><b>Why a token and not the administrator password.</b> Where the directory is OIDC, a
/// password is not merely wrong, it is the wrong kind of credential: the server answers
/// "Unsupported credentials type for OIDC backend" and the stored administrator password is
/// refused. It bypasses the directory only in recovery mode, which is why an apply restarts the
/// server twice to use it — not something a page may do. A bearer token is what that directory
/// accepts, so EntKube mints one the same way the support mailbox does: a confidential Keycloak
/// client whose own mapper hardcodes the username, here the administrator's.</para>
///
/// <para><b>What has not been proven.</b> Everything above was checked against a running server.
/// The call that returns the records themselves was not — it needs a token to see, and the token
/// needs this code. So the last step asks for the schema, looks in it for what holds DKIM, and
/// reports what it found; a response it cannot read is handed back verbatim rather than guessed
/// at. That is the honest shape for a step whose answer nobody here has seen.</para>
/// </summary>
public class StalwartDnsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    KeycloakService keycloak,
    MailboxTokenProvider tokens,
    IKubernetesClientFactory k8s,
    ILogger<StalwartDnsService> logger)
{
    /// <summary>The vault secret holding the client secret of the API identity.</summary>
    public const string ApiClientSecretName = "STALWART_API_CLIENT_SECRET";

    /// <summary>
    /// Reads every domain's DKIM records and writes them onto the domains.
    ///
    /// <para>Written down rather than only displayed: the list of DNS a zone needs has to be
    /// readable when the server is down, being upgraded or mid-apply, which is exactly when
    /// somebody is looking at it.</para>
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
            return [Failed("That mail server is no longer configured.")];
        }

        KubernetesCluster? cluster = await db.KubernetesClusters.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == component.ClusterId, ct);

        if (cluster?.Kubeconfig is not string kubeconfig || string.IsNullOrWhiteSpace(kubeconfig))
        {
            return [Failed("The cluster this mail server runs on has no kubeconfig stored.")];
        }

        string ns = component.Namespace ?? StalwartService.DefaultNamespace;
        string release = component.ReleaseName ?? component.Name;

        string? token;

        try
        {
            token = await TokenAsync(db, config, tenantId, componentId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mint an API token for mail server {Component}.", componentId);
            return [Failed($"Could not obtain a token to authenticate with: {ex.Message}")];
        }

        if (token is null)
        {
            return [Failed(
                "This mail server authenticates against OIDC, and no Keycloak realm is recorded for "
                + "its issuer — so there is no service account to mint a token from. Set the realm on "
                + "the mail server's authentication settings.")];
        }

        string? pod = await ReadyPodAsync(ns, release, kubeconfig, ct);

        if (pod is null)
        {
            return [Failed($"No Ready pod of '{release}' in '{ns}' to ask.")];
        }

        // One exec for the schema, because what holds the records is the part nobody here has
        // seen. What it finds decides the next call; what it cannot read is handed back.
        string schema = await AskAsync(pod, ns, "/api/schema", token, kubeconfig, ct);

        string? objectName = DkimObjectIn(schema);

        if (objectName is null)
        {
            return [new StalwartDnsFetch(
                "", null,
                "Reached the mail server and authenticated, but nothing in its schema named the "
                + "object that holds DKIM keys. The response is below — it names every object this "
                + "server has, and one of them is the one to read.",
                Truncate(schema))];
        }

        List<StalwartDnsFetch> results = [];

        foreach (StalwartMailDomain domain in config.Domains.OrderBy(d => d.Name))
        {
            string body = await AskAsync(
                pod, ns, $"/api/object/{Uri.EscapeDataString(objectName)}", token, kubeconfig, ct);

            string? records = DkimLinesIn(body, domain.Name);

            if (records is not null)
            {
                domain.DkimDnsRecords = records;
                results.Add(new StalwartDnsFetch(domain.Name, records, null));
            }
            else
            {
                results.Add(new StalwartDnsFetch(
                    domain.Name, null,
                    $"Read {objectName} from the mail server but found no DKIM record for "
                    + $"{domain.Name} in it.",
                    Truncate(body)));
            }
        }

        await db.SaveChangesAsync(ct);

        return results;
    }

    private static StalwartDnsFetch Failed(string why) => new("", null, why);

    private static string Truncate(string body) =>
        string.IsNullOrWhiteSpace(body) ? "(nothing)"
        : body.Length <= 1500 ? body : body[..1500] + "…";

    /// <summary>
    /// A bearer token for the administrator identity, from a Keycloak service account EntKube owns.
    ///
    /// <para>The same arrangement the support mailbox uses, for the same reason: an OIDC directory
    /// validates bearer tokens and nothing else, so a password stored anywhere would never be
    /// consulted. Null where the server does not authenticate against OIDC at all — then there is
    /// nothing to mint from and nothing this can do.</para>
    /// </summary>
    private async Task<string?> TokenAsync(
        ApplicationDbContext db, StalwartComponentConfig config, Guid tenantId, Guid componentId,
        CancellationToken ct)
    {
        if (config.AuthMode != StalwartAuthMode.Oidc || config.OidcKeycloakRealmId is not Guid realmId)
        {
            return null;
        }

        string clientId = $"entkube-mail-api-{componentId:N}"[..Math.Min(48, $"entkube-mail-api-{componentId:N}".Length)];

        (string id, string secret, string tokenEndpoint) =
            await keycloak.EnsureServiceAccountClientAsync(
                tenantId, realmId, clientId, config.AdminUsername,
                string.IsNullOrWhiteSpace(config.OidcRequireAudience)
                    ? "stalwart"
                    : config.OidcRequireAudience!.Trim(),
                ct);

        await vault.SetComponentSecretAsync(tenantId, componentId, ApiClientSecretName, secret, ct);

        return await tokens.GetAsync(
            componentId, tokenEndpoint, id, secret,
            config.OidcRequireScopes ?? "openid email", ct);
    }

    /// <summary>The first Ready pod of the mail server's StatefulSet.</summary>
    private async Task<string?> ReadyPodAsync(
        string ns, string release, string kubeconfig, CancellationToken ct)
    {
        try
        {
            string json = await k8s.GetJsonAsync("pods", ns, kubeconfig, $"app={release}", ct);

            using JsonDocument doc = JsonDocument.Parse(json);

            foreach (JsonElement pod in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                bool ready = pod.TryGetProperty("status", out JsonElement status)
                    && status.TryGetProperty("conditions", out JsonElement conditions)
                    && conditions.EnumerateArray().Any(c =>
                        c.TryGetProperty("type", out JsonElement t) && t.GetString() == "Ready"
                        && c.TryGetProperty("status", out JsonElement v) && v.GetString() == "True");

                if (ready
                    && pod.TryGetProperty("metadata", out JsonElement meta)
                    && meta.TryGetProperty("name", out JsonElement name))
                {
                    return name.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list pods of {Release} in {Namespace}.", release, ns);
        }

        return null;
    }

    /// <summary>
    /// Asks the server one thing, from inside one of its own pods.
    ///
    /// <para>The token arrives on stdin so it is never in an argument list, where it would be
    /// visible to anything that can read the process table in that container.</para>
    /// </summary>
    private async Task<string> AskAsync(
        string pod, string ns, string path, string token, string kubeconfig, CancellationToken ct)
    {
        try
        {
            return await k8s.RunCommandOnPodWithStdinAsync(
                pod, ns, ["sh", "-c", Query(path)], token + "\n", kubeconfig, ct, "stalwart");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not ask the mail server for {Path}.", path);
            return $"(the request failed: {ex.Message})";
        }
    }

    /// <summary>
    /// The shell run inside the pod. Loopback, so there is no proxy to strip the header and no
    /// PROXY greeting to send; plain HTTP, because the request never leaves the container.
    /// </summary>
    public static string Query(string path) =>
        "read -r TOK; "
        + $"curl -sS --max-time 20 -H \"Authorization: Bearer $TOK\" "
        + $"\"http://127.0.0.1:{StalwartPlanBuilder.HttpPort}{path}\"";

    /// <summary>
    /// The name of the schema object that holds DKIM keys, or null when nothing looks like one.
    ///
    /// <para>Found rather than hardcoded because this schema has never been read here: the server
    /// lists its own objects, and asking it which one holds DKIM is more honest than naming one
    /// from a reading of the documentation, which is how two earlier attempts went wrong.</para>
    /// </summary>
    public static string? DkimObjectIn(string schemaJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(schemaJson);

            return NamesIn(doc.RootElement)
                .FirstOrDefault(n => n.Contains("dkim", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }

        static IEnumerable<string> NamesIn(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        yield return property.Name;

                        foreach (string nested in NamesIn(property.Value))
                        {
                            yield return nested;
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        foreach (string nested in NamesIn(item))
                        {
                            yield return nested;
                        }
                    }

                    break;

                case JsonValueKind.String:
                    if (element.GetString() is string text)
                    {
                        yield return text;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The DKIM records for a domain in whatever the server returned, as <c>selector value</c>
    /// lines.
    ///
    /// <para><b>Read leniently, and that is not laziness.</b> This is the one shape EntKube reads
    /// and does not control. A strict reader would produce an empty list with no explanation the
    /// day the payload gains a field, and put the operator back to copying values out of a second
    /// admin interface by hand — which is how a key went unpublished for as long as it did. So:
    /// anything carrying a selector and a public key is taken, wrapped or bare, under whichever
    /// of the usual property names it arrives.</para>
    /// </summary>
    public static string? DkimLinesIn(string json, string domain)
    {
        List<string> lines = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            foreach (JsonElement record in Candidates(doc.RootElement))
            {
                string? selector = Text(record, "selector") ?? Text(record, "id") ?? Text(record, "name");
                string? value = Text(record, "content") ?? Text(record, "value")
                    ?? Text(record, "publicKey") ?? Text(record, "record");
                string? owner = Text(record, "domain") ?? Text(record, "domainName");

                if (selector is null
                    || value is null
                    || (owner is not null
                        && !owner.Trim().TrimEnd('.').Equals(
                            domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // A record name rather than a bare selector keeps its own shape; the list rebuilds
                // the full name from the domain otherwise.
                string trimmed = selector.TrimEnd('.');
                int marker = trimmed.IndexOf("._domainkey", StringComparison.OrdinalIgnoreCase);

                lines.Add($"{(marker > 0 ? trimmed[..marker] : trimmed)} {value.Trim().Trim('"')}");
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return lines.Count == 0 ? null : string.Join('\n', string.Join('\n', lines.Distinct()));
    }

    /// <summary>Every object in the payload, whether it arrived bare, wrapped, or nested in a list.</summary>
    private static IEnumerable<JsonElement> Candidates(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (string wrapper in (string[])["data", "items", "list", "records", "results"])
            {
                if (root.TryGetProperty(wrapper, out JsonElement inner))
                {
                    foreach (JsonElement nested in Candidates(inner))
                    {
                        yield return nested;
                    }

                    yield break;
                }
            }

            yield return root;
            yield break;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
            {
                foreach (JsonElement nested in Candidates(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
