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
            .FirstOrDefaultAsync(c => c.Id == componentId && c.Cluster.TenantId == tenantId, ct);

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
                "There is no identity to ask as. This mail server authenticates against OIDC, so a "
                + "token is the only credential it takes — which needs both a Keycloak realm "
                + "recorded for its issuer and an administrator whose address is in one of this "
                + "server's own domains. Set both on the mail server's settings.")];
        }

        string? pod = await ReadyPodAsync(ns, release, kubeconfig, ct);

        if (pod is null)
        {
            return [Failed($"No Ready pod of '{release}' in '{ns}' to ask.")];
        }

        // One exec for the schema, because what holds the records is the part nobody here has
        // seen. What it finds decides the next call; what it cannot read is handed back.
        (int? status, string schema) = SplitStatus(
            await AskAsync(pod, ns, "/api/schema", token, kubeconfig, ct));

        if (status is null)
        {
            return [new StalwartDnsFetch(
                "", null,
                "The request never reached the mail server, so there is no status to report. What "
                + "the attempt produced is below.",
                Truncate(schema))];
        }

        if (status is not 200)
        {
            return [new StalwartDnsFetch(
                "", null,
                status is 401 or 403
                    ? $"The mail server refused the token ({status}). The Keycloak service account "
                      + "exists and the request arrived, so what is missing is the token's standing "
                      + "with it: the username it claims has to resolve to an account the directory "
                      + "knows, that account has to carry the Admin role, and the audience and "
                      + "scopes the server requires have to match."
                    : $"The mail server answered {status} when asked for its schema.",
                Truncate(schema))];
        }

        string? objectName = DkimObjectIn(schema);

        if (objectName is null)
        {
            IReadOnlyList<string> known = ObjectNamesIn(schema);

            return [new StalwartDnsFetch(
                "", null,
                known.Count > 0
                    ? "Read the mail server's schema, and none of the objects it lists holds DKIM "
                      + "keys by name — so they belong to something named differently, or are not "
                      + "separate objects at all. What it lists: " + string.Join(", ", known)
                    : "Read something from the mail server that is not a schema this can make sense "
                      + "of. It is below, verbatim.",
                known.Count > 0 ? null : Truncate(schema))];
        }

        List<StalwartDnsFetch> results = [];

        foreach (StalwartMailDomain domain in config.Domains.OrderBy(d => d.Name))
        {
            (int? recordStatus, string body) = SplitStatus(await AskAsync(
                pod, ns, $"/api/object/{Uri.EscapeDataString(objectName)}", token, kubeconfig, ct));

            string? records = recordStatus is 200 ? DkimLinesIn(body, domain.Name) : null;

            if (records is not null)
            {
                domain.DkimDnsRecords = records;
                results.Add(new StalwartDnsFetch(domain.Name, records, null));
            }
            else
            {
                results.Add(new StalwartDnsFetch(
                    domain.Name, null,
                    recordStatus is 200
                        ? $"Read {objectName} from the mail server but found no DKIM record for "
                          + $"{domain.Name} in it."
                        : $"Asking the mail server for {objectName} answered "
                          + $"{recordStatus?.ToString() ?? "nothing"}.",
                    Truncate(body)));
            }
        }

        await db.SaveChangesAsync(ct);

        return results;
    }

    private static StalwartDnsFetch Failed(string why) => new("", null, why);

    /// <summary>
    /// Whether this is the server saying no rather than answering. Its refusals are RFC 7807
    /// problem documents, which parse perfectly well as JSON and would otherwise be reported as a
    /// schema that happens to mention nothing.
    /// </summary>
    public static bool Unauthorized(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out JsonElement status)
                && status.ValueKind == JsonValueKind.Number
                && status.GetInt32() is 401 or 403;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The object types the schema lists, for when none of them is the one being looked for.
    ///
    /// <para>Names rather than the whole document: the operator reading this needs to see what the
    /// server actually has, and a schema is thousands of lines of forms and layouts around a short
    /// list of objects.</para>
    /// </summary>
    public static IReadOnlyList<string> ObjectNamesIn(string schemaJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(schemaJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("objects", out JsonElement objects))
            {
                return [];
            }

            return objects.ValueKind switch
            {
                JsonValueKind.Object => [.. objects.EnumerateObject().Select(p => p.Name)],
                JsonValueKind.Array =>
                [
                    .. objects.EnumerateArray()
                        .Select(o => o.ValueKind == JsonValueKind.String
                            ? o.GetString()
                            : Text(o, "name") ?? Text(o, "id"))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                ],
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

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

        // The administrator as the server knows it, which is a mailbox in a domain and not the
        // bare word typed into the settings. Stalwart has no administrator concept of its own —
        // only an account carrying the Admin role — and this directory is told a usernameDomain,
        // so it resolves a claim with no domain by appending one. A token claiming "admin" is
        // therefore resolved as "admin@somewhere" and matches no account, which is a 401 that
        // looks exactly like a broken secret. The support mailbox has always claimed its full
        // address for the same reason; this now does too.
        List<StalwartMailDomain> domains = await db.StalwartMailDomains
            .AsNoTracking().Where(d => d.ConfigId == config.Id).ToListAsync(ct);

        if (StalwartService.ResolveAdminIdentity(config, domains) is not (_, _, string adminAddress))
        {
            return null;
        }

        string clientId = $"entkube-mail-api-{componentId:N}"[..Math.Min(48, $"entkube-mail-api-{componentId:N}".Length)];

        (string id, string secret, string tokenEndpoint) =
            await keycloak.EnsureServiceAccountClientAsync(
                tenantId, realmId, clientId, adminAddress,
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
        // The server is configured to read the client's address from this header, and warns on
        // every request arriving without one. Saying plainly that this came from loopback keeps
        // that warning out of the log, where it otherwise appears once per fetch and looks, to
        // whoever debugs next, like part of the problem.
        + "-H \"X-Forwarded-For: 127.0.0.1\" "
        // The status, written after the body. Without it every outcome looks alike from here: a
        // refusal is JSON, an answer is JSON, and a body that is neither cannot be told from a
        // request that never arrived. The server's log is no fallback — a rejected token and an
        // accepted one leave identical traces there, which is what made this take as long as it did.
        + $"-w \"\\n{StatusMarker}%{{http_code}}\" "
        + $"\"http://127.0.0.1:{StalwartPlanBuilder.HttpPort}{path}\"";

    /// <summary>What <see cref="Query"/> writes before the status, so the two can be told apart.</summary>
    public const string StatusMarker = "<<<entkube-http-status:";

    /// <summary>Splits what curl wrote into the status the server gave and the body it sent.</summary>
    /// <returns>
    /// A null status means the request never got far enough to have one, which is a different
    /// answer from any status and the one that used to be invisible.
    /// </returns>
    public static (int? Status, string Body) SplitStatus(string raw)
    {
        int marker = raw.LastIndexOf(StatusMarker, StringComparison.Ordinal);

        if (marker < 0)
        {
            return (null, raw);
        }

        string tail = raw[(marker + StatusMarker.Length)..].Trim();

        return (int.TryParse(tail, out int status) ? status : null, raw[..marker].TrimEnd());
    }

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
    /// The object holding DKIM keys, found in <c>describe</c>'s human-readable listing.
    ///
    /// <para>The CLI prints object names as text rather than JSON, so the structured reader finds
    /// nothing in it. Same rule either way: the server names its own objects, and whichever says
    /// "dkim" is the one to ask for. Guessing that name from documentation is what went wrong
    /// twice, and a wrong name is indistinguishable from a server that has no such object.</para>
    /// </summary>
    public static string? DkimObjectInText(string described)
    {
        // What the server itself lists, asked live:
        //   DkimReportSettings   Configures DKIM authentication failure report generation. [singleton]
        //   DkimSignature        Defines a DKIM signature used to sign outgoing email messages.
        // The first rule written here took the first name containing "dkim" and got the report
        // settings, because alphabetical order put them first — a singleton about failure reports,
        // with no key in it. The name alone is not enough to tell one from the other.
        List<(string Name, string Description)> candidates = [];

        foreach (string line in (described ?? "")
                 .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            string name = (gap > 0 ? line[..gap] : line).Trim();
            string description = gap > 0 ? line[gap..].Trim() : "";

            // Bare names only: snapshot rejects the view and variant slash forms, and describe
            // accepts a name with or without the x: prefix.
            int slash = name.IndexOf('/');
            name = slash > 0 ? name[..slash] : name;
            name = name.StartsWith("x:", StringComparison.OrdinalIgnoreCase) ? name[2..] : name;

            if (name.Length > 0
                && name.Contains("dkim", StringComparison.OrdinalIgnoreCase)
                && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                candidates.Add((name, description));
            }
        }

        // The one that holds keys describes itself as signing, and is not a settings singleton.
        // Ranked rather than filtered, so a server that words it differently still yields
        // something rather than nothing.
        return candidates
            .OrderBy(c => c.Description.Contains("[singleton]", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.Name.Contains("Report", StringComparison.OrdinalIgnoreCase)
                         || c.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.Description.Contains("sign", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// The objects worth snapshotting for a domain's DNS, beside whatever holds the keys.
    ///
    /// <para><c>Domain</c> is included because the server describes it as holding "its DNS, DKIM,
    /// and TLS certificate settings" — so if the published form of a key lives anywhere but the
    /// signature object, it is there. Snapshot takes several types at once, so asking for both
    /// costs one run rather than two.</para>
    /// </summary>
    public static readonly string[] AlsoSnapshot = ["Domain"];

    /// <summary>
    /// Every object type the server lists, from <c>describe</c>'s own output.
    ///
    /// <para>One name per line, followed by its description. Read so that a snapshot can wave
    /// through every reference it is not asking for, rather than discovering them one failed
    /// apply at a time — this server has 117 types and the CLI names only the first one missing.</para>
    /// </summary>
    public static IReadOnlyList<string> ObjectNamesInText(string described)
    {
        List<string> names = [];

        foreach (string line in (described ?? "")
                 .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            string name = (gap > 0 ? line[..gap] : line).Trim();

            int slash = name.IndexOf('/');
            name = slash > 0 ? name[..slash] : name;
            name = name.StartsWith("x:", StringComparison.OrdinalIgnoreCase) ? name[2..] : name;

            if (name.Length > 0 && char.IsLetter(name[0])
                && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                names.Add(name);
            }
        }

        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// What to wave through when the server's own listing could not be read — the references seen
    /// to be needed, rather than nothing at all.
    /// </summary>
    ///
    /// <para>The CLI refuses to export a plan with a dangling reference — "DkimSignature
    /// references Tenant but Tenant is not in the snapshot selection" — and offers two ways out:
    /// add the type, or allow it to be unresolved. Allowing it <em>drops that reference from the
    /// exported plan</em>, which is harmless for a tenant, a certificate or an ACME provider, and
    /// would be fatal for <c>Domain</c>: the domain is how a key is matched to the zone it has to
    /// be published in. So Domain is in the selection and never in this list.</para>
    ///
    /// <para>Named ahead of being asked for, because each missing type otherwise costs a whole
    /// apply to discover. Every name here is one the server lists among its own object types.</para>
    /// </summary>
    public static readonly string[] SnapshotUnresolved =
        ["Tenant", "Certificate", "AcmeProvider", "DnsServer"];

    /// <summary>
    /// The DNS value for a public key the server handed over, as it must be published.
    ///
    /// <para>The server returns PEM. A DKIM record carries the key as one unbroken base64 string
    /// in <c>p=</c>, so the armour and the line breaks come off — and what remains differs by
    /// algorithm. For RSA, <c>p=</c> is the SubjectPublicKeyInfo, which is exactly the PEM body.
    /// For Ed25519, RFC 8463 says <c>p=</c> is the bare 32-byte key, not the structure around it,
    /// so the DER prefix is dropped. Publishing an Ed25519 key in RSA's form produces a record
    /// that looks right and verifies nothing.</para>
    /// </summary>
    /// <param name="pem">The PEM the server returned.</param>
    /// <param name="algorithm">
    /// The signature's variant — <c>Dkim1Ed25519Sha256</c> and the like. Null is treated as RSA,
    /// which is what a record with no <c>k=</c> means to every verifier that defaults.
    /// </param>
    public static string? DnsValueForPublicKey(string? pem, string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        string body = string.Concat(
            pem.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));

        if (body.Length == 0)
        {
            return null;
        }

        bool ed25519 = algorithm?.Contains("ed25519", StringComparison.OrdinalIgnoreCase) == true;

        if (!ed25519)
        {
            return $"v=DKIM1; k=rsa; p={body}";
        }

        try
        {
            byte[] der = Convert.FromBase64String(body);

            // The last 32 bytes of the SubjectPublicKeyInfo are the key itself; everything before
            // them says what kind of key it is, which the record states as k=ed25519 instead.
            return der.Length < 32
                ? null
                : $"v=DKIM1; k=ed25519; p={Convert.ToBase64String(der[^32..])}";
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The published records for a domain, from what <c>query</c> returned.
    ///
    /// <para><b>Why query and not snapshot.</b> The public key is a server-set field, and a
    /// snapshot exports only what can be applied again — so it leaves out the one field that
    /// matters, and says so in a warning about secrets that reads like the whole story. Asked
    /// for directly, the server hands it over: "PEM-encoded public key used to verify
    /// signatures, derived from the private key".</para>
    /// </summary>
    /// <param name="signatures">What the signature query printed.</param>
    /// <param name="domainsById">Domain names by their identifier, from the domain query.</param>
    /// <param name="domain">The domain being asked about.</param>
    public static string? DkimLinesInQuery(
        string signatures, IReadOnlyDictionary<string, string> domainsById, string domain)
    {
        List<string> lines = [];

        foreach (JsonElement record in JsonObjectsIn(signatures))
        {
            string? selector = Text(record, "selector");
            string? pem = Text(record, "publicKey");
            string? owner = Text(record, "domainId") is string id
                && domainsById.TryGetValue(id.TrimStart('#'), out string? name) ? name : null;

            if (selector is null
                || owner is null
                || !owner.Trim().TrimEnd('.').Equals(
                    domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (DnsValueForPublicKey(pem, Text(record, "@type") ?? Text(record, "type")) is string value)
            {
                lines.Add($"{selector} {value}");
            }
        }

        return lines.Count == 0 ? null : string.Join('\n', lines.Distinct());
    }

    /// <summary>Domain names by identifier, from what the domain query printed.</summary>
    public static IReadOnlyDictionary<string, string> DomainsInQuery(string domains)
    {
        Dictionary<string, string> byId = [];

        foreach (JsonElement record in JsonObjectsIn(domains))
        {
            if (Text(record, "name") is string name
                && (Text(record, "id") ?? Text(record, "_id")) is string id)
            {
                byId[id.TrimStart('#')] = name;
            }
        }

        return byId;
    }

    /// <summary>
    /// Every object in what the CLI printed, whether it came as one array, one object per line,
    /// or an array wrapped in something — read leniently for the reason everything here is.
    /// </summary>
    private static IEnumerable<JsonElement> JsonObjectsIn(string output)
    {
        string whole = (output ?? "").Trim();

        // One document when the whole thing is one — a query prints an array — and line by line
        // when it is not, because a plan prints an object per line. Not both: an array read twice
        // reports every record twice.
        if (TryParse(whole) is JsonElement document)
        {
            foreach (JsonElement element in Candidates(document))
            {
                yield return element;
            }

            yield break;
        }

        foreach (string line in whole
                 .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(l => l.StartsWith('{') || l.StartsWith('[')))
        {
            if (TryParse(line) is JsonElement parsed)
            {
                foreach (JsonElement element in Candidates(parsed))
                {
                    yield return element;
                }
            }
        }

        static JsonElement? TryParse(string text)
        {
            if (text.Length == 0 || (!text.StartsWith('{') && !text.StartsWith('[')))
            {
                return null;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Progress chatter, not data.
                return null;
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
