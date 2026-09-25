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
        int configStart = y.Count;
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

        // Same reason as the mail stack's other workloads: a new ConfigMap is not a change to the
        // pod template, so without this the datastore can be repointed and the running nodes would
        // never notice. Applying configuration restarts them anyway; installing from the Components
        // tab does not, and that is the path where it would silently do nothing.
        string configHash = MailManifest.ConfigHash(y, configStart);

        AppendStatefulSet(y, config, releaseName, ns, recoveryMode, ha, configHash);
        AppendHeadlessService(y, releaseName, ns);
        AppendInternalService(y, config, releaseName, ns);

        if (ha is not null)
        {
            AppendCoordinator(y, releaseName, ns);
        }

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
        StalwartPlanBuilder.StalwartHaBackend? ha, string configHash)
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
        // One node while the configuration is being replayed, however many the deployment runs
        // normally. The plan is applied once, against one endpoint, and the other nodes have nothing
        // to do but read a datastore that is being rewritten underneath them — which is the one
        // situation Stalwart's own multi-node guidance warns about. Mail is already refused for the
        // duration of a recovery-mode apply, so this costs nothing that was not already lost.
        y.Add($"  replicas: {(recoveryMode || ha is null ? 1 : Math.Max(1, ha.Replicas))}");
        y.Add("  selector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {releaseName}");
        y.Add("  template:");
        y.Add("    metadata:");
        y.Add("      labels:");
        y.Add($"        app: {releaseName}");
        y.Add("      annotations:");
        y.Add($"        entkube.io/config-hash: {configHash}");
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
            List<(string EnvName, string SecretKey)> backendSecrets =
            [
                (StalwartPlanBuilder.DbPasswordEnv, StalwartPlanBuilder.DbPasswordEnv),
                (StalwartPlanBuilder.S3SecretKeyEnv, StalwartPlanBuilder.S3SecretKeyEnv),
            ];
            if (ha.RedisIsCluster)
            {
                // Only the cluster store can read a password from the environment; the standalone
                // one has no secret field at all and carries it inside the URL instead.
                backendSecrets.Add((StalwartPlanBuilder.RedisPasswordEnv, StalwartPlanBuilder.RedisPasswordEnv));
            }

            foreach ((string envName, string secretKey) in backendSecrets)
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
            y.Add($"            - name: {DataVolumeName}");
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
            y.Add($"        name: {DataVolumeName}");
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

    /// <summary>The coordinator's own name, in the mail server's namespace.</summary>
    public static string CoordinatorName(string releaseName) => $"{releaseName}-coordinator";

    /// <summary>In-cluster address of the bundled coordinator.</summary>
    public static string CoordinatorEndpoint(string releaseName, string ns) =>
        $"{CoordinatorName(releaseName)}.{ns}.svc.cluster.local:{CoordinatorPort}";

    /// <summary>Redis image for the bundled coordinator. Pinned; a coordinator is not a place for surprises.</summary>
    public const string CoordinatorImage = "redis:7.4-alpine";

    private const int CoordinatorPort = 6379;

    /// <summary>
    /// The <c>redis</c> user inside <see cref="CoordinatorImage"/>. Named explicitly because
    /// <c>runAsNonRoot</c> without a uid is a pod that fails admission rather than a pod that runs
    /// safely — the image sets no numeric USER of its own.
    /// </summary>
    private const int CoordinatorUid = 999;

    /// <summary>The <c>redis</c> group. 1000 on Alpine, unlike Debian's 999 — verified, not assumed.</summary>
    private const int CoordinatorGid = 1000;

    /// <summary>
    /// The Redis an HA deployment coordinates through, deployed as part of the mail server rather
    /// than chosen by an operator.
    ///
    /// <para>It used to be a picker. That asked the operator to answer three questions they had no
    /// way to get right: which of several near-identical Services belonging to one Redis to point
    /// at, what its password was, and whether it was sharded — because a sharded Redis needs a
    /// different store shape and a standalone client is redirected away from keys it does not own.
    /// Getting any of them wrong produced the same unhelpful sentence, and the one combination
    /// EntKube's own managed Redis produced — a cluster with a password — could not be made to
    /// authenticate at all on Stalwart 0.16.21, in any of the four spellings its schema allows.</para>
    ///
    /// <para>So the coordinator is plumbing now, like the headless Service: one node, no password,
    /// no shards, nothing to choose. What it holds is entirely ephemeral — pub/sub between nodes,
    /// task locks, rate limits — while durable state is the shared PostgreSQL and S3. That is what
    /// makes a single replica the right shape rather than a corner cut: losing it costs a blip and
    /// some re-run tasks, not mail. Persistence is switched off for the same reason; writing it to
    /// disk would only give the restart something stale to recover.</para>
    ///
    /// <para>Having no password is deliberate and is paid for by <see cref="AppendCoordinatorNetworkPolicy"/>:
    /// nothing but the mail server's own pods may open a connection to it. A credential that every
    /// node must be told, that an operator must copy, and that four separate code paths must agree
    /// on was the thing that kept breaking — network isolation makes the same guarantee with
    /// nothing to keep in sync.</para>
    /// </summary>
    private static void AppendCoordinator(List<string> y, string releaseName, string ns)
    {
        string name = CoordinatorName(releaseName);

        y.Add("apiVersion: apps/v1");
        y.Add("kind: Deployment");
        y.Add("metadata:");
        y.Add($"  name: {name}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {name}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  replicas: 1");
        // Never two at once: a second coordinator is a second, silently divergent view of who holds
        // which lock. Recreate means a restart is a gap rather than a split brain.
        y.Add("  strategy:");
        y.Add("    type: Recreate");
        y.Add("  selector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {name}");
        y.Add("  template:");
        y.Add("    metadata:");
        y.Add("      labels:");
        y.Add($"        app: {name}");
        y.Add("    spec:");
        y.Add("      containers:");
        y.Add("        - name: redis");
        y.Add($"          image: {CoordinatorImage}");
        // --save "" and --appendonly no: no RDB snapshot, no AOF. Nothing here outlives a restart
        // by design, and a coordinator that pauses to fork for a dump is a coordinator that stalls
        // every node waiting on a lock.
        y.Add("          args: [\"--save\", \"\", \"--appendonly\", \"no\"]");
        y.Add("          ports:");
        y.Add($"            - name: redis");
        y.Add($"              containerPort: {CoordinatorPort}");
        y.Add("          readinessProbe:");
        y.Add("            exec:");
        y.Add("              command: [\"redis-cli\", \"ping\"]");
        y.Add("            periodSeconds: 5");
        y.Add("          livenessProbe:");
        y.Add("            exec:");
        y.Add("              command: [\"redis-cli\", \"ping\"]");
        y.Add("            periodSeconds: 30");
        y.Add("          resources:");
        y.Add("            requests:");
        y.Add("              cpu: 25m");
        y.Add("              memory: 64Mi");
        y.Add("            limits:");
        y.Add("              memory: 256Mi");
        y.Add("          securityContext:");
        y.Add("            allowPrivilegeEscalation: false");
        // runAsNonRoot is a promise the kubelet checks and cannot verify on its own: the Redis
        // image declares no numeric USER (it starts as root and drops privileges in its own
        // entrypoint), so the flag alone fails admission with "container has runAsNonRoot and image
        // will run as root" and the pod never starts. The uid has to be named here. 999/1000 is the
        // redis user in the official image — confirmed against redis:7.4-alpine rather than assumed,
        // because the two variants disagree: Debian's redis is 999:999, Alpine's is 999:1000.
        y.Add("            runAsNonRoot: true");
        y.Add($"            runAsUser: {CoordinatorUid}");
        y.Add($"            runAsGroup: {CoordinatorGid}");
        y.Add("            capabilities:");
        y.Add("              drop: [ALL]");
        y.Add("---");

        y.Add("apiVersion: v1");
        y.Add("kind: Service");
        y.Add("metadata:");
        y.Add($"  name: {name}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add($"    app: {name}");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  type: ClusterIP");
        y.Add("  selector:");
        y.Add($"    app: {name}");
        y.Add("  ports:");
        y.Add("    - name: redis");
        y.Add($"      port: {CoordinatorPort}");
        y.Add($"      targetPort: {CoordinatorPort}");
        y.Add("---");

        AppendCoordinatorNetworkPolicy(y, releaseName, ns);
    }

    /// <summary>
    /// The isolation that stands in for the password: only the mail server's pods may reach the
    /// coordinator. Without this the "no credential" decision would be an open Redis rather than a
    /// private one, so the two belong together and are written together.
    /// </summary>
    private static void AppendCoordinatorNetworkPolicy(List<string> y, string releaseName, string ns)
    {
        string name = CoordinatorName(releaseName);

        y.Add("apiVersion: networking.k8s.io/v1");
        y.Add("kind: NetworkPolicy");
        y.Add("metadata:");
        y.Add($"  name: {name}");
        y.Add($"  namespace: {ns}");
        y.Add("  labels:");
        y.Add("    app.kubernetes.io/managed-by: entkube");
        y.Add("spec:");
        y.Add("  podSelector:");
        y.Add("    matchLabels:");
        y.Add($"      app: {name}");
        y.Add("  policyTypes: [Ingress]");
        y.Add("  ingress:");
        y.Add("    - from:");
        y.Add("        - podSelector:");
        y.Add("            matchLabels:");
        y.Add($"              app: {releaseName}");
        y.Add("      ports:");
        y.Add("        - protocol: TCP");
        y.Add($"          port: {CoordinatorPort}");
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
    /// published on the mail address the certificate is for. Only with a mail address to publish it
    /// on: a CA cannot reach a ClusterIP, so binding 80 there would open a port for a challenge that
    /// can never arrive.
    /// </summary>
    public static bool NeedsAcmeHttpPort(StalwartComponentConfig config) =>
        config.TlsMode == StalwartTlsMode.Acme
        && config.AcmeChallenge == StalwartAcmeChallenge.Http01
        && config.ExposeMode == StalwartMailExposeMode.LoadBalancer;

    /// <summary>
    /// True when HTTPS is published on the mail address. Always, whatever the TLS mode — because
    /// this is where a mail client looks before it has any settings, and where a receiving server
    /// fetches the MTA-STS policy.
    ///
    /// <para>Those lookups are the mail server's own business: <c>autoconfig.example.com</c>,
    /// <c>autodiscover.example.com</c> and <c>mta-sts.example.com</c> are names of the MAIL service,
    /// and every one of them is fetched over HTTPS from whatever they resolve to. Routing them
    /// through the gateway instead made them depend on the admin UI being published — so a
    /// deployment that deliberately kept the admin interface internal had no client
    /// auto-configuration at all, and no MTA-STS. They are served here, on the address the MX record
    /// already points at, in every TLS mode.</para>
    ///
    /// <para>What is served on this listener is exactly <see cref="StalwartPlanBuilder"/>'s public
    /// path list; the admin UI and JMAP are refused on it. In ACME TLS-ALPN-01 mode this is also the
    /// listener the CA opens its handshake against — same port, same listener, no second binding.</para>
    /// </summary>
    public static bool NeedsPublicWebPort(StalwartComponentConfig config) =>
        config.ExposeMode == StalwartMailExposeMode.LoadBalancer;

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
        if (NeedsPublicWebPort(config))
        {
            yield return (PublicWebListener, MailPorts.PublicWeb);
        }

        yield return ("http", StalwartPlanBuilder.HttpPort);
    }

    /// <summary>
    /// The local data volume, present only in single-node deployments. Named here because whether a
    /// live StatefulSet has this claim is how EntKube tells which shape it was built with, and
    /// volumeClaimTemplates cannot be changed in place.
    /// </summary>
    public const string DataVolumeName = "data";

    /// <summary>Listener name for the HTTP-01 challenge port. Referenced by the endpoint policy.</summary>
    public const string AcmeHttpListener = "acme-http";

    /// <summary>
    /// Listener name for HTTPS on the mail address — client auto-configuration, MTA-STS, and the
    /// TLS-ALPN-01 challenge. Referenced by the endpoint policy, which allows only the public paths
    /// on it and refuses everything else.
    /// </summary>
    public const string PublicWebListener = "public-web";

    /// <summary>
    /// A cert-manager Certificate for the mail hostname and the client-autodiscovery names of every
    /// served domain, issued into the Secret the StatefulSet mounts.
    ///
    /// <para>The autodiscovery names are on it because the mail server itself answers them, on its
    /// own address: <c>autoconfig.</c>, <c>autodiscover.</c>, <c>mta-sts.</c> and
    /// <c>ua-auto-config.</c> are all fetched over HTTPS and all validate the name, so a certificate
    /// carrying only the mail hostname means a client that reaches the right server and refuses to
    /// talk to it. In ACME mode Stalwart orders exactly the same set itself; this is the
    /// cert-manager spelling of it.</para>
    ///
    /// <para>The cost is that the issuer has to be able to solve a challenge for each of those
    /// names. That is the same DNS-01 solver the mail hostname already needs (the mail address is
    /// not behind the gateway, so HTTP-01 cannot reach it) and the same zone — but a name it cannot
    /// solve fails the whole certificate, so <c>PreflightAsync</c> lists what will be requested.
    /// cert-manager keeps the existing Secret until a new one is issued, so a failure here stalls a
    /// renewal rather than taking the current certificate away.</para>
    ///
    /// <para>The web hostname is deliberately absent: that certificate belongs to the gateway,
    /// which terminates that TLS itself.</para>
    /// </summary>
    public static string BuildTlsCertificateManifest(
        string clusterIssuer, string hostname, string releaseName, string ns,
        IReadOnlyList<StalwartMailDomain>? domains = null)
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
        ];

        foreach (string name in StalwartPlanBuilder.CertificateNames(hostname, domains ?? []))
        {
            y.Add($"    - {name}");
        }

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
