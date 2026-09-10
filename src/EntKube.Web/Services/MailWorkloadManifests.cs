namespace EntKube.Web.Services;

/// <summary>
/// Settings for the rspamd deployment. Rendered from the component's form values and vault
/// secrets rather than substituted into a static manifest, because the optional lines have to be
/// <em>absent</em> when unset: an rspamd told <c>password = ""</c> is not the same as an rspamd
/// told nothing, and a placeholder left unsubstituted becomes a literal password.
/// </summary>
/// <param name="RedisServers">Redis endpoint, <c>host:port</c>.</param>
/// <param name="RedisPassword">Redis password, or null for an unauthenticated Redis.</param>
/// <param name="ControllerPassword">Password for rspamd's web UI, or null to leave it open.</param>
/// <param name="StorageSize">Size of the volume holding rspamd's local state.</param>
/// <param name="StorageClass">Storage class for that volume, or null for the cluster default.</param>
/// <param name="SsoIssuerUrl">OIDC issuer (Keycloak realm) for the web UI, or null to keep password login.</param>
/// <param name="SsoClientId">OAuth client id registered for the rspamd UI.</param>
/// <param name="SsoEmailDomain">Email domain allowed to sign in; <c>*</c> for any the issuer authenticates.</param>
/// <param name="WebUiHostname">Public hostname the UI is published on — the OIDC redirect URL is built from it.</param>
public sealed record RspamdSettings(
    string RedisServers,
    string? RedisPassword,
    string? ControllerPassword,
    string StorageSize = "2Gi",
    string? StorageClass = null,
    string? SsoIssuerUrl = null,
    string? SsoClientId = null,
    string SsoEmailDomain = "*",
    string? WebUiHostname = null)
{
    /// <summary>
    /// Whether the web UI is fronted by the OIDC proxy. Requires all three of an issuer, a client id and
    /// the hostname: rspamd has no OIDC of its own, so single sign-on here means an authenticating proxy
    /// in front of the controller, and a proxy cannot be built without the address it redirects back to.
    /// </summary>
    public bool SsoEnabled =>
        !string.IsNullOrWhiteSpace(SsoIssuerUrl)
        && !string.IsNullOrWhiteSpace(SsoClientId)
        && !string.IsNullOrWhiteSpace(WebUiHostname);
}

/// <summary>
/// Renders the rspamd deployment: the scanner Stalwart hands every incoming message to over the
/// milter protocol, with Redis behind it for the Bayes classifier, greylisting, ratelimits and
/// reputation — all of which are shared, mutable state that must not live in a pod's filesystem.
/// </summary>
public static class RspamdManifestBuilder
{
    public const string Image = "rspamd/rspamd:4.1.5";

    /// <summary>Milter endpoint. This is the port Stalwart's MtaMilter connects to.</summary>
    public const int MilterPort = 11332;

    /// <summary>The normal worker, for direct scan protocol clients.</summary>
    public const int NormalPort = 11333;

    /// <summary>The controller: web UI, statistics, and Bayes training.</summary>
    public const int ControllerPort = 11334;

    /// <summary>The uid/gid the upstream image runs its workers as.</summary>
    public const int RunAsGroup = 11333;

    /// <summary>
    /// Where the OIDC proxy listens when single sign-on is on. This — not the controller — is the port the
    /// public route targets in that case, which is the whole mechanism: the controller keeps listening on
    /// its own port for in-cluster callers, and the only path from outside the cluster runs through a
    /// proxy that will not forward a request until the identity provider has authenticated it.
    /// </summary>
    public const int SsoProxyPort = 4180;

    /// <summary>
    /// Pinned rather than floating. This container is the authentication boundary for the UI, so
    /// "whatever :latest is today" is not an acceptable answer for what is enforcing it.
    /// </summary>
    public const string SsoProxyImage = "quay.io/oauth2-proxy/oauth2-proxy:v7.14.0";

    /// <summary>Vault secret name (and Secret key) holding the OIDC client secret.</summary>
    public const string SsoClientSecretName = "RSPAMD_OIDC_CLIENT_SECRET";

    /// <summary>Vault secret name (and Secret key) holding the proxy's cookie-signing key.</summary>
    public const string SsoCookieSecretName = "RSPAMD_OIDC_COOKIE_SECRET";

    public static string Build(RspamdSettings settings, string releaseName, string ns)
    {
        List<string> y = [];

        y.Add("apiVersion: v1");
        y.Add("kind: Namespace");
        y.Add("metadata:");
        y.Add($"  name: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("---");

        // ── Configuration ──
        //
        // The image keeps upstream defaults in /usr/share/rspamd/config and reads local overrides
        // from /etc/rspamd/local.d, so replacing that directory wholesale is the supported way to
        // configure it — everything not named here keeps the upstream default.
        y.Add("apiVersion: v1");
        y.Add("kind: ConfigMap");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-config");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("data:");

        List<string> redis = [$"servers = \"{settings.RedisServers}\";"];
        if (!string.IsNullOrWhiteSpace(settings.RedisPassword))
        {
            redis.Add($"password = \"{Escape(settings.RedisPassword!)}\";");
        }
        AddFile(y, "redis.conf", redis);

        AddFile(y, "worker-proxy.inc",
        [
            $"bind_socket = \"*:{MilterPort}\";",
            "milter = yes;",
            "timeout = 120s;",
            "upstream \"local\" {",
            "  default = yes;",
            // self_scan keeps the scan in this process instead of proxying it to a normal worker,
            // which is what makes a single-pod rspamd work at all.
            "  self_scan = yes;",
            "}",
        ]);

        AddFile(y, "worker-normal.inc", [$"bind_socket = \"*:{NormalPort}\";"]);

        List<string> controller = [$"bind_socket = \"*:{ControllerPort}\";"];
        if (!string.IsNullOrWhiteSpace(settings.ControllerPassword))
        {
            string escaped = Escape(settings.ControllerPassword!);
            controller.Add($"password = \"{escaped}\";");
            controller.Add($"enable_password = \"{escaped}\";");
        }
        if (settings.SsoEnabled)
        {
            // Requests arriving over loopback are already authenticated, because the only thing on this
            // pod's loopback is the OIDC proxy beside the controller — nothing else can reach it. This is
            // rspamd's documented way to defer authentication to a proxy in front, and it is scoped to
            // 127.0.0.1 on purpose: anything reaching the controller on its Service address, from
            // elsewhere in the cluster, still meets the password.
            controller.Add("secure_ip = \"127.0.0.1\";");
        }
        AddFile(y, "worker-controller.inc", controller);

        AddFile(y, "logging.inc",
        [
            // Logs go to stdout so they end up wherever the cluster's logs go, rather than in a
            // file inside a pod that nobody will read.
            "type = \"console\";",
            "level = \"info\";",
        ]);

        AddFile(y, "classifier-bayes.conf",
        [
            "backend = \"redis\";",
            "autolearn = true;",
        ]);

        AddFile(y, "milter_headers.conf",
        [
            // Stalwart reads the verdict from these headers, so they have to be added at the
            // milter stage rather than left to the recipient's client to infer.
            "use = [\"x-spamd-bar\", \"x-spam-level\", \"x-spam-status\", \"authentication-results\"];",
        ]);

        y.Add("---");

        // ── State volume ──
        y.Add("apiVersion: v1");
        y.Add("kind: PersistentVolumeClaim");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-data");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  accessModes: [ReadWriteOnce]");
        if (!string.IsNullOrWhiteSpace(settings.StorageClass))
        {
            y.Add($"  storageClassName: {settings.StorageClass!.Trim()}");
        }
        y.Add("  resources:");
        y.Add("    requests:");
        y.Add($"      storage: {settings.StorageSize}");
        y.Add("---");

        // ── Workload ──
        y.Add("apiVersion: apps/v1");
        y.Add("kind: Deployment");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  replicas: 1");
        y.Add("  strategy:");
        // The state volume is ReadWriteOnce, so a rolling update would deadlock waiting for a
        // second pod to mount a volume the first one still holds.
        y.Add("    type: Recreate");
        y.Add("  selector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {releaseName}");
        y.Add("  template:");
        y.Add("    metadata:");
        y.Add("      labels:");
        y.Add($"        app: {releaseName}");
        y.Add("    spec:");
        y.Add("      securityContext:");
        y.Add($"        fsGroup: {RunAsGroup}");
        y.Add("      containers:");
        y.Add("        - name: rspamd");
        y.Add($"          image: {Image}");
        y.Add("          ports:");
        y.Add("            - name: milter");
        y.Add($"              containerPort: {MilterPort}");
        y.Add("            - name: normal");
        y.Add($"              containerPort: {NormalPort}");
        y.Add("            - name: controller");
        y.Add($"              containerPort: {ControllerPort}");
        y.Add("          volumeMounts:");
        y.Add("            - name: config");
        y.Add("              mountPath: /etc/rspamd/local.d");
        y.Add("              readOnly: true");
        y.Add("            - name: data");
        y.Add("              mountPath: /var/lib/rspamd");
        y.Add("          readinessProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /ping");
        y.Add($"              port: {ControllerPort}");
        y.Add("            periodSeconds: 10");
        y.Add("          livenessProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /ping");
        y.Add($"              port: {ControllerPort}");
        y.Add("            periodSeconds: 30");
        y.Add("          resources:");
        y.Add("            requests:");
        y.Add("              cpu: 100m");
        y.Add("              memory: 256Mi");
        y.Add("            limits:");
        // Rspamd loads its rule set and Bayes cache into memory; too tight a ceiling shows up as
        // an OOMKill in the middle of a scan, which reads as a mail delivery failure.
        y.Add("              memory: 1Gi");
        if (settings.SsoEnabled)
        {
            // A sidecar rather than its own Deployment, and the reason is the security property: the
            // proxy reaches the controller over the pod's loopback, which no other pod can address. A
            // separate Deployment would have to talk to the controller's Service, and a controller that
            // trusts that Service address trusts every workload in the cluster.
            y.Add("        - name: oauth2-proxy");
            y.Add($"          image: {SsoProxyImage}");
            y.Add("          args:");
            y.Add("            - --provider=oidc");
            y.Add($"            - --oidc-issuer-url={settings.SsoIssuerUrl!.Trim().TrimEnd('/')}");
            y.Add($"            - --client-id={settings.SsoClientId!.Trim()}");
            y.Add($"            - --redirect-url=https://{settings.WebUiHostname!.Trim()}/oauth2/callback");
            y.Add($"            - --email-domain={settings.SsoEmailDomain}");
            y.Add($"            - --http-address=0.0.0.0:{SsoProxyPort}");
            y.Add($"            - --upstream=http://127.0.0.1:{ControllerPort}");
            // Behind the cluster gateway, so the scheme and host come from the forwarded headers;
            // without this the proxy builds its redirects from the pod's own address and the login
            // round-trip lands on a URL that only resolves inside the cluster.
            y.Add("            - --reverse-proxy=true");
            y.Add("            - --cookie-secure=true");
            // The provider button is a page with one button on it.
            y.Add("            - --skip-provider-button=true");
            y.Add("          env:");
            y.Add("            - name: OAUTH2_PROXY_CLIENT_SECRET");
            y.Add("              valueFrom:");
            y.Add("                secretKeyRef:");
            y.Add($"                  name: {releaseName}-credentials");
            y.Add($"                  key: {SsoClientSecretName}");
            y.Add("            - name: OAUTH2_PROXY_COOKIE_SECRET");
            y.Add("              valueFrom:");
            y.Add("                secretKeyRef:");
            y.Add($"                  name: {releaseName}-credentials");
            y.Add($"                  key: {SsoCookieSecretName}");
            y.Add("          ports:");
            y.Add("            - name: sso");
            y.Add($"              containerPort: {SsoProxyPort}");
            y.Add("          readinessProbe:");
            y.Add("            httpGet:");
            y.Add("              path: /ping");
            y.Add($"              port: {SsoProxyPort}");
            y.Add("            periodSeconds: 10");
            y.Add("          resources:");
            y.Add("            requests:");
            y.Add("              cpu: 10m");
            y.Add("              memory: 32Mi");
            y.Add("            limits:");
            y.Add("              memory: 128Mi");
        }
        y.Add("      volumes:");
        y.Add("        - name: config");
        y.Add("          configMap:");
        y.Add($"            name: {releaseName}-config");
        y.Add("        - name: data");
        y.Add("          persistentVolumeClaim:");
        y.Add($"            claimName: {releaseName}-data");
        y.Add("---");

        // ── Service ──
        y.Add("apiVersion: v1");
        y.Add("kind: Service");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  type: ClusterIP");
        y.Add("  selector:");
        y.Add($"    app: {releaseName}");
        y.Add("  ports:");
        y.Add("    - name: milter");
        y.Add($"      port: {MilterPort}");
        y.Add($"      targetPort: {MilterPort}");
        y.Add("    - name: normal");
        y.Add($"      port: {NormalPort}");
        y.Add($"      targetPort: {NormalPort}");
        y.Add("    - name: controller");
        y.Add($"      port: {ControllerPort}");
        y.Add($"      targetPort: {ControllerPort}");
        if (settings.SsoEnabled)
        {
            // The port the public route targets when single sign-on is on. The controller port stays
            // exposed for in-cluster clients, which is why it keeps its password.
            y.Add("    - name: sso");
            y.Add($"      port: {SsoProxyPort}");
            y.Add($"      targetPort: {SsoProxyPort}");
        }

        return string.Join("\n", y) + "\n";
    }

    private static void AddFile(List<string> y, string name, IEnumerable<string> lines)
    {
        y.Add($"  {name}: |");
        foreach (string line in lines)
        {
            y.Add("    " + line);
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>How a webmail client authenticates its users.</summary>
public enum WebmailAuthMode
{
    /// <summary>Username and password, checked by the mail server against its directory.</summary>
    Password = 0,

    /// <summary>
    /// An OpenID Connect login against Keycloak. The webmail obtains an access token and presents
    /// it to Stalwart over IMAP and SMTP as <c>XOAUTH2</c>, so the mail server never sees a
    /// password and the user gets the same single sign-on as everything else.
    /// </summary>
    Oidc = 1,
}

/// <summary>Settings for the Roundcube deployment.</summary>
/// <param name="ImapHost">IMAP endpoint, e.g. <c>tls://stalwart.stalwart.svc.cluster.local</c>.</param>
/// <param name="ImapPort">IMAP port.</param>
/// <param name="SmtpHost">SMTP submission endpoint.</param>
/// <param name="SmtpPort">SMTP submission port.</param>
/// <param name="DesKey">24-character key Roundcube encrypts session data with.</param>
/// <param name="AuthMode">Password login or Keycloak single sign-on.</param>
/// <param name="OidcIssuerUrl">Keycloak realm URL, when <paramref name="AuthMode"/> is Oidc.</param>
/// <param name="OidcClientId">OAuth client id registered in Keycloak.</param>
/// <param name="OidcClientSecret">That client's secret.</param>
/// <param name="OidcProviderName">Label on the sign-in button.</param>
/// <param name="OidcSkipLoginForm">Send users straight to Keycloak instead of showing a login form.</param>
/// <param name="StorageSize">Size of the volume holding the SQLite database.</param>
/// <param name="StorageClass">Storage class for that volume.</param>
public sealed record RoundcubeSettings(
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort,
    string DesKey,
    WebmailAuthMode AuthMode = WebmailAuthMode.Password,
    string? OidcIssuerUrl = null,
    string? OidcClientId = null,
    string? OidcClientSecret = null,
    string? OidcProviderName = "Single sign-on",
    bool OidcSkipLoginForm = false,
    string StorageSize = "2Gi",
    string? StorageClass = null);

/// <summary>Settings for the SnappyMail deployment.</summary>
public sealed record SnappyMailSettings(
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort,
    string StorageSize = "2Gi",
    string? StorageClass = null);

/// <summary>
/// Renders the two webmail clients.
///
/// <para>They are not interchangeable, and the difference is the whole reason both are offered.
/// Roundcube implements the OAuth2 authorization-code flow and forwards the resulting token to
/// IMAP and SMTP as <c>XOAUTH2</c>, which is what makes Keycloak single sign-on reach the mail
/// server. SnappyMail's OIDC support is partial and oriented at Nextcloud, so it is offered as the
/// lighter password-login client and is not wired for SSO here.</para>
/// </summary>
public static class WebmailManifestBuilder
{
    public const string RoundcubeImage = "roundcube/roundcubemail:1.7.4-apache";
    public const string SnappyMailImage = "djmaze/snappymail:v2.38.2";

    /// <summary>Port Roundcube's Apache serves on.</summary>
    public const int RoundcubePort = 80;

    /// <summary>Port SnappyMail's built-in server listens on.</summary>
    public const int SnappyMailPort = 8888;

    public static string BuildRoundcube(RoundcubeSettings settings, string releaseName, string ns)
    {
        List<string> y = [];

        AppendNamespace(y, ns);

        // ── Extra configuration ──
        //
        // The image writes its own config from the environment and then includes every *.php in
        // /var/roundcube/config afterwards, so anything set here wins. That ordering is what lets
        // the OAuth block and the connection options live in a ConfigMap instead of being
        // squeezed through environment variables.
        List<string> php =
        [
            "<?php",
            $"$config['imap_host'] = '{Escape(settings.ImapHost)}:{settings.ImapPort}';",
            $"$config['smtp_host'] = '{Escape(settings.SmtpHost)}:{settings.SmtpPort}';",
            "// The hop to the mail server stays inside the cluster, where the certificate is issued",
            "// for the public mail hostname rather than the Service name it is reached by. Point the",
            "// hosts at the public name instead if full verification matters more than the extra hop.",
            "$config['imap_conn_options'] = ['ssl' => ['verify_peer' => false, 'verify_peer_name' => false]];",
            "$config['smtp_conn_options'] = ['ssl' => ['verify_peer' => false, 'verify_peer_name' => false]];",
            "$config['smtp_user'] = '%u';",
            "$config['smtp_pass'] = '%p';",
        ];

        if (settings.AuthMode == WebmailAuthMode.Oidc
            && !string.IsNullOrWhiteSpace(settings.OidcIssuerUrl)
            && !string.IsNullOrWhiteSpace(settings.OidcClientId))
        {
            string issuer = settings.OidcIssuerUrl!.Trim().TrimEnd('/');
            php.AddRange(
            [
                "// Keycloak single sign-on. Roundcube performs the authorization-code flow and then",
                "// authenticates to IMAP and SMTP with XOAUTH2, so the mail server validates the",
                "// token itself and never sees a password.",
                "$config['oauth_provider'] = 'generic';",
                $"$config['oauth_provider_name'] = '{Escape(settings.OidcProviderName ?? "Single sign-on")}';",
                $"$config['oauth_client_id'] = '{Escape(settings.OidcClientId!)}';",
                $"$config['oauth_config_uri'] = '{Escape(issuer)}/.well-known/openid-configuration';",
                "$config['oauth_scope'] = 'openid email profile';",
                "$config['oauth_identity_fields'] = ['email'];",
                "// Required by the discovery-document and JWKS lookups.",
                "$config['oauth_cache'] = 'db';",
                $"$config['oauth_login_redirect'] = {(settings.OidcSkipLoginForm ? "true" : "false")};",
            ]);
        }

        y.Add("apiVersion: v1");
        y.Add("kind: ConfigMap");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-config");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("data:");
        // Named to sort last: the image includes these files in alphabetical order.
        y.Add("  zz-entkube.inc.php: |");
        foreach (string line in php)
        {
            y.Add("    " + line);
        }
        y.Add("---");

        AppendPvc(y, $"{releaseName}-data", ns, settings.StorageSize, settings.StorageClass);

        y.Add("apiVersion: apps/v1");
        y.Add("kind: Deployment");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  replicas: 1");
        y.Add("  strategy:");
        y.Add("    type: Recreate");
        y.Add("  selector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {releaseName}");
        y.Add("  template:");
        y.Add("    metadata:");
        y.Add("      labels:");
        y.Add($"        app: {releaseName}");
        y.Add("    spec:");
        y.Add("      containers:");
        y.Add("        - name: roundcube");
        y.Add($"          image: {RoundcubeImage}");
        y.Add("          env:");
        y.Add("            - name: ROUNDCUBEMAIL_DB_TYPE");
        y.Add("              value: sqlite");
        y.Add("            - name: ROUNDCUBEMAIL_DEFAULT_HOST");
        y.Add($"              value: \"{settings.ImapHost}\"");
        y.Add("            - name: ROUNDCUBEMAIL_DEFAULT_PORT");
        y.Add($"              value: \"{settings.ImapPort}\"");
        y.Add("            - name: ROUNDCUBEMAIL_SMTP_SERVER");
        y.Add($"              value: \"{settings.SmtpHost}\"");
        y.Add("            - name: ROUNDCUBEMAIL_SMTP_PORT");
        y.Add($"              value: \"{settings.SmtpPort}\"");
        y.Add("            - name: ROUNDCUBEMAIL_PLUGINS");
        y.Add("              value: \"archive,zipdownload,managesieve\"");
        y.Add("            - name: ROUNDCUBEMAIL_UPLOAD_MAX_FILESIZE");
        y.Add("              value: \"25M\"");
        // Without a fixed key the image generates one on every start, and because the config
        // directory is not persisted that silently invalidates every session on each restart.
        y.Add("            - name: ROUNDCUBEMAIL_DES_KEY");
        y.Add($"              value: \"{settings.DesKey}\"");
        if (settings.AuthMode == WebmailAuthMode.Oidc && !string.IsNullOrWhiteSpace(settings.OidcClientSecret))
        {
            y.Add("            - name: ROUNDCUBEMAIL_OAUTH_CLIENT_SECRET");
            y.Add("              valueFrom:");
            y.Add("                secretKeyRef:");
            y.Add($"                  name: {releaseName}-credentials");
            y.Add($"                  key: {RoundcubeOauthSecretName}");
        }
        y.Add("          ports:");
        y.Add("            - name: http");
        y.Add($"              containerPort: {RoundcubePort}");
        y.Add("          volumeMounts:");
        y.Add("            - name: db");
        y.Add("              mountPath: /var/roundcube/db");
        y.Add("            - name: config");
        y.Add("              mountPath: /var/roundcube/config");
        y.Add("              readOnly: true");
        y.Add("            - name: temp");
        y.Add("              mountPath: /tmp/roundcube-temp");
        y.Add("          readinessProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /");
        y.Add($"              port: {RoundcubePort}");
        y.Add("            initialDelaySeconds: 20");
        y.Add("            periodSeconds: 10");
        y.Add("          resources:");
        y.Add("            requests:");
        y.Add("              cpu: 100m");
        y.Add("              memory: 256Mi");
        y.Add("            limits:");
        y.Add("              memory: 768Mi");
        y.Add("      volumes:");
        y.Add("        - name: db");
        y.Add("          persistentVolumeClaim:");
        y.Add($"            claimName: {releaseName}-data");
        y.Add("        - name: config");
        y.Add("          configMap:");
        y.Add($"            name: {releaseName}-config");
        y.Add("        - name: temp");
        y.Add("          emptyDir: {}");
        y.Add("---");

        AppendService(y, releaseName, ns, RoundcubePort);

        return string.Join("\n", y) + "\n";
    }

    /// <summary>Vault secret name holding Roundcube's OAuth client secret.</summary>
    public const string RoundcubeOauthSecretName = "ROUNDCUBEMAIL_OAUTH_CLIENT_SECRET";

    public static string BuildSnappyMail(SnappyMailSettings settings, string releaseName, string ns)
    {
        List<string> y = [];

        AppendNamespace(y, ns);

        // SnappyMail keeps its domain definitions inside the data directory, which is a volume — so
        // the seed cannot simply be mounted over it. An init container drops the file in on first
        // start and then leaves it alone, so an administrator's later edits in the admin panel are
        // not overwritten on every restart.
        y.Add("apiVersion: v1");
        y.Add("kind: ConfigMap");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-config");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("data:");
        y.Add("  default.json: |");
        foreach (string line in SnappyMailDomainJson(settings).Split('\n'))
        {
            y.Add("    " + line);
        }
        y.Add("---");

        AppendPvc(y, $"{releaseName}-data", ns, settings.StorageSize, settings.StorageClass);

        y.Add("apiVersion: apps/v1");
        y.Add("kind: Deployment");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  replicas: 1");
        y.Add("  strategy:");
        y.Add("    type: Recreate");
        y.Add("  selector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {releaseName}");
        y.Add("  template:");
        y.Add("    metadata:");
        y.Add("      labels:");
        y.Add($"        app: {releaseName}");
        y.Add("    spec:");
        y.Add("      initContainers:");
        y.Add("        - name: seed-domain");
        y.Add($"          image: {SnappyMailImage}");
        y.Add("          command: [\"/bin/sh\", \"-c\"]");
        y.Add("          args:");
        y.Add("            - |");
        y.Add("              set -e");
        y.Add("              dir=/var/lib/snappymail/_data_/_default_/domains");
        y.Add("              mkdir -p \"$dir\"");
        y.Add("              if [ ! -f \"$dir/default.json\" ]; then");
        y.Add("                cp /seed/default.json \"$dir/default.json\"");
        y.Add("                echo \"seeded $dir/default.json\"");
        y.Add("              else");
        y.Add("                echo \"$dir/default.json already exists, leaving it alone\"");
        y.Add("              fi");
        y.Add("          volumeMounts:");
        y.Add("            - name: data");
        y.Add("              mountPath: /var/lib/snappymail");
        y.Add("            - name: seed");
        y.Add("              mountPath: /seed");
        y.Add("              readOnly: true");
        y.Add("      containers:");
        y.Add("        - name: snappymail");
        y.Add($"          image: {SnappyMailImage}");
        y.Add("          ports:");
        y.Add("            - name: http");
        y.Add($"              containerPort: {SnappyMailPort}");
        y.Add("          volumeMounts:");
        y.Add("            - name: data");
        y.Add("              mountPath: /var/lib/snappymail");
        y.Add("          readinessProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /");
        y.Add($"              port: {SnappyMailPort}");
        y.Add("            initialDelaySeconds: 10");
        y.Add("            periodSeconds: 10");
        y.Add("          resources:");
        y.Add("            requests:");
        y.Add("              cpu: 50m");
        y.Add("              memory: 128Mi");
        y.Add("            limits:");
        y.Add("              memory: 512Mi");
        y.Add("      volumes:");
        y.Add("        - name: data");
        y.Add("          persistentVolumeClaim:");
        y.Add($"            claimName: {releaseName}-data");
        y.Add("        - name: seed");
        y.Add("          configMap:");
        y.Add($"            name: {releaseName}-config");
        y.Add("---");

        AppendService(y, releaseName, ns, SnappyMailPort);

        return string.Join("\n", y) + "\n";
    }

    /// <summary>
    /// SnappyMail's fallback domain definition. Named <c>default</c> because that is the entry
    /// SnappyMail falls back to for any login domain it has no explicit configuration for — one
    /// file therefore covers every domain the mail server serves.
    /// </summary>
    internal static string SnappyMailDomainJson(SnappyMailSettings settings) =>
        $$"""
        {
          "IMAP": {
            "host": "{{settings.ImapHost}}",
            "port": {{settings.ImapPort}},
            "type": 0,
            "timeout": 300,
            "shortLogin": false,
            "lowerLogin": true,
            "ssl": { "verify_peer": false, "verify_peer_name": false }
          },
          "SMTP": {
            "host": "{{settings.SmtpHost}}",
            "port": {{settings.SmtpPort}},
            "type": 0,
            "timeout": 60,
            "shortLogin": false,
            "lowerLogin": true,
            "useAuth": true,
            "setSender": false,
            "usePhpMail": false,
            "ssl": { "verify_peer": false, "verify_peer_name": false }
          },
          "whiteList": ""
        }
        """;

    private static void AppendNamespace(List<string> y, string ns)
    {
        y.Add("apiVersion: v1");
        y.Add("kind: Namespace");
        y.Add("metadata:");
        y.Add($"  name: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("---");
    }

    private static void AppendPvc(
        List<string> y, string name, string ns, string size, string? storageClass)
    {
        y.Add("apiVersion: v1");
        y.Add("kind: PersistentVolumeClaim");
        y.Add("metadata:");
        y.Add($"  name: {name}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  accessModes: [ReadWriteOnce]");
        if (!string.IsNullOrWhiteSpace(storageClass))
        {
            y.Add($"  storageClassName: {storageClass!.Trim()}");
        }
        y.Add("  resources:");
        y.Add("    requests:");
        y.Add($"      storage: {size}");
        y.Add("---");
    }

    private static void AppendService(List<string> y, string releaseName, string ns, int port)
    {
        y.Add("apiVersion: v1");
        y.Add("kind: Service");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  type: ClusterIP");
        y.Add("  selector:");
        y.Add($"    app: {releaseName}");
        y.Add("  ports:");
        y.Add("    - name: http");
        y.Add("      port: 80");
        y.Add($"      targetPort: {port}");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
