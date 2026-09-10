using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages Stalwart mail servers deployed as catalog components. Follows the OpenLDAP pattern: a
/// <see cref="StalwartComponentConfig"/> attaches to an installed <see cref="ClusterComponent"/>,
/// credentials live in the vault, and the whole server is authored declaratively in EntKube and
/// converged onto the running deployment.
///
/// <para>Convergence happens in two halves, because Stalwart v0.16 splits its configuration in
/// two. The pod's shape — ports, storage, TLS mounts, the datastore — is a Kubernetes manifest
/// that EntKube regenerates and re-applies. Everything else — domains, listeners, the directory,
/// the spam filter's milter, mailboxes — lives inside the datastore and is reconciled by replaying
/// a declarative NDJSON plan through <c>stalwart-cli apply</c>, run as a Job in the cluster.</para>
///
/// <para>That second half needs an administrator, and the only credential that works before a
/// directory exists is the recovery administrator, which Stalwart honours only in recovery mode.
/// So <see cref="ApplyConfigurationAsync"/> switches the server into recovery mode, replays the
/// plan, and switches it back. It is deliberately an explicit action: mail is not accepted while
/// it runs.</para>
/// </summary>
public class StalwartService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IKubernetesClientFactory k8sFactory,
    ExternalRouteService routeService,
    KeycloakService keycloakService,
    ComponentLifecycleService lifecycleService,
    RedisService redisService,
    ILogger<StalwartService> logger) : IComponentFormValueProvider
{
    // Explicit implementation: the class already exposes CatalogKey as a const, and the interface
    // wants it as a property.
    string IComponentFormValueProvider.CatalogKey => CatalogKey;

    async Task<Dictionary<string, string>> IComponentFormValueProvider.ReadFormValuesAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct)
    {
        StalwartComponentConfig? config = await GetConfigForComponentAsync(tenantId, clusterComponentId, ct);
        return config is null ? [] : BuildFormValues(config);
    }

    public const string CatalogKey = "stalwart";
    public const string RspamdCatalogKey = "rspamd";
    public const string RoundcubeCatalogKey = "roundcube";
    public const string SnappyMailCatalogKey = "snappymail";

    /// <summary>Default namespace the catalog entry installs into.</summary>
    public const string DefaultNamespace = "stalwart";

    /// <summary>How long to wait for the StatefulSet to come back after a mode switch.</summary>
    private static readonly TimeSpan RolloutTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the apply Job to finish.</summary>
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A discovered Stalwart instance: its installed component and attached config (if any).</summary>
    public sealed record StalwartInstance(ClusterComponent Component, StalwartComponentConfig? Config);

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists installed Stalwart components for the tenant, each paired with its config (null until
    /// configured). When <paramref name="environmentId"/> is given, restricts to components on
    /// clusters bound to that environment.
    /// </summary>
    public async Task<List<StalwartInstance>> GetInstancesAsync(
        Guid tenantId, Guid? environmentId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<ClusterComponent> components = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Name == CatalogKey && c.Cluster.TenantId == tenantId)
            .Where(c => environmentId == null || c.Cluster.EnvironmentId == environmentId)
            .OrderBy(c => c.Cluster.Name)
            .ToListAsync(ct);

        List<Guid> componentIds = components.Select(c => c.Id).ToList();
        Dictionary<Guid, StalwartComponentConfig> configs = await db.StalwartComponentConfigs
            .Where(c => c.ClusterComponentId != null && componentIds.Contains(c.ClusterComponentId!.Value))
            .ToDictionaryAsync(c => c.ClusterComponentId!.Value, ct);

        return components
            .Select(c => new StalwartInstance(c, configs.GetValueOrDefault(c.Id)))
            .ToList();
    }

    /// <summary>The config attached to an installed component, or null when never configured.</summary>
    public async Task<StalwartComponentConfig?> GetConfigForComponentAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
    }

    /// <summary>The config with its domains and mailboxes loaded.</summary>
    public async Task<StalwartComponentConfig?> GetConfigAsync(Guid configId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.StalwartComponentConfigs
            .Include(c => c.Domains)
            .Include(c => c.Accounts)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
    }

    // ── Configure ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Upserts the config for an installed Stalwart component, stores the credentials in the vault,
    /// regenerates the component manifest, and reconciles the web route. Pass a null/blank secret
    /// to leave the stored one unchanged.
    /// </summary>
    public async Task<StalwartComponentConfig> ConfigureAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Action<StalwartComponentConfig> apply,
        string? adminPassword = null,
        string? ldapBindPassword = null,
        string? tlsCertificate = null,
        string? tlsPrivateKey = null,
        string? coordinatorRedisPassword = null,
        CancellationToken ct = default)
    {
        string ns;
        string releaseName;
        StalwartComponentConfig config;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            ClusterComponent component = await db.ClusterComponents
                .Include(c => c.Cluster)
                .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("Component not found.");

            ns = component.Namespace ?? DefaultNamespace;
            releaseName = component.ReleaseName ?? component.Name;

            StalwartComponentConfig? existing = await db.StalwartComponentConfigs
                .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId, ct);

            if (existing is null)
            {
                existing = new StalwartComponentConfig
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClusterComponentId = clusterComponentId,
                    Hostname = "mail.example.com",
                };
                db.StalwartComponentConfigs.Add(existing);
            }

            apply(existing);

            // A directory EntKube already manages knows its own URL, base DN and admin DN. Deriving
            // them here means the two components cannot drift apart through a typo, and the derived
            // values are stored rather than recomputed so the UI shows exactly what will be applied.
            await ResolveOpenLdapLinkAsync(db, existing, ct);
            await ResolveOidcRegistrationAsync(db, existing, ct);

            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            config = existing;
        }

        await vaultService.InitializeVaultAsync(tenantId, ct);
        string credentialsSecret = $"{releaseName}{StalwartManifestBuilder.CredentialsSecretSuffix}";

        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartManifestBuilder.AdminPasswordSecretName, adminPassword, ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);

            // STALWART_RECOVERY_ADMIN wants "username:password" in one value, while the CLI wants
            // the two separately. Storing both spellings costs one secret and avoids assembling a
            // credential inside a manifest, where it would be visible to anyone who can read pods.
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartManifestBuilder.RecoveryAdminSecretName,
                $"{config.AdminUsername}:{adminPassword}", ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);
        }

        else
        {
            // The recovery credential is "username:password" in one value, so changing only the
            // administrator name would otherwise leave it pointing at the old one — and the next
            // apply would authenticate as an account that no longer matches the configuration.
            string? stored = await vaultService.GetComponentSecretValueAsync(
                tenantId, clusterComponentId, StalwartManifestBuilder.AdminPasswordSecretName, ct);

            if (!string.IsNullOrWhiteSpace(stored))
            {
                await vaultService.SetComponentSecretAsync(
                    tenantId, clusterComponentId, StalwartManifestBuilder.RecoveryAdminSecretName,
                    $"{config.AdminUsername}:{stored}", ct,
                    k8sSecretName: credentialsSecret, k8sNamespace: ns);
            }
        }

        if (!string.IsNullOrWhiteSpace(ldapBindPassword))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartManifestBuilder.LdapBindPasswordSecretName, ldapBindPassword, ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);
        }

        if (!string.IsNullOrWhiteSpace(tlsCertificate))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartManifestBuilder.TlsCertSecretName, tlsCertificate, ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);
        }

        if (!string.IsNullOrWhiteSpace(tlsPrivateKey))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartManifestBuilder.TlsKeySecretName, tlsPrivateKey, ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);
        }

        if (!string.IsNullOrWhiteSpace(coordinatorRedisPassword))
        {
            // Vault only, not synced to the pod's Secret: the standalone-Redis store reads no env
            // var, so this password reaches the server inside the coordinator URL, not the
            // environment. Held here so BuildHaBackendAsync can read it back to build that URL.
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, "STALWART_COORDINATOR_REDIS_PASSWORD", coordinatorRedisPassword, ct);
        }

        await RefreshManifestIfConfiguredAsync(tenantId, clusterComponentId, ct);
        await EnsureWebRouteAsync(tenantId, clusterComponentId, ct);

        return config;
    }

    /// <summary>
    /// Fills the LDAP connection details in from the linked EntKube-managed OpenLDAP directory.
    /// No-op when nothing is linked, or when the link points at a directory this tenant no longer has.
    /// </summary>
    private static async Task ResolveOpenLdapLinkAsync(
        ApplicationDbContext db, StalwartComponentConfig config, CancellationToken ct)
    {
        if (config.OpenLdapConfigId is not Guid ldapConfigId)
        {
            return;
        }

        OpenLdapComponentConfig? ldap = await db.OpenLdapComponentConfigs
            .FirstOrDefaultAsync(c => c.Id == ldapConfigId && c.TenantId == config.TenantId, ct);
        if (ldap is null)
        {
            return;
        }

        ClusterComponent? ldapComponent = ldap.ClusterComponentId is Guid id
            ? await db.ClusterComponents.FirstOrDefaultAsync(c => c.Id == id, ct)
            : null;

        string release = ldapComponent?.ReleaseName ?? ldapComponent?.Name ?? "openldap";
        string ldapNs = ldapComponent?.Namespace ?? "openldap";

        bool tls = config.LdapUseTls && ldap.TlsMode != OpenLdapTlsMode.Off;
        int port = tls ? ldap.LdapsPort : ldap.LdapPort;
        string scheme = tls ? "ldaps" : "ldap";

        config.LdapUrl = $"{scheme}://{release}.{ldapNs}.svc.cluster.local:{port}";
        config.LdapBaseDn = ldap.BaseDn;
        config.LdapBindDn = $"cn={ldap.AdminUsername},{ldap.BaseDn}";
    }

    /// <summary>
    /// Fills the OIDC fields in from the selected stored app registration, the provider-correct way.
    /// Mirrors <see cref="ResolveOpenLdapLinkAsync"/>: derived values are written onto the config on
    /// save, so the stored config (and the UI) shows exactly what will be applied, and the plan
    /// builder stays a pure function of the config. No-op when nothing is linked or the secret is
    /// gone. Stalwart validates tokens rather than acting as an OAuth client, so only the issuer and
    /// claims matter here — not the client secret (that is the webmail's concern).
    /// </summary>
    private async Task ResolveOidcRegistrationAsync(
        ApplicationDbContext db, StalwartComponentConfig config, CancellationToken ct)
    {
        if (config.AuthMode != StalwartAuthMode.Oidc || config.OidcAppRegistrationSecretId is not Guid secretId)
        {
            return;
        }

        OAuthClientBundle? bundle = await vaultService.GetOAuthClientBundleByIdAsync(secretId, ct);
        if (bundle is null || OAuthClientHelper.Resolve(bundle) is not ResolvedOidc resolved)
        {
            return;
        }

        config.OidcIssuerUrl = resolved.IssuerUrl;
        config.OidcClaimUsername = resolved.ClaimUsername;
        config.OidcRequireAudience = resolved.RequireAudience;
        config.OidcRequireScopes = resolved.Scopes;
        config.OidcClaimGroups = resolved.ClaimGroups;
    }

    /// <summary>A stored OIDC app registration, for the Mail tab's provider picker.</summary>
    /// <param name="SecretId">Vault secret id — what <see cref="StalwartComponentConfig.OidcAppRegistrationSecretId"/> stores.</param>
    /// <param name="Name">The registration's name.</param>
    /// <param name="Provider">Which identity provider (Entra, Google, …).</param>
    public sealed record OidcRegistration(Guid SecretId, string Name, OAuthProvider Provider);

    /// <summary>Lists the tenant's stored OAuth/OIDC app registrations for the provider picker.</summary>
    public async Task<List<OidcRegistration>> GetOidcRegistrationsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await db.Set<VaultSecret>()
            .Include(x => x.Vault)
            .Where(x => x.Vault.TenantId == tenantId && x.SecretType == VaultSecretType.OAuthClient)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        List<OidcRegistration> result = [];
        foreach (VaultSecret secret in secrets)
        {
            OAuthClientInfo? info = await vaultService.GetOAuthClientInfoByIdAsync(secret.Id, ct);
            result.Add(new OidcRegistration(secret.Id, secret.Name, info?.Provider ?? OAuthProvider.GenericOidc));
        }
        return result;
    }

    /// <summary>
    /// Parses catalog form-field values and applies them to the component's config. Shared by the
    /// interactive install path and the blueprint bootstrap path so both capture the same settings.
    /// </summary>
    public async Task ConfigureFromFormAsync(
        Guid tenantId, Guid clusterComponentId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        form.TryGetValue("admin-password", out string? adminPassword);
        form.TryGetValue("ldap-bind-password", out string? bindPassword);
        form.TryGetValue("tls-cert", out string? cert);
        form.TryGetValue("tls-key", out string? key);

        // Picking a realm on this cluster's Keycloak fills the issuer URL in. Stalwart validates tokens
        // rather than issuing them, so unlike the webmail and the rspamd UI it needs no client of its own —
        // the realm's address is the whole of what it needs, and typing that by hand is the step that
        // silently mismatches the client's issuer and rejects every token.
        Dictionary<string, string> resolved = new(form, StringComparer.Ordinal);
        if (form.TryGetValue("oidc-realm", out string? realmValue)
            && Guid.TryParse(realmValue, out Guid realmId)
            && await ResolveRealmIssuerAsync(tenantId, realmId, ct) is string issuerFromRealm)
        {
            resolved["oidc-issuer"] = issuerFromRealm;
        }

        await ConfigureAsync(
            tenantId, clusterComponentId,
            cfg => ApplyFormValues(cfg, resolved),
            string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword,
            string.IsNullOrWhiteSpace(bindPassword) ? null : bindPassword,
            string.IsNullOrWhiteSpace(cert) ? null : cert,
            string.IsNullOrWhiteSpace(key) ? null : key,
            coordinatorRedisPassword: null,
            ct: ct);
    }

    /// <summary>
    /// Applies catalog form values onto a config. Only keys the form actually carries are applied:
    /// the component form has no say over the domains, mailboxes or protocol toggles authored in
    /// the Mail tab, and must leave those exactly as the operator left them rather than resetting
    /// them to a catalog default.
    /// </summary>
    public static void ApplyFormValues(
        StalwartComponentConfig cfg, IReadOnlyDictionary<string, string> form)
    {
        if (Text(form, "mail-hostname") is string hostname)
        {
            cfg.Hostname = hostname;
        }
        if (Text(form, "admin-hostname") is string adminHostname)
        {
            cfg.AdminHostname = adminHostname;
        }
        if (Text(form, "admin-username") is string adminUsername)
        {
            cfg.AdminUsername = adminUsername;
        }
        if (Text(form, "storage-size") is string storageSize)
        {
            cfg.StorageSize = storageSize;
        }
        if (Text(form, "storage-class") is string storageClass)
        {
            cfg.StorageClass = storageClass;
        }
        if (Enum(form, "auth-mode", StalwartAuthMode.Ldap) is StalwartAuthMode authMode)
        {
            cfg.AuthMode = authMode;
        }
        if (Text(form, "ldap-url") is string ldapUrl)
        {
            cfg.LdapUrl = ldapUrl;
        }
        if (Text(form, "ldap-base-dn") is string baseDn)
        {
            cfg.LdapBaseDn = baseDn;
        }
        if (Text(form, "ldap-bind-dn") is string bindDn)
        {
            cfg.LdapBindDn = bindDn;
        }
        if (Text(form, "oidc-issuer") is string issuer)
        {
            cfg.OidcIssuerUrl = issuer;
        }
        if (Text(form, "oidc-username-domain") is string usernameDomain)
        {
            cfg.OidcUsernameDomain = usernameDomain;
        }
        if (Enum(form, "tls-mode", StalwartTlsMode.ClusterIssuer) is StalwartTlsMode tlsMode)
        {
            cfg.TlsMode = tlsMode;
        }
        if (Text(form, "cluster-issuer") is string issuerName)
        {
            cfg.ClusterIssuer = issuerName;
        }
        if (Text(form, "acme-contact") is string acmeContact)
        {
            cfg.AcmeContact = acmeContact;
        }
        if (Enum(form, "expose-mode", StalwartMailExposeMode.LoadBalancer) is StalwartMailExposeMode expose)
        {
            cfg.ExposeMode = expose;
        }
        if (Text(form, "load-balancer-ip") is string lbIp)
        {
            cfg.LoadBalancerIp = lbIp;
        }
        if (Bool(form, "rspamd-enabled") is bool rspamd)
        {
            cfg.RspamdEnabled = rspamd;
        }
        if (Text(form, "rspamd-host") is string rspamdHost)
        {
            cfg.RspamdHost = rspamdHost;
        }
    }

    /// <summary>
    /// The administrator as three things the rest of the code keeps confusing: the local part of the
    /// mailbox, the domain it lives in, and the full address.
    ///
    /// <para><see cref="StalwartComponentConfig.AdminUsername"/> was built as a local part
    /// ("admin"), but an operator told "the administrator must be a directory user" will
    /// reasonably type the user's full address — and then the plan created a mailbox named
    /// <c>nils.blomgren@entit.eu</c> inside <c>entit.eu</c>, and the preflight looked for
    /// <c>…@entit.eu@entit.eu</c>. Both forms are accepted now. The typed value is still what the
    /// recovery admin and the CLI use verbatim, because Stalwart compares that name bare, before it
    /// appends any domain.</para>
    /// </summary>
    /// <returns>Null when the address names a domain this server does not serve.</returns>
    public static (string LocalPart, string Domain, string Address)? ResolveAdminIdentity(
        StalwartComponentConfig config, IReadOnlyList<StalwartMailDomain> domains)
    {
        string typed = (config.AdminUsername ?? "").Trim().ToLowerInvariant();
        StalwartMailDomain? primary = domains.FirstOrDefault(d => d.IsPrimary) ?? domains.FirstOrDefault();
        if (typed.Length == 0 || primary is null)
        {
            return null;
        }

        int at = typed.IndexOf('@');
        if (at < 0)
        {
            string domain = primary.Name.Trim().ToLowerInvariant();
            return (typed, domain, $"{typed}@{domain}");
        }

        string localPart = typed[..at];
        string typedDomain = typed[(at + 1)..];
        bool served = domains.Any(d => string.Equals(d.Name.Trim(), typedDomain, StringComparison.OrdinalIgnoreCase));
        return localPart.Length > 0 && served ? (localPart, typedDomain, typed) : null;
    }

    /// <summary>
    /// The inverse of <see cref="ApplyFormValues"/>: the component form's values, read back out of
    /// the stored configuration.
    ///
    /// <para>This exists because every one of Stalwart's form fields is a <c>stalwart:</c>
    /// pseudo-path, so there is nothing in the component's stored YAML to recover them from. Without
    /// it the Components tab re-opens on catalog defaults — mostly blank — and saving that form
    /// writes blanks and defaults over a working mail server.</para>
    ///
    /// <para>Secrets are deliberately absent. The administrator password, the LDAP bind password and
    /// a manually-supplied certificate are never echoed back into a form; leaving those keys out
    /// means <see cref="ApplyFormValues"/> and <c>ConfigureFromFormAsync</c> see no value and keep
    /// what is already stored.</para>
    /// </summary>
    public static Dictionary<string, string> BuildFormValues(StalwartComponentConfig config) => new()
    {
        ["mail-hostname"] = config.Hostname,
        ["admin-hostname"] = config.AdminHostname ?? "",
        ["admin-username"] = config.AdminUsername,
        ["storage-size"] = config.StorageSize,
        ["storage-class"] = config.StorageClass ?? "",
        ["auth-mode"] = config.AuthMode.ToString(),
        ["ldap-url"] = config.LdapUrl ?? "",
        ["ldap-base-dn"] = config.LdapBaseDn ?? "",
        ["ldap-bind-dn"] = config.LdapBindDn ?? "",
        ["oidc-issuer"] = config.OidcIssuerUrl ?? "",
        ["oidc-username-domain"] = config.OidcUsernameDomain ?? "",
        ["tls-mode"] = config.TlsMode.ToString(),
        ["cluster-issuer"] = config.ClusterIssuer ?? "",
        ["acme-contact"] = config.AcmeContact ?? "",
        ["expose-mode"] = config.ExposeMode.ToString(),
        ["load-balancer-ip"] = config.LoadBalancerIp ?? "",
        ["rspamd-enabled"] = config.RspamdEnabled ? "true" : "false",
        ["rspamd-host"] = config.RspamdHost ?? "",
    };

    /// <summary>
    /// Form keys that hold a secret and are therefore never read back into the UI. Named here so
    /// the round-trip test can tell "deliberately withheld" from "forgotten".
    /// </summary>
    public static readonly string[] SecretFormKeys =
        ["admin-password", "ldap-bind-password", "tls-cert", "tls-key"];

    /// <summary>
    /// Form keys that are inputs only: they are consumed while saving to derive something else and
    /// are never stored, so there is nothing to read back. <c>oidc-realm</c> is the picker that
    /// fills in <c>oidc-issuer</c> — the issuer is what the server actually validates against, and
    /// it is what gets persisted. Reopening the form therefore shows the issuer filled in and the
    /// realm unselected, which is the honest picture rather than a lost value.
    ///
    /// <para>Kept separate from <see cref="SecretFormKeys"/> because the reasons differ: a secret
    /// must never be echoed back, whereas this simply has nothing to echo.</para>
    /// </summary>
    public static readonly string[] DerivedFormKeys = ["oidc-realm"];

    private static string? Text(IReadOnlyDictionary<string, string> form, string key) =>
        form.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static bool? Bool(IReadOnlyDictionary<string, string> form, string key) =>
        form.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v)
            ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            : null;

    private static T? Enum<T>(IReadOnlyDictionary<string, string> form, string key, T _) where T : struct =>
        form.TryGetValue(key, out string? v) && System.Enum.TryParse(v, ignoreCase: true, out T parsed)
            ? parsed
            : null;

    // ── High availability ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the config's HA selections (CNPG database, S3 storage link, coordinator Redis) into
    /// the backend the builders consume, and mirrors the three backend secrets — the PostgreSQL
    /// password, the S3 secret key, the Redis password — into the component's credentials Secret so
    /// each pod reads them from its environment.
    ///
    /// <para>Returns null when HA is off, or on but incomplete. A null in the incomplete case is
    /// deliberate: the manifest then renders single-node, but <see cref="PreflightAsync"/> blocks the
    /// apply and says what is missing — so nothing half-configured ever reaches the cluster.</para>
    /// </summary>
    public async Task<StalwartPlanBuilder.StalwartHaBackend?> BuildHaBackendAsync(
        Guid tenantId, Guid clusterComponentId, StalwartComponentConfig config, string releaseName, string ns,
        CancellationToken ct = default)
    {
        if (!config.HighAvailability
            || config.CnpgDatabaseId is not Guid dbId
            || config.BlobStorageLinkId is not Guid linkId
            || string.IsNullOrWhiteSpace(config.CoordinatorRedisHost))
        {
            return null;
        }

        string credentialsSecret = $"{releaseName}{StalwartManifestBuilder.CredentialsSecretSuffix}";

        CnpgDatabase? cnpg;
        StorageLink? link;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            cnpg = await db.CnpgDatabases.Include(d => d.CnpgCluster)
                .FirstOrDefaultAsync(d => d.Id == dbId, ct);
            link = await db.StorageLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        }
        if (cnpg?.CnpgCluster is null || link is null)
        {
            return null;
        }

        string dbHost = $"{cnpg.CnpgCluster.Name}-rw.{cnpg.CnpgCluster.Namespace}.svc.cluster.local";

        // Mirror the datastore password from the CNPG-managed secret into ours, keyed as the env the
        // pod reads. Resolved fresh each time so a rotated database password follows on the next apply.
        string? dbPassword = await vaultService.GetCnpgDatabasePasswordAsync(tenantId, dbId, ct);
        if (!string.IsNullOrWhiteSpace(dbPassword))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartPlanBuilder.DbPasswordEnv, dbPassword, ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);
        }

        string accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "ACCESS_KEY", ct) ?? "";
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, linkId, "SECRET_KEY", ct);
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, StalwartPlanBuilder.S3SecretKeyEnv, secretKey, ct,
                k8sSecretName: credentialsSecret, k8sNamespace: ns);
        }

        // The standalone-Redis store carries no secret field, so a password rides in the URL.
        string? redisPassword = await Secret(tenantId, clusterComponentId, "STALWART_COORDINATOR_REDIS_PASSWORD", ct);
        string redisAuth = string.IsNullOrWhiteSpace(redisPassword)
            ? ""
            : $":{Uri.EscapeDataString(redisPassword)}@";
        string redisUrl = $"redis://{redisAuth}{config.CoordinatorRedisHost!.Trim()}:{config.CoordinatorRedisPort}";

        return new StalwartPlanBuilder.StalwartHaBackend(
            DbHost: dbHost, DbPort: 5432, DbName: cnpg.Name, DbUser: cnpg.Owner,
            S3Endpoint: link.Endpoint ?? "", S3Region: link.Region ?? "us-east-1", S3Bucket: link.BucketName ?? "",
            S3AccessKey: accessKey,
            RedisUrl: redisUrl,
            Replicas: Math.Max(2, config.Replicas));
    }

    // ── Manifest ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Regenerates the component's stored manifest from the config. Runs before every install so
    /// the deployed pod always matches what the operator authored.
    /// </summary>
    public async Task RefreshManifestIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StalwartComponentConfig? config = await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
        if (config is null)
        {
            return;
        }

        ClusterComponent? component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component is null)
        {
            return;
        }

        string releaseName = component.ReleaseName ?? component.Name;
        string ns = component.Namespace ?? DefaultNamespace;

        StalwartPlanBuilder.StalwartHaBackend? ha =
            await BuildHaBackendAsync(tenantId, clusterComponentId, config, releaseName, ns, ct);
        component.HelmValues = StalwartManifestBuilder.Build(config, releaseName, ns, ha: ha);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Refreshed Stalwart manifest for component {ComponentId} ({Hostname}, auth {AuthMode}).",
            clusterComponentId, config.Hostname, config.AuthMode);
    }

    /// <summary>
    /// Issues the mail certificate via cert-manager before the install, so the StatefulSet's TLS
    /// volume exists when the pod mounts it. No-op unless TLS mode is ClusterIssuer.
    /// </summary>
    public async Task ApplyTlsCertificateIfNeededAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StalwartComponentConfig? config = await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
        if (config is null
            || config.TlsMode != StalwartTlsMode.ClusterIssuer
            || string.IsNullOrWhiteSpace(config.ClusterIssuer))
        {
            return;
        }

        ClusterComponent? component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component?.Cluster?.Kubeconfig is not { Length: > 0 } kubeconfig)
        {
            logger.LogWarning(
                "Stalwart TLS certificate skipped — no kubeconfig for component {ComponentId}.", clusterComponentId);
            return;
        }

        string releaseName = component.ReleaseName ?? component.Name;
        string ns = component.Namespace ?? DefaultNamespace;

        await k8sFactory.EnsureNamespaceAsync(ns, kubeconfig, ct);
        await k8sFactory.ApplyManifestAsync(
            StalwartManifestBuilder.BuildTlsCertificateManifest(
                config.ClusterIssuer!, config.Hostname.Trim(), releaseName, ns),
            kubeconfig, ct);

        string secretName = $"{releaseName}{StalwartManifestBuilder.TlsSecretSuffix}";
        if (!await WaitForSecretAsync(ns, secretName, kubeconfig, TimeSpan.FromMinutes(3), ct))
        {
            throw new InvalidOperationException(
                $"cert-manager did not issue the '{secretName}' TLS Secret in namespace '{ns}' within 3 minutes "
                + $"(ClusterIssuer '{config.ClusterIssuer}'). The certificate is for '{config.Hostname}', so an "
                + "ACME issuer has to be able to solve a challenge for that name — check the issuer's DNS-01 "
                + "credentials, or switch TLS mode to ACME and let Stalwart obtain the certificate itself.");
        }
    }

    private async Task<bool> WaitForSecretAsync(
        string ns, string secretName, string kubeconfig, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await k8sFactory.GetJsonAsync($"secret/{secretName}", ns, kubeconfig, ct: ct);
                if (json.Contains("tls.crt", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                // kubectl exits non-zero until the Secret exists — keep polling.
                logger.LogDebug(ex, "Waiting for TLS Secret {Secret} in {Namespace}…", secretName, ns);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return false;
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reconciles the ExternalRoute publishing the web surfaces — admin UI, JMAP, autoconfig and
    /// the OAuth endpoints — through the cluster's gateway. Gateway name and namespace are left
    /// unset so they resolve to whichever ingress the cluster actually runs, Istio or Traefik.
    /// </summary>
    public async Task EnsureWebRouteAsync(Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        StalwartComponentConfig? config;
        string releaseName;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            config = await db.StalwartComponentConfigs
                .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
            if (config is null)
            {
                return;
            }

            ClusterComponent? component = await db.ClusterComponents
                .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
            if (component is null)
            {
                return;
            }
            releaseName = component.ReleaseName ?? component.Name;
        }

        List<ExternalRoute> existing = await routeService.GetRoutesAsync(clusterComponentId, ct);

        // Clear the route we own before re-adding it, so a hostname change or a switch back to
        // "not published" actually removes the old route rather than leaving it serving.
        foreach (ExternalRoute route in existing.Where(r => r.ServiceName == releaseName))
        {
            await routeService.DeleteRouteAsync(route.Id, ct);
        }

        if (string.IsNullOrWhiteSpace(config.AdminHostname))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.WebClusterIssuer))
        {
            logger.LogWarning(
                "Skipping the Stalwart web route for {Hostname} — no web ClusterIssuer is set, so the "
                + "gateway would have no certificate for it.", config.AdminHostname);
            return;
        }

        await routeService.AddRouteAsync(clusterComponentId, new ExternalRouteRequest
        {
            Hostname = config.AdminHostname!.Trim(),
            ServiceName = releaseName,
            ServicePort = StalwartPlanBuilder.HttpPort,
            PathPrefix = "/",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = config.WebClusterIssuer!.Trim(),
        }, ct);
    }

    // ── Preflight ─────────────────────────────────────────────────────────────

    /// <summary>One thing that will stop this mail server working, and what to do about it.</summary>
    /// <param name="IsBlocking">True when applying cannot produce a usable server.</param>
    /// <param name="Problem">What is wrong.</param>
    /// <param name="Remedy">The action that fixes it.</param>
    public sealed record MailPreflightIssue(bool IsBlocking, string Problem, string Remedy);

    /// <summary>
    /// Checks the things that leave a mail server running but unusable — the failures that look like
    /// success until somebody tries to log in.
    ///
    /// <para>Each of these has already cost a real debugging session. A directory with no user the
    /// login filter can match authenticates nobody, however healthy the pod looks; an LDAP bind with
    /// no password fails every login with a server error rather than a rejection; and a deployment
    /// with no administrator has nothing that can sign in to the web interface at all, because the
    /// recovery administrator is honoured only in recovery mode. EntKube owns the directory, the
    /// domains and the account list, so it can answer all of this before the install rather than
    /// leaving it to be discovered afterwards.</para>
    /// </summary>
    public async Task<List<MailPreflightIssue>> PreflightAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        List<MailPreflightIssue> issues = [];

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StalwartComponentConfig? config = await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
        if (config is null)
        {
            return issues;
        }

        List<StalwartMailDomain> domains = await db.StalwartMailDomains
            .Where(d => d.ConfigId == config.Id)
            .ToListAsync(ct);

        if (domains.Count == 0)
        {
            issues.Add(new(true,
                "No mail domains are configured.",
                "Add at least one domain on the Domains tab. The primary domain is what Stalwart "
                + "appends to a login that arrives without one."));
            return issues;
        }

        StalwartMailDomain primary = domains.FirstOrDefault(d => d.IsPrimary) ?? domains[0];
        (string LocalPart, string Domain, string Address)? admin = ResolveAdminIdentity(config, domains);
        string adminName = admin?.LocalPart ?? "";
        string adminAddress = admin?.Address ?? "";

        if (string.IsNullOrWhiteSpace(config.AdminUsername))
        {
            issues.Add(new(true,
                "No administrator name is set, so no account will carry the Admin role.",
                "Set an administrator on the Configuration tab. Nothing can sign in to the web "
                + "interface without one."));
        }
        else if (admin is null)
        {
            issues.Add(new(true,
                $"The administrator {config.AdminUsername.Trim()} is in a domain this server does not serve.",
                "Use a local part (it goes in the primary domain, "
                + $"{primary.Name}) or a full address in one of: {string.Join(", ", domains.Select(d => d.Name))}."));
        }

        if (config.HighAvailability)
        {
            if (config.CnpgDatabaseId is null)
            {
                issues.Add(new(true,
                    "High availability is on but no shared database is selected.",
                    "Pick a CNPG PostgreSQL database on the Configuration tab. In HA the datastore "
                    + "cannot be the single node's local disk — every node reads it."));
            }
            if (config.BlobStorageLinkId is null)
            {
                issues.Add(new(true,
                    "High availability is on but no shared blob store is selected.",
                    "Pick an S3 storage link. Message bodies must live where every node can reach "
                    + "them; a local volume would strand them on one node."));
            }
            if (string.IsNullOrWhiteSpace(config.CoordinatorRedisHost))
            {
                issues.Add(new(true,
                    "High availability is on but no coordinator is configured.",
                    "Set the coordinator Redis host. The nodes share state and stay in step through "
                    + "it; without one they cannot form a cluster."));
            }
            if (config.Replicas < 2)
            {
                issues.Add(new(false,
                    "High availability is on but only one replica is requested.",
                    "Set at least two replicas, or turn HA off — a one-node HA deployment carries "
                    + "the cost of shared backends with none of the redundancy."));
            }
        }

        switch (config.AuthMode)
        {
            case StalwartAuthMode.Internal:
                if (await Secret(tenantId, clusterComponentId, StalwartManifestBuilder.AdminPasswordSecretName, ct) is null)
                {
                    issues.Add(new(true,
                        "Authentication is internal but no administrator password is stored.",
                        "Set an administrator password on the Configuration tab; it becomes the "
                        + $"credential for {adminAddress}."));
                }
                break;

            case StalwartAuthMode.Ldap:
                await CheckLdapAsync(db, config, domains, primary, adminName, adminAddress,
                    tenantId, clusterComponentId, issues, ct);
                break;

            case StalwartAuthMode.Oidc:
                if (string.IsNullOrWhiteSpace(config.OidcIssuerUrl))
                {
                    issues.Add(new(true,
                        "Authentication is OIDC but no realm URL is set.",
                        "Set the Keycloak realm URL on the Configuration tab."));
                }
                if (!await db.StalwartMailAccounts.AnyAsync(a => a.ConfigId == config.Id, ct))
                {
                    issues.Add(new(true,
                        "OIDC authentication cannot discover accounts, and none are defined.",
                        "Add mailboxes on the Mailboxes tab, or import them from the linked "
                        + "directory. A valid token for an account that does not exist is refused."));
                }
                break;
        }

        return issues;
    }

    /// <summary>
    /// The LDAP-specific half of <see cref="PreflightAsync"/>: can Stalwart bind, is there a user
    /// the login filter can match, and is one of them the administrator.
    ///
    /// <para>Two sources, honestly labelled. EntKube's own authored user list is cheap but only
    /// knows entries EntKube wrote — a directory populated with phpLDAPadmin or <c>ldapadd</c>
    /// looks empty to it, and treating that as "nobody can log in" blocked a perfectly good apply.
    /// So the authored list only ever warns. The check that can actually <em>block</em> is the live
    /// one: an <c>ldapsearch</c> inside the OpenLDAP pod using the same bind DN, password, base DN
    /// and login filter Stalwart will use. If the bind is rejected there, it will be rejected by
    /// Stalwart, and that is the "temporary server failure" that started all of this.</para>
    ///
    /// <para>The live check fails open. It depends on <c>ldapsearch</c> existing in the pod and on
    /// exec working; when either does not, it says it could not verify and lets the apply proceed.
    /// A check that cannot run must not masquerade as a check that failed.</para>
    /// </summary>
    private async Task CheckLdapAsync(
        ApplicationDbContext db,
        StalwartComponentConfig config,
        List<StalwartMailDomain> domains,
        StalwartMailDomain primary,
        string adminName,
        string adminAddress,
        Guid tenantId,
        Guid clusterComponentId,
        List<MailPreflightIssue> issues,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.LdapUrl) || string.IsNullOrWhiteSpace(config.LdapBaseDn))
        {
            issues.Add(new(true,
                "Authentication is LDAP but the directory connection is incomplete.",
                "Link an EntKube-managed directory on the Configuration tab, or fill in the URL "
                + "and base DN by hand."));
            return;
        }

        string? bindPassword = await Secret(
            tenantId, clusterComponentId, StalwartManifestBuilder.LdapBindPasswordSecretName, ct);
        bool hasBindDn = !string.IsNullOrWhiteSpace(config.LdapBindDn);

        if (hasBindDn && bindPassword is null)
        {
            issues.Add(new(true,
                "A bind DN is set but no bind password is stored, so every bind will fail.",
                "Set the bind password on the Configuration tab. Without it the server answers "
                + "every login with a temporary failure rather than rejecting the credentials, "
                + "which reads like the server being broken rather than unconfigured."));
        }

        HashSet<string> served = domains.Select(d => d.Name.Trim().ToLowerInvariant()).ToHashSet();

        // ── The directory itself, asked the way Stalwart will ask it. ──
        LiveLdapResult live = await QueryLiveDirectoryAsync(db, config, bindPassword, ct);

        switch (live.Outcome)
        {
            case LiveLdapOutcome.NotRun:
                issues.Add(new(false,
                    "The live directory could not be checked from here.",
                    live.Detail + " Logins will succeed only if the directory holds an inetOrgPerson "
                    + $"entry whose mail attribute is {adminAddress}."));

                // Only now is the authored list worth mentioning — as the fallback it is, and only
                // when it is empty. Shown alongside a successful live check it read as an error
                // about a directory that was fine, because the users had been created outside
                // EntKube and that is the normal case, not a defect.
                await NoteAuthoredListIfEmptyAsync(db, config, served, issues, ct);
                return;

            case LiveLdapOutcome.BindRejected:
                // This is certain: the same DN and password, rejected by the same server.
                issues.Add(new(true,
                    "The directory rejected the bind DN and password Stalwart will use.",
                    $"Check the bind DN ({config.LdapBindDn}) and the bind password on the "
                    + "Configuration tab. Until they are accepted, Stalwart answers every login with "
                    + "a temporary server failure. Detail: " + live.Detail));
                return;

            case LiveLdapOutcome.Ok:
                break;
        }

        List<string> liveUsers = live.Addresses.Where(a => InServedDomain(a, served)).ToList();

        if (liveUsers.Count == 0)
        {
            // The bind worked and the operator's own login filter matched nothing in the served
            // domains. That is what Stalwart will see, so nobody can log in.
            issues.Add(new(true,
                "The directory holds no entry the login filter can match in these domains, so "
                + "nobody can log in.",
                $"The filter is {config.LdapLoginFilter}. Add an inetOrgPerson entry with a mail "
                + $"attribute in {string.Join(" or ", served.Select(d => "@" + d))}. The directory "
                + "administrator (the root DN) does not count: it is not an inetOrgPerson and has no "
                + "mail attribute."));
            return;
        }

        // Accurate framing, learned the hard way: a weak scheme is a SECURITY concern, not a
        // functional one — slapd verifies {MD5} at bind time perfectly well (a wrong DN, not the
        // hash, was the login bug). So this never blocks; it advises.
        List<string> weak = live.WeakSchemeAddresses.Where(a => InServedDomain(a, served)).ToList();
        if (weak.Count > 0)
        {
            issues.Add(new(false,
                $"{weak.Count} mailbox user(s) have a weak password hash: {string.Join(", ", weak.Take(5))}"
                + (weak.Count > 5 ? ", …" : "") + ".",
                "These accounts store their password as unsalted MD5 or SHA — logins still work, but the "
                + "hash is weak. phpLDAPadmin defaults to MD5; EntKube's own Directory (LDAP) tab uses "
                + "salted SHA-1 (SSHA). Reset the password there, or with ldappasswd, to upgrade it."));
        }

        if (adminName.Length > 0 && !liveUsers.Contains(adminAddress))
        {
            issues.Add(new(true,
                $"No directory entry has the address {adminAddress}, so the administrator cannot log in.",
                "Either add an entry with that email address, or change the administrator name to "
                + $"one that exists: {string.Join(", ", liveUsers.Take(5))}."));
        }
    }

    /// <summary>
    /// The advisory note about EntKube's own authored user list, added only when that list is
    /// empty. Never blocking: users created with phpLDAPadmin or ldapadd are invisible to it,
    /// and a directory populated that way is working, not broken.
    /// </summary>
    private static async Task NoteAuthoredListIfEmptyAsync(
        ApplicationDbContext db, StalwartComponentConfig config, HashSet<string> served,
        List<MailPreflightIssue> issues, CancellationToken ct)
    {
        if (config.OpenLdapConfigId is not Guid ldapConfigId)
        {
            return;
        }

        bool anyAuthored = (await db.OpenLdapUsers
                .Where(u => u.ConfigId == ldapConfigId && u.Email != null)
                .Select(u => u.Email!)
                .ToListAsync(ct))
            .Any(e => InServedDomain(e.Trim().ToLowerInvariant(), served));

        if (!anyAuthored)
        {
            issues.Add(new(false,
                "EntKube's own directory list has no user with an email address in these domains.",
                "This only sees users authored on the Directory (LDAP) tab; users created with "
                + "phpLDAPadmin or ldapadd are invisible to it. If the directory really is empty, add "
                + "users on that tab with an address in " + string.Join(" or ", served.Select(d => "@" + d)) + "."));
        }
    }

    private static bool InServedDomain(string address, HashSet<string> served) =>
        address.Contains('@') && served.Contains(address[(address.IndexOf('@') + 1)..]);

    private enum LiveLdapOutcome { NotRun, BindRejected, Ok }

    private sealed record LiveLdapResult(
        LiveLdapOutcome Outcome, List<string> Addresses, List<string> WeakSchemeAddresses, string Detail);

    /// <summary>
    /// Runs the operator's login filter against the live directory, inside the OpenLDAP pod, as the
    /// bind identity Stalwart will use. Only possible for an EntKube-managed directory (it needs
    /// the pod); otherwise reports NotRun with the reason.
    /// </summary>
    private async Task<LiveLdapResult> QueryLiveDirectoryAsync(
        ApplicationDbContext db, StalwartComponentConfig config, string? bindPassword, CancellationToken ct)
    {
        static LiveLdapResult NotRun(string why) => new(LiveLdapOutcome.NotRun, [], [], why);

        if (config.OpenLdapConfigId is not Guid ldapConfigId)
        {
            return NotRun("The directory is not one EntKube manages, so there is no pod to query it from.");
        }

        OpenLdapComponentConfig? ldap = await db.OpenLdapComponentConfigs
            .FirstOrDefaultAsync(c => c.Id == ldapConfigId, ct);
        ClusterComponent? ldapComponent = ldap?.ClusterComponentId is Guid id
            ? await db.ClusterComponents.Include(c => c.Cluster).FirstOrDefaultAsync(c => c.Id == id, ct)
            : null;

        if (ldap is null || ldapComponent?.Cluster?.Kubeconfig is not { Length: > 0 } kubeconfig)
        {
            return NotRun("The linked directory's component or cluster kubeconfig could not be found.");
        }

        string release = ldapComponent.ReleaseName ?? ldapComponent.Name;
        string ns = ldapComponent.Namespace ?? "openldap";
        string pod = $"{release}-0";
        // The chart names its container after itself; kubectl would default to it anyway, but
        // saying so keeps a "Defaulted container" notice from leading every error message.
        string? container = string.IsNullOrWhiteSpace(ldapComponent.HelmChartName) ? null : ldapComponent.HelmChartName;

        List<string> command = BuildLdapSearchCommand(config);

        string output;
        try
        {
            output = await k8sFactory.RunCommandOnPodWithStdinAsync(
                pod, ns, command, bindPassword ?? "", kubeconfig, ct, container);
        }
        catch (Exception ex)
        {
            string message = SummariseExecError(ex.Message);
            if (message.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase)
                || message.Contains("(49)", StringComparison.Ordinal))
            {
                return new LiveLdapResult(LiveLdapOutcome.BindRejected, [], [], message);
            }
            logger.LogInformation(ex, "Live LDAP preflight could not run in {Pod}/{Namespace}.", pod, ns);
            return NotRun($"Running ldapsearch in {ns}/{pod} failed: {message}");
        }

        List<DirectoryEntry> entries = ParseDirectoryEntries(output);
        return new LiveLdapResult(
            LiveLdapOutcome.Ok,
            entries.Where(e => e.Mail is not null).Select(e => e.Mail!).ToList(),
            entries.Where(e => e.Mail is not null && IsWeakScheme(e.PasswordScheme)).Select(e => e.Mail!).ToList(),
            "");
    }

    /// <summary>
    /// The ldapsearch invocation, built from the exact connection Stalwart is configured with.
    ///
    /// <para>The URL is <see cref="StalwartComponentConfig.LdapUrl"/> verbatim, not
    /// <c>localhost</c>. The first version queried <c>localhost:389</c> from inside the pod, and 389
    /// is the Service port — the container listens on 1389, so the check failed with "can't contact
    /// LDAP server" against a directory that was working perfectly. Using the Service URL from
    /// inside the pod tests the same path Stalwart takes and makes the container port irrelevant.</para>
    /// </summary>
    public static List<string> BuildLdapSearchCommand(StalwartComponentConfig config)
    {
        // The login filter with its login placeholder widened to "anything": every entry Stalwart
        // could ever resolve a login to.
        string filter = (config.LdapLoginFilter ?? "(&(objectClass=inetOrgPerson)(mail=?))").Replace("?", "*");

        List<string> command =
        [
            "ldapsearch", "-x", "-LLL",
            "-H", config.LdapUrl!.Trim(),
            "-b", config.LdapBaseDn!.Trim(),
        ];
        if (!string.IsNullOrWhiteSpace(config.LdapBindDn))
        {
            // -y reads the password from a file, verbatim — so it arrives on stdin with no trailing
            // newline, and never on the command line where anyone who can read pods would see it.
            command.AddRange(["-D", config.LdapBindDn!.Trim(), "-y", "/dev/stdin"]);
        }
        // userPassword is requested so a weak hash can be flagged. The rootdn bind account can read
        // it; a non-privileged bind account gets nothing back and simply produces no warning.
        command.AddRange([filter, "mail", "userPassword"]);
        return command;
    }

    /// <summary>
    /// The lines of a kubectl exec failure worth showing: kubectl's own "Defaulted container …"
    /// notice comes first on stderr and says nothing about what failed, so the first version of
    /// this — which kept only the first line — reported the notice and discarded the actual error.
    /// </summary>
    public static string SummariseExecError(string message)
    {
        // The notice is not always on its own line: the exec wrapper prefixes "kubectl exec failed
        // (exit N): " to stderr, so it can lead a line that also carries the exit code. Strip the
        // notice wherever it sits and keep whatever else that line said.
        string stripped = System.Text.RegularExpressions.Regex.Replace(
            message, @"Defaulted container ""[^""]*"" out of: [^\r\n]*?\.\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        List<string> lines = stripped.Split('\n')
            .Select(l => l.Trim().TrimEnd(':').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return lines.Count == 0 ? message.Trim() : string.Join(" | ", lines.Take(3));
    }

    /// <summary>One parsed directory entry: its mail address and the scheme of its userPassword.</summary>
    public sealed record DirectoryEntry(string? Mail, string? PasswordScheme);

    /// <summary>Password schemes weak enough to be worth flagging: unsalted, or cleartext.</summary>
    private static readonly HashSet<string> WeakSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "MD5", "SMD5", "SHA", "" };

    /// <summary>True when a userPassword scheme is one worth advising an upgrade from. Null (no
    /// userPassword returned, e.g. a non-privileged bind account) is not weak — it is unknown.</summary>
    private static bool IsWeakScheme(string? scheme) => scheme is not null && WeakSchemes.Contains(scheme);

    /// <summary>
    /// Parses LDIF into entries, reading each entry's mail and the scheme prefix of its userPassword.
    ///
    /// <para>LDIF is folded (continuation lines start with a space) and a non-ASCII or binary value
    /// is base64-encoded behind a <c>::</c> marker — a userPassword hash is almost always the latter.
    /// Both are handled, because a value read half-unfolded would misreport the scheme.</para>
    /// </summary>
    public static List<DirectoryEntry> ParseDirectoryEntries(string ldif)
    {
        // Unfold: a line beginning with a single space continues the previous one.
        List<string> unfolded = [];
        foreach (string raw in ldif.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(' ') && unfolded.Count > 0)
            {
                unfolded[^1] += line[1..];
            }
            else
            {
                unfolded.Add(line);
            }
        }

        List<DirectoryEntry> entries = [];
        string? mail = null;
        string? scheme = null;
        bool inEntry = false;

        void Flush()
        {
            if (inEntry)
            {
                entries.Add(new DirectoryEntry(mail, scheme));
            }
            mail = null;
            scheme = null;
            inEntry = false;
        }

        foreach (string line in unfolded)
        {
            if (line.StartsWith("dn:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                inEntry = true;
            }
            else if (line.Length == 0)
            {
                Flush();
            }
            else if (mail is null && line.StartsWith("mail:", StringComparison.OrdinalIgnoreCase))
            {
                mail = ReadValue(line, "mail").ToLowerInvariant();
            }
            else if (scheme is null && line.StartsWith("userPassword:", StringComparison.OrdinalIgnoreCase))
            {
                string value = ReadValue(line, "userPassword");
                int close = value.StartsWith('{') ? value.IndexOf('}') : -1;
                scheme = close > 0 ? value[1..close] : "";
            }
        }
        Flush();

        return entries;
    }

    /// <summary>Reads an LDIF attribute value, decoding a <c>::</c> base64 form.</summary>
    private static string ReadValue(string line, string attr)
    {
        if (line.StartsWith(attr + "::", StringComparison.OrdinalIgnoreCase))
        {
            string b64 = line[(attr.Length + 2)..].Trim();
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch (FormatException) { return ""; }
        }
        return line[(attr.Length + 1)..].Trim();
    }

    /// <summary>Extracts every <c>mail:</c> value from LDIF output, lower-cased.</summary>
    public static List<string> ParseMailAttributes(string ldif)
    {
        List<string> addresses = [];
        foreach (string raw in ldif.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("mail:", StringComparison.OrdinalIgnoreCase))
            {
                // "mail:: <base64>" is LDIF for a non-ASCII value; a plain address is never encoded.
                string value = line.StartsWith("mail::", StringComparison.OrdinalIgnoreCase)
                    ? DecodeBase64OrEmpty(line["mail::".Length..].Trim())
                    : line["mail:".Length..].Trim();
                if (value.Length > 0)
                {
                    addresses.Add(value.ToLowerInvariant());
                }
            }
        }
        return addresses;
    }

    private static string DecodeBase64OrEmpty(string text)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch (FormatException) { return ""; }
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converges the running server onto the authored configuration: switch into recovery mode,
    /// replay the apply plan as a Job, switch back. Returns the combined output of every step.
    ///
    /// <para>The two mode switches are what makes this work at all — the recovery administrator is
    /// the only credential that exists before a directory does, and it is honoured only in recovery
    /// mode. They also mean the mail listeners are down for the duration, which is why this is a
    /// deliberate operator action rather than something that runs on save.</para>
    /// </summary>
    /// <param name="clearBlockedIps">
    /// Also remove every IP Stalwart has banned. An explicit operator choice, never a side effect:
    /// it lifts bans on real attackers along with a wrongly-banned gateway.
    /// </param>
    /// <param name="verboseLogging">
    /// Set the server's tracer to debug for this apply only, so a silent rejection explains itself.
    /// </param>
    public async Task<HelmExecutionResult> ApplyConfigurationAsync(
        Guid tenantId, Guid clusterComponentId, bool clearBlockedIps = false, bool verboseLogging = false,
        CancellationToken ct = default)
    {
        StalwartComponentConfig? config;
        List<StalwartMailDomain> domains;
        List<StalwartMailAccount> accounts;
        List<ClusterComponent> clusterComponents;
        string releaseName;
        string ns;
        string kubeconfig;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            config = await db.StalwartComponentConfigs
                .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
            if (config is null)
            {
                return Failure("This Stalwart component has not been configured yet.");
            }

            ClusterComponent? component = await db.ClusterComponents
                .Include(c => c.Cluster)
                .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
            if (component is null)
            {
                return Failure("Component not found.");
            }
            if (component.Cluster?.Kubeconfig is not { Length: > 0 } kc)
            {
                return Failure("The cluster has no kubeconfig, so the configuration cannot be applied.");
            }
            if (component.Status != ComponentStatus.Installed)
            {
                // The plan is replayed against the running server by a Job that reads the
                // administrator password out of the component's Secret. Neither exists until the
                // component has been installed, and the Job's failure would say so far less clearly.
                return Failure(
                    "Install the Stalwart component first — there is no running server to apply this to yet. "
                    + "Use \u201cRe-deploy pod\u201d, or install it from the cluster's Components tab.");
            }

            kubeconfig = kc;
            releaseName = component.ReleaseName ?? component.Name;
            ns = component.Namespace ?? DefaultNamespace;

            domains = await db.StalwartMailDomains
                .Where(d => d.ConfigId == config.Id)
                .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Name)
                .ToListAsync(ct);
            accounts = await db.StalwartMailAccounts
                .Where(a => a.ConfigId == config.Id)
                .OrderBy(a => a.LocalPart)
                .ToListAsync(ct);

            // Needed to find the ingress gateway whose pods must never be banned.
            clusterComponents = await db.ClusterComponents
                .Where(c => c.ClusterId == component.ClusterId)
                .ToListAsync(ct);
        }

        // Refuse to converge onto a configuration that cannot work. Applying restarts the server
        // twice, so finding out afterwards costs an outage as well as the debugging.
        List<MailPreflightIssue> blocking =
            (await PreflightAsync(tenantId, clusterComponentId, ct)).Where(i => i.IsBlocking).ToList();

        if (blocking.Count > 0)
        {
            return Failure(
                "This configuration would produce a mail server nobody can log in to:\n\n"
                + string.Join("\n\n", blocking.Select(i => $"• {i.Problem}\n  {i.Remedy}")));
        }

        // Only Internal mode carries a password into the plan; the other modes authenticate against
        // the directory and an internal copy would only drift from it.
        string? adminPassword = config.AuthMode == StalwartAuthMode.Internal
            ? await Secret(tenantId, clusterComponentId, StalwartManifestBuilder.AdminPasswordSecretName, ct)
            : null;

        // The gateway's pod addresses are what Stalwart sees as the TCP peer for every web request.
        // Resolved fresh on every apply because they change whenever the gateway restarts.
        List<string> proxyAddresses = await ResolveGatewayAddressesAsync(clusterComponents, kubeconfig, ct);

        StalwartPlanBuilder.StalwartHaBackend? ha =
            await BuildHaBackendAsync(tenantId, clusterComponentId, config, releaseName, ns, ct);

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, domains, accounts, adminPassword, proxyAddresses, clearBlockedIps, verboseLogging, ha);
        List<string> output = [];

        output.Add(proxyAddresses.Count > 0
            ? $"Allow-listing ingress gateway addresses so they can never be banned: {string.Join(", ", proxyAddresses)}"
            : "WARNING: no ingress gateway pods were found to allow-list. The gateway can still be banned "
              + "by repeated failed logins if a request arrives without a forwarding header.");
        if (clearBlockedIps)
        {
            output.Add("Clearing every blocked IP address.");
        }
        if (verboseLogging)
        {
            output.Add("Server logging set to debug for this apply; the next apply restores info.");
        }

        try
        {
            // The recovery pod reads its identity from the Kubernetes Secret at creation, and the
            // CLI reads the password from the same Secret. Both come from the vault — but the vault
            // is only mirrored into the cluster on install, so an administrator renamed in the Mail
            // tab existed in the vault and not yet on the cluster. The recovery pod then carried the
            // old name, the CLI sent the new one, the fallback never matched, and the login fell
            // through to LDAP with the wrong password: a 401 that took a day to read. Mirror first,
            // then prove the two halves agree, before anything restarts.
            output.Add("--- Syncing credentials to the cluster ---");
            HelmExecutionResult sync = await lifecycleService.SyncComponentSecretsAsync(clusterComponentId, ct);
            if (!string.IsNullOrWhiteSpace(sync.Output))
            {
                output.Add(sync.Output.Trim());
            }

            string credentialsSecret = $"{releaseName}{StalwartManifestBuilder.CredentialsSecretSuffix}";
            string? recovery = await k8sFactory.GetSecretValueAsync(
                credentialsSecret, StalwartManifestBuilder.RecoveryAdminSecretName, ns, kubeconfig, ct);
            string? cliPassword = await k8sFactory.GetSecretValueAsync(
                credentialsSecret, StalwartManifestBuilder.AdminPasswordSecretName, ns, kubeconfig, ct);

            string? mismatch = DescribeCredentialMismatch(config.AdminUsername, recovery, cliPassword);
            if (mismatch is not null)
            {
                return Failure(string.Join("\n", output)
                    + "\nRefusing to restart the server: the recovery credential and the CLI credential on "
                    + "the cluster do not agree, so the apply would fail with 401 after two restarts.\n"
                    + mismatch
                    + "\nSave the configuration again (re-enter the administrator password if in doubt) "
                    + "and retry.");
            }
            output.Add($"Recovery identity on the cluster matches the CLI identity ({config.AdminUsername}).");

            output.Add("--- Entering recovery mode ---");
            await k8sFactory.ApplyManifestAsync(
                StalwartManifestBuilder.Build(config, releaseName, ns, recoveryMode: true, ha: ha), kubeconfig, ct);
            if (!await WaitForRolloutAsync(releaseName, ns, kubeconfig, RolloutTimeout, ct))
            {
                return Failure(string.Join("\n", output)
                    + $"\nThe {releaseName} StatefulSet did not become ready in recovery mode within "
                    + $"{RolloutTimeout.TotalMinutes:0} minutes. Nothing was applied; the server is still in "
                    + "recovery mode and is not accepting mail — check the pod's events and logs, then retry.");
            }

            output.Add("--- Applying configuration ---");
            (bool applied, string applyLog) =
                await RunApplyJobAsync(config, releaseName, ns, kubeconfig, plan, ct);
            output.Add(applyLog);

            output.Add("--- Leaving recovery mode ---");
            await k8sFactory.ApplyManifestAsync(
                StalwartManifestBuilder.Build(config, releaseName, ns, ha: ha), kubeconfig, ct);
            bool back = await WaitForRolloutAsync(releaseName, ns, kubeconfig, RolloutTimeout, ct);
            if (!back)
            {
                output.Add(
                    $"WARNING: the {releaseName} StatefulSet has not become ready again yet. Recovery mode has "
                    + "been switched off, so it should return on its own; check the pod if mail stays down.");
            }

            if (!applied)
            {
                return Failure(string.Join("\n", output));
            }

            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                StalwartComponentConfig? stored = await db.StalwartComponentConfigs
                    .FirstOrDefaultAsync(c => c.Id == config.Id, ct);
                if (stored is not null)
                {
                    stored.LastAppliedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            return new HelmExecutionResult { Success = back, Output = string.Join("\n", output) };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying Stalwart configuration for component {ComponentId} failed.", clusterComponentId);
            output.Add($"ERROR: {ex.Message}");
            output.Add(
                "The server may still be in recovery mode and therefore not accepting mail. Re-run "
                + "\"Apply configuration\" once the underlying problem is fixed, or re-apply the component "
                + "from the Components tab to bring it straight back up.");
            return Failure(string.Join("\n", output));
        }
    }

    /// <summary>
    /// The pod addresses of the cluster's ingress gateway — every pod in the gateway's namespace,
    /// which is entirely ingress infrastructure. These are the TCP peers Stalwart sees behind the
    /// gateway, and the addresses that must be exempt from auto-ban: banning one bans every user.
    /// Returns an empty list rather than throwing, so an unusual cluster still gets an apply, with
    /// the gap called out in the output.
    /// </summary>
    private async Task<List<string>> ResolveGatewayAddressesAsync(
        IReadOnlyList<ClusterComponent> components, string kubeconfig, CancellationToken ct)
    {
        (_, string gatewayNamespace) = ExternalRouteService.ResolveGateway(components);

        try
        {
            string json = await k8sFactory.GetJsonAsync("pods", gatewayNamespace, kubeconfig, ct: ct);
            using JsonDocument doc = JsonDocument.Parse(json);

            List<string> addresses = [];
            if (doc.RootElement.TryGetProperty("items", out JsonElement items))
            {
                foreach (JsonElement pod in items.EnumerateArray())
                {
                    if (pod.TryGetProperty("status", out JsonElement status)
                        && status.TryGetProperty("podIP", out JsonElement ip)
                        && ip.GetString() is { Length: > 0 } address)
                    {
                        addresses.Add(address);
                    }
                }
            }
            return addresses;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not list pods in gateway namespace {Namespace} to allow-list them against auto-ban.",
                gatewayNamespace);
            return [];
        }
    }

    /// <summary>
    /// Runs the CLI apply Job to completion and returns its log. Deletes any previous Job first —
    /// a Job's pod template is immutable, so a second apply would otherwise be rejected outright.
    /// </summary>
    private async Task<(bool Success, string Log)> RunApplyJobAsync(
        StalwartComponentConfig config, string releaseName, string ns, string kubeconfig,
        string plan, CancellationToken ct)
    {
        string jobName = StalwartManifestBuilder.ApplyJobName(releaseName);

        try
        {
            await k8sFactory.DeleteManifestAsync("job", jobName, ns, kubeconfig, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No previous {Job} Job to delete in {Namespace}.", jobName, ns);
        }

        await k8sFactory.ApplyManifestAsync(
            StalwartManifestBuilder.BuildApplyJobManifest(releaseName, ns, plan, config.AdminUsername),
            kubeconfig, ct);

        DateTime deadline = DateTime.UtcNow + ApplyTimeout;
        bool? succeeded = null;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            string json;
            try
            {
                json = await k8sFactory.GetJsonAsync($"job/{jobName}", ns, kubeconfig, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Waiting for the {Job} Job to appear in {Namespace}.", jobName, ns);
                continue;
            }

            (int completed, int failed) = ReadJobStatus(json);
            if (completed > 0)
            {
                succeeded = true;
                break;
            }
            if (failed > 0)
            {
                succeeded = false;
                break;
            }
        }

        string log = await ReadJobLogAsync(jobName, ns, kubeconfig, ct);

        return succeeded switch
        {
            true => (true, string.IsNullOrWhiteSpace(log) ? "Configuration applied." : log),
            false => (false, $"The apply job failed.\n{log}"),
            _ => (false,
                $"The apply job did not finish within {ApplyTimeout.TotalMinutes:0} minutes.\n{log}"),
        };
    }

    /// <summary>
    /// Compares the two credential copies the apply depends on, exactly the way Stalwart will:
    /// the username half of <c>STALWART_RECOVERY_ADMIN</c> against the CLI user, bare, byte for
    /// byte; the password half against <c>STALWART_ADMIN_PASSWORD</c>, byte for byte. Returns a
    /// description of the first disagreement, or null when they match. Never includes a password.
    /// </summary>
    public static string? DescribeCredentialMismatch(string adminUsername, string? recovery, string? cliPassword)
    {
        if (string.IsNullOrEmpty(recovery))
        {
            return $"• The Secret has no {StalwartManifestBuilder.RecoveryAdminSecretName} entry, so the "
                + "recovery pod would have no fallback administrator at all and every CLI login would go to "
                + "the directory.";
        }
        if (string.IsNullOrEmpty(cliPassword))
        {
            return $"• The Secret has no {StalwartManifestBuilder.AdminPasswordSecretName} entry, so the CLI "
                + "would send an empty password.";
        }

        int colon = recovery.IndexOf(':');
        if (colon <= 0)
        {
            return $"• {StalwartManifestBuilder.RecoveryAdminSecretName} is not in the form username:password.";
        }

        string recoveryUser = recovery[..colon];
        string recoveryPassword = recovery[(colon + 1)..];
        string expectedUser = (adminUsername ?? "").Trim();

        if (!string.Equals(recoveryUser, expectedUser, StringComparison.Ordinal))
        {
            return $"• The recovery administrator on the cluster is '{recoveryUser}' but the CLI will log in as "
                + $"'{expectedUser}'. Stalwart compares these bare and exactly, so the fallback would be "
                + "skipped and the login sent to the directory instead.";
        }
        if (!string.Equals(recoveryPassword, cliPassword, StringComparison.Ordinal))
        {
            string hint = recoveryPassword.TrimEnd('\n', '\r') == cliPassword.TrimEnd('\n', '\r')
                ? " They differ only by a trailing newline."
                : "";
            return "• The recovery password and the CLI password on the cluster are different values." + hint;
        }
        return null;
    }

    /// <summary>Reads succeeded/failed counts out of a Job's status JSON.</summary>
    public static (int Succeeded, int Failed) ReadJobStatus(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out JsonElement status))
            {
                return (0, 0);
            }

            int succeeded = status.TryGetProperty("succeeded", out JsonElement s) ? s.GetInt32() : 0;
            int failed = status.TryGetProperty("failed", out JsonElement f) ? f.GetInt32() : 0;
            return (succeeded, failed);
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

    private async Task<string> ReadJobLogAsync(string jobName, string ns, string kubeconfig, CancellationToken ct)
    {
        try
        {
            return (await k8sFactory.GetPodLogsAsync(
                $"job/{jobName}", ns, kubeconfig, tailLines: 200, ct: ct)).Trim();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read logs for the {Job} Job in {Namespace}.", jobName, ns);
            return "";
        }
    }

    /// <summary>Polls a StatefulSet until its single replica reports ready on the current revision.</summary>
    private async Task<bool> WaitForRolloutAsync(
        string releaseName, string ns, string kubeconfig, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            try
            {
                string json = await k8sFactory.GetJsonAsync($"statefulset/{releaseName}", ns, kubeconfig, ct: ct);
                if (IsStatefulSetReady(json))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Waiting for StatefulSet {Name} in {Namespace}…", releaseName, ns);
            }
        }
        return false;
    }

    /// <summary>
    /// True when every replica is both updated and ready. Checking the updated revision matters:
    /// the pod from before a mode switch is still ready for a few seconds afterwards, and treating
    /// that as success would run the apply against a server that has not restarted yet.
    /// </summary>
    public static bool IsStatefulSetReady(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out JsonElement status))
            {
                return false;
            }

            int replicas = doc.RootElement.TryGetProperty("spec", out JsonElement spec)
                           && spec.TryGetProperty("replicas", out JsonElement r)
                ? r.GetInt32()
                : 1;

            int ready = status.TryGetProperty("readyReplicas", out JsonElement rr) ? rr.GetInt32() : 0;
            int updated = status.TryGetProperty("updatedReplicas", out JsonElement ur) ? ur.GetInt32() : 0;

            string? current = status.TryGetProperty("currentRevision", out JsonElement cr) ? cr.GetString() : null;
            string? update = status.TryGetProperty("updateRevision", out JsonElement udr) ? udr.GetString() : null;

            return ready >= replicas && updated >= replicas && current == update;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HelmExecutionResult Failure(string message) => new() { Success = false, Output = message };

    // ── Domain CRUD ───────────────────────────────────────────────────────────

    public async Task<StalwartMailDomain> AddDomainAsync(
        Guid configId, string name, bool isPrimary, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool first = !await db.StalwartMailDomains.AnyAsync(d => d.ConfigId == configId, ct);

        StalwartMailDomain domain = new()
        {
            Id = Guid.NewGuid(),
            ConfigId = configId,
            Name = name.Trim().ToLowerInvariant(),
            // The first domain is always primary: it is what Stalwart appends to a bare login, so
            // a deployment without one refuses every client that does not send a full address.
            IsPrimary = isPrimary || first,
        };

        if (domain.IsPrimary)
        {
            await db.StalwartMailDomains
                .Where(d => d.ConfigId == configId && d.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPrimary, false), ct);
        }

        db.StalwartMailDomains.Add(domain);
        await db.SaveChangesAsync(ct);
        return domain;
    }

    public async Task UpdateDomainAsync(StalwartMailDomain domain, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (domain.IsPrimary)
        {
            await db.StalwartMailDomains
                .Where(d => d.ConfigId == domain.ConfigId && d.Id != domain.Id && d.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPrimary, false), ct);
        }

        db.StalwartMailDomains.Update(domain);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a domain from EntKube's authored state. Refuses while mailboxes still reference it —
    /// dropping the domain would leave those addresses pointing nowhere.
    ///
    /// <para>This does not delete anything from the running server: <c>Domain</c> is upserted, never
    /// reconciled, so mail already delivered to the domain stays where it is until an administrator
    /// removes it in Stalwart deliberately.</para>
    /// </summary>
    public async Task DeleteDomainAsync(Guid domainId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (await db.StalwartMailAccounts.AnyAsync(a => a.DomainId == domainId, ct))
        {
            throw new InvalidOperationException(
                "This domain still has mailboxes. Remove them first — a mailbox without its domain "
                + "has no address.");
        }

        await db.StalwartMailDomains.Where(d => d.Id == domainId).ExecuteDeleteAsync(ct);
    }

    // ── Mailbox CRUD ──────────────────────────────────────────────────────────

    public async Task<StalwartMailAccount> AddAccountAsync(
        StalwartMailAccount account, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        account.Id = Guid.NewGuid();
        account.LocalPart = account.LocalPart.Trim().ToLowerInvariant();
        db.StalwartMailAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task UpdateAccountAsync(StalwartMailAccount account, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        account.LocalPart = account.LocalPart.Trim().ToLowerInvariant();
        db.StalwartMailAccounts.Update(account);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a mailbox from EntKube's authored state. It is not deleted from the running server:
    /// accounts are only ever upserted, because a configuration change should never be able to
    /// destroy somebody's mail. Delete it in Stalwart's own admin UI when that is what is meant.
    /// </summary>
    public async Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.StalwartMailAccounts.Where(a => a.Id == accountId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Creates a mailbox for every LDAP user that has an email address in one of this server's
    /// domains and does not have one yet. The point is <see cref="StalwartAuthMode.Oidc"/>: tokens
    /// prove who somebody is but cannot conjure a mailbox, so an account that was never created is
    /// refused at first login. EntKube already holds the directory, so it can create them.
    ///
    /// <para>Returns how many were added. Existing mailboxes are left alone, and users whose
    /// address is in a domain this server does not serve are skipped.</para>
    /// </summary>
    public async Task<int> ImportMailboxesFromLdapAsync(Guid configId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        StalwartComponentConfig? config = await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
        if (config?.OpenLdapConfigId is not Guid ldapConfigId)
        {
            throw new InvalidOperationException(
                "This mail server is not linked to an EntKube-managed LDAP directory, so there is no "
                + "user list to import from.");
        }

        List<StalwartMailDomain> domains = await db.StalwartMailDomains
            .Where(d => d.ConfigId == configId)
            .ToListAsync(ct);
        Dictionary<string, Guid> domainIds = domains.ToDictionary(
            d => d.Name.ToLowerInvariant(), d => d.Id);

        HashSet<(Guid, string)> existing = (await db.StalwartMailAccounts
                .Where(a => a.ConfigId == configId)
                .Select(a => new { a.DomainId, a.LocalPart })
                .ToListAsync(ct))
            .Select(a => (a.DomainId, a.LocalPart.ToLowerInvariant()))
            .ToHashSet();

        List<OpenLdapUser> users = await db.OpenLdapUsers
            .Where(u => u.ConfigId == ldapConfigId && u.Email != null && !u.IsServiceAccount)
            .ToListAsync(ct);

        int added = 0;
        foreach (OpenLdapUser user in users)
        {
            string email = user.Email!.Trim().ToLowerInvariant();
            int at = email.IndexOf('@');
            if (at <= 0 || at == email.Length - 1)
            {
                continue;
            }

            string localPart = email[..at];
            if (!domainIds.TryGetValue(email[(at + 1)..], out Guid domainId)
                || existing.Contains((domainId, localPart)))
            {
                continue;
            }

            db.StalwartMailAccounts.Add(new StalwartMailAccount
            {
                Id = Guid.NewGuid(),
                ConfigId = configId,
                DomainId = domainId,
                LocalPart = localPart,
                DisplayName = user.DisplayName ?? user.Cn,
            });
            existing.Add((domainId, localPart));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Imported {Added} mailbox(es) into Stalwart config {ConfigId} from the linked LDAP directory.",
            added, configId);
        return added;
    }

    /// <summary>
    /// The EntKube-managed LDAP directories on the same cluster as this component — the candidates
    /// for the directory link, so an operator picks one rather than retyping its URL and base DN.
    /// </summary>
    /// <summary>The CNPG PostgreSQL databases on this component's cluster — candidates for the HA datastore.</summary>
    public async Task<List<CnpgDatabase>> GetCnpgDatabasesAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? component = await db.ClusterComponents.FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component is null)
        {
            return [];
        }
        return await db.CnpgDatabases
            .Include(d => d.CnpgCluster)
            .Where(d => d.CnpgCluster.TenantId == tenantId && d.CnpgCluster.KubernetesClusterId == component.ClusterId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
    }

    /// <summary>The tenant's S3 storage links — candidates for the HA blob store.</summary>
    public async Task<List<StorageLink>> GetStorageLinksAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.StorageLinks.Where(l => l.TenantId == tenantId).OrderBy(l => l.Name).ToListAsync(ct);
    }

    /// <summary>
    /// The Redis endpoints on this component's cluster — candidates for the HA coordinator. Same list
    /// the catalog's <see cref="FormFieldType.RedisSelector"/> shows: EntKube-managed clusters plus any
    /// other Service answering on the Redis port. Picking one means the operator never has to type the
    /// in-cluster host, and for a managed one its vaulted password is filled in automatically.
    /// </summary>
    public async Task<List<RedisEndpointOption>> GetCoordinatorRedisEndpointsAsync(
        Guid clusterComponentId, CancellationToken ct = default)
    {
        Guid clusterId;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            ClusterComponent? component = await db.ClusterComponents.FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
            if (component is null)
            {
                return [];
            }
            clusterId = component.ClusterId;
        }
        return await redisService.DiscoverEndpointsAsync(clusterId, ct);
    }

    /// <summary>
    /// The password for a chosen coordinator endpoint, when it is an EntKube-managed Redis (its
    /// credential lives in the vault). Returns null for an unmanaged endpoint — the caller then keeps
    /// whatever password was typed, or none.
    /// </summary>
    public async Task<string?> ResolveCoordinatorRedisPasswordAsync(
        Guid tenantId, RedisEndpointOption endpoint, CancellationToken ct = default)
    {
        if (!endpoint.Managed || endpoint.RedisClusterId is not Guid clusterId)
        {
            return null;
        }
        (string? password, _, _) = await redisService.GetCredentialsAsync(tenantId, clusterId, ct);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    public async Task<List<OpenLdapComponentConfig>> GetLinkableDirectoriesAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component is null)
        {
            return [];
        }

        List<Guid> ldapComponentIds = await db.ClusterComponents
            .Where(c => c.ClusterId == component.ClusterId && c.Name == OpenLdapService.CatalogKey)
            .Select(c => c.Id)
            .ToListAsync(ct);

        return await db.OpenLdapComponentConfigs
            .Where(c => c.TenantId == tenantId
                        && c.ClusterComponentId != null
                        && ldapComponentIds.Contains(c.ClusterComponentId!.Value))
            .OrderBy(c => c.BaseDn)
            .ToListAsync(ct);
    }

    // ── Supporting mail components ────────────────────────────────────────────
    //
    // rspamd and the two webmail clients have no configuration worth an entity of their own: what
    // they need is a handful of endpoints and credentials. Those are kept as component vault
    // secrets — the same thing headscale does — and rendered into a manifest here rather than
    // substituted into a static one, because several of the values are optional and a manifest
    // needs the corresponding lines to be absent, not empty. A blank password substituted into a
    // template is a password; a blank password rendered here is no password line at all.

    /// <summary>Regenerates the rspamd component's manifest from its stored settings.</summary>
    public async Task RefreshRspamdManifestAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        (ClusterComponent? component, string releaseName, string ns) =
            await ResolveComponentAsync(clusterComponentId, RspamdCatalogKey, ct);
        if (component is null)
        {
            return;
        }

        // If a realm on this cluster's Keycloak was chosen, the client is EntKube's to create and keep
        // correct — issuer, id, secret and redirect URI all land in the vault below as if they had been
        // typed. Runs before the settings are read, so the first apply is already configured.
        await ProvisionOidcFromRealmAsync(
            tenantId, clusterComponentId,
            realmSecret: "RSPAMD_OIDC_REALM",
            hostnameSecret: "RSPAMD_HOSTNAME",
            clientIdSuffix: releaseName,
            redirectPath: "/oauth2/callback",
            issuerSecret: "RSPAMD_OIDC_ISSUER",
            clientIdSecret: "RSPAMD_OIDC_CLIENT_ID",
            clientSecretName: RspamdManifestBuilder.SsoClientSecretName, ct);

        // A stored app registration (Entra, …) fills the same three OIDC secrets the realm path does.
        await ProvisionOidcFromRegistrationAsync(
            tenantId, clusterComponentId,
            registrationSecret: "RSPAMD_OIDC_APP_REGISTRATION",
            issuerSecret: "RSPAMD_OIDC_ISSUER",
            clientIdSecret: "RSPAMD_OIDC_CLIENT_ID",
            clientSecretName: RspamdManifestBuilder.SsoClientSecretName, ct);

        RspamdSettings settings = new(
            RedisServers: await SecretOr(tenantId, clusterComponentId, "RSPAMD_REDIS_SERVERS",
                $"redis.redis.svc.cluster.local:6379", ct),
            RedisPassword: await Secret(tenantId, clusterComponentId, "RSPAMD_REDIS_PASSWORD", ct),
            ControllerPassword: await Secret(tenantId, clusterComponentId, "RSPAMD_CONTROLLER_PASSWORD", ct),
            StorageSize: await SecretOr(tenantId, clusterComponentId, "RSPAMD_STORAGE_SIZE", "2Gi", ct),
            StorageClass: await Secret(tenantId, clusterComponentId, "RSPAMD_STORAGE_CLASS", ct),
            SsoIssuerUrl: await Secret(tenantId, clusterComponentId, "RSPAMD_OIDC_ISSUER", ct),
            SsoClientId: await Secret(tenantId, clusterComponentId, "RSPAMD_OIDC_CLIENT_ID", ct),
            SsoEmailDomain: await SecretOr(tenantId, clusterComponentId, "RSPAMD_OIDC_EMAIL_DOMAIN", "*", ct),
            // The redirect URL is built from this, so single sign-on is only on for a UI that is
            // actually published — an unpublished one has no address to come back to.
            WebUiHostname: await Secret(tenantId, clusterComponentId, "RSPAMD_HOSTNAME", ct));

        if (settings.SsoEnabled)
        {
            await EnsureRspamdSsoSecretsAsync(tenantId, clusterComponentId, releaseName, ns, ct);
        }

        await StoreManifestAsync(
            clusterComponentId, RspamdManifestBuilder.Build(settings, releaseName, ns), ct);

        await EnsureSimpleRouteAsync(
            tenantId, clusterComponentId, releaseName,
            hostnameSecret: "RSPAMD_HOSTNAME", issuerSecret: "RSPAMD_CLUSTER_ISSUER",
            // With single sign-on on, the route must land on the proxy. Routing to the controller would
            // publish the UI with the proxy sitting beside it authenticating nobody.
            servicePort: settings.SsoEnabled
                ? RspamdManifestBuilder.SsoProxyPort
                : RspamdManifestBuilder.ControllerPort, ct);
    }

    /// <summary>
    /// Makes sure the OIDC proxy's two credentials exist in the Secret its container names.
    ///
    /// <para>The client secret is the operator's, re-stored so it syncs into the Secret for THIS release —
    /// which the catalog cannot know, because the release may have been renamed. The cookie key is ours:
    /// it signs the proxy's session cookies, nobody needs to see it, and oauth2-proxy requires exactly
    /// 16, 24 or 32 bytes, so it is minted once and kept rather than asked for.</para>
    /// </summary>
    private async Task EnsureRspamdSsoSecretsAsync(
        Guid tenantId, Guid clusterComponentId, string releaseName, string ns, CancellationToken ct)
    {
        string k8sSecretName = $"{releaseName}-credentials";

        string? clientSecret = await Secret(
            tenantId, clusterComponentId, RspamdManifestBuilder.SsoClientSecretName, ct);
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, RspamdManifestBuilder.SsoClientSecretName, clientSecret!, ct,
                k8sSecretName: k8sSecretName, k8sNamespace: ns);
        }

        string cookieSecret =
            await Secret(tenantId, clusterComponentId, RspamdManifestBuilder.SsoCookieSecretName, ct)
            // 32 raw bytes, base64url — the length oauth2-proxy accepts, in the encoding it accepts.
            ?? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await vaultService.SetComponentSecretAsync(
            tenantId, clusterComponentId, RspamdManifestBuilder.SsoCookieSecretName, cookieSecret, ct,
            k8sSecretName: k8sSecretName, k8sNamespace: ns);
    }

    /// <summary>The realm's issuer URL, or null when it cannot be resolved — a realm deleted since it was
    /// chosen must not blank out an issuer that is still working.</summary>
    private async Task<string?> ResolveRealmIssuerAsync(Guid tenantId, Guid realmId, CancellationToken ct)
    {
        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            var realm = await db.KeycloakRealms
                .Include(r => r.ComponentConfig)
                .Where(r => r.Id == realmId && r.TenantId == tenantId)
                .Select(r => new { r.RealmName, r.ComponentConfig.AdminUrl })
                .FirstOrDefaultAsync(ct);

            return realm is null || string.IsNullOrWhiteSpace(realm.AdminUrl)
                ? null
                : $"{realm.AdminUrl!.TrimEnd('/')}/realms/{realm.RealmName}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the issuer URL for realm {RealmId}.", realmId);
            return null;
        }
    }

    /// <summary>
    /// Turns "use the Keycloak in this cluster" into the three values a component actually needs.
    ///
    /// <para>The manual fields stay exactly as they are and keep working — an identity provider EntKube
    /// does not manage is a perfectly ordinary thing to have. This is only what happens when a realm on
    /// this cluster is picked instead: the client is created if it is missing, its redirect URI is kept in
    /// step with the component's hostname, and the issuer, client id and secret are written into the same
    /// vault secrets an operator would otherwise have pasted them into. Nothing downstream can tell the
    /// difference, which is the point — the manifest builders do not know Keycloak exists.</para>
    ///
    /// <para>Silent when the component has no hostname yet: the redirect URI is built from it, and
    /// registering a client that redirects nowhere would be worse than leaving SSO off.</para>
    /// </summary>
    private async Task ProvisionOidcFromRealmAsync(
        Guid tenantId, Guid clusterComponentId, string realmSecret, string hostnameSecret,
        string clientIdSuffix, string redirectPath, string issuerSecret, string clientIdSecret,
        string clientSecretName, CancellationToken ct)
    {
        string? realmValue = await Secret(tenantId, clusterComponentId, realmSecret, ct);
        if (!Guid.TryParse(realmValue, out Guid realmId)) return;

        string? hostname = await Secret(tenantId, clusterComponentId, hostnameSecret, ct);
        if (string.IsNullOrWhiteSpace(hostname))
        {
            logger.LogWarning(
                "Component {ComponentId} selects a Keycloak realm for single sign-on but has no public "
                + "hostname, so there is no redirect URI to register. Set the hostname and apply again.",
                clusterComponentId);
            return;
        }

        string redirectUri = $"https://{hostname!.Trim()}{redirectPath}";

        try
        {
            KeycloakService.ProvisionedOidcClient client = await keycloakService.EnsureOidcClientAsync(
                tenantId, realmId, clientIdSuffix, [redirectUri], displayName: clientIdSuffix, ct);

            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, issuerSecret, client.IssuerUrl, ct);
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, clientIdSecret, client.ClientId, ct);
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, clientSecretName, client.ClientSecret, ct);

            logger.LogInformation(
                "Registered OIDC client {ClientId} in realm {RealmId} for component {ComponentId} "
                + "(redirect {Redirect}).", clientIdSuffix, realmId, clusterComponentId, redirectUri);
        }
        catch (Exception ex)
        {
            // Not fatal to the apply: the component is still installable with whatever credentials it
            // already had, and failing the whole deployment because an identity provider was briefly
            // unreachable would be a worse trade. It is loud, because the symptom otherwise is a login
            // that fails much later with nothing pointing here.
            logger.LogError(ex,
                "Could not register the OIDC client for component {ComponentId} in realm {RealmId}. "
                + "Single sign-on will use whatever issuer, client id and secret are already stored.",
                clusterComponentId, realmId);
        }
    }

    /// <summary>
    /// The stored-app-registration counterpart to <see cref="ProvisionOidcFromRealmAsync"/>. When a
    /// registration (e.g. Microsoft Entra) is selected, its issuer, client id and client secret are
    /// written into the same three component secrets the realm path uses — so everything downstream
    /// is provider-agnostic. Unlike the realm path there is no client to create: the registration
    /// already exists at the provider, and its redirect URI is registered there, not here (a note
    /// worth surfacing to the operator, since a missing redirect URI is the usual sign-in failure).
    /// </summary>
    private async Task ProvisionOidcFromRegistrationAsync(
        Guid tenantId, Guid clusterComponentId, string registrationSecret,
        string issuerSecret, string clientIdSecret, string clientSecretName, CancellationToken ct)
    {
        string? value = await Secret(tenantId, clusterComponentId, registrationSecret, ct);
        if (!Guid.TryParse(value, out Guid secretId)) return;

        OAuthClientBundle? bundle = await vaultService.GetOAuthClientBundleByIdAsync(secretId, ct);
        if (bundle is null || OAuthClientHelper.Resolve(bundle) is not ResolvedOidc resolved)
        {
            logger.LogWarning(
                "Component {ComponentId} selects an OIDC app registration that could not be resolved "
                + "to an issuer (incomplete registration?). Single sign-on will use whatever is stored.",
                clusterComponentId);
            return;
        }

        await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, issuerSecret, resolved.IssuerUrl, ct);
        if (!string.IsNullOrWhiteSpace(resolved.ClientId))
        {
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, clientIdSecret, resolved.ClientId!, ct);
        }
        if (!string.IsNullOrWhiteSpace(bundle.ClientSecret))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, clientSecretName, bundle.ClientSecret!, ct);
        }
    }

    /// <summary>Regenerates the Roundcube component's manifest from its stored settings.</summary>
    public async Task RefreshRoundcubeManifestAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        (ClusterComponent? component, string releaseName, string ns) =
            await ResolveComponentAsync(clusterComponentId, RoundcubeCatalogKey, ct);
        if (component is null)
        {
            return;
        }

        // Roundcube's redirect URI is fixed by the application, not by us — getting it wrong by one path
        // segment is the single most common reason an otherwise correct SSO setup fails at sign-in.
        await ProvisionOidcFromRealmAsync(
            tenantId, clusterComponentId,
            realmSecret: "RC_OIDC_REALM",
            hostnameSecret: "RC_HOSTNAME",
            clientIdSuffix: releaseName,
            redirectPath: "/index.php/login/oauth",
            issuerSecret: "RC_OIDC_ISSUER",
            clientIdSecret: "RC_OIDC_CLIENT_ID",
            clientSecretName: WebmailManifestBuilder.RoundcubeOauthSecretName, ct);

        // A stored app registration (Entra, …) fills the same three OIDC secrets the realm path does.
        await ProvisionOidcFromRegistrationAsync(
            tenantId, clusterComponentId,
            registrationSecret: "RC_OIDC_APP_REGISTRATION",
            issuerSecret: "RC_OIDC_ISSUER",
            clientIdSecret: "RC_OIDC_CLIENT_ID",
            clientSecretName: WebmailManifestBuilder.RoundcubeOauthSecretName, ct);

        string mode = await SecretOr(tenantId, clusterComponentId, "RC_AUTH_MODE", "Password", ct);
        WebmailAuthMode authMode = System.Enum.TryParse(mode, ignoreCase: true, out WebmailAuthMode parsed)
            ? parsed
            : WebmailAuthMode.Password;

        // Roundcube encrypts the IMAP password it holds for the session with this key. The image
        // will invent one per start if none is set, and because the config directory is not a
        // volume that means every restart logs everybody out. Generate one once and keep it.
        string desKey = await Secret(tenantId, clusterComponentId, "RC_DES_KEY", ct)
                        ?? await MintDesKeyAsync(tenantId, clusterComponentId, ct);

        string? clientSecret = await Secret(
            tenantId, clusterComponentId, WebmailManifestBuilder.RoundcubeOauthSecretName, ct);

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            // Re-store it so it syncs into the Secret this release's Deployment actually names —
            // which the catalog cannot know, because the operator may have renamed the release.
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, WebmailManifestBuilder.RoundcubeOauthSecretName, clientSecret, ct,
                k8sSecretName: $"{releaseName}-credentials", k8sNamespace: ns);
        }

        RoundcubeSettings settings = new(
            ImapHost: await SecretOr(tenantId, clusterComponentId, "RC_IMAP_HOST",
                $"tls://{CatalogKey}.{DefaultNamespace}.svc.cluster.local", ct),
            ImapPort: await SecretInt(tenantId, clusterComponentId, "RC_IMAP_PORT", MailPorts.Imap, ct),
            SmtpHost: await SecretOr(tenantId, clusterComponentId, "RC_SMTP_HOST",
                $"tls://{CatalogKey}.{DefaultNamespace}.svc.cluster.local", ct),
            SmtpPort: await SecretInt(tenantId, clusterComponentId, "RC_SMTP_PORT", MailPorts.Submission, ct),
            DesKey: desKey,
            AuthMode: authMode,
            OidcIssuerUrl: await Secret(tenantId, clusterComponentId, "RC_OIDC_ISSUER", ct),
            OidcClientId: await Secret(tenantId, clusterComponentId, "RC_OIDC_CLIENT_ID", ct),
            OidcClientSecret: clientSecret,
            OidcProviderName: await SecretOr(
                tenantId, clusterComponentId, "RC_OIDC_PROVIDER_NAME", "Single sign-on", ct),
            OidcSkipLoginForm: string.Equals(
                await SecretOr(tenantId, clusterComponentId, "RC_OIDC_SKIP_FORM", "false", ct),
                "true", StringComparison.OrdinalIgnoreCase),
            StorageSize: await SecretOr(tenantId, clusterComponentId, "RC_STORAGE_SIZE", "2Gi", ct),
            StorageClass: await Secret(tenantId, clusterComponentId, "RC_STORAGE_CLASS", ct));

        await StoreManifestAsync(
            clusterComponentId, WebmailManifestBuilder.BuildRoundcube(settings, releaseName, ns), ct);

        await EnsureSimpleRouteAsync(
            tenantId, clusterComponentId, releaseName,
            hostnameSecret: "RC_HOSTNAME", issuerSecret: "RC_CLUSTER_ISSUER", servicePort: 80, ct);
    }

    /// <summary>Regenerates the SnappyMail component's manifest from its stored settings.</summary>
    public async Task RefreshSnappyMailManifestAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        (ClusterComponent? component, string releaseName, string ns) =
            await ResolveComponentAsync(clusterComponentId, SnappyMailCatalogKey, ct);
        if (component is null)
        {
            return;
        }

        SnappyMailSettings settings = new(
            ImapHost: await SecretOr(tenantId, clusterComponentId, "SM_IMAP_HOST",
                $"{CatalogKey}.{DefaultNamespace}.svc.cluster.local", ct),
            ImapPort: await SecretInt(tenantId, clusterComponentId, "SM_IMAP_PORT", MailPorts.Imap, ct),
            SmtpHost: await SecretOr(tenantId, clusterComponentId, "SM_SMTP_HOST",
                $"{CatalogKey}.{DefaultNamespace}.svc.cluster.local", ct),
            SmtpPort: await SecretInt(tenantId, clusterComponentId, "SM_SMTP_PORT", MailPorts.Submission, ct),
            StorageSize: await SecretOr(tenantId, clusterComponentId, "SM_STORAGE_SIZE", "2Gi", ct),
            StorageClass: await Secret(tenantId, clusterComponentId, "SM_STORAGE_CLASS", ct));

        await StoreManifestAsync(
            clusterComponentId, WebmailManifestBuilder.BuildSnappyMail(settings, releaseName, ns), ct);

        await EnsureSimpleRouteAsync(
            tenantId, clusterComponentId, releaseName,
            hostnameSecret: "SM_HOSTNAME", issuerSecret: "SM_CLUSTER_ISSUER", servicePort: 80, ct);
    }

    private async Task<(ClusterComponent? Component, string ReleaseName, string Namespace)>
        ResolveComponentAsync(Guid clusterComponentId, string expectedName, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Name == expectedName, ct);

        return component is null
            ? (null, "", "")
            : (component, component.ReleaseName ?? component.Name, component.Namespace ?? expectedName);
    }

    private async Task StoreManifestAsync(Guid clusterComponentId, string manifest, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterComponent? component = await db.ClusterComponents
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component is null)
        {
            return;
        }
        component.HelmValues = manifest;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> Secret(Guid tenantId, Guid componentId, string name, CancellationToken ct)
    {
        string? value = await vaultService.GetComponentSecretValueAsync(tenantId, componentId, name, ct);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<string> SecretOr(
        Guid tenantId, Guid componentId, string name, string fallback, CancellationToken ct) =>
        await Secret(tenantId, componentId, name, ct) ?? fallback;

    private async Task<int> SecretInt(
        Guid tenantId, Guid componentId, string name, int fallback, CancellationToken ct) =>
        int.TryParse(await Secret(tenantId, componentId, name, ct), out int value) ? value : fallback;

    private async Task<string> MintDesKeyAsync(Guid tenantId, Guid componentId, CancellationToken ct)
    {
        // Roundcube wants exactly 24 characters. Base64 of 18 random bytes is 24 with no padding,
        // and drops the two characters that would need escaping inside single-quoted PHP.
        string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
            .Replace('+', 'A').Replace('/', 'B');

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(tenantId, componentId, "RC_DES_KEY", key, ct);
        return key;
    }

    /// <summary>
    /// Reconciles the single ExternalRoute a supporting component publishes, from a hostname held
    /// as one of its vault secrets. Gateway name and namespace are left unset so they resolve to
    /// whichever ingress the cluster runs.
    /// </summary>
    private async Task EnsureSimpleRouteAsync(
        Guid tenantId, Guid clusterComponentId, string releaseName,
        string hostnameSecret, string issuerSecret, int servicePort, CancellationToken ct)
    {
        List<ExternalRoute> existing = await routeService.GetRoutesAsync(clusterComponentId, ct);
        foreach (ExternalRoute route in existing.Where(r => r.ServiceName == releaseName))
        {
            await routeService.DeleteRouteAsync(route.Id, ct);
        }

        string? hostname = await Secret(tenantId, clusterComponentId, hostnameSecret, ct);
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return;
        }

        string issuer = await SecretOr(tenantId, clusterComponentId, issuerSecret, "letsencrypt-prod", ct);

        await routeService.AddRouteAsync(clusterComponentId, new ExternalRouteRequest
        {
            Hostname = hostname!,
            ServiceName = releaseName,
            ServicePort = servicePort,
            PathPrefix = "/",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = issuer,
        }, ct);
    }
}
