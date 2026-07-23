using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages OpenLDAP directories deployed as a catalog component (symas/openldap Helm
/// chart). Follows the Keycloak/Harbor pattern: an <see cref="OpenLdapComponentConfig"/>
/// attaches to an installed <see cref="ClusterComponent"/>, admin/config passwords are
/// stored as component vault secrets (synced to the K8s Secret the chart consumes), and
/// the directory contents (OUs, users, groups) are authored declaratively in EntKube.
///
/// Because we manage the directory declaratively (no live LDAP bind), changes are applied
/// by regenerating the chart's Helm values + a bootstrap LDIF and re-running the install
/// via <see cref="ComponentInstallOrchestrator"/>. <see cref="RefreshHelmValuesIfConfiguredAsync"/>
/// runs automatically before every install/upgrade so the running server converges on the
/// authored state.
///
/// NOTE: the exact Helm value keys target the symas/openldap chart (global.ldapDomain,
/// replicaCount, customLdifFiles, global.existingSecret). If the pinned chart version
/// renames these, only <see cref="BuildHelmValues"/> needs to change — the entity model,
/// seed-LDIF generation, and UI are chart-agnostic.
/// </summary>
public class OpenLdapService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IKubernetesClientFactory k8sFactory,
    ExternalRouteService routeService,
    ILogger<OpenLdapService> logger)
{
    /// <summary>Subchart Service name suffixes (openldap-stack-ha convention: {release}-{suffix}).</summary>
    private const string PhpLdapAdminServiceSuffix = "phpldapadmin";
    private const string LtbPasswdServiceSuffix = "ltb-passwd";
    public const string CatalogKey = "openldap";
    // Key names the openldap-stack-ha chart expects inside global.existingSecret.
    private const string AdminPasswordSecretName = "LDAP_ADMIN_PASSWORD";
    private const string ConfigPasswordSecretName = "LDAP_CONFIG_ADMIN_PASSWORD";
    // kubernetes.io/tls Secret the chart's initTLSSecret mounts as the server certificate.
    public const string TlsSecretName = "openldap-tls";

    /// <summary>A discovered OpenLDAP instance: its installed component and attached config (if any).</summary>
    public sealed record OpenLdapInstance(ClusterComponent Component, OpenLdapComponentConfig? Config);

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists installed OpenLDAP components for the tenant, each paired with its config
    /// (null until configured). When <paramref name="environmentId"/> is given, restricts
    /// to components on clusters bound to that environment (Component.Cluster.EnvironmentId).
    /// </summary>
    public async Task<List<OpenLdapInstance>> GetInstancesAsync(
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
        Dictionary<Guid, OpenLdapComponentConfig> configs = await db.OpenLdapComponentConfigs
            .Where(c => c.ClusterComponentId != null && componentIds.Contains(c.ClusterComponentId!.Value))
            .ToDictionaryAsync(c => c.ClusterComponentId!.Value, ct);

        return components
            .Select(c => new OpenLdapInstance(c, configs.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<OpenLdapComponentConfig?> GetConfigAsync(Guid configId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.OpenLdapComponentConfigs
            .Include(c => c.OrganizationalUnits)
            .Include(c => c.Users)
            .Include(c => c.Groups).ThenInclude(g => g.Members)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
    }

    // ── Configure (mirrors KeycloakService.ConfigureAsync) ────────────────────

    /// <summary>
    /// Upserts the directory config for an installed OpenLDAP component, stores the admin
    /// and config passwords in the vault (synced to the chart's credentials Secret), and
    /// refreshes the component's Helm values. Pass a null/blank password to leave the
    /// stored one unchanged on re-configure.
    /// </summary>
    public async Task<OpenLdapComponentConfig> ConfigureAsync(
        Guid tenantId,
        Guid clusterComponentId,
        Action<OpenLdapComponentConfig> apply,
        string? adminPassword,
        string? configPassword,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        OpenLdapComponentConfig? config = await db.OpenLdapComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId, ct);

        if (config is null)
        {
            config = new OpenLdapComponentConfig
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterComponentId = clusterComponentId,
                BaseDn = "dc=example,dc=com",
            };
            db.OpenLdapComponentConfigs.Add(config);
        }

        apply(config);
        config.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);

        string ns = component.Namespace ?? "openldap";
        string credSecretName = $"{component.ReleaseName ?? component.Name}-credentials";

        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, AdminPasswordSecretName, adminPassword, ct,
                k8sSecretName: credSecretName, k8sNamespace: ns);
        }
        if (!string.IsNullOrWhiteSpace(configPassword))
        {
            await vaultService.SetComponentSecretAsync(
                tenantId, clusterComponentId, ConfigPasswordSecretName, configPassword, ct,
                k8sSecretName: credSecretName, k8sNamespace: ns);
        }

        await RefreshHelmValuesIfConfiguredAsync(tenantId, clusterComponentId, ct);
        await EnsureWebUiRoutesAsync(tenantId, clusterComponentId, ct);
        return config;
    }

    /// <summary>
    /// Reconciles EntKube ExternalRoutes for the two bundled web UIs. For a UI in
    /// <see cref="OpenLdapExposeMode.Gateway"/> mode with a hostname, ensures a route exists targeting
    /// the subchart's Service (published via the cluster's gateway — traefik/istio — with cert-manager TLS
    /// from <see cref="OpenLdapComponentConfig.WebUiClusterIssuer"/>). Routes for UIs that are disabled or
    /// switched to Ingress/None mode are removed. The routes are applied to the cluster by the normal
    /// install/apply flow (ComponentInstallOrchestrator → ApplyExternalRoutesAsync).
    /// </summary>
    public async Task EnsureWebUiRoutesAsync(Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        OpenLdapComponentConfig? config;
        string release;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            config = await db.OpenLdapComponentConfigs
                .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
            if (config is null) return;

            ClusterComponent? component = await db.ClusterComponents.FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
            if (component is null) return;
            release = component.ReleaseName ?? component.Name;
        }

        string phpSvc = $"{release}-{PhpLdapAdminServiceSuffix}";
        string ltbSvc = $"{release}-{LtbPasswdServiceSuffix}";

        // Remove any existing web-UI routes (by their backing Service) so mode/host/disable changes reconcile.
        List<ExternalRoute> existing = await routeService.GetRoutesAsync(clusterComponentId, ct);
        foreach (ExternalRoute r in existing.Where(r => r.ServiceName == phpSvc || r.ServiceName == ltbSvc))
        {
            await routeService.DeleteRouteAsync(r.Id, ct);
        }

        await AddGatewayRouteIfNeeded(clusterComponentId, config.PhpLdapAdminEnabled, config.PhpLdapAdminExposeMode,
            config.PhpLdapAdminHostname, phpSvc, config.WebUiClusterIssuer, ct);
        // LTB is only actually deployed when an image is supplied — don't route to a Service that won't exist.
        bool ltbDeployed = config.LtbPasswdEnabled && !string.IsNullOrWhiteSpace(config.LtbPasswdImage);
        await AddGatewayRouteIfNeeded(clusterComponentId, ltbDeployed, config.LtbPasswdExposeMode,
            config.LtbPasswdHostname, ltbSvc, config.WebUiClusterIssuer, ct);
    }

    private async Task AddGatewayRouteIfNeeded(
        Guid componentId, bool enabled, OpenLdapExposeMode mode, string? hostname,
        string serviceName, string? issuer, CancellationToken ct)
    {
        if (!enabled || mode != OpenLdapExposeMode.Gateway || string.IsNullOrWhiteSpace(hostname))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(issuer))
        {
            logger.LogWarning("Skipping Gateway route for {Service} — no web-UI ClusterIssuer set for TLS.", serviceName);
            return;
        }

        await routeService.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = hostname.Trim(),
            ServiceName = serviceName,
            ServicePort = 80,
            PathPrefix = "/",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = issuer.Trim(),
            // GatewayName/Namespace left null → auto-resolved from the installed gateway (traefik/istio).
        }, ct);
    }

    /// <summary>
    /// Parses catalog form-field values (keyed by <see cref="ComponentFormField.Key"/>) and
    /// applies them to the component's config. Single source of truth shared by the interactive
    /// install/edit path (ClusterDetail) and the blueprint bootstrap path (CatalogComponentRegistrar),
    /// so both capture the base DN, storage size, TLS, replication, and bundled-UI settings identically.
    /// </summary>
    public async Task ConfigureFromFormAsync(
        Guid tenantId, Guid clusterComponentId, IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        string baseDn = Get(form, "base-dn", "dc=example,dc=com");
        string org = Get(form, "organization", "EntKube");
        string tlsMode = Get(form, "tls-mode", "SelfSigned");
        string? issuer = form.TryGetValue("cluster-issuer", out string? i) && !string.IsNullOrWhiteSpace(i) ? i : null;
        int replicas = form.TryGetValue("replica-count", out string? r) && int.TryParse(r, out int rp) && rp > 0 ? rp : 1;
        string storage = Get(form, "storage-size", "8Gi");
        form.TryGetValue("admin-password", out string? adminPassword);
        form.TryGetValue("config-password", out string? configPassword);
        bool phpEnabled = IsOn(form, "phpldapadmin-enabled");
        string? phpHost = form.TryGetValue("phpldapadmin-hostname", out string? ph) && !string.IsNullOrWhiteSpace(ph) ? ph.Trim() : null;
        bool ltbEnabled = IsOn(form, "ltb-passwd-enabled");
        string? ltbHost = form.TryGetValue("ltb-passwd-hostname", out string? lh) && !string.IsNullOrWhiteSpace(lh) ? lh.Trim() : null;

        OpenLdapTlsMode mode = tlsMode switch
        {
            "Off" => OpenLdapTlsMode.Off,
            "Manual" => OpenLdapTlsMode.Manual,
            "ClusterIssuer" => OpenLdapTlsMode.ClusterIssuer,
            _ => OpenLdapTlsMode.SelfSigned,
        };

        await ConfigureAsync(
            tenantId, clusterComponentId,
            cfg =>
            {
                cfg.BaseDn = baseDn;
                cfg.Organization = org;
                cfg.TlsMode = mode;
                cfg.ClusterIssuer = mode == OpenLdapTlsMode.ClusterIssuer ? issuer : null;
                cfg.ReplicaCount = replicas;
                cfg.ReplicationEnabled = replicas > 1;
                cfg.StorageSize = storage;
                cfg.PhpLdapAdminEnabled = phpEnabled;
                cfg.PhpLdapAdminHostname = phpEnabled ? phpHost : null;
                cfg.LtbPasswdEnabled = ltbEnabled;
                cfg.LtbPasswdHostname = ltbEnabled ? ltbHost : null;
            },
            string.IsNullOrWhiteSpace(adminPassword) ? null : adminPassword,
            string.IsNullOrWhiteSpace(configPassword) ? null : configPassword,
            ct);

        static string Get(IReadOnlyDictionary<string, string> f, string k, string dflt) =>
            f.TryGetValue(k, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : dflt;
        static bool IsOn(IReadOnlyDictionary<string, string> f, string k) =>
            f.TryGetValue(k, out string? v) && (v == "true" || v == "on" || v == "1");
    }

    /// <summary>
    /// Called automatically before every install/upgrade. If an OpenLdapComponentConfig
    /// exists for this component, regenerates the chart values + bootstrap LDIF from the
    /// authored directory and writes them to the component's HelmValues. No-op otherwise.
    /// </summary>
    public async Task RefreshHelmValuesIfConfiguredAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OpenLdapComponentConfig? config = await db.OpenLdapComponentConfigs
            .Include(c => c.OrganizationalUnits)
            .Include(c => c.Users)
            .Include(c => c.Groups).ThenInclude(g => g.Members)
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);

        if (config is null)
        {
            return;
        }

        ClusterComponent? component = await db.ClusterComponents.FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component is null)
        {
            return;
        }

        // Members are needed to render groupOfNames — build a user lookup once.
        Dictionary<Guid, OpenLdapUser> usersById = config.Users.ToDictionary(u => u.Id);
        string seed = BuildSeedLdif(config, usersById);
        string credSecretName = $"{component.ReleaseName ?? component.Name}-credentials";

        component.HelmValues = BuildHelmValues(config, seed, credSecretName);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Refreshed OpenLDAP Helm values for component {ComponentId}: {Ous} OUs, {Users} users, {Groups} groups.",
            clusterComponentId, config.OrganizationalUnits.Count, config.Users.Count, config.Groups.Count);
    }

    /// <summary>
    /// When TLS mode is ClusterIssuer, applies a cert-manager <c>Certificate</c> that issues the
    /// server certificate (with the CA chain) into the <see cref="TlsSecretName"/> Secret the chart
    /// mounts. Covers the ClusterIP service and the per-pod headless names so replication's
    /// <c>tls_reqcert=demand</c> peer verification succeeds. Must run BEFORE the Helm install so the
    /// Secret exists when the StatefulSet's init container mounts it. No-op for Manual/Off or when
    /// no issuer is set. NOTE: use a CA-based / self-signed ClusterIssuer — a public ACME issuer
    /// (Let's Encrypt) cannot issue certificates for cluster-internal service names.
    /// </summary>
    public async Task ApplyTlsCertificateIfNeededAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OpenLdapComponentConfig? config = await db.OpenLdapComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == clusterComponentId && c.TenantId == tenantId, ct);
        if (config is null || config.TlsMode != OpenLdapTlsMode.ClusterIssuer || string.IsNullOrWhiteSpace(config.ClusterIssuer))
        {
            return;
        }

        ClusterComponent? component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId, ct);
        if (component?.Cluster?.Kubeconfig is not { Length: > 0 } kubeconfig)
        {
            logger.LogWarning("OpenLDAP TLS certificate skipped — no kubeconfig for component {ComponentId}.", clusterComponentId);
            return;
        }

        string release = component.ReleaseName ?? component.Name;
        string ns = component.Namespace ?? "openldap";
        string manifest = BuildTlsCertificateManifest(config.ClusterIssuer!, release, ns);

        await k8sFactory.ApplyManifestAsync(manifest, kubeconfig, ct);
        logger.LogInformation(
            "Applied cert-manager Certificate '{Secret}' for OpenLDAP component {ComponentId} via ClusterIssuer '{Issuer}'.",
            TlsSecretName, clusterComponentId, config.ClusterIssuer);

        // Wait for cert-manager to actually issue the Secret BEFORE the install proceeds — the chart
        // mounts it as a volume, so a missing Secret would hang every pod in ContainerCreating. Fail
        // fast with an actionable message instead of letting `helm --wait` block for the full timeout.
        if (!await WaitForSecretAsync(ns, TlsSecretName, kubeconfig, TimeSpan.FromMinutes(2), ct))
        {
            throw new InvalidOperationException(
                $"cert-manager did not issue the '{TlsSecretName}' TLS Secret in namespace '{ns}' within 2 minutes " +
                $"(ClusterIssuer '{config.ClusterIssuer}'). Ensure cert-manager is installed and the ClusterIssuer is a " +
                $"ready CA/self-signed issuer that can sign cluster-internal names — a public ACME issuer (Let's Encrypt) " +
                $"cannot. Or switch TLS mode to 'Self-signed' to avoid the dependency.");
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
                if (json.Contains("\"kind\"", StringComparison.Ordinal) && json.Contains("tls.crt", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                // kubectl returns non-zero until the Secret exists — keep polling.
                logger.LogDebug(ex, "Waiting for TLS Secret {Secret} in {Namespace}…", secretName, ns);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return false;
    }

    /// <summary>Builds a cert-manager Certificate CR that issues the LDAP server cert into <see cref="TlsSecretName"/>.</summary>
    public static string BuildTlsCertificateManifest(string clusterIssuer, string releaseName, string ns)
    {
        string svc = releaseName;
        string headless = $"{releaseName}-headless";
        return $"""
            apiVersion: cert-manager.io/v1
            kind: Certificate
            metadata:
              name: {TlsSecretName}
              namespace: {ns}
            spec:
              secretName: {TlsSecretName}
              duration: 8760h
              renewBefore: 720h
              privateKey:
                algorithm: RSA
                size: 2048
              commonName: {svc}.{ns}.svc.cluster.local
              dnsNames:
                - {svc}
                - {svc}.{ns}
                - {svc}.{ns}.svc
                - {svc}.{ns}.svc.cluster.local
                - {headless}.{ns}.svc.cluster.local
                - '*.{headless}.{ns}.svc.cluster.local'
              issuerRef:
                name: {clusterIssuer}
                kind: ClusterIssuer
                group: cert-manager.io
            """;
    }

    // ── Directory CRUD ────────────────────────────────────────────────────────

    public async Task<OpenLdapOrganizationalUnit> AddOuAsync(Guid configId, string name, string? description, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        OpenLdapOrganizationalUnit ou = new()
        {
            Id = Guid.NewGuid(), ConfigId = configId, Name = name.Trim().ToLowerInvariant(), Description = description,
        };
        db.OpenLdapOrganizationalUnits.Add(ou);
        await db.SaveChangesAsync(ct);
        return ou;
    }

    public async Task DeleteOuAsync(Guid ouId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.OpenLdapOrganizationalUnits.Where(o => o.Id == ouId).ExecuteDeleteAsync(ct);
    }

    public async Task<OpenLdapUser> AddUserAsync(OpenLdapUser user, string? password, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        user.Id = user.Id == Guid.Empty ? Guid.NewGuid() : user.Id;
        user.Uid = user.Uid.Trim();
        if (!string.IsNullOrEmpty(password))
        {
            user.PasswordSsha = HashSsha(password);
        }
        db.OpenLdapUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateUserAsync(OpenLdapUser user, string? newPassword, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        OpenLdapUser existing = await db.OpenLdapUsers.FirstAsync(u => u.Id == user.Id, ct);
        existing.OrganizationalUnitId = user.OrganizationalUnitId;
        existing.Cn = user.Cn;
        existing.Sn = user.Sn;
        existing.GivenName = user.GivenName;
        existing.Email = user.Email;
        existing.DisplayName = user.DisplayName;
        existing.UidNumber = user.UidNumber;
        existing.GidNumber = user.GidNumber;
        existing.HomeDirectory = user.HomeDirectory;
        existing.LoginShell = user.LoginShell;
        existing.IsServiceAccount = user.IsServiceAccount;
        existing.Enabled = user.Enabled;
        if (!string.IsNullOrEmpty(newPassword))
        {
            existing.PasswordSsha = HashSsha(newPassword);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        // Remove memberships first — the join's user FK is Restrict (no cascade) to keep
        // SQL Server happy with a single cascade path through the group.
        await db.OpenLdapGroupMembers.Where(m => m.UserId == userId).ExecuteDeleteAsync(ct);
        await db.OpenLdapUsers.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
    }

    public async Task<OpenLdapGroup> AddGroupAsync(OpenLdapGroup group, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        group.Id = group.Id == Guid.Empty ? Guid.NewGuid() : group.Id;
        group.Cn = group.Cn.Trim();
        db.OpenLdapGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task DeleteGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.OpenLdapGroups.Where(g => g.Id == groupId).ExecuteDeleteAsync(ct);
    }

    public async Task SetGroupMembersAsync(Guid groupId, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.OpenLdapGroupMembers.Where(m => m.GroupId == groupId).ExecuteDeleteAsync(ct);
        foreach (Guid uid in userIds.Distinct())
        {
            db.OpenLdapGroupMembers.Add(new OpenLdapGroupMember { Id = Guid.NewGuid(), GroupId = groupId, UserId = uid });
        }
        await db.SaveChangesAsync(ct);
    }

    // ── LDIF + Helm values generation ─────────────────────────────────────────

    /// <summary>Builds the bootstrap LDIF for the authored directory (OUs → users → groups).</summary>
    public static string BuildSeedLdif(OpenLdapComponentConfig config, Dictionary<Guid, OpenLdapUser> usersById)
    {
        string baseDn = config.BaseDn.Trim();
        StringBuilder sb = new();

        // Map OU id → its DN (falls back to base DN for entries with no OU).
        Dictionary<Guid, string> ouDnById = config.OrganizationalUnits
            .ToDictionary(o => o.Id, o => $"ou={o.Name},{baseDn}");

        foreach (OpenLdapOrganizationalUnit ou in config.OrganizationalUnits.OrderBy(o => o.Name))
        {
            sb.Append($"dn: ou={ou.Name},{baseDn}\n");
            sb.Append("objectClass: organizationalUnit\n");
            sb.Append($"ou: {ou.Name}\n");
            if (!string.IsNullOrWhiteSpace(ou.Description))
            {
                sb.Append($"description: {ou.Description}\n");
            }
            sb.Append('\n');
        }

        foreach (OpenLdapUser u in config.Users.OrderBy(u => u.Uid))
        {
            string parent = u.OrganizationalUnitId is Guid oid && ouDnById.TryGetValue(oid, out string? d) ? d : baseDn;
            sb.Append($"dn: uid={u.Uid},{parent}\n");
            sb.Append("objectClass: inetOrgPerson\n");
            bool posix = u.UidNumber.HasValue;
            if (posix)
            {
                sb.Append("objectClass: posixAccount\n");
            }
            sb.Append($"uid: {u.Uid}\n");
            sb.Append($"cn: {u.Cn}\n");
            sb.Append($"sn: {(string.IsNullOrWhiteSpace(u.Sn) ? u.Cn : u.Sn)}\n");
            if (!string.IsNullOrWhiteSpace(u.GivenName)) sb.Append($"givenName: {u.GivenName}\n");
            if (!string.IsNullOrWhiteSpace(u.DisplayName)) sb.Append($"displayName: {u.DisplayName}\n");
            if (!string.IsNullOrWhiteSpace(u.Email)) sb.Append($"mail: {u.Email}\n");
            if (posix)
            {
                sb.Append($"uidNumber: {u.UidNumber}\n");
                sb.Append($"gidNumber: {u.GidNumber ?? u.UidNumber!.Value}\n");
                sb.Append($"homeDirectory: {(string.IsNullOrWhiteSpace(u.HomeDirectory) ? $"/home/{u.Uid}" : u.HomeDirectory)}\n");
                if (!string.IsNullOrWhiteSpace(u.LoginShell)) sb.Append($"loginShell: {u.LoginShell}\n");
            }
            if (!string.IsNullOrWhiteSpace(u.PasswordSsha)) sb.Append($"userPassword: {u.PasswordSsha}\n");
            if (!u.Enabled) sb.Append("pwdAccountLockedTime: 000001010000Z\n");
            sb.Append('\n');
        }

        foreach (OpenLdapGroup g in config.Groups.OrderBy(g => g.Cn))
        {
            string parent = g.OrganizationalUnitId is Guid oid && ouDnById.TryGetValue(oid, out string? d) ? d : baseDn;
            sb.Append($"dn: cn={g.Cn},{parent}\n");
            if (g.GroupType == OpenLdapGroupType.PosixGroup)
            {
                sb.Append("objectClass: posixGroup\n");
                sb.Append($"cn: {g.Cn}\n");
                sb.Append($"gidNumber: {g.GidNumber ?? 10000}\n");
                if (!string.IsNullOrWhiteSpace(g.Description)) sb.Append($"description: {g.Description}\n");
                foreach (OpenLdapGroupMember m in g.Members)
                {
                    if (usersById.TryGetValue(m.UserId, out OpenLdapUser? mu)) sb.Append($"memberUid: {mu.Uid}\n");
                }
            }
            else
            {
                sb.Append("objectClass: groupOfNames\n");
                sb.Append($"cn: {g.Cn}\n");
                if (!string.IsNullOrWhiteSpace(g.Description)) sb.Append($"description: {g.Description}\n");
                bool any = false;
                foreach (OpenLdapGroupMember m in g.Members)
                {
                    if (usersById.TryGetValue(m.UserId, out OpenLdapUser? mu))
                    {
                        string mp = mu.OrganizationalUnitId is Guid moid && ouDnById.TryGetValue(moid, out string? md) ? md : baseDn;
                        sb.Append($"member: uid={mu.Uid},{mp}\n");
                        any = true;
                    }
                }
                // groupOfNames requires ≥1 member — seed the admin DN as a placeholder.
                if (!any) sb.Append($"member: cn={config.AdminUsername},{baseDn}\n");
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the Helm values document for the jp-gouin <c>openldap-stack-ha</c> chart from
    /// the authored config. Value keys are verified against chart v4.3.3
    /// (global.ldapDomain/existingSecret, replicaCount, replication, persistence, initTLSSecret,
    /// customLdifFiles). The chart does NOT auto-create the base DN when customLdifFiles is used,
    /// so the root organization entry is emitted first.
    /// </summary>
    public static string BuildHelmValues(OpenLdapComponentConfig config, string seedLdif, string credSecretName)
    {
        StringBuilder sb = new();

        sb.Append("# Generated by EntKube from the OpenLDAP directory config — edits here are\n");
        sb.Append("# overwritten on the next Save & Apply. Manage the directory in the LDAP tab.\n\n");
        sb.Append($"replicaCount: {Math.Max(1, config.ReplicaCount)}\n\n");
        sb.Append("global:\n");
        // ldapDomain accepts an explicit DN (dc=example,dc=com) — pass the base DN verbatim.
        sb.Append($"  ldapDomain: \"{config.BaseDn.Trim()}\"\n");
        sb.Append($"  adminUser: \"{config.AdminUsername}\"\n");
        sb.Append("  configUser: \"admin\"\n");
        // Admin/config passwords come from the EntKube-managed Secret (vault-synced):
        // keys LDAP_ADMIN_PASSWORD + LDAP_CONFIG_ADMIN_PASSWORD.
        sb.Append($"  existingSecret: \"{credSecretName}\"\n\n");

        // Bundled admin UIs. The subchart is deployed when enabled; how it's published depends on
        // the expose mode: Ingress → the chart's own classic Ingress (ingressClassName, cert-manager
        // annotation); Gateway/None → the chart Ingress is OFF (EntKube ExternalRoutes handle Gateway
        // exposure — see EnsureWebUiRoutesAsync). Default off: this is a headless directory service.
        // phpLDAPadmin has a working default image (image override optional).
        AppendWebUi(sb, "phpldapadmin", config.PhpLdapAdminEnabled, config.PhpLdapAdminHostname,
            config.PhpLdapAdminExposeMode, config.PhpLdapAdminIngressClass, config.WebUiClusterIssuer,
            config.PhpLdapAdminImage, imageRequired: false);
        // LTB self-service: the chart's default image is gone upstream, so an override image is REQUIRED
        // to deploy it — otherwise it's left disabled to avoid a guaranteed ImagePullBackOff.
        AppendWebUi(sb, "ltb-passwd", config.LtbPasswdEnabled, config.LtbPasswdHostname,
            config.LtbPasswdExposeMode, config.LtbPasswdIngressClass, config.WebUiClusterIssuer,
            config.LtbPasswdImage, imageRequired: true);

        // Replication (multi-master) only when enabled AND more than one replica.
        sb.Append("replication:\n");
        sb.Append($"  enabled: {Bool(config.ReplicationEnabled && config.ReplicaCount > 1)}\n\n");

        // Persistence.
        sb.Append("persistence:\n");
        sb.Append("  enabled: true\n");
        sb.Append($"  size: {config.StorageSize}\n");
        if (!string.IsNullOrWhiteSpace(config.StorageClass))
        {
            sb.Append($"  storageClass: \"{config.StorageClass}\"\n");
        }
        sb.Append('\n');

        // TLS. IMPORTANT: when initTLSSecret.tls_enabled is true AND a secret is named, the chart
        // MOUNTS that Secret as a volume and copies its tls.crt/tls.key — it does NOT self-sign, so the
        // Secret must already exist or the pod hangs in ContainerCreating forever. We therefore only set
        // tls_enabled:true for ClusterIssuer (a cert-manager Certificate populates it — waited on in
        // ApplyTlsCertificateIfNeededAsync) and Manual (operator pre-creates it). SelfSigned/Off leave
        // tls_enabled:false so the chart generates its own cert with zero external dependencies (and the
        // chart relaxes replication to tls_reqcert=never, so multi-master still works).
        bool externalCert = config.TlsMode is OpenLdapTlsMode.ClusterIssuer or OpenLdapTlsMode.Manual;
        if (externalCert)
        {
            sb.Append("initTLSSecret:\n  tls_enabled: true\n");
            sb.Append($"  secret: \"{TlsSecretName}\"\n\n");
            sb.Append("env:\n");
            sb.Append("  LDAP_ENABLE_TLS: \"yes\"\n");
            sb.Append($"  LDAP_REQUIRE_TLS: \"{Bool(!config.StartTlsEnabled)}\"\n\n");
        }
        else
        {
            // SelfSigned or Off — chart self-signs into an emptyDir (no external Secret to wait for).
            sb.Append("initTLSSecret:\n  tls_enabled: false\n\n");
            sb.Append("env:\n");
            sb.Append($"  LDAP_ENABLE_TLS: \"{(config.TlsMode == OpenLdapTlsMode.Off ? "no" : "yes")}\"\n");
            if (config.TlsMode != OpenLdapTlsMode.Off)
            {
                sb.Append($"  LDAP_REQUIRE_TLS: \"{Bool(!config.StartTlsEnabled)}\"\n");
            }
            sb.Append('\n');
        }

        // Custom overlay notes + seed entries. The root org entry MUST be present because the
        // chart skips default-tree creation when customLdifFiles is set.
        sb.Append("customLdifFiles:\n");
        sb.Append("  00-entkube-overlays.ldif: |-\n");
        AppendIndented(sb, BuildOverlayLdif(config), "    ");
        sb.Append("  01-entkube-root.ldif: |-\n");
        AppendIndented(sb, BuildRootLdif(config), "    ");
        sb.Append("  02-entkube-seed.ldif: |-\n");
        AppendIndented(sb, string.IsNullOrWhiteSpace(seedLdif) ? "# no seed entries\n" : seedLdif, "    ");

        return sb.ToString();
    }

    /// <summary>The base DN's root organization entry (dcObject + organization).</summary>
    public static string BuildRootLdif(OpenLdapComponentConfig config)
    {
        string baseDn = config.BaseDn.Trim();
        // First RDN, e.g. "dc=example" → attr "dc", value "example".
        string firstRdn = baseDn.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        string[] kv = firstRdn.Split('=', 2);
        string attr = kv.Length == 2 ? kv[0].Trim().ToLowerInvariant() : "dc";
        string val = kv.Length == 2 ? kv[1].Trim() : "example";

        StringBuilder sb = new();
        sb.Append($"dn: {baseDn}\n");
        if (attr == "dc")
        {
            sb.Append("objectClass: dcObject\n");
            sb.Append("objectClass: organization\n");
            sb.Append($"o: {config.Organization}\n");
            sb.Append($"dc: {val}\n");
        }
        else
        {
            sb.Append("objectClass: organization\n");
            sb.Append($"o: {config.Organization}\n");
        }
        return sb.ToString();
    }

    private static string Bool(bool b) => b ? "true" : "false";

    /// <summary>Splits "repository:tag" into parts, tolerating a registry:port in the repository.</summary>
    public static (string Repository, string? Tag) ParseImage(string image)
    {
        int lastSlash = image.LastIndexOf('/');
        int lastColon = image.LastIndexOf(':');
        // A colon after the last slash is the tag separator; a colon before it is a registry port.
        return lastColon > lastSlash ? (image[..lastColon], image[(lastColon + 1)..]) : (image, null);
    }

    /// <summary>
    /// Emits a chart subchart (phpldapadmin / ltb-passwd) block. The subchart is deployed when enabled;
    /// its built-in classic Ingress is emitted only for <see cref="OpenLdapExposeMode.Ingress"/> (with the
    /// chosen ingressClassName + cert-manager TLS). For Gateway/None the chart Ingress stays off — Gateway
    /// exposure is done via EntKube ExternalRoutes instead (<see cref="EnsureWebUiRoutesAsync"/>).
    /// </summary>
    private static void AppendWebUi(
        StringBuilder sb, string key, bool enabled, string? hostname,
        OpenLdapExposeMode mode, string? ingressClass, string? webUiIssuer,
        string? imageOverride, bool imageRequired)
    {
        // Refuse to deploy a UI whose only usable image must be supplied but wasn't (avoids ImagePullBackOff).
        bool effectiveEnabled = enabled && (!imageRequired || !string.IsNullOrWhiteSpace(imageOverride));
        sb.Append($"{key}:\n  enabled: {Bool(effectiveEnabled)}\n");

        if (effectiveEnabled && !string.IsNullOrWhiteSpace(imageOverride))
        {
            (string repo, string? tag) = ParseImage(imageOverride.Trim());
            sb.Append("  image:\n");
            sb.Append($"    repository: {repo}\n");
            if (tag is not null)
            {
                sb.Append($"    tag: \"{tag}\"\n");
            }
        }

        bool classicIngress = effectiveEnabled && mode == OpenLdapExposeMode.Ingress && !string.IsNullOrWhiteSpace(hostname);
        if (classicIngress)
        {
            string host = hostname!.Trim();
            sb.Append("  ingress:\n");
            sb.Append("    enabled: true\n");
            if (!string.IsNullOrWhiteSpace(ingressClass))
            {
                sb.Append($"    ingressClassName: {ingressClass.Trim()}\n");
            }
            if (!string.IsNullOrWhiteSpace(webUiIssuer))
            {
                sb.Append("    annotations:\n");
                sb.Append($"      cert-manager.io/cluster-issuer: {webUiIssuer.Trim()}\n");
            }
            sb.Append("    hosts:\n");
            sb.Append($"      - {host}\n");
            if (!string.IsNullOrWhiteSpace(webUiIssuer))
            {
                sb.Append("    tls:\n");
                sb.Append($"      - secretName: {key}-tls\n");
                sb.Append("        hosts:\n");
                sb.Append($"          - {host}\n");
            }
        }
        else
        {
            // Gateway or None → the chart must not create its own Ingress.
            sb.Append("  ingress:\n    enabled: false\n");
        }
        sb.Append('\n');
    }

    /// <summary>cn=config overlay/ppolicy directives derived from the config toggles.</summary>
    private static string BuildOverlayLdif(OpenLdapComponentConfig config)
    {
        StringBuilder sb = new();
        sb.Append("# Overlays and password policy configured by EntKube.\n");
        if (config.MemberOfEnabled) sb.Append("# memberof overlay: enabled\n");
        if (config.RefIntEnabled) sb.Append("# refint overlay: enabled\n");
        if (config.PasswordPolicyEnabled)
        {
            sb.Append("# ppolicy overlay: enabled\n");
            sb.Append($"# pwdMinLength: {config.PpolicyMinLength}\n");
            sb.Append($"# pwdMaxFailure: {config.PpolicyMaxFailure}\n");
            sb.Append($"# pwdLockoutDuration: {config.PpolicyLockoutDurationSeconds}\n");
            sb.Append($"# pwdMaxAge (days): {config.PpolicyMaxAgeDays}\n");
            sb.Append($"# pwdInHistory: {config.PpolicyInHistory}\n");
        }
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Salted SHA-1 (<c>{SSHA}</c>) userPassword hash, the OpenLDAP default scheme.</summary>
    public static string HashSsha(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(8);
        byte[] pw = Encoding.UTF8.GetBytes(password);
        byte[] digest = SHA1.HashData([.. pw, .. salt]);
        return "{SSHA}" + Convert.ToBase64String([.. digest, .. salt]);
    }

    /// <summary>Derives a DNS domain (example.com) from a base DN (dc=example,dc=com).</summary>
    public static string DomainFromBaseDn(string baseDn)
    {
        IEnumerable<string> dcs = baseDn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.StartsWith("dc=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]);
        string joined = string.Join('.', dcs);
        return string.IsNullOrEmpty(joined) ? "example.com" : joined;
    }

    private static void AppendIndented(StringBuilder sb, string content, string indent)
    {
        foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) sb.Append('\n');
            else sb.Append(indent).Append(line).Append('\n');
        }
    }
}
