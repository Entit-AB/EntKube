using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Services;

/// <summary>
/// A form field describes a single configurable value that can be edited
/// via a simple form control instead of requiring the user to hand-edit YAML.
/// Each field maps to a dot-notation path in the Helm values YAML, so when
/// the user fills in the form, the value gets merged into the correct spot.
///
/// Think of this as making the YAML "approachable" — you can always drop
/// into the advanced YAML editor for full control, but common settings
/// (like passwords, port numbers, storage sizes) get a proper form field.
/// </summary>
public class ComponentFormField
{
    /// <summary>Unique key for this field within a catalog entry.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable label shown next to the form control.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Dot-notation path into the YAML where this value lives.
    /// For example "grafana.adminPassword" maps to:
    ///   grafana:
    ///     adminPassword: &lt;value&gt;
    /// </summary>
    public required string YamlPath { get; init; }

    /// <summary>What kind of form control to render.</summary>
    public required FormFieldType Type { get; init; }

    /// <summary>
    /// True when <see cref="YamlPath"/> is a pseudo-path — a "prefix:name" marker such as
    /// "cnpg:", "harbor:", "ldap:", "velero:" or "subchart:" — rather than a dot-notation path
    /// into the Helm values. These fields are owned by component-specific side configuration
    /// (a KeycloakComponentConfig, an OpenLdapComponentConfig, a storage link, …), so they must
    /// never be merged into, or read back out of, the values YAML: merging one writes a literal
    /// "ldap:base-dn" key the chart ignores (and, for a password field, leaks the plaintext into
    /// the stored values), while reading one back always misses and silently re-fills the form
    /// with catalog defaults. No real Helm value key contains a colon, so the colon is the test.
    /// </summary>
    public bool IsPseudoPath => YamlPath.Contains(':');

    /// <summary>Default value for the field (shown when the form first renders).</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Placeholder text for text-like inputs.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Short help text shown below the field.</summary>
    public string? HelpText { get; init; }

    /// <summary>Fixed options for Select fields.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>
    /// When true, this field's value is stored as an encrypted secret in the
    /// tenant vault rather than in plain text in the YAML values. At install time,
    /// the secret is retrieved from the vault and injected into the Helm values.
    /// </summary>
    public bool StoreAsSecret { get; init; }

    /// <summary>
    /// The name to use when storing this field as a vault secret.
    /// Defaults to the field Key if not specified.
    /// </summary>
    public string? SecretName { get; init; }

    /// <summary>
    /// When set, this field is only shown when the sibling field with this key
    /// has the value specified in <see cref="DependsOnValue"/>.
    /// Used for conditional fields (e.g. show cert/key inputs only when TLS mode is Manual).
    /// </summary>
    public string? DependsOnKey { get; init; }

    /// <summary>The value that <see cref="DependsOnKey"/> must equal for this field to be visible.</summary>
    public string? DependsOnValue { get; init; }

    /// <summary>
    /// When StoreAsSecret is true, automatically sync this vault secret to a Kubernetes Secret
    /// with this name. The vault secret name becomes the key inside the K8s Secret.
    /// Useful for secrets that external resources (like cert-manager ClusterIssuers) reference directly.
    /// </summary>
    public string? KubernetesSecretName { get; init; }

    /// <summary>
    /// Namespace for the auto-synced Kubernetes Secret. Defaults to the component's namespace.
    /// </summary>
    public string? KubernetesSecretNamespace { get; init; }

    /// <summary>
    /// For subchart toggle fields (YamlPath starting with "subchart:"), the Helm values YAML
    /// to pass when installing that subchart. Lets a combined component (e.g. istio-base which
    /// installs both base and istiod) supply sensible defaults for each subchart's install.
    /// </summary>
    public string? SubchartDefaultValues { get; init; }

    /// <summary>
    /// When true, this field is not rendered in the form UI. Hidden fields with
    /// StoreAsSecret=true are used for credentials that are injected programmatically
    /// (e.g. S3 credentials from a StorageLink, CNPG passwords from the vault) rather
    /// than entered by the user.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// When true, the plaintext value is bcrypt-hashed (work factor 12) before being
    /// written to the Kubernetes Secret during sync. Use for components like wg-easy
    /// that require a bcrypt hash in their env vars (PASSWORD_HASH) rather than
    /// the raw password. The vault always stores the original plaintext so it can be
    /// revealed in the UI and re-hashed on subsequent syncs.
    /// </summary>
    public bool BcryptOnSync { get; init; }

    /// <summary>
    /// A literal placeholder string in the manifest YAML (e.g. "%%WG_GATEWAY%%") that
    /// is replaced with this field's vault secret value at apply-time. Used for
    /// Manifest-type components where the value must appear verbatim in the YAML
    /// (e.g. a gateway name in an EnvoyFilter workloadSelector) rather than being
    /// injected via a YAML path. Only honoured when StoreAsSecret is also true.
    /// </summary>
    public string? ManifestPlaceholder { get; init; }
}

/// <summary>
/// The type of form control to render for a ComponentFormField.
/// </summary>
public enum FormFieldType
{
    Text,
    Number,
    Password,
    Toggle,
    Select,
    CnpgDatabase,
    ClusterIssuer,
    StorageLink,
    /// <summary>
    /// Renders as a dropdown of installed Istio gateway components on the cluster.
    /// Selecting a gateway also auto-detects its LoadBalancer IP and writes it to
    /// any sibling field whose Key is "wg-host". The stored value is the gateway's
    /// Helm release name (e.g. "istio-ingress-external"), which is used for
    /// ManifestPlaceholder substitution at apply-time.
    /// </summary>
    GatewaySelector,

    /// <summary>
    /// Renders as a dropdown of the Redis endpoints on the cluster: the ones EntKube manages (Cache tab)
    /// and any other Service answering on the Redis port. The stored value is the <c>host:port</c> the
    /// component's configuration wants, so nothing downstream has to know this was a picker rather than a
    /// text box — and when the chosen Redis is one EntKube manages, its vaulted password is filled into a
    /// sibling field whose Key is "redis-password", because an endpoint without its credential is only
    /// half an answer. Falls back to free text when the cluster has none.
    /// </summary>
    RedisSelector,

    /// <summary>
    /// Renders as a dropdown of the Keycloak realms on this cluster. The stored value is the realm's
    /// EntKube id, and choosing one is a standing instruction rather than a lookup: on every apply, the
    /// component's OIDC client is created in that realm if it is missing, its redirect URI is kept
    /// correct, and the issuer/client id/secret are filled in from it. Left empty, the manual OIDC fields
    /// beside it are used instead — which is what an external identity provider needs.
    /// </summary>
    KeycloakRealmSelector,

    /// <summary>
    /// Renders as a dropdown of the tenant's stored OIDC app registrations (Microsoft Entra, Google, …
    /// held in the vault as <see cref="VaultSecretType.OAuthClient"/>). The stored value is the
    /// registration secret's id, and choosing one is a standing instruction like <see cref="KeycloakRealmSelector"/>:
    /// on every apply the issuer, client id and client secret are resolved from it — with the
    /// provider-correct issuer, audience, username claim and scopes filled in — and written into the same
    /// OIDC secrets a Keycloak realm would populate, so nothing downstream knows which provider it was.
    /// Left empty, the Keycloak realm or the manual OIDC fields beside it are used instead.
    /// </summary>
    OidcAppRegistrationSelector
}

/// <summary>
/// Takes form field values (dot-notation path → value) and merges them into
/// a YAML document. This bridges the gap between the friendly form UI and the
/// underlying Helm values YAML that actually drives configuration.
///
/// The merge is additive: existing YAML keys not targeted by form fields are
/// preserved. If a form field targets a path that already exists, its value
/// is overwritten. If the path doesn't exist, the necessary structure is created.
/// </summary>
public static class YamlFormMerger
{
    /// <summary>
    /// Merges form values into a base YAML string. Each form value is identified
    /// by a dot-notation path (e.g. "grafana.adminPassword") and gets set at the
    /// corresponding location in the YAML tree.
    ///
    /// If the base YAML is empty or null, a new document is created from scratch.
    /// Boolean-like values ("true"/"false") are stored as YAML booleans (unquoted).
    /// Numeric values are stored as YAML scalars (unquoted).
    /// </summary>
    public static string MergeFormValues(string baseYaml, IReadOnlyDictionary<string, string> formValues)
    {
        // If there are no form values to apply, just return the original YAML
        // exactly as-is — no point in parsing and re-serializing.

        if (formValues.Count == 0)
        {
            return baseYaml;
        }

        // Parse the base YAML into a mutable tree we can manipulate.
        // If the YAML is empty, start with an empty mapping node.

        YamlMappingNode root = ParseOrCreateRoot(baseYaml);

        // For each form value, walk the dot-notation path and set the leaf value.
        // We create intermediate mapping nodes as needed.

        foreach (KeyValuePair<string, string> entry in formValues)
        {
            string[] segments = entry.Key.Split('.');
            SetValueAtPath(root, segments, entry.Value);
        }

        // Serialize the modified tree back to a YAML string.

        return SerializeToYaml(root);
    }

    /// <summary>
    /// Parses existing YAML into a mapping node, or creates an empty one
    /// if the input is null/whitespace.
    /// </summary>
    private static YamlMappingNode ParseOrCreateRoot(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new YamlMappingNode();
        }

        YamlStream stream = new();

        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch
        {
            // The stored YAML may contain un-substituted %%PLACEHOLDER%% tokens
            // (e.g. Manifest-type components like wg-easy), which are not valid YAML.
            // Treat unparseable input as an empty document rather than throwing,
            // so callers like ExtractValue degrade gracefully instead of crashing.
            return new YamlMappingNode();
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
        {
            return new YamlMappingNode();
        }

        return mapping;
    }

    /// <summary>
    /// Walks the path segments through the YAML tree, creating intermediate
    /// mapping nodes as needed, and sets the final leaf to the given value.
    /// </summary>
    private static void SetValueAtPath(YamlMappingNode current, string[] segments, string value)
    {
        // Walk down the path, creating mappings for intermediate segments.
        // Numeric segments (e.g. "0", "1") index into sequence nodes.
        // On the last segment, we set the scalar value.

        for (int i = 0; i < segments.Length - 1; i++)
        {
            string seg = segments[i];
            YamlScalarNode key = new(seg);
            YamlNode? existing = current.Children.Keys
                .OfType<YamlScalarNode>()
                .FirstOrDefault(k => k.Value == seg);

            // When the next segment is a numeric index, this segment must hold a
            // YAML sequence. Reuse an existing sequence, or create one — this is what
            // lets indexed paths like "solvers.0.http01" build a real list from scratch
            // (not a mapping with a "0" key, which would be invalid).
            if (i + 1 < segments.Length - 1 && int.TryParse(segments[i + 1], out int arrayIndex))
            {
                YamlSequenceNode seq;
                if (existing is not null && current.Children[existing] is YamlSequenceNode existingSeq)
                {
                    seq = existingSeq;
                }
                else
                {
                    seq = new YamlSequenceNode();
                    if (existing is not null)
                    {
                        current.Children.Remove(existing);
                    }
                    current.Children[key] = seq;
                }

                // Ensure the sequence is long enough.
                while (seq.Children.Count <= arrayIndex)
                {
                    seq.Children.Add(new YamlMappingNode());
                }

                if (seq.Children[arrayIndex] is YamlMappingNode seqMapping)
                {
                    current = seqMapping;
                }
                else
                {
                    YamlMappingNode replacement = new();
                    seq.Children[arrayIndex] = replacement;
                    current = replacement;
                }

                i++; // skip the numeric index segment — already consumed
                continue;
            }

            if (existing is not null && current.Children[existing] is YamlMappingNode childMapping)
            {
                current = childMapping;
            }
            else
            {
                // Either the key doesn't exist or it points to a non-mapping.
                // We create a new mapping node and replace whatever was there.

                YamlMappingNode newMapping = new();

                if (existing is not null)
                {
                    current.Children.Remove(existing);
                }

                current.Children[key] = newMapping;
                current = newMapping;
            }
        }

        // Set the leaf value. We determine the appropriate YAML scalar style
        // based on the value content — booleans and numbers stay unquoted.

        string leafKey = segments[^1];
        YamlScalarNode? existingLeafKey = current.Children.Keys
            .OfType<YamlScalarNode>()
            .FirstOrDefault(k => k.Value == leafKey);

        YamlScalarNode valueNode = CreateScalarNode(value);

        if (existingLeafKey is not null)
        {
            current.Children[existingLeafKey] = valueNode;
        }
        else
        {
            current.Children[new YamlScalarNode(leafKey)] = valueNode;
        }
    }

    /// <summary>
    /// Creates a YAML scalar node with appropriate style — booleans and numbers
    /// stay unquoted (plain), strings that might be ambiguous get quoted.
    /// </summary>
    private static YamlScalarNode CreateScalarNode(string value)
    {
        // Booleans and numbers should be plain (unquoted) in YAML.
        // Everything else is also plain unless it contains special chars.

        bool isBoolean = value is "true" or "false";
        bool isNumeric = double.TryParse(value, out _);

        return new YamlScalarNode(value)
        {
            Style = isBoolean || isNumeric
                ? YamlDotNet.Core.ScalarStyle.Plain
                : YamlDotNet.Core.ScalarStyle.Plain
        };
    }

    /// <summary>
    /// Serializes the YAML tree back to a string without the document markers (---/...).
    /// </summary>
    private static string SerializeToYaml(YamlMappingNode root)
    {
        YamlDocument document = new(root);
        YamlStream stream = new(document);

        using StringWriter writer = new();
        stream.Save(writer, assignAnchors: false);

        // YamlDotNet adds "..." at the end and "---" at the start. Strip them
        // for cleaner output that matches what users expect in Helm values.

        string output = writer.ToString();
        output = output.Replace("---\r\n", "").Replace("---\n", "");
        output = output.TrimEnd('\r', '\n', ' ');

        if (output.EndsWith("..."))
        {
            output = output[..^3].TrimEnd('\r', '\n', ' ');
        }

        return output;
    }

    /// <summary>
    /// Extracts a scalar value from a YAML string at the given dot-notation path.
    /// Returns null if the path doesn't exist or doesn't point to a scalar.
    /// Used to read current form field values from existing component YAML.
    /// </summary>
    public static string? ExtractValue(string yaml, string dotPath)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        YamlMappingNode root = ParseOrCreateRoot(yaml);
        string[] segments = dotPath.Split('.');
        YamlMappingNode current = root;

        // Walk down the tree following the path segments. A numeric segment indexes into a
        // sequence, mirroring SetValueAtPath: a field that can write "volume.dataDirs.0.size"
        // must be able to read it back, or the edit form shows the default and saving writes
        // that default over the installed value.

        for (int i = 0; i < segments.Length - 1; i++)
        {
            YamlNode? key = current.Children.Keys
                .OfType<YamlScalarNode>()
                .FirstOrDefault(k => k.Value == segments[i]);

            if (key is null)
            {
                return null;
            }

            YamlNode child = current.Children[key];

            if (child is YamlSequenceNode sequence
                && i + 1 < segments.Length - 1
                && int.TryParse(segments[i + 1], out int index))
            {
                if (index < 0 || index >= sequence.Children.Count
                    || sequence.Children[index] is not YamlMappingNode item)
                {
                    return null;
                }

                current = item;
                i++; // the index segment is consumed
                continue;
            }

            if (child is not YamlMappingNode mapping)
            {
                return null;
            }

            current = mapping;
        }

        // Read the leaf value.

        YamlNode? leafKey = current.Children.Keys
            .OfType<YamlScalarNode>()
            .FirstOrDefault(k => k.Value == segments[^1]);

        if (leafKey is not null && current.Children[leafKey] is YamlScalarNode scalar)
        {
            return scalar.Value;
        }

        return null;
    }

    /// <summary>
    /// Ensures an Istio gateway's Helm values expose the WireGuard UDP port (51820)
    /// on its LoadBalancer Service, so the wg-easy component can ride the gateway's
    /// existing external IP. Because specifying service.ports overrides the chart
    /// defaults, when no ports are present yet we also add the gateway's standard
    /// ports (status-port/http2/https). No-op if a 51820 entry already exists.
    /// Done as explicit node manipulation (not dot-path merge) because the merger
    /// cannot create a brand-new sequence from numeric paths.
    /// </summary>
    public static string EnsureWireGuardGatewayPort(string yaml)
    {
        YamlMappingNode root = ParseOrCreateRoot(yaml);
        YamlMappingNode service = GetOrCreateMapping(root, "service");

        YamlScalarNode? portsKey = service.Children.Keys
            .OfType<YamlScalarNode>()
            .FirstOrDefault(k => k.Value == "ports");

        YamlSequenceNode ports = portsKey is not null && service.Children[portsKey] is YamlSequenceNode existing
            ? existing
            : new YamlSequenceNode();

        bool hasWireguard = ports.Children
            .OfType<YamlMappingNode>()
            .Any(m => m.Children.Keys.OfType<YamlScalarNode>()
                .Any(k => k.Value == "port"
                    && m.Children[k] is YamlScalarNode v && v.Value == "51820"));

        if (!hasWireguard)
        {
            // No ports listed yet → seed the gateway's standard ports first, since
            // setting service.ports replaces the chart defaults.
            if (ports.Children.Count == 0)
            {
                ports.Children.Add(PortNode("status-port", 15021, "TCP"));
                ports.Children.Add(PortNode("http2", 80, "TCP"));
                ports.Children.Add(PortNode("https", 443, "TCP"));
            }

            ports.Children.Add(PortNode("wireguard", 51820, "UDP"));
        }

        if (portsKey is not null)
        {
            service.Children[portsKey] = ports;
        }
        else
        {
            service.Children[new YamlScalarNode("ports")] = ports;
        }

        return SerializeToYaml(root);
    }

    /// <summary>
    /// Makes an enabled <c>secretTargets</c> block coherent for trust-manager, by granting the Secret
    /// permission the chart requires alongside it.
    ///
    /// <c>secretTargets.enabled: true</c> passes <c>--secret-targets-enabled</c> to the binary, which then
    /// starts a cluster-wide Secret informer. The RBAC for that informer is gated on
    /// <c>authorizedSecretsAll</c> (or a non-empty <c>authorizedSecrets</c> list) — so <c>enabled</c> on its
    /// own produces a controller that watches Secrets it is not permitted to list: the cache never syncs
    /// and the pod crash-loops, with nothing in the Bundle status to say why.
    ///
    /// The catalog's defaults carry both keys, but that only helps a fresh install.
    /// <see cref="ComponentLifecycleService.FillMissingCatalogDefaults"/> is top-level and additive by
    /// design, so a component registered before those defaults keeps its own <c>secretTargets</c> block and
    /// never grows the companion key — including at the moment an operator turns the "Allow Secret targets"
    /// toggle on, which is exactly when it starts to matter.
    ///
    /// Only ever adds permission to a target the operator has already switched on: values with
    /// <c>enabled</c> absent or false are returned untouched, so this cannot widen a cluster's trust-manager
    /// RBAC on its own.
    /// </summary>
    public static string EnsureTrustManagerSecretTargets(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;

        YamlMappingNode root = ParseOrCreateRoot(yaml);

        if (!TryGetMapping(root, "secretTargets", out YamlMappingNode? secretTargets)) return yaml;
        if (!IsTrue(secretTargets, "enabled")) return yaml;
        if (IsTrue(secretTargets, "authorizedSecretsAll")) return yaml;

        // An explicit list of Secret names is the other way the chart grants the permission, and it is the
        // narrower one. Someone who wrote a list meant that list.
        if (TryGetValue(secretTargets, "authorizedSecrets", out YamlNode? authorized)
            && authorized is YamlSequenceNode { Children.Count: > 0 })
        {
            return yaml;
        }

        SetScalar(secretTargets, "authorizedSecretsAll", "true");
        return SerializeToYaml(root);
    }

    /// <summary>
    /// Fills in the OpenTelemetry eBPF Instrumentation memory bounds that any component registered before
    /// they existed is missing, without touching a value that is already there.
    ///
    /// <para>The catalog's defaults are read once, at registration, and
    /// <c>ComponentLifecycleService.FillMissingCatalogDefaults</c> deliberately fills only TOP-LEVEL keys
    /// afterwards. For this chart that is the whole difference between a fix shipping and a fix arriving:
    /// every bound except <c>env</c> lives under <c>config.data</c>, and an install made before them has a
    /// <c>config</c> key — so the entire block is "present" and left exactly as it was. The agent then keeps
    /// running with the configuration that made it cost gigabytes:</para>
    /// <list type="bullet">
    /// <item><c>otel_traces_export.instrumentations</c> absent → OBI loads all THIRTEEN protocol probe sets
    ///   (http, grpc, sql, redis, kafka, mqtt, nats, amqp, mongo, couchbase, memcached, sunrpc, aerospike)
    ///   on every instrumented executable. The earlier catalog wrote <c>instrumentations</c> at the top
    ///   level of the config, which OBI does not read — so nothing was ever selected, by us or by the
    ///   operator.</item>
    /// <item><c>ebpf.maps_config</c> absent → every eBPF map is preallocated at upstream's full size, per
    ///   instrumented executable, as kernel memory charged to this pod's cgroup where the Go runtime can
    ///   neither see nor free it.</item>
    /// <item><c>prometheus_export</c> absent → the CHART's own default of <c>:9090</c> survives the merge,
    ///   so OBI aggregates RED metrics per service and per route in its heap for a scrape endpoint that
    ///   nothing reads (the chart's ServiceMonitor is off by default).</item>
    /// <item><c>routes</c> absent → the <c>heuristic</c> decorator collapses only the path segments that
    ///   look like ids, so a path shaped like a directory tree stays unique forever and every variant
    ///   becomes another permanent series behind those metrics.</item>
    /// </list>
    ///
    /// <para>Strictly additive, and that is the whole safety argument: a key the operator has set — to
    /// anything at all — is left alone, exactly as the top-level fill-in treats one. Absent-means-
    /// unconsidered holds here more strongly than usual, because these keys did not exist in any values
    /// document EntKube ever wrote before 2026-09-10. Settings that ARE present and expensive
    /// (<c>ebpf.context_propagation: all</c>, an <c>otel_metrics_export</c> endpoint) are deliberately not
    /// touched — they are form fields instead, so changing them stays the operator's decision.</para>
    /// </summary>
    public static string EnsureObiResourceBounds(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;

        YamlMappingNode root = ParseOrCreateRoot(yaml);
        if (!TryGetMapping(root, "config", out YamlMappingNode? config)) return yaml;
        if (!TryGetMapping(config!, "data", out YamlMappingNode? data)) return yaml;

        bool changed = false;

        // Protocol probe sets. Absent here even on installs whose values say `instrumentations: ["*"]`,
        // because that key belongs to the exporter section and is ignored at the top level.
        if (TryGetMapping(data!, "otel_traces_export", out YamlMappingNode? traces))
        {
            if (!TryGetValue(traces!, "instrumentations", out _))
            {
                var protocols = new YamlSequenceNode(
                    new YamlScalarNode("http"), new YamlScalarNode("grpc"), new YamlScalarNode("sql"));
                traces!.Children.Add(new YamlScalarNode("instrumentations"), protocols);
                changed = true;
            }

            // The exporter's own logger, which upstream leaves off. Not a memory bound — it is the
            // reason an agent that cannot deliver a single span still looks perfectly healthy, logging
            // "instrumenting process" for every workload it finds while the Traces view stays empty for
            // weeks. Filling it in is what makes the next failure of this component say so.
            if (!TryGetValue(traces!, "otel_sdk_log_level", out _))
            {
                SetScalar(traces!, "otel_sdk_log_level", "error");
                changed = true;
            }
        }

        // eBPF map sizes — kernel memory, preallocated, charged to this cgroup.
        YamlMappingNode ebpf = GetOrCreateMapping(data!, "ebpf");
        if (!TryGetValue(ebpf, "maps_config", out _))
        {
            YamlMappingNode maps = GetOrCreateMapping(ebpf, "maps_config");
            SetScalar(maps, "global_scale_factor", "-1");
            changed = true;
        }

        // The chart's own :9090 default, and the unbounded route cardinality behind it.
        if (!TryGetValue(data!, "prometheus_export", out _))
        {
            SetScalar(GetOrCreateMapping(data!, "prometheus_export"), "port", "0");
            changed = true;
        }
        if (!TryGetValue(data!, "routes", out _))
        {
            YamlMappingNode routes = GetOrCreateMapping(data!, "routes");
            SetScalar(routes, "unmatched", "low-cardinality");
            SetScalar(routes, "max_path_segment_cardinality", "10");
            changed = true;
        }

        // The metadata cache is a second Go process under a second limit, and it got its own heap
        // ceiling in the same pass. Same rule: only when nothing is set.
        if (TryGetMapping(root, "k8sCache", out YamlMappingNode? cache))
        {
            YamlMappingNode cacheEnv = GetOrCreateMapping(cache!, "env");
            if (!TryGetValue(cacheEnv, "GOMEMLIMIT", out _))
            {
                SetScalar(cacheEnv, "GOMEMLIMIT", "350MiB");
                changed = true;
            }
        }

        return changed ? SerializeToYaml(root) : yaml;
    }

    /// <summary>The port OBI serves RED metrics on when nothing else is said — the convention, and the chart's own.</summary>
    public const string ObiPrometheusPort = "9090";

    /// <summary>
    /// URL paths that are scraped or probed rather than used: their RED series are noise in every
    /// dashboard and, on a busy node, a large share of the series OBI holds.
    /// </summary>
    public static readonly IReadOnlyList<string> ObiIgnoredRoutes = ["/metrics", "/healthz", "/readyz", "/livez"];

    /// <summary>
    /// Turns OBI's per-workload HTTP/gRPC/SQL RED metrics on, for a cluster where
    /// <c>kube-prometheus-stack</c> is installed. Runs at apply time; the stored values are not touched.
    ///
    /// <para><b>A 0 port means "not chosen", and becomes <see cref="ObiPrometheusPort"/>.</b> Until
    /// 2026-11-24 the catalog default and the form both wrote 0, as a memory bound for an endpoint
    /// nothing scraped — so every agent installed since then carries a 0 nobody chose, and an operator's
    /// deliberate 0 is the same scalar at the same path (the chart's strict values schema leaves nowhere
    /// to keep a marker). Treating 0 as "off" would keep HTTP metrics off on every one of those clusters,
    /// against the rule this exists for: every app gets basic metrics wherever Prometheus is installed,
    /// with nothing to configure. Any other number moves the port; an absent or non-numeric one is also
    /// <see cref="ObiPrometheusPort"/>.</para>
    ///
    /// <para><b>Derived, not filled in:</b> <c>service.enabled</c> and <c>serviceMonitor.enabled</c>
    /// are set from the port, overriding what is stored. The chart renders a ServiceMonitor only when
    /// BOTH are true and there is a port to scrape, and a Service with no port is invalid — so the old
    /// form toggle, which set only <c>serviceMonitor.enabled</c>, never produced one, and the
    /// <c>false</c> it stored in every component registered through the form is not a decision anyone
    /// saw take effect. Leaving that <c>false</c> in place would leave the endpoint open with nothing
    /// scraping it, which is the one state that costs memory for nothing.</para>
    ///
    /// <para><b>Filled in only where absent</b> (an operator's value wins): the scrape interval, the
    /// relabelling, the protocol list, the feature set and the ignored routes.</para>
    /// <list type="bullet">
    /// <item><c>serviceMonitor.metrics.endpoint.metricRelabelings</c> copies
    ///   <c>k8s_namespace_name</c>/<c>k8s_pod_name</c>/<c>k8s_container_name</c> onto
    ///   <c>namespace</c>/<c>pod</c>/<c>container</c>. OBI runs on the host network and is scraped as
    ///   ITS pod, so without this every series carries <c>namespace="monitoring"</c> and the OBI pod's
    ///   name — and no per-app query (which selects on <c>namespace</c>) can find them. The regex is
    ///   <c>(.+)</c> so that a series without the source label (OBI's own) keeps its target label
    ///   instead of having it blanked.</item>
    /// <item><c>prometheus_export.instrumentations</c>: the Prometheus exporter does NOT inherit
    ///   <c>otel_traces_export</c>'s list; its own default is every protocol, which loads every probe set
    ///   on every instrumented executable.</item>
    /// <item><c>metrics.features: [application_red]</c>: the top-level selector (the exporter-level
    ///   <c>features</c> is deprecated in v1.0.0). <c>application</c>, the default, also emits the
    ///   request and response body-size histograms.</item>
    /// <item><c>routes.ignored_patterns</c> with <c>ignore_mode: metrics</c>, so probes and scrapes stay
    ///   out of the metrics and still appear in traces as before.</item>
    /// </list>
    ///
    /// <para>Only a Config v1 document is edited — non-empty <c>config.data</c> without
    /// <c>extensions.obi.version: "2.0"</c>, which is every document the catalog writes. Creating
    /// <c>config.data</c> where there is none would switch the chart from its v2 defaults to its v1
    /// ones, a reinstall with different settings rather than a fill-in.</para>
    /// </summary>
    public static string EnableObiPrometheusMetrics(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;

        YamlMappingNode root = ParseOrCreateRoot(yaml);
        if (!TryGetObiV1Config(root, out YamlMappingNode? data)) return yaml;

        bool changed = false;
        YamlMappingNode export = GetOrCreateMapping(data!, "prometheus_export", ref changed);

        string? port = TryGetValue(export, "port", out YamlNode? portNode) && portNode is YamlScalarNode s
            && int.TryParse(s.Value, out int parsed) && parsed > 0
                ? parsed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

        if (port is null)
        {
            SetScalar(export, "port", ObiPrometheusPort);
            port = ObiPrometheusPort;
            changed = true;
        }

        bool serve = port != "0" || ObiInternalMetricsPort(data!) > 0;

        YamlMappingNode service = GetOrCreateMapping(root, "service", ref changed);
        YamlMappingNode monitor = GetOrCreateMapping(root, "serviceMonitor", ref changed);
        changed |= SetBool(service, "enabled", serve);
        changed |= SetBool(monitor, "enabled", serve);

        if (port == "0") return changed ? SerializeToYaml(root) : yaml;

        if (!TryGetValue(export, "instrumentations", out _))
        {
            export.Children.Add(new YamlScalarNode("instrumentations"), Sequence("http", "grpc", "sql"));
            changed = true;
        }

        YamlMappingNode metrics = GetOrCreateMapping(data!, "metrics", ref changed);
        if (!TryGetValue(metrics, "features", out _))
        {
            metrics.Children.Add(new YamlScalarNode("features"), Sequence("application_red"));
            changed = true;
        }

        YamlMappingNode routes = GetOrCreateMapping(data!, "routes", ref changed);
        if (!TryGetValue(routes, "ignored_patterns", out _))
        {
            routes.Children.Add(new YamlScalarNode("ignored_patterns"), Sequence([.. ObiIgnoredRoutes]));
            // Only alongside patterns EntKube wrote: an operator's own patterns keep upstream's
            // default (`all`), which is what they were written against.
            if (!TryGetValue(routes, "ignore_mode", out _)) SetScalar(routes, "ignore_mode", "metrics");
            changed = true;
        }

        YamlMappingNode endpoint = GetOrCreateMapping(
            GetOrCreateMapping(monitor, "metrics", ref changed), "endpoint", ref changed);
        if (!TryGetValue(endpoint, "interval", out _))
        {
            SetScalar(endpoint, "interval", "30s");
            changed = true;
        }
        if (!TryGetValue(endpoint, "metricRelabelings", out _))
        {
            endpoint.Children.Add(new YamlScalarNode("metricRelabelings"), new YamlSequenceNode(
                ObiRelabel("k8s_namespace_name", "namespace"),
                ObiRelabel("k8s_pod_name", "pod"),
                ObiRelabel("k8s_container_name", "container")));
            changed = true;
        }

        return changed ? SerializeToYaml(root) : yaml;
    }

    /// <summary>
    /// The other half of <see cref="EnableObiPrometheusMetrics"/>, for a cluster WITHOUT Prometheus:
    /// the RED-metrics port is forced to 0 and the Service and ServiceMonitor off, whatever is stored.
    ///
    /// <para>The ServiceMonitor is the hard reason. Its kind exists only where the Prometheus Operator's
    /// CRDs are installed, so rendering one on a cluster without them fails the whole release with "no
    /// matches for kind ServiceMonitor" — the tracing agent would not install at all. The port is the
    /// soft one: OBI evicts expired series only when scraped, so an endpoint with no scraper is heap
    /// that grows and is never given back. Forcing both, rather than respecting a stored value, is what
    /// lets the catalog default the port to 9090.</para>
    ///
    /// <para>An absent port is written as 0 too: in a v1 document the chart's own default (9090)
    /// survives the merge otherwise.</para>
    /// </summary>
    public static string KeepObiPrometheusMetricsOff(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;

        YamlMappingNode root = ParseOrCreateRoot(yaml);
        bool changed = false;

        // Absent means the chart's default, which is false for both — nothing to write.
        foreach (string key in new[] { "service", "serviceMonitor" })
        {
            if (TryGetMapping(root, key, out YamlMappingNode? section) && IsTrue(section!, "enabled"))
            {
                changed |= SetBool(section!, "enabled", false);
            }
        }

        if (TryGetObiV1Config(root, out YamlMappingNode? data))
        {
            YamlMappingNode export = GetOrCreateMapping(data!, "prometheus_export", ref changed);
            if (!(TryGetValue(export, "port", out YamlNode? port) && port is YamlScalarNode { Value: "0" }))
            {
                SetScalar(export, "port", "0");
                changed = true;
            }
        }

        return changed ? SerializeToYaml(root) : yaml;
    }

    /// <summary>
    /// <c>config.data</c> when it is a Config v1 document — non-empty, without the v2 marker — which is
    /// the only shape whose keys these steps know. See the chart's <c>obi.configData</c>.
    /// </summary>
    private static bool TryGetObiV1Config(YamlMappingNode root, out YamlMappingNode? data)
    {
        data = null;
        if (!TryGetMapping(root, "config", out YamlMappingNode? config)
            || !TryGetMapping(config!, "data", out YamlMappingNode? found)
            || found!.Children.Count == 0)
        {
            return false;
        }

        if (TryGetMapping(found, "extensions", out YamlMappingNode? extensions)
            && TryGetMapping(extensions!, "obi", out YamlMappingNode? obi)
            && TryGetValue(obi!, "version", out _))
        {
            return false;
        }

        data = found;
        return true;
    }

    /// <summary>OBI's own-metrics port (<c>internal_metrics.prometheus.port</c>), which the chart's Service also serves.</summary>
    private static int ObiInternalMetricsPort(YamlMappingNode data)
        => TryGetMapping(data, "internal_metrics", out YamlMappingNode? internalMetrics)
           && TryGetMapping(internalMetrics!, "prometheus", out YamlMappingNode? prometheus)
           && TryGetValue(prometheus!, "port", out YamlNode? port)
           && port is YamlScalarNode scalar
           && int.TryParse(scalar.Value, out int value)
            ? value
            : 0;

    private static YamlMappingNode ObiRelabel(string sourceLabel, string targetLabel)
    {
        YamlMappingNode node = new();
        node.Children.Add(new YamlScalarNode("sourceLabels"), Sequence(sourceLabel));
        node.Children.Add(new YamlScalarNode("regex"), new YamlScalarNode("(.+)"));
        node.Children.Add(new YamlScalarNode("targetLabel"), new YamlScalarNode(targetLabel));
        node.Children.Add(new YamlScalarNode("replacement"), new YamlScalarNode("$1"));
        node.Children.Add(new YamlScalarNode("action"), new YamlScalarNode("replace"));
        return node;
    }

    private static YamlSequenceNode Sequence(params string[] items)
        => new(items.Select(i => (YamlNode)new YamlScalarNode(i)));

    /// <summary>Sets a boolean, reporting whether the stored value was anything else.</summary>
    private static bool SetBool(YamlMappingNode parent, string key, bool value)
    {
        string wanted = value ? "true" : "false";
        if (TryGetValue(parent, key, out YamlNode? existing)
            && existing is YamlScalarNode scalar
            && string.Equals(scalar.Value, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SetScalar(parent, key, wanted);
        return true;
    }

    /// <summary><see cref="GetOrCreateMapping(YamlMappingNode, string)"/>, noting when it had to create one.</summary>
    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode parent, string key, ref bool changed)
    {
        if (TryGetMapping(parent, key, out YamlMappingNode? existing)) return existing!;
        changed = true;
        return GetOrCreateMapping(parent, key);
    }

    /// <summary>
    /// The keys under <c>prometheus.prometheusSpec</c> that an existing kube-prometheus-stack install is
    /// given from the catalog when it has none of its own: the five monitor-selector flags, and the
    /// annotation scrape job.
    ///
    /// <para>⚠️ <c>storageSpec</c> is deliberately not among them. Adding it to a running Prometheus
    /// changes the StatefulSet's volume claim templates, and the metrics history kept on the pod so far
    /// does not move to the new volume — a change an operator makes on purpose (the storage field on the
    /// component form), never one an apply makes on the side.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> PrometheusSpecFillIns =
    [
        "podMonitorSelectorNilUsesHelmValues",
        "serviceMonitorSelectorNilUsesHelmValues",
        "probeSelectorNilUsesHelmValues",
        "scrapeConfigSelectorNilUsesHelmValues",
        "ruleSelectorNilUsesHelmValues",
        "additionalScrapeConfigs",
    ];

    /// <summary>
    /// Carries the catalog's scrape defaults to a kube-prometheus-stack installed before they existed.
    ///
    /// <para><b>Why this is needed.</b> <c>FillMissingCatalogDefaults</c> fills top-level keys only, and
    /// every install already has a <c>prometheus:</c> key — so the selector flags that let Prometheus see
    /// monitors other operators create (CNPG, Strimzi, RabbitMQ) reached new installs only, and the CNPG
    /// panel had to tell operators to paste them in by hand. The annotation scrape job would have met the
    /// same fate.</para>
    ///
    /// <para><b>Strictly additive.</b> Each key in <see cref="PrometheusSpecFillIns"/> is copied from the
    /// catalog only when the stored values do not have it. A key the operator set — to anything, including
    /// <c>true</c> or an <c>additionalScrapeConfigs</c> of their own — is left exactly as it is. The values
    /// come from the catalog document itself, so this cannot drift from what a new install gets.</para>
    /// </summary>
    public static string EnsurePrometheusScrapeDefaults(string yaml, string? catalogDefaults)
    {
        if (string.IsNullOrWhiteSpace(yaml) || string.IsNullOrWhiteSpace(catalogDefaults)) return yaml;

        YamlMappingNode catalog = ParseOrCreateRoot(catalogDefaults);
        if (!TryGetMapping(catalog, "prometheus", out YamlMappingNode? catalogPrometheus)
            || !TryGetMapping(catalogPrometheus!, "prometheusSpec", out YamlMappingNode? catalogSpec))
        {
            return yaml;
        }

        YamlMappingNode root = ParseOrCreateRoot(yaml);
        YamlMappingNode spec = GetOrCreateMapping(GetOrCreateMapping(root, "prometheus"), "prometheusSpec");

        bool changed = false;
        foreach (string key in PrometheusSpecFillIns)
        {
            if (TryGetValue(spec, key, out _) || !TryGetValue(catalogSpec!, key, out YamlNode? wanted)) continue;
            spec.Children[new YamlScalarNode(key)] = wanted!;
            changed = true;
        }

        return changed ? SerializeToYaml(root) : yaml;
    }

    private static bool TryGetMapping(YamlMappingNode parent, string key, out YamlMappingNode? mapping)
    {
        mapping = TryGetValue(parent, key, out YamlNode? value) ? value as YamlMappingNode : null;
        return mapping is not null;
    }

    private static bool TryGetValue(YamlMappingNode parent, string key, out YamlNode? value)
    {
        YamlScalarNode? found = parent.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);
        value = found is null ? null : parent.Children[found];
        return value is not null;
    }

    private static bool IsTrue(YamlMappingNode parent, string key)
        => TryGetValue(parent, key, out YamlNode? value)
           && value is YamlScalarNode scalar
           && string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static void SetScalar(YamlMappingNode parent, string key, string value)
    {
        YamlScalarNode? found = parent.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);
        if (found is not null) parent.Children.Remove(found);
        parent.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);
    }

    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode parent, string key)
    {
        YamlScalarNode? existing = parent.Children.Keys
            .OfType<YamlScalarNode>()
            .FirstOrDefault(k => k.Value == key);

        if (existing is not null && parent.Children[existing] is YamlMappingNode mapping)
        {
            return mapping;
        }

        YamlMappingNode created = new();

        if (existing is not null)
        {
            parent.Children.Remove(existing);
        }

        parent.Children.Add(new YamlScalarNode(key), created);
        return created;
    }

    private static YamlMappingNode PortNode(string name, int port, string protocol)
    {
        YamlMappingNode node = new();
        node.Children.Add(new YamlScalarNode("name"), new YamlScalarNode(name));
        node.Children.Add(new YamlScalarNode("port"),
            new YamlScalarNode(port.ToString()) { Style = YamlDotNet.Core.ScalarStyle.Plain });
        node.Children.Add(new YamlScalarNode("protocol"), new YamlScalarNode(protocol));
        node.Children.Add(new YamlScalarNode("targetPort"),
            new YamlScalarNode(port.ToString()) { Style = YamlDotNet.Core.ScalarStyle.Plain });
        return node;
    }
}
