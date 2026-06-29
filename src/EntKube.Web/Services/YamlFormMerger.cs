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
    GatewaySelector
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

            // When the child is a sequence and the next segment is a numeric index,
            // navigate into the sequence element and continue walking from there.
            if (existing is not null
                && current.Children[existing] is YamlSequenceNode seq
                && i + 1 < segments.Length - 1
                && int.TryParse(segments[i + 1], out int arrayIndex))
            {
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

        // Walk down the tree following the path segments.

        for (int i = 0; i < segments.Length - 1; i++)
        {
            YamlNode? key = current.Children.Keys
                .OfType<YamlScalarNode>()
                .FirstOrDefault(k => k.Value == segments[i]);

            if (key is null || current.Children[key] is not YamlMappingNode child)
            {
                return null;
            }

            current = child;
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
