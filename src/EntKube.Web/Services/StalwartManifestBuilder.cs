using System.Text;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Renders the Kubernetes manifest for a Stalwart deployment from its
/// <see cref="StalwartComponentConfig"/>. EntKube regenerates and re-applies this on every save,
/// so the manifest is a projection of the config rather than something an operator edits by hand.
///
/// <para>Two Services, on purpose. The cluster-internal one carries every port, because webmail
/// and the apply Job talk to it from inside. The public one carries only the mail ports and is a
/// LoadBalancer, because mail wants an address of its own: an MX record has to point at something
/// with matching reverse DNS, and <c>externalTrafficPolicy: Local</c> keeps the client IP that
/// SPF, DNSBL lookups and rspamd's scoring all read. The web surfaces deliberately do not appear
/// there — they go through the cluster's Istio or Traefik gateway like every other HTTPS hostname
/// EntKube publishes.</para>
///
/// <para>YAML is emitted line by line with explicit indentation rather than by interpolating
/// blocks into each other, because the indentation is the syntax and getting it wrong produces a
/// manifest that parses into something subtly different rather than failing loudly.</para>
/// </summary>
public static class StalwartManifestBuilder
{
    /// <summary>Container image. Pinned: mail server upgrades are migrations, not restarts.</summary>
    public const string Image = "stalwartlabs/stalwart:v0.16.21";

    /// <summary>Image carrying <c>stalwart-cli</c>, used by the apply Job. Distroless, no shell.</summary>
    public const string CliImage = "stalwartlabs/cli:1.0.12";

    /// <summary>UID the upstream image runs as.</summary>
    public const int RunAsUser = 2000;

    /// <summary>Suffix of the cert-manager-issued TLS Secret for the mail hostname.</summary>
    public const string TlsSecretSuffix = "-tls";

    /// <summary>Suffix of the Secret the vault syncs component credentials into.</summary>
    public const string CredentialsSecretSuffix = "-credentials";

    /// <summary>Vault secret name for the recovery/administrator password.</summary>
    public const string AdminPasswordSecretName = "STALWART_ADMIN_PASSWORD";

    /// <summary>Vault secret name for the LDAP bind password.</summary>
    public const string LdapBindPasswordSecretName = StalwartPlanBuilder.LdapBindPasswordEnv;

    /// <summary>Vault secret names holding an operator-supplied certificate.</summary>
    public const string TlsCertSecretName = "STALWART_TLS_CERT";
    public const string TlsKeySecretName = "STALWART_TLS_KEY";

    /// <summary>Name of the Job that replays the apply plan.</summary>
    public static string ApplyJobName(string releaseName) => $"{releaseName}-apply";

    /// <summary>
    /// Builds the full manifest: Namespace, config ConfigMap, StatefulSet and Services.
    /// </summary>
    /// <param name="recoveryMode">
    /// Render the StatefulSet in recovery mode. Recovery mode is how a declaratively-deployed
    /// server gets configured at all: the recovery administrator is the only credential that
    /// bypasses the directory, and Stalwart honours it only in recovery mode or during first
    /// bootstrap. An apply run therefore re-applies this manifest with recovery on, replays the
    /// plan against the management port, and re-applies it with recovery off. Mail is not accepted
    /// in between, which is why applying configuration is an explicit operator action rather than
    /// something that happens on a timer.
    /// </param>
    public static string Build(
        StalwartComponentConfig config, string releaseName, string ns, bool recoveryMode = false,
        StalwartPlanBuilder.StalwartHaBackend? ha = null)
    {
        List<string> y = [];

        // ── Namespace ──
        y.Add("apiVersion: v1");
        y.Add("kind: Namespace");
        y.Add("metadata:");
        y.Add($"  name: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("---");

        // ── config.json ──
        y.Add("apiVersion: v1");
        y.Add("kind: ConfigMap");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-config");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("data:");
        y.Add("  # v0.16 keeps only the datastore on disk. Domains, listeners, the directory and the");
        y.Add("  # spam filter live inside that store and are reconciled by the apply plan instead.");
        y.Add("  config.json: |");
        foreach (string line in StalwartPlanBuilder.BuildConfigJson(ha).Split('\n'))
        {
            y.Add("    " + line.TrimEnd('\r'));
        }
        y.Add("---");

        AppendStatefulSet(y, config, releaseName, ns, recoveryMode, ha);
        AppendHeadlessService(y, releaseName, ns);
        AppendInternalService(y, config, releaseName, ns);

        if (config.ExposeMode == StalwartMailExposeMode.LoadBalancer)
        {
            AppendMailService(y, config, releaseName, ns);
        }

        // Each document appends its own separator, so the last one leaves a trailing "---" and an
        // empty document behind it.
        if (y[^1] == "---")
        {
            y.RemoveAt(y.Count - 1);
        }

        return string.Join("\n", y) + "\n";
    }

    private static void AppendStatefulSet(
        List<string> y, StalwartComponentConfig config, string releaseName, string ns, bool recoveryMode,
        StalwartPlanBuilder.StalwartHaBackend? ha)
    {
        y.Add("apiVersion: apps/v1");
        y.Add("kind: StatefulSet");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add($"  serviceName: {releaseName}-headless");
        y.Add($"  replicas: {(ha is null ? 1 : Math.Max(1, ha.Replicas))}");
        y.Add("  selector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {releaseName}");
        y.Add("  template:");
        y.Add("    metadata:");
        y.Add("      labels:");
        y.Add($"        app: {releaseName}");
        y.Add("    spec:");
        y.Add("      securityContext:");
        y.Add($"        fsGroup: {RunAsUser}");
        y.Add("      containers:");
        y.Add("        - name: stalwart");
        y.Add($"          image: {Image}");
        y.Add($"          args: [\"--config\", \"{StalwartPlanBuilder.ConfigPath}\"]");
        y.Add("          securityContext:");
        y.Add($"            runAsUser: {RunAsUser}");
        y.Add($"            runAsGroup: {RunAsUser}");
        y.Add("            allowPrivilegeEscalation: false");
        y.Add("            capabilities:");
        y.Add("              drop: [\"ALL\"]");
        y.Add("              # Port 25 is privileged, and a mail server that cannot bind it cannot");
        y.Add("              # receive mail. This is the one capability the image genuinely needs.");
        y.Add("              add: [\"NET_BIND_SERVICE\"]");
        y.Add("          ports:");
        foreach ((string name, int port) in ContainerPorts(config))
        {
            y.Add($"            - name: {name}");
            y.Add($"              containerPort: {port}");
        }

        List<string> env = [];
        if (recoveryMode)
        {
            env.Add("            - name: STALWART_RECOVERY_MODE");
            env.Add("              value: \"1\"");
            env.Add($"            - name: {RecoveryAdminSecretName}");
            env.Add("              valueFrom:");
            env.Add("                secretKeyRef:");
            env.Add($"                  name: {releaseName}{CredentialsSecretSuffix}");
            env.Add($"                  key: {RecoveryAdminSecretName}");
        }
        if (!string.IsNullOrWhiteSpace(config.AdminHostname))
        {
            env.Add("            - name: STALWART_PUBLIC_URL");
            env.Add($"              value: https://{config.AdminHostname!.Trim()}");
        }
        if (config.AuthMode == StalwartAuthMode.Ldap)
        {
            // The directory object references this variable by name rather than carrying the
            // password, so the credential exists only in the Secret and the process environment —
            // never in the apply plan, which is written to disk and logged.
            env.Add($"            - name: {StalwartPlanBuilder.LdapBindPasswordEnv}");
            env.Add("              valueFrom:");
            env.Add("                secretKeyRef:");
            env.Add($"                  name: {releaseName}{CredentialsSecretSuffix}");
            env.Add($"                  key: {LdapBindPasswordSecretName}");
            env.Add("                  optional: true");
        }
        if (ha is not null)
        {
            // Every node runs the same role; config.json's PostgreSQL datastore, the S3 blob store
            // and the Redis coordinator all read their secret from the environment, never from a
            // value written into config or the plan. node-id is the StatefulSet's stable hostname,
            // so no identity env is needed.
            env.Add("            - name: STALWART_ROLE");
            env.Add($"              value: {StalwartPlanBuilder.ClusterRoleName}");
            foreach ((string envName, string secretKey) in new[]
            {
                (StalwartPlanBuilder.DbPasswordEnv, StalwartPlanBuilder.DbPasswordEnv),
                (StalwartPlanBuilder.S3SecretKeyEnv, StalwartPlanBuilder.S3SecretKeyEnv),
                // The Redis password is not injected: the standalone-Redis store reads no env var,
                // so the coordinator/in-memory URL carries the password inline instead.
            })
            {
                env.Add($"            - name: {envName}");
                env.Add("              valueFrom:");
                env.Add("                secretKeyRef:");
                env.Add($"                  name: {releaseName}{CredentialsSecretSuffix}");
                env.Add($"                  key: {secretKey}");
                // A missing secret should surface as an auth error the node logs, not a pod that
                // will not schedule — so both are marked optional and their absence is a preflight
                // concern, checked before the apply.
                env.Add("                  optional: true");
            }
        }
        if (env.Count > 0)
        {
            y.Add("          env:");
            y.AddRange(env);
        }

        y.Add("          volumeMounts:");
        if (ha is null)
        {
            y.Add("            - name: data");
            y.Add($"              mountPath: {StalwartPlanBuilder.DataPath}");
        }
        y.Add("            - name: config");
        y.Add($"              mountPath: {StalwartPlanBuilder.ConfigPath}");
        y.Add("              subPath: config.json");
        y.Add("              readOnly: true");
        if (MountsTlsSecret(config))
        {
            y.Add("            - name: tls");
            y.Add($"              mountPath: {StalwartPlanBuilder.TlsMountPath}");
            y.Add("              readOnly: true");
        }

        // The probes carry a forwarding header. Every web request arrives through the gateway, so
        // the server is told to trust X-Forwarded-For — and then logs a WARN for each request that
        // lacks one. Kubelet's probes lack one, every five seconds, which buried the only auth
        // failure anyone needed to read under three minutes of identical noise.
        y.Add("          startupProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /healthz/live");
        y.Add($"              port: {StalwartPlanBuilder.HttpPort}");
        y.Add("              httpHeaders:");
        y.Add("                - name: X-Forwarded-For");
        y.Add("                  value: 127.0.0.1");
        y.Add("            periodSeconds: 5");
        y.Add("            # Opening a large RocksDB store is not instant, and a server killed");
        y.Add("            # part-way through opening it never gets far enough to finish.");
        y.Add("            failureThreshold: 60");
        y.Add("          livenessProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /healthz/live");
        y.Add($"              port: {StalwartPlanBuilder.HttpPort}");
        y.Add("              httpHeaders:");
        y.Add("                - name: X-Forwarded-For");
        y.Add("                  value: 127.0.0.1");
        y.Add("            periodSeconds: 30");
        y.Add("          readinessProbe:");
        y.Add("            httpGet:");
        y.Add("              path: /healthz/ready");
        y.Add($"              port: {StalwartPlanBuilder.HttpPort}");
        y.Add("              httpHeaders:");
        y.Add("                - name: X-Forwarded-For");
        y.Add("                  value: 127.0.0.1");
        y.Add("            periodSeconds: 10");
        y.Add("          resources:");
        y.Add("            requests:");
        y.Add("              cpu: 200m");
        y.Add("              memory: 512Mi");
        y.Add("            limits:");
        y.Add("              memory: 2Gi");

        y.Add("      volumes:");
        y.Add("        - name: config");
        y.Add("          configMap:");
        y.Add($"            name: {releaseName}-config");
        if (config.TlsMode == StalwartTlsMode.ClusterIssuer)
        {
            y.Add("        - name: tls");
            y.Add("          secret:");
            y.Add($"            secretName: {releaseName}{TlsSecretSuffix}");
        }
        else if (config.TlsMode == StalwartTlsMode.Manual)
        {
            // The vault syncs every component secret into one Opaque Secret keyed by vault name, so
            // the PEMs are projected onto the same file names cert-manager would have produced. The
            // Certificate object then names one pair of paths no matter where the cert came from.
            y.Add("        - name: tls");
            y.Add("          secret:");
            y.Add($"            secretName: {releaseName}{CredentialsSecretSuffix}");
            y.Add("            items:");
            y.Add($"              - key: {TlsCertSecretName}");
            y.Add("                path: tls.crt");
            y.Add($"              - key: {TlsKeySecretName}");
            y.Add("                path: tls.key");
        }

        // No local data volume in HA: the datastore is PostgreSQL and the blobs are in S3, so a
        // ReadWriteOnce PVC (which only one node could mount) would be exactly the wrong thing.
        if (ha is null)
        {
            y.Add("  volumeClaimTemplates:");
            y.Add("    - metadata:");
            y.Add("        name: data");
            y.Add("      spec:");
            y.Add("        accessModes: [ReadWriteOnce]");
            if (!string.IsNullOrWhiteSpace(config.StorageClass))
            {
                y.Add($"        storageClassName: {config.StorageClass!.Trim()}");
            }
            y.Add("        resources:");
            y.Add("          requests:");
            y.Add($"            storage: {config.StorageSize}");
        }
        y.Add("---");
    }

    private static void AppendHeadlessService(List<string> y, string releaseName, string ns)
    {
        y.Add("apiVersion: v1");
        y.Add("kind: Service");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-headless");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  clusterIP: None");
        y.Add("  selector:");
        y.Add($"    app: {releaseName}");
        y.Add("  ports:");
        y.Add("    - name: http");
        y.Add($"      port: {StalwartPlanBuilder.HttpPort}");
        y.Add("---");
    }

    private static void AppendInternalService(
        List<string> y, StalwartComponentConfig config, string releaseName, string ns)
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
        foreach ((string name, int port) in ContainerPorts(config))
        {
            y.Add($"    - name: {name}");
            y.Add($"      port: {port}");
            y.Add($"      targetPort: {port}");
        }
        y.Add("---");
    }

    private static void AppendMailService(
        List<string> y, StalwartComponentConfig config, string releaseName, string ns)
    {
        y.Add("apiVersion: v1");
        y.Add("kind: Service");
        y.Add("metadata:");
        y.Add($"  name: {releaseName}-mail");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {releaseName}");
        y.Add("    app.kubernetes.io/managed-by: entkube");

        List<(string Key, string Value)> annotations = ParseAnnotations(config.LoadBalancerAnnotations);
        if (annotations.Count > 0)
        {
            y.Add("  annotations:");
            foreach ((string key, string value) in annotations)
            {
                y.Add($"    {key}: \"{value.Replace("\"", "\\\"")}\"");
            }
        }

        y.Add("spec:");
        y.Add("  type: LoadBalancer");
        y.Add("  # Without this the forwarding node becomes the apparent sender, and every spam");
        y.Add("  # signal that reads the connecting IP — SPF, DNSBLs, rspamd's reputation scoring —");
        y.Add("  # is computed against a node in this cluster instead of the real client.");
        y.Add("  externalTrafficPolicy: Local");
        if (!string.IsNullOrWhiteSpace(config.LoadBalancerIp))
        {
            y.Add($"  loadBalancerIP: {config.LoadBalancerIp!.Trim()}");
        }
        y.Add("  selector:");
        y.Add($"    app: {releaseName}");
        y.Add("  ports:");
        foreach ((string name, int port) in ContainerPorts(config))
        {
            if (port == StalwartPlanBuilder.HttpPort)
            {
                continue;
            }
            y.Add($"    - name: {name}");
            y.Add($"      port: {port}");
            y.Add($"      targetPort: {port}");
        }
        y.Add("---");
    }

    /// <summary>Parses the operator's <c>key: value</c> lines into LoadBalancer annotations.</summary>
    public static List<(string Key, string Value)> ParseAnnotations(string? text)
    {
        List<(string, string)> result = [];
        foreach (string line in (text ?? "")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }
            int sep = line.IndexOf(':');
            if (sep <= 0)
            {
                continue;
            }
            result.Add((line[..sep].Trim(), line[(sep + 1)..].Trim()));
        }
        return result;
    }

    private static bool MountsTlsSecret(StalwartComponentConfig config) =>
        config.TlsMode is StalwartTlsMode.ClusterIssuer or StalwartTlsMode.Manual;

    /// <summary>
    /// True when Stalwart answers its own ACME challenge over plain HTTP, so port 80 has to be
    /// published on the mail address the certificate is for.
    /// </summary>
    public static bool NeedsAcmeHttpPort(StalwartComponentConfig config) =>
        config.TlsMode == StalwartTlsMode.Acme && config.AcmeChallenge == StalwartAcmeChallenge.Http01;

    /// <summary>
    /// True when the challenge is answered inside the TLS handshake (ALPN <c>acme-tls/1</c>), which
    /// the CA always attempts on port 443.
    /// </summary>
    public static bool NeedsAcmeTlsPort(StalwartComponentConfig config) =>
        config.TlsMode == StalwartTlsMode.Acme && config.AcmeChallenge == StalwartAcmeChallenge.TlsAlpn01;

    /// <summary>The ports the container listens on, given which protocols are enabled.</summary>
    public static IEnumerable<(string Name, int Port)> ContainerPorts(StalwartComponentConfig config)
    {
        if (config.SmtpEnabled)
        {
            yield return ("smtp", MailPorts.Smtp);
        }
        if (config.SubmissionEnabled)
        {
            yield return ("submission", MailPorts.Submission);
            yield return ("submissions", MailPorts.Submissions);
        }
        if (config.ImapEnabled)
        {
            yield return ("imap", MailPorts.Imap);
            yield return ("imaps", MailPorts.Imaps);
        }
        if (config.Pop3Enabled)
        {
            yield return ("pop3", MailPorts.Pop3);
            yield return ("pop3s", MailPorts.Pop3s);
        }
        if (config.ManageSieveEnabled)
        {
            yield return ("sieve", MailPorts.ManageSieve);
        }

        // The ACME ports exist only so the CA can reach the server it is issuing for, and they are
        // published on the mail address precisely because that is where the certificate's name
        // resolves. Nothing else is served on them: the allowedEndpoints expression in the apply
        // plan refuses every request on these listeners except the challenge itself, so opening
        // them does not put the admin interface on a public address.
        if (NeedsAcmeHttpPort(config))
        {
            yield return (AcmeHttpListener, MailPorts.AcmeHttp);
        }
        if (NeedsAcmeTlsPort(config))
        {
            yield return (AcmeTlsListener, MailPorts.AcmeTls);
        }

        yield return ("http", StalwartPlanBuilder.HttpPort);
    }

    /// <summary>Listener name for the HTTP-01 challenge port. Referenced by the endpoint policy.</summary>
    public const string AcmeHttpListener = "acme-http";

    /// <summary>Listener name for the TLS-ALPN-01 challenge port.</summary>
    public const string AcmeTlsListener = "acme-tls";

    /// <summary>
    /// A cert-manager Certificate for the mail hostname, issued into the Secret the StatefulSet
    /// mounts. Only the mail hostname is on it — the web hostname's certificate belongs to the
    /// gateway, which terminates that TLS itself.
    /// </summary>
    public static string BuildTlsCertificateManifest(
        string clusterIssuer, string hostname, string releaseName, string ns)
    {
        List<string> y =
        [
            "apiVersion: cert-manager.io/v1",
            "kind: Certificate",
            "metadata:",
            $"  name: {releaseName}{TlsSecretSuffix}",
            $"  namespace: {ns}",
            "  labels:",
            "    app.kubernetes.io/managed-by: entkube",
            "spec:",
            $"  secretName: {releaseName}{TlsSecretSuffix}",
            "  issuerRef:",
            $"    name: {clusterIssuer}",
            "    kind: ClusterIssuer",
            $"  commonName: {hostname}",
            "  dnsNames:",
            $"    - {hostname}",
        ];
        return string.Join("\n", y) + "\n";
    }

    /// <summary>
    /// The Secret holding the apply plan and the Job that replays it against the server's
    /// management port.
    ///
    /// <para>A Job rather than an exec: <c>stalwart-cli</c> ships in its own distroless image, so
    /// there is no shell to exec into and no CLI inside the mail container. Running it as a pod
    /// also means the administrator password arrives by <c>secretKeyRef</c> rather than on a
    /// command line visible to anyone who can read pods.</para>
    /// </summary>
    public static string BuildApplyJobManifest(
        string releaseName, string ns, string plan, string adminUsername)
    {
        string encodedPlan = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan));

        List<string> y =
        [
            "apiVersion: v1",
            "kind: Secret",
            "metadata:",
            $"  name: {releaseName}-apply-plan",
            $"  namespace: {ns}",
            "  labels:",
            "    app.kubernetes.io/managed-by: entkube",
            "type: Opaque",
            "data:",
            $"  plan.ndjson: {encodedPlan}",
            "---",
            "apiVersion: batch/v1",
            "kind: Job",
            "metadata:",
            $"  name: {ApplyJobName(releaseName)}",
            $"  namespace: {ns}",
            "  labels:",
            "    app.kubernetes.io/managed-by: entkube",
            "spec:",
            // No retries. A failed plan fails the same way every time, and EntKube reports the
            // first failure rather than making an operator wait out six identical ones.
            "  backoffLimit: 0",
            // No TTL. The previous Job is deleted before a new one is created, so nothing
            // accumulates — and a TTL would erase the only on-cluster record of what the last apply
            // did, right when somebody is trying to work out why the server misbehaves. (It did.)
            "  template:",
            "    metadata:",
            "      labels:",
            $"        app: {ApplyJobName(releaseName)}",
            "    spec:",
            "      restartPolicy: Never",
            "      containers:",
            "        - name: apply",
            $"          image: {CliImage}",
            "          args: [\"apply\", \"--file\", \"/plan/plan.ndjson\"]",
            "          env:",
            "            - name: STALWART_URL",
            $"              value: http://{releaseName}.{ns}.svc.cluster.local:{StalwartPlanBuilder.HttpPort}",
            "            - name: STALWART_USER",
            $"              value: {adminUsername}",
            "            - name: STALWART_PASSWORD",
            "              valueFrom:",
            "                secretKeyRef:",
            $"                  name: {releaseName}{CredentialsSecretSuffix}",
            $"                  key: {AdminPasswordSecretName}",
            "          volumeMounts:",
            "            - name: plan",
            "              mountPath: /plan",
            "              readOnly: true",
            "          resources:",
            "            requests:",
            "              cpu: 50m",
            "              memory: 64Mi",
            "            limits:",
            "              memory: 256Mi",
            "      volumes:",
            "        - name: plan",
            "          secret:",
            $"            secretName: {releaseName}-apply-plan",
        ];
        return string.Join("\n", y) + "\n";
    }

    /// <summary>
    /// Vault secret holding <c>username:password</c> in the form <c>STALWART_RECOVERY_ADMIN</c>
    /// expects. Kept separate from the bare password, which the CLI needs on its own.
    /// </summary>
    public const string RecoveryAdminSecretName = "STALWART_RECOVERY_ADMIN";
}
