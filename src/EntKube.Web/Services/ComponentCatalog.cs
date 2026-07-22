namespace EntKube.Web.Services;

/// <summary>
/// A catalog entry describes a well-known component that EntKube can manage.
/// These are pre-configured with Helm chart details so operators only need
/// to choose which component to install and optionally tweak the values —
/// they never need to hunt for repo URLs, chart names, or recommended versions.
///
/// Think of this as a curated app store for Kubernetes infrastructure.
/// </summary>
public class CatalogEntry
{
    /// <summary>Unique key for this catalog entry (e.g. "kube-prometheus-stack").</summary>
    public required string Key { get; init; }

    /// <summary>Human-friendly display name (e.g. "Kube Prometheus Stack").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Short description of what this component does.</summary>
    public required string Description { get; init; }

    /// <summary>Bootstrap icon class for the UI (e.g. "bi-graph-up").</summary>
    public required string Icon { get; init; }

    /// <summary>Category for grouping in the UI (e.g. "Monitoring", "Storage").</summary>
    public required string Category { get; init; }

    /// <summary>Component type to store on ClusterComponent.</summary>
    public string ComponentType { get; init; } = "HelmChart";

    /// <summary>Helm repository URL where the chart lives.</summary>
    public required string HelmRepoUrl { get; init; }

    /// <summary>Chart name within the repo.</summary>
    public required string HelmChartName { get; init; }

    /// <summary>Recommended/tested version. Null means latest.</summary>
    public string? HelmChartVersion { get; init; }

    /// <summary>
    /// Overrides the Helm <c>--wait</c> timeout (Go duration, e.g. "30m0s"). Null uses the
    /// default 10m. Extend it for heavy charts or DaemonSets that roll out node-by-node and
    /// may take longer to become Ready on busy/constrained clusters.
    /// </summary>
    public string? InstallTimeout { get; init; }

    /// <summary>Default namespace where this should be deployed.</summary>
    public required string DefaultNamespace { get; init; }

    /// <summary>
    /// CRD names in "plural.group" form (e.g. "rabbitmqclusters.rabbitmq.com") whose
    /// presence on a cluster indicates this component is already installed. Used by the
    /// component scan to detect operators installed from raw manifests (ComponentType
    /// "ManifestUrl", e.g. via <c>kubectl apply</c> or Terraform) — these leave no
    /// "owner=helm" release secret, so the Helm scan can't see them. Detection succeeds
    /// if ANY listed CRD is present.
    /// </summary>
    public IReadOnlyList<string> DetectionCrds { get; init; } = [];

    /// <summary>
    /// A cluster-scoped custom resource whose live instances mark this manifest component
    /// as installed. Use this instead of <see cref="DetectionCrds"/> when CRD presence would
    /// false-positive because the CRD is owned by a dependency rather than this component.
    /// The Let's Encrypt ClusterIssuer is the motivating case: clusterissuers.cert-manager.io
    /// ships with cert-manager, so only the presence of an actual ClusterIssuer instance
    /// means this issuer was applied. See <see cref="DetectionResource"/>.
    /// </summary>
    public DetectionResource? DetectionResource { get; init; }

    /// <summary>Default release name for Helm.</summary>
    public string? DefaultReleaseName { get; init; }

    /// <summary>Default Helm values YAML that provides a sensible starting config.</summary>
    public string? DefaultValues { get; init; }

    /// <summary>
    /// Components that MUST be present on the cluster before this one can be installed.
    /// References other catalog entry keys. If any dependency is missing, the UI
    /// will warn the operator and block install until they're satisfied.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Components that can satisfy a "group" dependency. For example, a component
    /// may depend on "ingress" which can be satisfied by either "traefik" or "istio".
    /// Keys here reference DependencyGroup names, not catalog entry keys.
    /// </summary>
    public IReadOnlyList<DependencyRequirement> RequiresOneOf { get; init; } = [];

    /// <summary>
    /// Form fields that provide a user-friendly way to configure the most common
    /// Helm values. These render as simple form controls (text boxes, selects,
    /// toggles) so operators don't need to understand YAML for routine settings.
    /// An "Advanced" accordion always remains available for full YAML editing.
    /// </summary>
    public IReadOnlyList<ComponentFormField> FormFields { get; init; } = [];
}

/// <summary>
/// Identifies a cluster-scoped custom resource whose live instances signal that a
/// manifest-installed component is present. Detection lists the instances and, when
/// <see cref="Matches"/> is set, keeps only those whose serialized JSON contains that
/// substring — e.g. "letsencrypt.org" so a self-signed or CA ClusterIssuer doesn't
/// masquerade as the Let's Encrypt issuer.
/// </summary>
/// <param name="Group">API group, e.g. "cert-manager.io".</param>
/// <param name="Version">API version, e.g. "v1".</param>
/// <param name="Plural">Resource plural, e.g. "clusterissuers".</param>
/// <param name="Matches">Optional case-insensitive substring an instance's JSON must contain to count.</param>
public sealed record DetectionResource(string Group, string Version, string Plural, string? Matches = null);

/// <summary>
/// A dependency requirement where any one of several components can satisfy it.
/// For example, "ingress" can be satisfied by either Traefik or Istio.
/// </summary>
public class DependencyRequirement
{
    /// <summary>Human-readable name for this requirement (e.g. "Ingress Controller").</summary>
    public required string Label { get; init; }

    /// <summary>Catalog entry keys that can satisfy this requirement.</summary>
    public required IReadOnlyList<string> Options { get; init; }
}

/// <summary>
/// The component catalog is a static, in-memory registry of all the infrastructure
/// components that EntKube knows how to manage out of the box. When an operator
/// wants to install something on a cluster, they pick from this catalog — the
/// Helm details are already filled in. They just configure the values if needed.
///
/// Components can declare dependencies on other components. The UI will show
/// which dependencies are satisfied and which are missing before allowing install.
/// Some dependencies are "one-of" — e.g. you need an ingress controller, but
/// it can be either Traefik or Istio.
///
/// Adding a new component to the platform is as simple as adding a new entry here.
/// </summary>
public static class ComponentCatalog
{
    /// <summary>
    /// All available catalog entries. Order here determines display order in the UI.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Entries { get; } =
    [
        // ── Ingress ──

        new CatalogEntry
        {
            Key = "gateway-api-crds",
            DisplayName = "Gateway API CRDs",
            Description = "Installs the Kubernetes Gateway API CRDs (experimental channel): HTTPRoute, TCPRoute, TLSRoute, UDPRoute, GRPCRoute, Gateway, GatewayClass. Required by Traefik and Istio Gateway API support.",
            Icon = "bi-diagram-2",
            Category = "Ingress",
            ComponentType = "ManifestUrl",
            HelmRepoUrl = "https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.2.1/experimental-install.yaml",
            HelmChartName = "",
            DefaultNamespace = "gateway-system",
            DefaultReleaseName = "gateway-api-crds",
            // Applied from a raw manifest URL, so there's no owner=helm secret for the Helm
            // scan to find. Detect by the presence of the Gateway API CRDs themselves — they
            // ARE this component, so any one being present means it's installed.
            DetectionCrds =
            [
                "gateways.gateway.networking.k8s.io",
                "httproutes.gateway.networking.k8s.io",
                "gatewayclasses.gateway.networking.k8s.io"
            ],
            FormFields = [],
            DefaultValues = ""
        },

        new CatalogEntry
        {
            Key = "traefik",
            DisplayName = "Traefik (Gateway API)",
            Description = "Cloud-native ingress controller with Gateway API support. Handles TLS termination, routing, and load balancing using the standard Kubernetes Gateway API.",
            Icon = "bi-signpost-split",
            Category = "Ingress",
            HelmRepoUrl = "https://traefik.github.io/charts",
            HelmChartName = "traefik",
            DefaultNamespace = "traefik",
            DefaultReleaseName = "traefik",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "gateway-name", Label = "Gateway Name",
                    YamlPath = "gateway.name", Type = FormFieldType.Text,
                    DefaultValue = "traefik-gateway",
                    HelpText = "Name of the Gateway resource Traefik manages"
                },
                new ComponentFormField
                {
                    Key = "http-port", Label = "HTTP Port",
                    YamlPath = "ports.web.exposedPort", Type = FormFieldType.Number,
                    DefaultValue = "80"
                },
                new ComponentFormField
                {
                    Key = "https-port", Label = "HTTPS Port",
                    YamlPath = "ports.websecure.exposedPort", Type = FormFieldType.Number,
                    DefaultValue = "443"
                },
                new ComponentFormField
                {
                    Key = "gateway-api-provider", Label = "Enable Gateway API Provider",
                    YamlPath = "gatewayAPI.enabled", Type = FormFieldType.Toggle,
                    DefaultValue = "true",
                    HelpText = "Enable Traefik's Gateway API provider (requires gateway-api-crds installed)"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "128Mi", Placeholder = "e.g. 128Mi, 256Mi"
                }
            ],
            DefaultValues = """
                # Enable Gateway API provider (standard Kubernetes Gateway API)
                providers:
                  kubernetesGateway:
                    enabled: true
                
                # Gateway resource — Traefik will create and manage this
                gateway:
                  enabled: true
                  name: traefik-gateway
                  listeners:
                    - name: web
                      protocol: HTTP
                      port: 80
                    - name: websecure
                      protocol: HTTPS
                      port: 443
                      tls:
                        mode: Terminate
                        certificateRefs:
                          - name: wildcard-tls
                
                # Entrypoints
                ports:
                  web:
                    port: 8000
                    exposedPort: 80
                  websecure:
                    port: 8443
                    exposedPort: 443
                    tls:
                      enabled: true
                
                # Resource allocation
                resources:
                  requests:
                    memory: 128Mi
                    cpu: 100m
                
                gatewayAPI:
                  enabled: true
                """,
            Dependencies = ["gateway-api-crds"]
        },

        new CatalogEntry
        {
            Key = "istio",
            DisplayName = "Istio Gateway (External)",
            Description = "Internet-facing ingress gateway with Gateway API support. Provides a public LoadBalancer for external traffic entering the cluster.",
            Icon = "bi-diagram-3",
            Category = "Ingress",
            HelmRepoUrl = "https://istio-release.storage.googleapis.com/charts",
            HelmChartName = "gateway",
            DefaultNamespace = "istio-system",
            DefaultReleaseName = "istio-ingress-external",
            Dependencies = ["gateway-api-crds", "istio-base"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "service-type", Label = "Service Type",
                    YamlPath = "service.type", Type = FormFieldType.Select,
                    DefaultValue = "LoadBalancer",
                    Options = ["LoadBalancer", "NodePort", "ClusterIP"],
                    HelpText = "How the external gateway is exposed"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "128Mi", Placeholder = "e.g. 128Mi, 256Mi"
                }
            ],
            DefaultValues = """
                # Istio External Gateway — internet-facing LoadBalancer
                # Requires istio-base (istiod) to be installed first

                service:
                  type: LoadBalancer

                # Resource allocation
                resources:
                  requests:
                    memory: 128Mi
                    cpu: 100m
                """
        },

        new CatalogEntry
        {
            Key = "istio-internal",
            DisplayName = "Istio Gateway (Internal)",
            Description = "Internal ingress gateway for private/mesh-internal traffic. Provides a private LoadBalancer only reachable from within the network (VPC/VNET).",
            Icon = "bi-diagram-3",
            Category = "Ingress",
            HelmRepoUrl = "https://istio-release.storage.googleapis.com/charts",
            HelmChartName = "gateway",
            DefaultNamespace = "istio-system",
            DefaultReleaseName = "istio-ingress-internal",
            Dependencies = ["gateway-api-crds", "istio-base"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "service-type", Label = "Service Type",
                    YamlPath = "service.type", Type = FormFieldType.Select,
                    DefaultValue = "LoadBalancer",
                    Options = ["LoadBalancer", "NodePort", "ClusterIP"],
                    HelpText = "How the internal gateway is exposed"
                },
                new ComponentFormField
                {
                    Key = "internal-annotation", Label = "Internal LB Annotation",
                    YamlPath = "service.annotations.networking\\.istio\\.io/internal", Type = FormFieldType.Text,
                    DefaultValue = "true",
                    HelpText = "Cloud provider annotation to make the LB internal (override per provider)"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "128Mi", Placeholder = "e.g. 128Mi, 256Mi"
                }
            ],
            DefaultValues = """
                # Istio Internal Gateway — private LoadBalancer (not internet-facing)
                # Requires istio-base (istiod) to be installed first
                
                service:
                  type: LoadBalancer
                  annotations:
                    # Generic internal annotation — override with your cloud provider's annotation:
                    # Azure: service.beta.kubernetes.io/azure-load-balancer-internal: "true"
                    # GCP: cloud.google.com/load-balancer-type: "Internal"
                    # AWS: service.beta.kubernetes.io/aws-load-balancer-scheme: "internal"
                    networking.istio.io/internal: "true"
                
                # Resource allocation
                resources:
                  requests:
                    memory: 128Mi
                    cpu: 100m
                """
        },

        new CatalogEntry
        {
            Key = "istio-base",
            DisplayName = "Istio Control Plane",
            Description = "Installs Istio CRDs (base) and the istiod control plane in one step. Required foundation for Istio service mesh — manages proxies, certificates, and Gateway API routing.",
            Icon = "bi-diagram-3",
            Category = "Ingress",
            HelmRepoUrl = "https://istio-release.storage.googleapis.com/charts",
            HelmChartName = "base",
            DefaultNamespace = "istio-system",
            DefaultReleaseName = "istio-base",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "install-istiod", Label = "Install istiod control plane",
                    YamlPath = "subchart:istiod", Type = FormFieldType.Toggle,
                    DefaultValue = "true",
                    HelpText = "Install the Istio control plane (istiod) alongside the base CRDs",
                    SubchartDefaultValues = """
                        # Enable Gateway API support in istiod
                        pilot:
                          env:
                            PILOT_ENABLE_GATEWAY_API: "true"
                            PILOT_ENABLE_GATEWAY_API_STATUS: "true"
                            PILOT_ENABLE_GATEWAY_API_DEPLOYMENT_CONTROLLER: "true"

                        # Resource allocation
                        resources:
                          requests:
                            memory: 256Mi
                            cpu: 100m
                        """
                }
            ],
            Dependencies = ["gateway-api-crds"]
        },

        // ── Certificate Management ──

        new CatalogEntry
        {
            Key = "cert-manager",
            DisplayName = "cert-manager",
            Description = "Automatic TLS certificate management for Kubernetes. Issues and renews certificates from Let's Encrypt and other CAs. Required for HTTPS on exposed services.",
            Icon = "bi-file-earmark-lock",
            Category = "Certificate Management",
            HelmRepoUrl = "https://charts.jetstack.io",
            HelmChartName = "cert-manager",
            DefaultNamespace = "cert-manager",
            DefaultReleaseName = "cert-manager",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "install-crds", Label = "Install CRDs",
                    YamlPath = "crds.enabled", Type = FormFieldType.Toggle,
                    DefaultValue = "true",
                    HelpText = "Install cert-manager Custom Resource Definitions"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "50m", Placeholder = "e.g. 50m, 100m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "128Mi", Placeholder = "e.g. 128Mi, 256Mi"
                }
            ],
            DefaultValues = """
                # Install CRDs with the chart
                crds:
                  enabled: true

                # Resource allocation
                resources:
                  requests:
                    memory: 128Mi
                    cpu: 50m
                """
        },

        new CatalogEntry
        {
            Key = "letsencrypt-issuer",
            DisplayName = "Let's Encrypt ClusterIssuer",
            Description = "Configures a ClusterIssuer that uses Let's Encrypt to automatically issue and renew TLS certificates. Supports HTTP-01 (Gateway API) and DNS-01 challenge solvers — both can be active simultaneously — with DNS-01 across Azure DNS, Cloudflare, AWS Route53, or Google Cloud DNS.",
            Icon = "bi-lock",
            Category = "Certificate Management",
            HelmRepoUrl = "https://charts.jetstack.io",
            HelmChartName = "letsencrypt-issuer",
            DefaultNamespace = "cert-manager",
            DefaultReleaseName = "letsencrypt-issuer",
            Dependencies = ["cert-manager"],
            ComponentType = "Manifest",
            // The clusterissuers CRD ships with cert-manager, so CRD presence can't tell us
            // whether a Let's Encrypt issuer was actually applied. Detect the live instance
            // instead, keeping only ACME issuers pointed at Let's Encrypt.
            DetectionResource = new DetectionResource(
                "cert-manager.io", "v1", "clusterissuers", Matches: "letsencrypt.org"),
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "acme-email", Label = "ACME Email",
                    YamlPath = "spec.acme.email", Type = FormFieldType.Text,
                    DefaultValue = "ops@example.com",
                    Placeholder = "ops@yourdomain.com",
                    HelpText = "Contact email for Let's Encrypt notifications"
                },
                new ComponentFormField
                {
                    Key = "acme-server", Label = "ACME Server",
                    YamlPath = "spec.acme.server", Type = FormFieldType.Select,
                    DefaultValue = "https://acme-v02.api.letsencrypt.org/directory",
                    Options = ["https://acme-v02.api.letsencrypt.org/directory", "https://acme-staging-v02.api.letsencrypt.org/directory"],
                    HelpText = "Use staging for testing, production for real certificates"
                },
                // ── Challenge solver selection ──
                // These carry an empty YamlPath: they are read by LetsEncryptSolverBuilder,
                // which constructs the spec.acme.solvers list, rather than being merged directly.
                new ComponentFormField
                {
                    Key = "enable-http01", Label = "HTTP-01 solver (Gateway API)",
                    YamlPath = "", Type = FormFieldType.Toggle,
                    DefaultValue = "true",
                    HelpText = "Solve challenges over HTTP via the cluster's ingress Gateway. Good default for standard certificates."
                },
                new ComponentFormField
                {
                    Key = "enable-dns01", Label = "DNS-01 solver",
                    YamlPath = "", Type = FormFieldType.Toggle,
                    DefaultValue = "false",
                    HelpText = "Solve challenges via DNS. Required for wildcard (*.example.com) certificates."
                },
                new ComponentFormField
                {
                    Key = "dns-provider", Label = "DNS-01 Provider",
                    YamlPath = "", Type = FormFieldType.Select,
                    Options = ["azure", "cloudflare", "route53", "google"],
                    Placeholder = "— select DNS provider —",
                    DependsOnKey = "enable-dns01", DependsOnValue = "true",
                    HelpText = "Which DNS provider hosts your zone."
                },

                // Azure DNS
                new ComponentFormField
                {
                    Key = "dns-zone", Label = "Azure — Hosted Zone",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "example.com",
                    DependsOnKey = "dns-provider", DependsOnValue = "azure",
                    HelpText = "Azure DNS zone name (e.g. example.com)"
                },
                new ComponentFormField
                {
                    Key = "dns-resource-group", Label = "Azure — Resource Group",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "my-dns-rg",
                    DependsOnKey = "dns-provider", DependsOnValue = "azure",
                    HelpText = "Azure resource group containing the DNS zone"
                },
                new ComponentFormField
                {
                    Key = "dns-subscription-id", Label = "Azure — Subscription ID",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "00000000-0000-0000-0000-000000000000",
                    DependsOnKey = "dns-provider", DependsOnValue = "azure",
                    HelpText = "Azure subscription ID containing the DNS zone"
                },
                new ComponentFormField
                {
                    Key = "dns-tenant-id", Label = "Azure — Tenant ID",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "00000000-0000-0000-0000-000000000000",
                    DependsOnKey = "dns-provider", DependsOnValue = "azure",
                    HelpText = "Azure AD tenant ID for the service principal"
                },
                new ComponentFormField
                {
                    Key = "dns-client-id", Label = "Azure — Client ID",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "00000000-0000-0000-0000-000000000000",
                    DependsOnKey = "dns-provider", DependsOnValue = "azure",
                    HelpText = "Service principal (app registration) client ID"
                },
                new ComponentFormField
                {
                    Key = "dns-client-secret", Label = "Azure — Client Secret",
                    YamlPath = "", Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    SecretName = "azuredns-client-secret",
                    KubernetesSecretName = "azuredns-config",
                    KubernetesSecretNamespace = "cert-manager",
                    DependsOnKey = "dns-provider", DependsOnValue = "azure",
                    HelpText = "Service principal client secret — stored in vault and synced to K8s Secret 'azuredns-config'"
                },

                // Cloudflare
                new ComponentFormField
                {
                    Key = "cloudflare-api-token", Label = "Cloudflare — API Token",
                    YamlPath = "", Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    SecretName = "cloudflare-api-token",
                    KubernetesSecretName = "cloudflare-api-token-secret",
                    KubernetesSecretNamespace = "cert-manager",
                    DependsOnKey = "dns-provider", DependsOnValue = "cloudflare",
                    HelpText = "Scoped API token with Zone.DNS edit rights — stored in vault, synced to K8s Secret 'cloudflare-api-token-secret'"
                },

                // AWS Route53
                new ComponentFormField
                {
                    Key = "route53-region", Label = "Route53 — Region",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "eu-west-1",
                    DependsOnKey = "dns-provider", DependsOnValue = "route53",
                    HelpText = "AWS region for the Route53 API"
                },
                new ComponentFormField
                {
                    Key = "route53-hosted-zone-id", Label = "Route53 — Hosted Zone ID (optional)",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "Z0123456789ABC",
                    DependsOnKey = "dns-provider", DependsOnValue = "route53",
                    HelpText = "Restrict the issuer to a specific hosted zone (optional)"
                },
                new ComponentFormField
                {
                    Key = "route53-access-key-id", Label = "Route53 — Access Key ID",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "AKIA...",
                    DependsOnKey = "dns-provider", DependsOnValue = "route53",
                    HelpText = "IAM access key ID with Route53 change permissions"
                },
                new ComponentFormField
                {
                    Key = "route53-secret-access-key", Label = "Route53 — Secret Access Key",
                    YamlPath = "", Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    SecretName = "route53-secret-access-key",
                    KubernetesSecretName = "route53-credentials",
                    KubernetesSecretNamespace = "cert-manager",
                    DependsOnKey = "dns-provider", DependsOnValue = "route53",
                    HelpText = "IAM secret access key — stored in vault, synced to K8s Secret 'route53-credentials'"
                },

                // Google Cloud DNS
                new ComponentFormField
                {
                    Key = "clouddns-project", Label = "Google — Project ID",
                    YamlPath = "", Type = FormFieldType.Text, Placeholder = "my-gcp-project",
                    DependsOnKey = "dns-provider", DependsOnValue = "google",
                    HelpText = "GCP project ID hosting the Cloud DNS zone"
                },
                new ComponentFormField
                {
                    Key = "clouddns-service-account", Label = "Google — Service Account JSON",
                    YamlPath = "", Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    SecretName = "clouddns-service-account",
                    KubernetesSecretName = "clouddns-sa",
                    KubernetesSecretNamespace = "cert-manager",
                    DependsOnKey = "dns-provider", DependsOnValue = "google",
                    HelpText = "Service account key JSON with dns.admin — stored in vault, synced to K8s Secret 'clouddns-sa'"
                }
            ],
            // No solvers here — LetsEncryptSolverBuilder appends HTTP-01 and/or DNS-01
            // solvers based on the form selections above.
            DefaultValues = """
                apiVersion: cert-manager.io/v1
                kind: ClusterIssuer
                metadata:
                  name: letsencrypt-prod
                spec:
                  acme:
                    server: https://acme-v02.api.letsencrypt.org/directory
                    email: ops@example.com
                    privateKeySecretRef:
                      name: letsencrypt-prod-key
                """
        },

        new CatalogEntry
        {
            Key = "trust-manager",
            DisplayName = "trust-manager",
            Description = "Distributes CA trust bundles across the cluster. Syncs a set of trusted CA certificates into a ConfigMap (or Secret) in every selected namespace so workloads can mount a common trust store. Companion to cert-manager; drives the CA & Trust management UI.",
            Icon = "bi-patch-check",
            Category = "Certificate Management",
            HelmRepoUrl = "https://charts.jetstack.io",
            HelmChartName = "trust-manager",
            DefaultNamespace = "cert-manager",
            DefaultReleaseName = "trust-manager",
            Dependencies = ["cert-manager"],
            // trust-manager ships the Bundle CRD; detect it directly so an out-of-band
            // install (kubectl/Terraform) is still recognised by the component scan.
            DetectionCrds = ["bundles.trust.cert-manager.io"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "trust-namespace", Label = "Trust namespace",
                    YamlPath = "app.trust.namespace", Type = FormFieldType.Text,
                    DefaultValue = "cert-manager",
                    HelpText = "Namespace trust-manager watches for source ConfigMaps/Secrets referenced by Bundles."
                },
                new ComponentFormField
                {
                    Key = "enable-secret-targets", Label = "Allow Secret targets",
                    YamlPath = "secretTargets.enabled", Type = FormFieldType.Toggle,
                    DefaultValue = "false",
                    HelpText = "Permit Bundles to write their trust store to a Secret (not just a ConfigMap). Required if you distribute a bundle as a Secret."
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "50m", Placeholder = "e.g. 50m, 100m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "80Mi", Placeholder = "e.g. 80Mi, 128Mi"
                }
            ],
            DefaultValues = """
                # Namespace trust-manager watches for Bundle source material.
                app:
                  trust:
                    namespace: cert-manager

                # Only enable if you distribute trust bundles as Secrets rather than ConfigMaps.
                secretTargets:
                  enabled: false

                resources:
                  requests:
                    memory: 80Mi
                    cpu: 50m
                """
        },

        // ── Monitoring ──

        new CatalogEntry
        {
            Key = "kube-prometheus-stack",
            DisplayName = "Kube Prometheus Stack",
            Description = "Full monitoring stack with Prometheus, Grafana, and Alertmanager. Provides metrics collection, dashboards, and alerting for your cluster. Can be exposed externally via Gateway API.",
            Icon = "bi-graph-up",
            Category = "Monitoring",
            HelmRepoUrl = "https://prometheus-community.github.io/helm-charts",
            HelmChartName = "kube-prometheus-stack",
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "kube-prometheus-stack",
            // Heavy: Prometheus + Grafana + Alertmanager + a node-exporter DaemonSet + CRDs.
            InstallTimeout = "30m0s",
            RequiresOneOf =
            [
                new DependencyRequirement
                {
                    Label = "Ingress Controller",
                    Options = ["traefik", "istio"]
                }
            ],
            Dependencies = ["cert-manager", "letsencrypt-issuer"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "grafana-password", Label = "Grafana Admin Password",
                    YamlPath = "grafana.adminPassword", Type = FormFieldType.Password,
                    DefaultValue = "admin",
                    HelpText = "Password for the Grafana 'admin' user — stored encrypted in the vault",
                    StoreAsSecret = true,
                    SecretName = "GRAFANA_ADMIN_PASSWORD"
                },
                new ComponentFormField
                {
                    Key = "grafana-enabled", Label = "Enable Grafana",
                    YamlPath = "grafana.enabled", Type = FormFieldType.Toggle,
                    DefaultValue = "true"
                },
                new ComponentFormField
                {
                    Key = "grafana-version", Label = "Grafana Version",
                    YamlPath = "grafana.image.tag", Type = FormFieldType.Text,
                    DefaultValue = "13.1.0", Placeholder = "e.g. 13.1.0",
                    HelpText = "Grafana application image tag to run. Pinned by default; change to upgrade/downgrade."
                },
                new ComponentFormField
                {
                    Key = "alertmanager-enabled", Label = "Enable Alertmanager",
                    YamlPath = "alertmanager.enabled", Type = FormFieldType.Toggle,
                    DefaultValue = "true"
                },
                new ComponentFormField
                {
                    Key = "retention", Label = "Prometheus Retention",
                    YamlPath = "prometheus.prometheusSpec.retention", Type = FormFieldType.Text,
                    DefaultValue = "15d", Placeholder = "e.g. 15d, 30d, 90d",
                    HelpText = "How long to keep metrics data"
                },
                new ComponentFormField
                {
                    Key = "prom-cpu-request", Label = "Prometheus CPU Request",
                    YamlPath = "prometheus.prometheusSpec.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "250m", Placeholder = "e.g. 250m, 500m"
                },
                new ComponentFormField
                {
                    Key = "prom-memory-request", Label = "Prometheus Memory Request",
                    YamlPath = "prometheus.prometheusSpec.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "512Mi", Placeholder = "e.g. 512Mi, 1Gi"
                }
            ],
            DefaultValues = """
                # Grafana configuration
                grafana:
                  enabled: true
                  adminPassword: admin
                  image:
                    tag: "13.1.0"
                
                # Alertmanager configuration
                alertmanager:
                  enabled: true
                
                # Prometheus configuration
                prometheus:
                  prometheusSpec:
                    retention: 15d
                    resources:
                      requests:
                        memory: 512Mi
                        cpu: 250m
                
                # ── External access via Gateway API HTTPRoutes ──
                # Uncomment and adjust the hostnames to expose services externally.
                # Requires an ingress controller (Traefik or Istio) and cert-manager.
                
                # grafana:
                #   ingress:
                #     enabled: false  # We use HTTPRoute instead
                # ---
                # To expose Grafana, apply an HTTPRoute like:
                # apiVersion: gateway.networking.k8s.io/v1
                # kind: HTTPRoute
                # metadata:
                #   name: grafana
                #   namespace: monitoring
                #   annotations:
                #     cert-manager.io/cluster-issuer: letsencrypt-prod
                # spec:
                #   parentRefs:
                #     - name: traefik-gateway
                #       namespace: traefik
                #   hostnames:
                #     - grafana.example.com
                #   rules:
                #     - backendRefs:
                #         - name: kube-prometheus-stack-grafana
                #           port: 80
                """
        },

        new CatalogEntry
        {
            Key = "loki",
            DisplayName = "Grafana Loki",
            Description = "Log aggregation system. Collects, indexes, and queries logs from all pods on the cluster. Integrates with Grafana for log browsing and with the EntKube log viewer.",
            Icon = "bi-journal-text",
            Category = "Monitoring",
            HelmRepoUrl = "https://grafana.github.io/helm-charts",
            HelmChartName = "loki",
            HelmChartVersion = "6.30.0",
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "loki",
            // Stateful (persistence + optional S3) — can be slow to become Ready.
            InstallTimeout = "20m0s",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "loki-version", Label = "Loki Version",
                    YamlPath = "loki.image.tag", Type = FormFieldType.Text,
                    DefaultValue = "3.7.0", Placeholder = "e.g. 3.7.0",
                    HelpText = "Loki application image tag to run. Pinned by default; change to upgrade/downgrade."
                },
                new ComponentFormField
                {
                    Key = "deployment-mode", Label = "Deployment Mode",
                    YamlPath = "deploymentMode", Type = FormFieldType.Select,
                    DefaultValue = "SingleBinary",
                    Options = ["SingleBinary", "SimpleScalable"],
                    HelpText = "SingleBinary runs all Loki components in one pod — suitable for most clusters. SimpleScalable splits read/write/backend for higher throughput."
                },
                new ComponentFormField
                {
                    Key = "storage-link", Label = "S3 Chunk Storage",
                    YamlPath = "loki:storage-link-id", Type = FormFieldType.StorageLink,
                    HelpText = "S3-compatible bucket for log chunk storage. Leave empty to use local filesystem (not recommended for production)."
                },
                new ComponentFormField
                {
                    Key = "storage-class", Label = "Storage Class",
                    YamlPath = "singleBinary.persistence.storageClass", Type = FormFieldType.Text,
                    Placeholder = "e.g. local-path, longhorn, standard",
                    HelpText = "StorageClass for the data volume. Run 'kubectl get storageclass' to find the name. Use '-' to disable dynamic provisioning."
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "singleBinary.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "singleBinary.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                },
                new ComponentFormField
                {
                    Key = "cpu-limit", Label = "CPU Limit",
                    YamlPath = "singleBinary.resources.limits.cpu", Type = FormFieldType.Text,
                    DefaultValue = "1000m", Placeholder = "e.g. 500m, 1000m",
                    HelpText = "Required on clusters that enforce a LimitRange — pods without limits are rejected."
                },
                new ComponentFormField
                {
                    Key = "memory-limit", Label = "Memory Limit",
                    YamlPath = "singleBinary.resources.limits.memory", Type = FormFieldType.Text,
                    DefaultValue = "1Gi", Placeholder = "e.g. 1Gi, 2Gi",
                    HelpText = "Hard memory cap. Loki's startup (TSDB index + WAL replay) is the memory peak — set this well above the request, or the pod gets OOMKilled mid-replay and crashloops."
                },
                // Hidden secret fields — written by LokiService.WriteStorageHelmValuesAsync,
                // injected at install time via InjectSecretsIntoValuesAsync.
                new ComponentFormField
                {
                    Key = "loki-s3-access-key", Label = "S3 Access Key",
                    YamlPath = "loki.storage.s3.accessKeyId", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "loki-s3-access-key", Hidden = true
                },
                new ComponentFormField
                {
                    Key = "loki-s3-secret-key", Label = "S3 Secret Key",
                    YamlPath = "loki.storage.s3.secretAccessKey", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "loki-s3-secret-key", Hidden = true
                }
            ],
            DefaultValues = """
                deploymentMode: SingleBinary

                loki:
                  image:
                    tag: "3.7.0"
                  auth_enabled: false
                  commonConfig:
                    replication_factor: 1
                    # Working dir / data root — the singleBinary PVC is mounted here, so all
                    # write paths below must live under it (the rootfs is read-only).
                    path_prefix: /var/loki
                  storage:
                    type: filesystem
                    # Pin the filesystem object store to the mounted volume. Without explicit
                    # directories the store path resolves to an empty/relative path, so chunk
                    # writes and loki_cluster_seed.json land on the read-only root FS and the
                    # ingester fails every flush ("store put chunk: mkdir fake: read-only
                    # file system"), which stalls the rollout until the helm --wait deadline.
                    filesystem:
                      chunks_directory: /var/loki/chunks
                      rules_directory: /var/loki/rules
                  # Explicit schema instead of useTestSchema (which is documented for quick
                  # testing only and does not wire up persistent filesystem paths). Mirrors the
                  # chart's test schema exactly so any already-written chunks stay queryable.
                  schemaConfig:
                    configs:
                      - from: "2024-04-01"
                        store: tsdb
                        object_store: filesystem
                        schema: v13
                        index:
                          prefix: index_
                          period: 24h
                  # The chart hardens Loki (non-root 10001, drop ALL); only seccompProfile
                  # is missing — add it without disturbing the chart's fsGroup/uid defaults
                  # (deep-merged).
                  podSecurityContext:
                    seccompProfile:
                      type: RuntimeDefault
                  # Loki's filesystem object store and assorted /tmp scratch writes need a
                  # writable rootfs, so the chart's default readOnlyRootFilesystem: true breaks
                  # startup ("read-only file system" on chunk flush / temp writes). Disable it
                  # for the Loki container only; the rest of the chart's hardening (drop ALL,
                  # no-privesc, non-root) is left intact. Mirrors the MinIO entry, which also
                  # writes to local paths.
                  containerSecurityContext:
                    readOnlyRootFilesystem: false
                  # Promote the OTLP resource attributes the EntKube Telemetry Collector
                  # (otel-collector) sets — namespace/pod/container/app/node — to stream
                  # labels under their SHORT names, so logs ingested via Loki's OTLP
                  # endpoint carry the exact labels the EntKube log viewer's LogQL filters
                  # on (BuildLogQL). Without this, Loki's OTLP path would emit sanitized
                  # names (k8s_namespace_name, …) and the namespace/pod dropdowns would be
                  # empty. Harmless for logs pushed the classic way (Alloy → /loki push),
                  # which already carry these labels directly.
                  limits_config:
                    otlp_config:
                      resource_attributes:
                        attributes_config:
                          - action: index_label
                            attributes: [namespace, pod, container, app, node]

                # Same seccomp gap on the gateway (nginx) pod.
                gateway:
                  podSecurityContext:
                    seccompProfile:
                      type: RuntimeDefault

                singleBinary:
                  replicas: 1
                  persistence:
                    enabled: true
                    size: 10Gi
                  resources:
                    requests:
                      cpu: 100m
                      memory: 256Mi
                    # Startup (TSDB index + WAL replay) is the memory peak, not steady state.
                    # Keep the limit well above the request, or the pod is OOMKilled mid-replay
                    # and crashloops (the loki-canary then replays its spot-checks each restart).
                    limits:
                      cpu: 1000m
                      memory: 1Gi

                # Disable distributed components for SingleBinary mode
                read:
                  replicas: 0
                write:
                  replicas: 0
                backend:
                  replicas: 0

                # The chart enables Memcached caches by default with 8 GiB requests each,
                # which is excessive for most clusters. Disable them for SingleBinary.
                chunksCache:
                  enabled: false
                resultsCache:
                  enabled: false
                """
        },

        new CatalogEntry
        {
            Key = "mimir",
            DisplayName = "Grafana Mimir",
            Description = "Horizontally scalable, long-term storage for Prometheus metrics. Runs the ingest-storage architecture (writes buffer through Kafka before reaching ingesters), so it requires the Apache Kafka Cluster component. Prometheus-compatible query API — the remote-write target for kube-prometheus-stack or Grafana Alloy.",
            Icon = "bi-graph-up",
            Category = "Monitoring",
            HelmRepoUrl = "https://grafana.github.io/helm-charts",
            HelmChartName = "mimir-distributed",
            HelmChartVersion = "6.1.0",
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "mimir",
            // Distributed microservices + object storage — slow to become Ready.
            InstallTimeout = "20m0s",
            // Ingest storage needs a Kafka backend. Install the Strimzi operator, then
            // provision a Kafka cluster in the Messaging tab and point the Kafka Bootstrap
            // Address field below at its bootstrap service.
            Dependencies = ["strimzi-kafka-operator"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "mimir-version", Label = "Mimir Version",
                    YamlPath = "image.tag", Type = FormFieldType.Text,
                    DefaultValue = "3.1.2", Placeholder = "e.g. 3.1.2",
                    HelpText = "Grafana Mimir application image tag. Pinned by default; change to upgrade/downgrade."
                },
                new ComponentFormField
                {
                    Key = "kafka-address", Label = "Kafka Bootstrap Address",
                    YamlPath = "mimir.structuredConfig.ingest_storage.kafka.address", Type = FormFieldType.Text,
                    DefaultValue = "kafka-kafka-bootstrap.kafka.svc.cluster.local:9092",
                    HelpText = "host:port of the Kafka bootstrap service used by the ingest-storage write path. Defaults to the Apache Kafka Cluster component's service."
                },
                new ComponentFormField
                {
                    Key = "storage-link", Label = "S3 Metrics Storage",
                    YamlPath = "mimir:storage-link-id", Type = FormFieldType.StorageLink,
                    HelpText = "S3-compatible bucket for long-term metric (block) storage. Leave empty to use the chart's bundled MinIO (fine for testing, not for production)."
                },
                // Hidden secret fields — written by MimirService.WriteStorageHelmValuesAsync,
                // injected at install time via InjectSecretsIntoValuesAsync.
                new ComponentFormField
                {
                    Key = "mimir-s3-access-key", Label = "S3 Access Key",
                    YamlPath = "mimir.structuredConfig.common.storage.s3.access_key_id", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "mimir-s3-access-key", Hidden = true
                },
                new ComponentFormField
                {
                    Key = "mimir-s3-secret-key", Label = "S3 Secret Key",
                    YamlPath = "mimir.structuredConfig.common.storage.s3.secret_access_key", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "mimir-s3-secret-key", Hidden = true
                }
            ],
            DefaultValues = """
                # Grafana Mimir (mimir-distributed 6.x, ingest-storage architecture).
                # The write path buffers through Kafka, so the chart's own bundled Kafka
                # is disabled and Mimir points at the shared Apache Kafka Cluster component
                # instead. Object storage defaults to the chart's bundled MinIO; select an
                # S3 storage link to switch to external object storage — that flips
                # minio.enabled to false and writes mimir.structuredConfig.common.storage
                # (see MimirService.WriteStorageHelmValuesAsync).
                image:
                  tag: "3.1.2"

                # Use the shared Strimzi-managed Kafka, not the chart's demo single-node one.
                kafka:
                  enabled: false

                minio:
                  enabled: true

                mimir:
                  structuredConfig:
                    ingest_storage:
                      enabled: true
                      kafka:
                        address: kafka-kafka-bootstrap.kafka.svc.cluster.local:9092
                        topic: mimir-ingest
                        auto_create_topic_enabled: true
                        auto_create_topic_default_partitions: 100
                """
        },

        new CatalogEntry
        {
            Key = "tempo",
            DisplayName = "Grafana Tempo",
            Description = "Distributed tracing backend (microservices mode). Ingests spans via OTLP / Jaeger / Zipkin and stores them in object storage, queryable from Grafana by trace ID. Pairs with Grafana Alloy or the OpenTelemetry Collector as the trace pipeline. Note: the published chart tracks Tempo 2.9.x (classic distributor→ingester→S3); the Kafka-based 3.0 architecture is not yet in a released chart.",
            Icon = "bi-diagram-3",
            Category = "Monitoring",
            HelmRepoUrl = "https://grafana.github.io/helm-charts",
            HelmChartName = "tempo-distributed",
            HelmChartVersion = "1.61.3",
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "tempo",
            // Distributed microservices + object storage — allow extra time to become Ready.
            InstallTimeout = "20m0s",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "tempo-version", Label = "Tempo Version",
                    YamlPath = "tempo.image.tag", Type = FormFieldType.Text,
                    DefaultValue = "2.9.0", Placeholder = "e.g. 2.9.0",
                    HelpText = "Grafana Tempo application image tag. Defaults to the chart's app version; change to upgrade/downgrade."
                },
                new ComponentFormField
                {
                    Key = "storage-link", Label = "S3 Trace Storage",
                    YamlPath = "tempo:storage-link-id", Type = FormFieldType.StorageLink,
                    HelpText = "S3-compatible bucket for trace block storage. Strongly recommended — in microservices mode the components must share object storage; the local backend does not work across pods."
                },
                // Hidden secret fields — written by TempoService.WriteStorageHelmValuesAsync,
                // injected at install time via InjectSecretsIntoValuesAsync.
                new ComponentFormField
                {
                    Key = "tempo-s3-access-key", Label = "S3 Access Key",
                    YamlPath = "storage.trace.s3.access_key", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "tempo-s3-access-key", Hidden = true
                },
                new ComponentFormField
                {
                    Key = "tempo-s3-secret-key", Label = "S3 Secret Key",
                    YamlPath = "storage.trace.s3.secret_key", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "tempo-s3-secret-key", Hidden = true
                }
            ],
            DefaultValues = """
                # Grafana Tempo (tempo-distributed, microservices mode). Trace blocks live
                # in object storage — select an S3 storage link to configure storage.trace
                # (see TempoService.WriteStorageHelmValuesAsync). In distributed mode the
                # local backend can't be shared across pods, so S3 is effectively required.

                # Apply the restricted Pod Security baseline to all components. The chart's
                # global securityContext runs non-root but omits seccompProfile / the
                # container hardening that the "restricted" PSA level checks.
                global:
                  podSecurityContext:
                    runAsNonRoot: true
                    seccompProfile:
                      type: RuntimeDefault
                  securityContext:
                    allowPrivilegeEscalation: false
                    runAsNonRoot: true
                    capabilities:
                      drop:
                        - ALL
                    seccompProfile:
                      type: RuntimeDefault
                """
        },

        new CatalogEntry
        {
            Key = "grafana-alloy",
            DisplayName = "Grafana Alloy (Log Collector)",
            Description = "Node-level log collector. Runs as a DaemonSet, tails container logs on every node, and ships them to Loki tagged with namespace/pod/container labels so the EntKube log viewer can filter them. Required for the Operations → Logs view to show any data — Loki on its own only stores logs, it does not collect them.",
            Icon = "bi-arrow-down-up",
            Category = "Monitoring",
            HelmRepoUrl = "https://grafana.github.io/helm-charts",
            HelmChartName = "alloy",
            // Version intentionally unpinned (latest). The chart's value schema
            // (alloy.configMap.content, alloy.mounts.varlog) is stable across recent releases.
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "alloy",
            // Rolls out as a DaemonSet (one pod per node) and can be slow to become Ready on
            // busy/constrained clusters — give the --wait more headroom before failing.
            InstallTimeout = "30m0s",
            // Alloy ships logs to Loki — installing it before Loki exists is pointless.
            Dependencies = ["loki"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "alloy-version", Label = "Alloy Version",
                    YamlPath = "image.tag", Type = FormFieldType.Text,
                    DefaultValue = "v1.17.0", Placeholder = "e.g. v1.17.0",
                    HelpText = "Grafana Alloy image tag to run (tags are 'v'-prefixed). Pinned by default; change to upgrade/downgrade."
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "alloy.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "50m", Placeholder = "e.g. 50m, 100m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "alloy.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "128Mi", Placeholder = "e.g. 128Mi, 256Mi"
                },
                new ComponentFormField
                {
                    Key = "cpu-limit", Label = "CPU Limit",
                    YamlPath = "alloy.resources.limits.cpu", Type = FormFieldType.Text,
                    DefaultValue = "200m", Placeholder = "e.g. 200m, 500m",
                    HelpText = "Required on clusters that enforce a LimitRange — pods without limits are rejected."
                },
                new ComponentFormField
                {
                    Key = "memory-limit", Label = "Memory Limit",
                    YamlPath = "alloy.resources.limits.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi",
                    HelpText = "Hard memory cap. Keep comfortably above the request to avoid OOMKills under log bursts."
                }
            ],
            // The Alloy pipeline below discovers every pod, relabels Kubernetes
            // metadata into the EXACT Loki stream labels the UI queries on
            // (namespace, pod, container), tails the on-disk CRI logs under
            // /var/log/pods, and pushes to the in-cluster Loki gateway.
            //
            // The push URL targets the default Loki release (release "loki" in
            // "monitoring", which is this catalog's Loki default). If Loki was
            // installed under a different name/namespace, edit loki.write below.
            DefaultValues = """
                image:
                  # Grafana Alloy application image tag to run. Pinned here; also
                  # editable via the "Alloy Version" form field.
                  tag: v1.17.0

                controller:
                  type: daemonset

                alloy:
                  mounts:
                    # Mount host /var/log so Alloy can read /var/log/pods/*.
                    varlog: true
                  resources:
                    requests:
                      cpu: 50m
                      memory: 128Mi
                    limits:
                      cpu: 200m
                      memory: 256Mi
                  configMap:
                    content: |-
                      discovery.kubernetes "pods" {
                        role = "pod"
                      }

                      discovery.relabel "pod_logs" {
                        targets = discovery.kubernetes.pods.targets

                        rule {
                          source_labels = ["__meta_kubernetes_namespace"]
                          target_label  = "namespace"
                        }
                        rule {
                          source_labels = ["__meta_kubernetes_pod_name"]
                          target_label  = "pod"
                        }
                        rule {
                          source_labels = ["__meta_kubernetes_pod_container_name"]
                          target_label  = "container"
                        }
                        rule {
                          source_labels = ["__meta_kubernetes_pod_label_app_kubernetes_io_name"]
                          target_label  = "app"
                        }
                        rule {
                          source_labels = ["__meta_kubernetes_node_name"]
                          target_label  = "node"
                        }
                        // Build the on-disk log path: /var/log/pods/<ns>_<pod>_<uid>/<container>/*.log
                        rule {
                          source_labels = ["__meta_kubernetes_pod_uid", "__meta_kubernetes_pod_container_name"]
                          separator     = "/"
                          action        = "replace"
                          replacement   = "/var/log/pods/*$1/*.log"
                          target_label  = "__path__"
                        }
                      }

                      local.file_match "pod_logs" {
                        path_targets = discovery.relabel.pod_logs.output
                      }

                      loki.source.file "pod_logs" {
                        targets    = local.file_match.pod_logs.targets
                        forward_to = [loki.process.default.receiver]
                      }

                      loki.process "default" {
                        // Parse the containerd/CRI-O log envelope (timestamp, stream, flags).
                        stage.cri {}
                        forward_to = [loki.write.default.receiver]
                      }

                      loki.write "default" {
                        endpoint {
                          url = "http://loki-gateway.monitoring.svc.cluster.local/loki/api/v1/push"
                        }
                      }
                """
        },

        new CatalogEntry
        {
            Key = "otel-collector",
            DisplayName = "EntKube Telemetry Collector",
            Description = "Node-level telemetry collector built on the OpenTelemetry Collector (Apache 2.0). Runs as a DaemonSet, tails container logs on every node, and ships them to the EntKube native telemetry store over OTLP/JSON (Bearer-authenticated with a per-cluster ingest token) — no Loki required. Also exposes OTLP receivers (4317/4318) so instrumented apps can send traces and metrics to the platform. For a Loki-backed setup instead, use Grafana Alloy.",
            Icon = "bi-arrow-down-up",
            Category = "Monitoring",
            HelmRepoUrl = "https://open-telemetry.github.io/opentelemetry-helm-charts",
            HelmChartName = "opentelemetry-collector",
            // Version intentionally unpinned (latest tested). The value schema
            // (mode, presets.logsCollection, config.*) is stable across recent releases.
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "otel-collector",
            // DaemonSet rollout (one pod per node) can be slow on busy clusters — give --wait headroom.
            InstallTimeout = "30m0s",
            // No component dependency: it ships to the EntKube management plane, not an in-cluster
            // backend. Requires only the Ingest URL + per-cluster token (form fields below).
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "image-tag", Label = "Collector Version",
                    YamlPath = "image.tag", Type = FormFieldType.Text,
                    DefaultValue = "", Placeholder = "e.g. 0.119.0 (blank = chart default)",
                    HelpText = "opentelemetry-collector-contrib image tag. Leave blank to use the chart's tested default; set to pin/upgrade."
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "50m", Placeholder = "e.g. 50m, 100m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                },
                new ComponentFormField
                {
                    Key = "cpu-limit", Label = "CPU Limit",
                    YamlPath = "resources.limits.cpu", Type = FormFieldType.Text,
                    DefaultValue = "200m", Placeholder = "e.g. 200m, 500m",
                    HelpText = "Required on clusters that enforce a LimitRange — pods without limits are rejected."
                },
                new ComponentFormField
                {
                    Key = "memory-limit", Label = "Memory Limit",
                    YamlPath = "resources.limits.memory", Type = FormFieldType.Text,
                    DefaultValue = "512Mi", Placeholder = "e.g. 512Mi, 1Gi",
                    HelpText = "The memory_limiter processor sheds load at ~80% of this ceiling; keep it well above the request and raise to 1Gi+ on high-throughput nodes."
                },
                new ComponentFormField
                {
                    Key = "ingest-endpoint", Label = "EntKube Ingest URL",
                    YamlPath = "config.exporters.otlphttp/entkube.endpoint", Type = FormFieldType.Text,
                    DefaultValue = "", Placeholder = "https://entkube.example.com/ingest/otlp",
                    HelpText = "EntKube's telemetry ingest base URL reachable from this cluster (ending in /ingest/otlp). The collector appends /v1/logs and /v1/traces."
                },
                new ComponentFormField
                {
                    Key = "ingest-token", Label = "Ingest Token",
                    YamlPath = "config.extensions.bearertokenauth.token", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "otel-ingest-token",
                    HelpText = "Per-cluster ingest token from the cluster's Telemetry Ingest panel. Sent as a Bearer token; stored encrypted in the vault and injected at install."
                }
            ],
            // DaemonSet. The contrib image is required for the filelog receiver + k8sattributes
            // processor; when overriding image.repository to contrib you MUST also set command.name
            // to otelcol-contrib or the pod won't start.
            //
            // presets.logsCollection wires the filelog receiver (with the `container` operator that
            // parses the CRI envelope and extracts k8s.namespace.name/k8s.pod.name/k8s.container.name
            // from the log path) AND the hostPath mounts for /var/log/pods.
            // presets.kubernetesAttributes creates the ClusterRole/RBAC + k8sattributes processor.
            //
            // The resource/short-labels processor aliases the long k8s.* resource attributes to
            // short names; the EntKube ingest reads k8s.namespace.name/pod/container primarily and
            // falls back to these. The bearertokenauth extension adds "Authorization: Bearer <token>"
            // to every export request; the token is injected from the vault at install time
            // (config.extensions.bearertokenauth.token, from the "Ingest Token" field).
            DefaultValues = """
                mode: daemonset

                # Pin a stable, predictable resource/Service name. Without this the chart
                # derives "<release>-opentelemetry-collector", so the in-cluster OTLP
                # Service would be otel-collector-opentelemetry-collector — awkward to
                # reference. Fixing it to "otel-collector" gives a clean OTLP endpoint
                # (otel-collector.<ns>.svc:4317/:4318) for the eBPF tracer and for any
                # app you instrument directly.
                fullnameOverride: otel-collector

                # CRITICAL: in daemonset mode the chart does NOT create a Service by
                # default (serviceEnabled helper: daemonset + unset => false), so nothing
                # would expose the OTLP receiver as a stable ClusterIP. Force it on so the
                # otlp (4317) / otlp-http (4318) ports are reachable at otel-collector.<ns>
                # for the eBPF tracer and instrumented apps. (Each pod also keeps its
                # hostPort 4317/4318 for node-local delivery.)
                service:
                  enabled: true

                image:
                  repository: otel/opentelemetry-collector-contrib
                command:
                  name: otelcol-contrib

                presets:
                  logsCollection:
                    enabled: true
                    includeCollectorLogs: false
                    # Container-runtime partial-line splits (a multi-line app log chopped by containerd)
                    # are already recombined by the preset's `container` operator. For APP-level
                    # multi-line (e.g. stack traces the app writes as separate lines), add a `recombine`
                    # operator under config.receivers.filelog.operators keyed on your log-start pattern —
                    # it's log-format-specific, so it's intentionally not a default.
                  kubernetesAttributes:
                    enabled: true

                resources:
                  requests:
                    cpu: 50m
                    memory: 256Mi
                  limits:
                    cpu: 200m
                    # Log-tailing DaemonSet: memory scales with log throughput and OTLP
                    # ingest volume. Paired with the memory_limiter processor below, which
                    # sheds load at ~80% of this ceiling instead of letting the batch queue
                    # grow until the kubelet OOMKills the pod. Raise both together on busy
                    # nodes (1Gi+).
                    memory: 512Mi

                config:
                  extensions:
                    # Liveness/readiness endpoint. The chart ships this by default and wires its
                    # probes to port 13133, but because we override service.extensions below (and Helm
                    # REPLACES lists rather than appending), we must re-declare it here and include it
                    # in service.extensions — otherwise the chart's NOTES.txt guard aborts the install
                    # with "requires the health_check extension ... to be included in the extension list".
                    health_check:
                      endpoint: ${env:MY_POD_IP}:13133
                    # Bearer auth for the exporter. Token is replaced at install by the vault-stored
                    # per-cluster ingest token (see the "Ingest Token" form field).
                    bearertokenauth:
                      scheme: "Bearer"
                      token: "REPLACE_WITH_INGEST_TOKEN"

                  receivers:
                    # Accept traces/metrics/logs from instrumented apps now so Phase 3 needs no redeploy.
                    otlp:
                      protocols:
                        grpc:
                          endpoint: 0.0.0.0:4317
                        http:
                          endpoint: 0.0.0.0:4318

                  processors:
                    # Backpressure guard — MUST be first in every pipeline. Checks heap against
                    # the container's cgroup memory limit and, above spike/limit thresholds,
                    # starts refusing new data (receivers return retryable errors) so the
                    # collector sheds load gracefully instead of being OOMKilled by the kubelet.
                    # Percentages track the resources.limits.memory ceiling above, so raising
                    # that limit scales these automatically.
                    memory_limiter:
                      check_interval: 1s
                      limit_percentage: 80
                      spike_limit_percentage: 25
                    # Enrich with node name + the app.kubernetes.io/name pod label.
                    k8sattributes:
                      extract:
                        metadata:
                          - k8s.namespace.name
                          - k8s.pod.name
                          - k8s.container.name
                          - k8s.node.name
                        labels:
                          - tag_name: app.kubernetes.io/name
                            key: app.kubernetes.io/name
                            from: pod
                    # Alias long OTel resource attributes -> short names (ingest fallback).
                    resource/short-labels:
                      attributes:
                        - { action: insert, key: namespace, from_attribute: k8s.namespace.name }
                        - { action: insert, key: pod,       from_attribute: k8s.pod.name }
                        - { action: insert, key: container, from_attribute: k8s.container.name }
                        - { action: insert, key: node,      from_attribute: k8s.node.name }
                        - { action: insert, key: app,       from_attribute: app.kubernetes.io/name }

                  exporters:
                    # EntKube native telemetry ingest. JSON encoding (no protobuf dep on the ingest
                    # side); gzip is applied by default. The base endpoint is set via the "EntKube
                    # Ingest URL" field; the otlphttp exporter appends /v1/logs and /v1/traces. Auth via
                    # the bearertokenauth extension above.
                    otlphttp/entkube:
                      endpoint: https://REPLACE_WITH_ENTKUBE_URL/ingest/otlp
                      encoding: json
                      auth:
                        authenticator: bearertokenauth

                  service:
                    extensions: [health_check, bearertokenauth]
                    pipelines:
                      logs:
                        receivers: [filelog, otlp]
                        processors: [memory_limiter, k8sattributes, resource/short-labels, batch]
                        exporters: [otlphttp/entkube]
                      # Traces from instrumented apps (OTLP receiver) → EntKube for APM/trace view.
                      traces:
                        receivers: [otlp]
                        processors: [memory_limiter, k8sattributes, batch]
                        exporters: [otlphttp/entkube]
                      # NB: no metrics pipeline. EntKube has no native metrics ingest — app/host
                      # metrics go straight to Prometheus (there is no /ingest/otlp/v1/metrics
                      # endpoint). A metrics pipeline here just POSTs to a dead URL and the whole
                      # batch is rejected (HTTP 400), spamming "Exporting failed" and dropping data.
                      # OTLP metrics the receiver accepts (e.g. from eBPF/apps) are simply not
                      # forwarded; scrape those via Prometheus instead.
                """
        },

        new CatalogEntry
        {
            Key = "otel-ebpf",
            DisplayName = "EntKube Trace Auto-Instrumentation (eBPF)",
            Description = "Zero-code distributed tracing. Runs the OpenTelemetry eBPF Instrumentation (OBI) — the vendor-neutral, Apache-2.0 upstream of Grafana Beyla, donated to OpenTelemetry — as a privileged DaemonSet that uses eBPF to synthesize HTTP/gRPC/SQL spans and RED metrics for EVERY workload on the node without touching application code or requiring a service mesh. Exports OTLP to the EntKube Telemetry Collector, which enriches and forwards to the native store, so the Traces and Metrics views populate automatically. Requires the EntKube Telemetry Collector. Linux kernel 5.8+ (5.17+ for network-level distributed-trace context propagation); note eBPF-only traces are best-effort across TLS/L7/multi-node hops — pair with SDK instrumentation on boundary services for guaranteed end-to-end context.",
            Icon = "bi-diagram-3-fill",
            Category = "Monitoring",
            HelmRepoUrl = "https://open-telemetry.github.io/opentelemetry-helm-charts",
            HelmChartName = "opentelemetry-ebpf-instrumentation",
            // Version intentionally unpinned (OBI is v0.x beta with a shifting config
            // surface — the Helm chart tracks it). Pin the image via the field below for
            // reproducibility. The chart provisions the DaemonSet securityContext
            // (CAP_BPF/PERFMON/SYS_PTRACE/NET_RAW), hostPID, ServiceAccount + RBAC, and
            // the K8s metadata cache — the fragile eBPF plumbing we would otherwise hand-roll.
            DefaultNamespace = "monitoring",
            DefaultReleaseName = "otel-ebpf",
            // DaemonSet rollout (one pod per node) — give --wait headroom.
            InstallTimeout = "15m0s",
            // Ships spans to the collector's in-cluster OTLP receiver, so the collector must
            // exist first. It carries the EntKube ingest token; OBI needs no token itself.
            Dependencies = ["otel-collector"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "image-tag", Label = "OBI Version",
                    YamlPath = "image.tag", Type = FormFieldType.Text,
                    DefaultValue = "", Placeholder = "e.g. v0.8.0 (blank = chart default)",
                    HelpText = "opentelemetry-ebpf-instrumentation image tag. Leave blank for the chart's tested default; pin an explicit tag for reproducibility (OBI is v0.x beta — avoid 'main')."
                },
                new ComponentFormField
                {
                    Key = "traces-endpoint", Label = "Collector OTLP Endpoint",
                    YamlPath = "config.data.otel_traces_export.endpoint", Type = FormFieldType.Text,
                    DefaultValue = "http://otel-collector.monitoring:4317",
                    Placeholder = "http://otel-collector.monitoring:4317",
                    HelpText = "OTLP gRPC endpoint of the EntKube Telemetry Collector (the collector's Service, pinned to name 'otel-collector'). Change only if the collector runs in another namespace. Metrics are sent to the same host:port."
                }
            ],
            // OBI config lives under the chart's config.data block. CRITICAL: the chart's
            // OWN default config.data sets otel_traces_export.endpoint=http://${HOST_IP}:4317
            // (per-node host IP). Because config.data is a map-merge, we MUST override those
            // exact export paths here — the EntKube collector is a ClusterIP Service, not a
            // hostPort, so the chart default would silently ship spans into the void. The
            // chart already defaults securityContext.privileged=true, RBAC, and ServiceAccount.
            DefaultValues = """
                # Enable the in-cluster K8s metadata cache (decorates spans/metrics with
                # namespace/pod/deployment). Chart default is 0 (disabled).
                k8sCache:
                  replicas: 1

                config:
                  data:
                    # Auto-discover and instrument EVERY workload in EVERY namespace. OBI
                    # excludes itself and apps already carrying an OTel SDK.
                    discovery:
                      instrument:
                        - k8s_namespace: "*"
                      exclude_otel_instrumented_services: true
                    # Decorate telemetry with Kubernetes resource attributes.
                    attributes:
                      kubernetes:
                        enable: true
                    # All protocol instrumentations (http, grpc, sql, redis, kafka, ...).
                    instrumentations:
                      - "*"
                    # eBPF context propagation so spans link into connected distributed
                    # traces where the kernel allows it (network-level path needs 5.17+).
                    ebpf:
                      context_propagation: all
                    # Override the chart's ${HOST_IP} defaults → the EntKube collector's
                    # OTLP gRPC receiver. The collector enriches (k8sattributes) and forwards
                    # to the EntKube native store. Both signals go to :4317 (gRPC).
                    otel_traces_export:
                      endpoint: http://otel-collector.monitoring:4317
                      protocol: grpc
                    otel_metrics_export:
                      endpoint: http://otel-collector.monitoring:4317
                      protocol: grpc
                """
        },

        // ── Storage ──

        new CatalogEntry
        {
            Key = "minio",
            DisplayName = "MinIO",
            Description = "High-performance S3-compatible object storage. Use for application data, backups, and artifact storage.",
            Icon = "bi-bucket",
            Category = "Storage",
            HelmRepoUrl = "https://charts.min.io/",
            HelmChartName = "minio",
            DefaultNamespace = "minio",
            DefaultReleaseName = "minio",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "root-user", Label = "Root User",
                    YamlPath = "rootUser", Type = FormFieldType.Text,
                    DefaultValue = "admin",
                    HelpText = "Admin username for the MinIO console"
                },
                new ComponentFormField
                {
                    Key = "root-password", Label = "Root Password",
                    YamlPath = "rootPassword", Type = FormFieldType.Password,
                    DefaultValue = "changeme123",
                    HelpText = "Admin password — change this before production use"
                },
                new ComponentFormField
                {
                    Key = "mode", Label = "Deployment Mode",
                    YamlPath = "mode", Type = FormFieldType.Select,
                    DefaultValue = "standalone",
                    Options = ["standalone", "distributed"],
                    HelpText = "Standalone for dev/small, distributed for HA"
                },
                new ComponentFormField
                {
                    Key = "storage-size", Label = "Storage Size",
                    YamlPath = "persistence.size", Type = FormFieldType.Text,
                    DefaultValue = "50Gi", Placeholder = "e.g. 50Gi, 100Gi, 500Gi",
                    HelpText = "Persistent volume size for object storage"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "250m", Placeholder = "e.g. 250m, 500m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "512Mi", Placeholder = "e.g. 512Mi, 1Gi"
                }
            ],
            DefaultValues = """
                # MinIO root credentials
                rootUser: admin
                rootPassword: changeme123
                
                # Storage configuration
                mode: standalone
                persistence:
                  enabled: true
                  size: 50Gi

                # Resource allocation
                resources:
                  requests:
                    memory: 512Mi
                    cpu: 250m

                # Container hardening. MinIO already runs as non-root uid 1000 with
                # fsGroup 1000 (pod-level securityContext, left untouched). readOnly
                # rootfs is kept off — MinIO writes to local paths.
                containerSecurityContext:
                  runAsNonRoot: true
                  allowPrivilegeEscalation: false
                  capabilities:
                    drop: ["ALL"]
                  seccompProfile:
                    type: RuntimeDefault
                  readOnlyRootFilesystem: false
                """
        },

        new CatalogEntry
        {
            Key = "minio-operator",
            DisplayName = "MinIO Operator",
            Description = "Kubernetes operator for managing MinIO Tenant instances. Installs the minio.min.io/v2 Tenant CRD so tenants can be provisioned as first-class Kubernetes resources.",
            Icon = "bi-bucket-fill",
            Category = "Storage",
            HelmRepoUrl = "https://operator.min.io/",
            HelmChartName = "operator",
            DefaultNamespace = "minio-operator",
            DefaultReleaseName = "minio-operator",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "operator.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "200m", Placeholder = "e.g. 200m, 500m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "operator.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                }
            ],
            DefaultValues = """
                operator:
                  resources:
                    requests:
                      memory: 256Mi
                      cpu: 200m
                """
        },

        // ── Databases ──

        new CatalogEntry
        {
            Key = "cloudnative-pg",
            DisplayName = "CloudNativePG",
            Description = "Kubernetes operator for managing PostgreSQL clusters. Handles provisioning, high availability, backups, and automated failover.",
            Icon = "bi-database",
            Category = "Databases",
            HelmRepoUrl = "https://cloudnative-pg.github.io/charts",
            HelmChartName = "cloudnative-pg",
            DefaultNamespace = "cnpg-system",
            DefaultReleaseName = "cnpg",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "barman-cloud-plugin", Label = "Install Barman Cloud Plugin",
                    YamlPath = "subchart:plugin-barman-cloud", Type = FormFieldType.Toggle,
                    DefaultValue = "true"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                }
            ],
            DefaultValues = """
                # Operator configuration
                resources:
                  requests:
                    memory: 256Mi
                    cpu: 100m
                """
        },

        new CatalogEntry
        {
            Key = "mongodb-community-operator",
            DisplayName = "MongoDB Community Operator",
            Description = "Official MongoDB Kubernetes operator for deploying and managing MongoDB replica sets. Handles provisioning, scaling, upgrades, and user management. Backups are managed independently via S3-backed Jobs.",
            Icon = "bi-database-gear",
            Category = "Databases",
            HelmRepoUrl = "https://mongodb.github.io/helm-charts",
            HelmChartName = "community-operator",
            DefaultNamespace = "mongodb-system",
            DefaultReleaseName = "mongodb-operator",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "watch-namespace", Label = "Watch Namespace",
                    YamlPath = "operator.watchNamespace", Type = FormFieldType.Text,
                    DefaultValue = "*", Placeholder = "* for all, or specific namespace",
                    HelpText = "Which namespaces the operator watches for MongoDBCommunity resources"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "operator.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "operator.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                }
            ],
            DefaultValues = """
                # MongoDB Community Operator configuration
                operator:
                  watchNamespace: "*"
                  resources:
                    requests:
                      memory: 256Mi
                      cpu: 100m
                  # Container hardening for the operator pod (chart's operator.securityContext
                  # default is empty). Pod-level already defaults to runAsNonRoot/uid 2000.
                  # The managed mongod pods are handled separately in MongoService.
                  securityContext:
                    runAsNonRoot: true
                    allowPrivilegeEscalation: false
                    capabilities:
                      drop: ["ALL"]
                    seccompProfile:
                      type: RuntimeDefault
                """
        },

        // ── Identity ──

        new CatalogEntry
        {
            Key = "keycloak",
            DisplayName = "Keycloak",
            Description = "Open-source identity and access management. Provides SSO, user federation, social login, and fine-grained authorization. Can be exposed externally via Gateway API.",
            Icon = "bi-shield-lock",
            Category = "Identity",
            HelmRepoUrl = "https://codecentric.github.io/helm-charts",
            HelmChartName = "keycloakx",
            DefaultNamespace = "keycloak",
            DefaultReleaseName = "keycloak",
            // Slow startup: DB schema init/migration before the server reports Ready.
            InstallTimeout = "30m0s",
            RequiresOneOf =
            [
                new DependencyRequirement
                {
                    Label = "Ingress Controller",
                    Options = ["traefik", "istio"]
                }
            ],
            Dependencies = ["cert-manager", "letsencrypt-issuer", "cloudnative-pg"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "cnpg-database", Label = "Database",
                    YamlPath = "cnpg:database-id", Type = FormFieldType.CnpgDatabase,
                    HelpText = "Managed PostgreSQL database for Keycloak (credentials synced to K8s Secret)"
                },
                new ComponentFormField
                {
                    Key = "admin-url", Label = "Admin URL",
                    YamlPath = "cnpg:admin-url", Type = FormFieldType.Text,
                    Placeholder = "https://keycloak.example.com/auth",
                    HelpText = "Public URL of the Keycloak admin console"
                },
                new ComponentFormField
                {
                    Key = "admin-username", Label = "Admin Username",
                    YamlPath = "cnpg:admin-username", Type = FormFieldType.Text,
                    DefaultValue = "admin"
                },
                new ComponentFormField
                {
                    Key = "admin-password", Label = "Admin Password",
                    YamlPath = "cnpg:admin-password", Type = FormFieldType.Password,
                    Placeholder = "Strong password for the Keycloak admin account",
                    HelpText = "Stored in vault and synced to Kubernetes Secret"
                },
                new ComponentFormField
                {
                    Key = "hostname", Label = "Public Hostname",
                    YamlPath = "cnpg:hostname", Type = FormFieldType.Text,
                    Placeholder = "keycloak.example.com",
                    HelpText = "Creates an HTTPRoute and Certificate to publish Keycloak"
                },
                new ComponentFormField
                {
                    Key = "tls-mode", Label = "TLS Mode",
                    YamlPath = "cnpg:tls-mode", Type = FormFieldType.Select,
                    DefaultValue = "ClusterIssuer",
                    Options = ["ClusterIssuer", "Manual"]
                },
                new ComponentFormField
                {
                    Key = "cluster-issuer", Label = "Cluster Issuer",
                    YamlPath = "cnpg:cluster-issuer", Type = FormFieldType.ClusterIssuer,
                    DefaultValue = "letsencrypt-prod",
                    HelpText = "cert-manager ClusterIssuer to request the TLS certificate",
                    DependsOnKey = "tls-mode", DependsOnValue = "ClusterIssuer"
                },
                new ComponentFormField
                {
                    Key = "tls-cert", Label = "TLS Certificate (PEM)",
                    YamlPath = "cnpg:tls-cert", Type = FormFieldType.Password,
                    Placeholder = "-----BEGIN CERTIFICATE-----",
                    HelpText = "Full certificate chain in PEM format",
                    DependsOnKey = "tls-mode", DependsOnValue = "Manual"
                },
                new ComponentFormField
                {
                    Key = "tls-key", Label = "TLS Private Key (PEM)",
                    YamlPath = "cnpg:tls-key", Type = FormFieldType.Password,
                    Placeholder = "-----BEGIN PRIVATE KEY-----",
                    HelpText = "Private key in PEM format",
                    DependsOnKey = "tls-mode", DependsOnValue = "Manual"
                },
                new ComponentFormField
                {
                    Key = "http-path", Label = "HTTP Relative Path",
                    YamlPath = "http.relativePath", Type = FormFieldType.Text,
                    DefaultValue = "/auth",
                    HelpText = "Base path where Keycloak is served"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "250m", Placeholder = "e.g. 250m, 500m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "512Mi", Placeholder = "e.g. 512Mi, 1Gi"
                }
            ],
            DefaultValues = """
                # Database vendor — credentials (KC_DB_URL/USERNAME/PASSWORD) are injected via the
                # K8s Secret below, not set here directly.
                database:
                  vendor: postgres

                # All env vars (KC_BOOTSTRAP_ADMIN_USERNAME, KC_BOOTSTRAP_ADMIN_PASSWORD,
                # KC_DB_URL, KC_DB_USERNAME, KC_DB_PASSWORD) are written to this secret by
                # EntKube and read here at startup.  The name must match {releaseName}-credentials.
                extraEnvFrom: |
                  - secretRef:
                      name: keycloak-credentials

                # Disable the chart's legacy proxy setting — it injects KC_PROXY which was
                # removed in Keycloak 26 and causes kc.sh to exit with the help text.
                proxy:
                  enabled: false

                # Keycloak 26+ proxy configuration replaces KC_PROXY=edge.
                # KC_HTTP_ENABLED is set by the chart via http.relativePath — omit here to avoid duplicates.
                extraEnv: |
                  - name: KC_PROXY_HEADERS
                    value: xforwarded
                  - name: KC_HOSTNAME_STRICT
                    value: "false"

                # Explicit start args — overrides the chart's default which may include
                # --hostname-strict=false (removed in Keycloak 26, causes help-text crash).
                args:
                  - start

                # HTTP configuration
                http:
                  relativePath: /auth

                # Resource allocation
                resources:
                  requests:
                    memory: 512Mi
                    cpu: 250m
                """
        },

        // ── Registry ──

        new CatalogEntry
        {
            Key = "harbor",
            DisplayName = "Harbor",
            Description = "Open-source container registry with role-based access control, image signing, vulnerability scanning, and S3-compatible artifact storage. Requires a CNPG database and optionally an S3 bucket for production use.",
            Icon = "bi-box-seam",
            Category = "Registry",
            HelmRepoUrl = "https://helm.goharbor.io",
            HelmChartName = "harbor",
            HelmChartVersion = "1.16.1",
            DefaultNamespace = "harbor",
            DefaultReleaseName = "harbor",
            // Very heavy: core, registry, jobservice, portal, trivy, database, redis.
            InstallTimeout = "30m0s",
            Dependencies = ["cert-manager", "letsencrypt-issuer", "cloudnative-pg"],
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "cnpg-database", Label = "Database",
                    YamlPath = "harbor:database-id", Type = FormFieldType.CnpgDatabase,
                    HelpText = "Managed PostgreSQL database (credentials injected as Helm values at install time)"
                },
                new ComponentFormField
                {
                    Key = "storage-link", Label = "S3 Storage",
                    YamlPath = "harbor:storage-link-id", Type = FormFieldType.StorageLink,
                    HelpText = "S3-compatible bucket for registry artifact storage (optional — uses PVC when not set)"
                },
                new ComponentFormField
                {
                    Key = "admin-password", Label = "Admin Password",
                    YamlPath = "harborAdminPassword", Type = FormFieldType.Password,
                    Placeholder = "Strong password for the Harbor admin account",
                    HelpText = "Stored in vault and injected at install time",
                    StoreAsSecret = true,
                    SecretName = "HARBOR_ADMIN_PASSWORD"
                },
                new ComponentFormField
                {
                    Key = "hostname", Label = "Public Hostname",
                    YamlPath = "harbor:hostname", Type = FormFieldType.Text,
                    Placeholder = "registry.example.com",
                    HelpText = "Creates an HTTPRoute and Certificate for external access"
                },
                new ComponentFormField
                {
                    Key = "tls-mode", Label = "TLS Mode",
                    YamlPath = "harbor:tls-mode", Type = FormFieldType.Select,
                    DefaultValue = "ClusterIssuer",
                    Options = ["ClusterIssuer", "Manual"]
                },
                new ComponentFormField
                {
                    Key = "cluster-issuer", Label = "Cluster Issuer",
                    YamlPath = "harbor:cluster-issuer", Type = FormFieldType.ClusterIssuer,
                    DefaultValue = "letsencrypt-prod",
                    HelpText = "cert-manager ClusterIssuer to issue the TLS certificate",
                    DependsOnKey = "tls-mode", DependsOnValue = "ClusterIssuer"
                },
                new ComponentFormField
                {
                    Key = "tls-cert", Label = "TLS Certificate (PEM)",
                    YamlPath = "harbor:tls-cert", Type = FormFieldType.Password,
                    Placeholder = "-----BEGIN CERTIFICATE-----",
                    HelpText = "Full certificate chain in PEM format",
                    DependsOnKey = "tls-mode", DependsOnValue = "Manual"
                },
                new ComponentFormField
                {
                    Key = "tls-key", Label = "TLS Private Key (PEM)",
                    YamlPath = "harbor:tls-key", Type = FormFieldType.Password,
                    Placeholder = "-----BEGIN PRIVATE KEY-----",
                    HelpText = "Private key in PEM format",
                    DependsOnKey = "tls-mode", DependsOnValue = "Manual"
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "core.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "core.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                },
                // Hidden secret-backed fields — injected programmatically by HarborService,
                // not entered by the user. InjectSecretsIntoValuesAsync picks these up at install time.
                new ComponentFormField
                {
                    Key = "harbor-db-password", Label = "DB Password",
                    YamlPath = "database.external.password", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "harbor-db-password", Hidden = true
                },
                new ComponentFormField
                {
                    Key = "harbor-s3-access-key", Label = "S3 Access Key",
                    YamlPath = "persistence.imageChartStorage.s3.accesskey", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "harbor-s3-access-key", Hidden = true
                },
                new ComponentFormField
                {
                    Key = "harbor-s3-secret-key", Label = "S3 Secret Key",
                    YamlPath = "persistence.imageChartStorage.s3.secretkey", Type = FormFieldType.Password,
                    StoreAsSecret = true, SecretName = "harbor-s3-secret-key", Hidden = true
                }
            ],
            DefaultValues = """
                expose:
                  type: clusterIP
                  tls:
                    enabled: false

                # externalURL and expose.clusterIP.name are written by EntKube when a hostname
                # is configured, so they always match the release name used for the route backend.
                externalURL: https://registry.example.com

                # Use internal Postgres by default; overridden to 'external' by HarborService
                # when a CNPG database is configured.
                database:
                  type: internal

                persistence:
                  enabled: true
                  resourcePolicy: "keep"
                  # Storage type defaults to filesystem; overridden to 's3' by HarborService
                  # when a StorageLink is configured.
                  imageChartStorage:
                    type: filesystem
                    disableredirect: false

                core:
                  resources:
                    requests:
                      cpu: 100m
                      memory: 256Mi
                """
        },

        // ── Messaging ──

        new CatalogEntry
        {
            Key = "rabbitmq-cluster-operator",
            DisplayName = "RabbitMQ Cluster Operator",
            Description = "Kubernetes operator that manages RabbitMQ broker clusters. Provisions RabbitmqCluster CRDs as StatefulSets with automatic secret generation, persistent storage, and cluster health conditions. Installed via the official GitHub release manifest.",
            Icon = "bi-collection",
            Category = "Messaging",
            ComponentType = "ManifestUrl",
            HelmRepoUrl = "https://github.com/rabbitmq/cluster-operator/releases/latest/download/cluster-operator.yml",
            HelmChartName = "",
            DefaultNamespace = "rabbitmq-system",
            DefaultReleaseName = "rabbitmq-cluster-operator",
            DetectionCrds = ["rabbitmqclusters.rabbitmq.com"],
            FormFields = [],
            DefaultValues = ""
        },

        new CatalogEntry
        {
            Key = "argo-cd",
            DisplayName = "Argo CD",
            Description = "Declarative GitOps continuous delivery for Kubernetes. Continuously reconciles the live cluster state against manifests in Git, exposing each app as an Application custom resource with sync and health status. Optional for EntKube — when present, EntKube can read its Applications to enrich deployment discovery.",
            Icon = "bi-arrow-repeat",
            Category = "GitOps",
            HelmRepoUrl = "https://argoproj.github.io/argo-helm",
            HelmChartName = "argo-cd",
            DefaultNamespace = "argocd",
            DefaultReleaseName = "argocd",
            DetectionCrds = ["applications.argoproj.io", "appprojects.argoproj.io"],
            FormFields = [],
            DefaultValues = """
                # Argo CD Helm values — see https://github.com/argoproj/argo-helm for the full reference.
                # To publish the API server behind your ingress/gateway (with TLS terminated upstream),
                # set configs.params."server.insecure": true and add a Gateway/HTTPRoute or ingress.
                configs:
                  params:
                    server.insecure: false
                """
        },

        new CatalogEntry
        {
            Key = "rabbitmq-messaging-topology-operator",
            DisplayName = "RabbitMQ Messaging Topology Operator",
            Description = "Manages RabbitMQ topology objects (vhosts, queues, exchanges, bindings, policies, users, permissions) as Kubernetes CRDs. Requires cert-manager for webhook TLS certificate management.",
            Icon = "bi-diagram-3",
            Category = "Messaging",
            ComponentType = "ManifestUrl",
            HelmRepoUrl = "https://github.com/rabbitmq/messaging-topology-operator/releases/latest/download/messaging-topology-operator-with-certmanager.yaml",
            HelmChartName = "",
            DefaultNamespace = "rabbitmq-system",
            DefaultReleaseName = "rabbitmq-topology-operator",
            // The topology operator registers the messaging-topology CRDs. Any one of
            // these being present means the operator is installed.
            DetectionCrds =
            [
                "queues.rabbitmq.com",
                "exchanges.rabbitmq.com",
                "bindings.rabbitmq.com",
                "users.rabbitmq.com",
                "vhosts.rabbitmq.com",
                "permissions.rabbitmq.com"
            ],
            Dependencies = ["rabbitmq-cluster-operator", "cert-manager"],
            FormFields = [],
            DefaultValues = ""
        },

        new CatalogEntry
        {
            Key = "strimzi-kafka-operator",
            DisplayName = "Strimzi Kafka Operator",
            Description = "Kubernetes operator that runs Apache Kafka (KRaft mode, no ZooKeeper). Manages Kafka and KafkaNodePool CRDs. Install this first, then add the 'Apache Kafka Cluster' component. Provides the self-hosted streaming backend for Grafana Mimir's ingest-storage architecture.",
            Icon = "bi-hdd-stack",
            Category = "Messaging",
            ComponentType = "HelmChart",
            HelmRepoUrl = "https://strimzi.io/charts/",
            HelmChartName = "strimzi-kafka-operator",
            HelmChartVersion = "1.1.0",
            DefaultNamespace = "kafka",
            DefaultReleaseName = "strimzi-kafka-operator",
            FormFields = [],
            DefaultValues = """
                # Watch all namespaces so a single shared operator reconciles Kafka
                # clusters wherever they live (the Kafka Cluster component installs into
                # this same 'kafka' namespace by default).
                watchAnyNamespace: true

                # Make every pod the operator creates (Kafka brokers, entity operator)
                # compliant with the "restricted" Pod Security Standard out of the box.
                extraEnvs:
                  - name: STRIMZI_POD_SECURITY_PROVIDER_CLASS
                    value: restricted

                # The chart ships empty securityContext/podSecurityContext, so apply the
                # restricted baseline to the operator pod itself. The operator is a static
                # controller and runs fine as non-root.
                podSecurityContext:
                  runAsNonRoot: true
                  seccompProfile:
                    type: RuntimeDefault
                securityContext:
                  allowPrivilegeEscalation: false
                  runAsNonRoot: true
                  capabilities:
                    drop: ["ALL"]
                  seccompProfile:
                    type: RuntimeDefault
                """
        },

        // ── Cache ──

        new CatalogEntry
        {
            Key = "redis-operator",
            DisplayName = "Redis Operator (OT-Container-Kit)",
            Description = "Kubernetes operator for Redis clusters by OT-Container-Kit. Manages RedisCluster CRDs (redis.redis.opstreelabs.in/v1beta2) as sharded StatefulSets with persistence, Prometheus metrics via a sidecar exporter, and built-in auth via pre-provisioned Secrets.",
            Icon = "bi-lightning-charge",
            Category = "Cache",
            ComponentType = "HelmChart",
            HelmRepoUrl = "https://ot-container-kit.github.io/helm-charts/",
            HelmChartName = "redis-operator",
            DefaultNamespace = "redis-operator",
            DefaultReleaseName = "redis-operator",
            FormFields = [],
            // This chart ships empty securityContext/podSecurityContext by default,
            // so apply the full baseline. The operator is a static controller and runs
            // fine as non-root; read-only rootfs is left off per house policy.
            DefaultValues = """
                podSecurityContext:
                  runAsNonRoot: true
                  runAsUser: 1000
                  runAsGroup: 1000
                  seccompProfile:
                    type: RuntimeDefault
                securityContext:
                  runAsNonRoot: true
                  allowPrivilegeEscalation: false
                  capabilities:
                    drop: ["ALL"]
                  seccompProfile:
                    type: RuntimeDefault
                """
        },

        // ── Security ──

        new CatalogEntry
        {
            Key = "kyverno",
            DisplayName = "Kyverno",
            Description = "Kubernetes-native policy engine that enforces admission policies as webhook rules — no new language to learn. Required for Policy Enforcement in the Governance tab. Install on every cluster where you want governance policies applied.",
            Icon = "bi-shield-lock",
            Category = "Security",
            HelmRepoUrl = "https://kyverno.github.io/kyverno/",
            HelmChartName = "kyverno",
            HelmChartVersion = "3.8.1",
            DefaultNamespace = "kyverno",
            DefaultReleaseName = "kyverno",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "replicas", Label = "Replicas",
                    YamlPath = "admissionController.replicas", Type = FormFieldType.Number,
                    DefaultValue = "1",
                    HelpText = "Number of admission controller replicas. 1 replica shows an expected HA warning — use 3 for production."
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "admissionController.container.resources.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "admissionController.container.resources.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "256Mi", Placeholder = "e.g. 256Mi, 512Mi"
                },
                new ComponentFormField
                {
                    Key = "reports-memory-limit", Label = "Reports Controller Memory Limit",
                    YamlPath = "reportsController.resources.limits.memory", Type = FormFieldType.Text,
                    DefaultValue = "1Gi", Placeholder = "e.g. 1Gi, 2Gi, 4Gi",
                    HelpText = "Memory ceiling for the reports controller. It caches metadata for every scanned resource, so raise this (2Gi+) on clusters with many namespaces/workloads to avoid OOMKilled crash loops."
                }
            ],
            DefaultValues = """
                # Admission controller — validates and mutates incoming resources
                # Use replicas: 3 for high-availability production clusters
                admissionController:
                  replicas: 1
                  container:
                    resources:
                      requests:
                        cpu: 100m
                        memory: 256Mi

                # Background controller — audits existing resources against policies
                backgroundController:
                  resources:
                    requests:
                      cpu: 50m
                      memory: 128Mi

                # Cleanup controller — removes generated resources when a policy is deleted
                cleanupController:
                  resources:
                    requests:
                      cpu: 50m
                      memory: 128Mi

                # Reports controller — generates PolicyReport and ClusterPolicyReport objects.
                # Builds PartialObjectMetadata informer caches for every scanned resource kind,
                # so memory scales with cluster object count. A limit is required to keep the
                # kubelet from OOMKilling it right after cache sync on larger clusters; bump the
                # limit further (2Gi+) for clusters with many namespaces/workloads.
                reportsController:
                  resources:
                    requests:
                      cpu: 50m
                      memory: 256Mi
                    limits:
                      memory: 1Gi

                # Enable PolicyException resources so workloads can opt out of specific policies
                features:
                  policyExceptions:
                    enabled: true
                """
        },

        // ── Autoscaling ──

        new CatalogEntry
        {
            Key = "keda",
            DisplayName = "KEDA",
            Description = "Kubernetes Event-Driven Autoscaling. Scales workloads based on event sources (queues, streams, metrics, cron) in addition to CPU/memory — including scale-to-zero. Installs the keda.sh CRDs (ScaledObject, ScaledJob, TriggerAuthentication) and the operator, metrics adapter, and admission webhooks.",
            Icon = "bi-arrows-angle-expand",
            Category = "Autoscaling",
            HelmRepoUrl = "https://kedacore.github.io/charts",
            HelmChartName = "keda",
            HelmChartVersion = "2.20.0",
            DefaultNamespace = "keda",
            DefaultReleaseName = "keda",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "install-crds", Label = "Install CRDs",
                    YamlPath = "crds.install", Type = FormFieldType.Toggle,
                    DefaultValue = "true",
                    HelpText = "Install the KEDA Custom Resource Definitions (ScaledObject, ScaledJob, TriggerAuthentication) with the chart"
                },
                new ComponentFormField
                {
                    Key = "operator-replicas", Label = "Operator Replicas",
                    YamlPath = "operator.replicaCount", Type = FormFieldType.Number,
                    DefaultValue = "1",
                    HelpText = "Number of KEDA operator replicas. Use 2 for high-availability production clusters (leader election handles failover)."
                },
                new ComponentFormField
                {
                    Key = "cpu-request", Label = "CPU Request",
                    YamlPath = "resources.operator.requests.cpu", Type = FormFieldType.Text,
                    DefaultValue = "100m", Placeholder = "e.g. 100m, 250m"
                },
                new ComponentFormField
                {
                    Key = "memory-request", Label = "Memory Request",
                    YamlPath = "resources.operator.requests.memory", Type = FormFieldType.Text,
                    DefaultValue = "128Mi", Placeholder = "e.g. 128Mi, 256Mi"
                }
            ],
            DefaultValues = """
                # Install the KEDA CRDs with the chart
                crds:
                  install: true

                # KEDA operator — watches ScaledObject/ScaledJob resources
                operator:
                  replicaCount: 1

                # Resource allocation for the core KEDA pods
                resources:
                  operator:
                    requests:
                      cpu: 100m
                      memory: 128Mi
                  metricServer:
                    requests:
                      cpu: 100m
                      memory: 128Mi
                """
        },

        // ── Networking ──

        new CatalogEntry
        {
            Key = "strongswan-gateway",
            DisplayName = "StrongSwan IPsec Gateway",
            Description = "IKEv2/IPsec gateway pod for site-to-site VPN tunnels. Exposes UDP 500/4500 via a LoadBalancer Service. swanctl.conf and the PSK secret are synced from EntKube — use the VPN tunnel's 'Sync Config' action after install.",
            Icon = "bi-shield-lock",
            Category = "Networking",
            ComponentType = "Manifest",
            // No Helm repo — this is a raw manifest applied via kubectl apply.
            HelmRepoUrl = "",
            HelmChartName = "",
            DefaultNamespace = "vpn-system",
            DefaultReleaseName = "strongswan",
            // The DefaultValues field holds the initial Kubernetes manifest YAML.
            // Operators can edit this YAML in the component's advanced editor before install.
            // After install, use VPN > Sync Config to push the swanctl.conf + PSK.
            DefaultValues = """
                # StrongSwan IKEv2/IPsec gateway
                # Deployed as a Deployment (not DaemonSet) so it gets exactly one pod
                # and the LoadBalancer Service routes to it.
                # The swanctl.conf and PSK are managed by EntKube via the VPN tunnel's
                # "Sync Config" action, which writes a ConfigMap + Secret into this namespace.
                ---
                apiVersion: v1
                kind: Namespace
                metadata:
                  name: vpn-system
                ---
                apiVersion: v1
                kind: ServiceAccount
                metadata:
                  name: strongswan
                  namespace: vpn-system
                ---
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: strongswan
                  namespace: vpn-system
                  labels:
                    app: strongswan
                spec:
                  replicas: 1
                  selector:
                    matchLabels:
                      app: strongswan
                  template:
                    metadata:
                      labels:
                        app: strongswan
                    spec:
                      serviceAccountName: strongswan
                      containers:
                        - name: strongswan
                          # linux/amd64 only — change tag to match your cluster arch if needed.
                          # Available tags: 6.0.6, 6.0.5, latest (see hub.docker.com/r/strongx509/strongswan)
                          image: strongx509/strongswan:6.0.6
                          imagePullPolicy: IfNotPresent
                          securityContext:
                            capabilities:
                              add:
                                - NET_ADMIN
                                - NET_RAW
                                - SYS_MODULE
                          ports:
                            - name: ike
                              containerPort: 500
                              protocol: UDP
                            - name: nat-t
                              containerPort: 4500
                              protocol: UDP
                          resources:
                            requests:
                              cpu: 100m
                              memory: 64Mi
                            limits:
                              memory: 128Mi
                          volumeMounts:
                            # Both connections and secrets must be in conf.d/ so swanctl --load-all
                            # picks them up. Use subPath mounts to avoid clobbering each other.
                            - name: config
                              mountPath: /etc/swanctl/conf.d/connections.conf
                              subPath: connections.conf
                              readOnly: true
                            - name: secrets
                              mountPath: /etc/swanctl/conf.d/secrets.conf
                              subPath: secrets.conf
                              readOnly: true
                      volumes:
                        - name: config
                          configMap:
                            name: strongswan-config
                            optional: true
                        - name: secrets
                          secret:
                            secretName: strongswan-secrets
                            optional: true
                ---
                apiVersion: v1
                kind: Service
                metadata:
                  name: strongswan
                  namespace: vpn-system
                  annotations:
                    # Remove this annotation if your cloud provider does not support it
                    # service.beta.kubernetes.io/aws-load-balancer-type: nlb
                spec:
                  type: LoadBalancer
                  selector:
                    app: strongswan
                  ports:
                    - name: ike
                      port: 500
                      targetPort: 500
                      protocol: UDP
                    - name: nat-t
                      port: 4500
                      targetPort: 4500
                      protocol: UDP
                """,
            FormFields = []
        },

        new CatalogEntry
        {
            Key = "wg-easy",
            DisplayName = "WireGuard Easy (wg-easy)",
            Description = "WireGuard VPN server with web UI. Reuses the existing Istio external gateway's LoadBalancer (no new LB Service): the gateway exposes UDP 51820 and an EnvoyFilter proxies it to the wg-easy pod, so WireGuard rides the gateway's existing external IP. The web UI is exposed as an HTTPRoute on the same gateway. Requires the Istio External Gateway to expose the WireGuard UDP port (its Helm values include it). All resources are lifecycle-managed.",
            Icon = "bi-shield-check",
            Category = "Networking",
            ComponentType = "Manifest",
            HelmRepoUrl = "",
            HelmChartName = "",
            DefaultNamespace = "wg-easy",
            DefaultReleaseName = "wg-easy",
            DefaultValues = """
                ---
                apiVersion: v1
                kind: Namespace
                metadata:
                  name: wg-easy
                ---
                apiVersion: v1
                kind: PersistentVolumeClaim
                metadata:
                  name: wg-easy-config
                  namespace: wg-easy
                spec:
                  accessModes:
                    - ReadWriteOnce
                  resources:
                    requests:
                      storage: 1Gi
                ---
                # Internal ClusterIP — upstream for the gateway's Envoy UDP proxy (51820)
                # and backend for the HTTPRoute web UI (51821).
                apiVersion: v1
                kind: Service
                metadata:
                  name: wg-easy-svc
                  namespace: wg-easy
                spec:
                  type: ClusterIP
                  selector:
                    app: wg-easy
                  ports:
                    - name: wireguard
                      port: 51820
                      targetPort: 51820
                      protocol: UDP
                    - name: web-ui
                      port: 51821
                      targetPort: 51821
                      protocol: TCP
                ---
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: wg-easy
                  namespace: wg-easy
                  labels:
                    app: wg-easy
                spec:
                  replicas: 1
                  selector:
                    matchLabels:
                      app: wg-easy
                  strategy:
                    type: Recreate
                  template:
                    metadata:
                      labels:
                        app: wg-easy
                    spec:
                      containers:
                        - name: wg-easy
                          image: ghcr.io/wg-easy/wg-easy:14
                          imagePullPolicy: IfNotPresent
                          securityContext:
                            privileged: true
                          ports:
                            - name: wireguard
                              containerPort: 51820
                              protocol: UDP
                            - name: web-ui
                              containerPort: 51821
                              protocol: TCP
                          # WG_HOST and PASSWORD_HASH come from the wg-easy-env K8s Secret
                          # (created by EntKube vault sync — set the FormFields before installing).
                          envFrom:
                            - secretRef:
                                name: wg-easy-env
                          env:
                            - name: LANG
                              value: en
                            - name: WG_PRE_UP
                              value: "sysctl -w net.ipv4.ip_forward=1"
                            - name: WG_PORT
                              value: "51820"
                            - name: WG_DEFAULT_ADDRESS
                              value: "10.8.0.x"
                            - name: WG_DEFAULT_DNS
                              value: "%%WG_DNS%%"
                            - name: WG_ALLOWED_IPS
                              value: "%%WG_ALLOWED_IPS%%"
                            - name: WG_MTU
                              value: "1480"
                          volumeMounts:
                            - name: config
                              mountPath: /etc/wireguard
                            - name: modules
                              mountPath: /lib/modules
                              readOnly: true
                          resources:
                            requests:
                              cpu: 100m
                              memory: 64Mi
                            limits:
                              memory: 256Mi
                      volumes:
                        - name: config
                          persistentVolumeClaim:
                            claimName: wg-easy-config
                        - name: modules
                          hostPath:
                            path: /lib/modules
                      restartPolicy: Always
                      terminationGracePeriodSeconds: 30
                ---
                # EnvoyFilter: makes the existing Istio gateway forward WireGuard UDP 51820
                # to the wg-easy pod, reusing the gateway's LoadBalancer IP (no new LB
                # Service). The gateway's LoadBalancer must already expose UDP 51820 — the
                # Istio External Gateway component adds that port to its Helm values.
                #
                # %%WG_GATEWAY%% is substituted at apply-time with the gateway's release
                # name (e.g. istio-ingress-external), which is also the gateway pods' 'app'
                # label, so the workloadSelector targets exactly those pods.
                #
                # Two patches are required:
                #   1. CLUSTER — Istio does NOT auto-generate an outbound cluster for a UDP
                #      service port, so we define one explicitly (STRICT_DNS to wg-easy-svc).
                #      The previous version pointed at outbound|51820|... which never existed,
                #      which is why no UDP ever reached the pod.
                #   2. LISTENER — a UDP listener on 0.0.0.0:51820 whose udp_proxy filter
                #      forwards to the explicit cluster above.
                apiVersion: networking.istio.io/v1alpha3
                kind: EnvoyFilter
                metadata:
                  name: wg-easy-udp-proxy
                  namespace: istio-system
                spec:
                  workloadSelector:
                    labels:
                      app: %%WG_GATEWAY%%
                  configPatches:
                    - applyTo: CLUSTER
                      match:
                        context: GATEWAY
                      patch:
                        operation: ADD
                        value:
                          name: wg_easy_udp_cluster
                          connect_timeout: 5s
                          type: STRICT_DNS
                          lb_policy: ROUND_ROBIN
                          load_assignment:
                            cluster_name: wg_easy_udp_cluster
                            endpoints:
                              - lb_endpoints:
                                  - endpoint:
                                      address:
                                        socket_address:
                                          protocol: UDP
                                          address: wg-easy-svc.wg-easy.svc.cluster.local
                                          port_value: 51820
                    - applyTo: LISTENER
                      match:
                        context: GATEWAY
                      patch:
                        operation: ADD
                        value:
                          name: wg_easy_udp_51820
                          address:
                            socket_address:
                              protocol: UDP
                              address: 0.0.0.0
                              port_value: 51820
                          udp_listener_config:
                            downstream_socket_config: {}
                          listener_filters:
                            - name: envoy.filters.udp_listener.udp_proxy
                              typed_config:
                                "@type": type.googleapis.com/envoy.extensions.filters.udp.udp_proxy.v3.UdpProxyConfig
                                stat_prefix: wg_easy_wireguard
                                # 'matcher' (not the deprecated 'cluster' field) — this
                                # solution installs the latest Istio/Envoy, where udp_proxy
                                # routing is matcher-based. on_no_match routes all packets
                                # to the explicit cluster defined above.
                                matcher:
                                  on_no_match:
                                    action:
                                      name: route
                                      typed_config:
                                        "@type": type.googleapis.com/envoy.extensions.filters.udp.udp_proxy.v3.Route
                                        cluster: wg_easy_udp_cluster
                                idle_timeout: 120s
                ---
                # HTTPRoute: exposes the wg-easy web UI through the Istio external gateway.
                # %%WG_HOST%% is substituted at apply-time with the Public Hostname / IP
                # entered in the component's FormFields. Note: an HTTPRoute hostname must be
                # a DNS name — if WG_HOST is a bare IP, edit this to a real hostname in the
                # advanced YAML editor (WireGuard UDP itself works fine with an IP).
                # Requires the Gateway to allow routes from outside its own namespace —
                # standard Istio setup includes allowedRoutes.namespaces.from: All.
                apiVersion: gateway.networking.k8s.io/v1
                kind: HTTPRoute
                metadata:
                  name: wg-easy-ui
                  namespace: wg-easy
                spec:
                  parentRefs:
                    - name: %%WG_GATEWAY%%
                      namespace: istio-system
                  hostnames:
                    - "%%WG_HOST%%"
                  rules:
                    - matches:
                        - path:
                            type: PathPrefix
                            value: /
                      backendRefs:
                        - name: wg-easy-svc
                          port: 51821
                """,
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "wg-gateway",
                    Label = "Istio Gateway",
                    YamlPath = "",
                    Type = FormFieldType.GatewaySelector,
                    StoreAsSecret = true,
                    SecretName = "WG_GATEWAY_NAME",
                    ManifestPlaceholder = "%%WG_GATEWAY%%",
                    HelpText = "Which Istio gateway to route WireGuard through. Selecting one auto-detects its LoadBalancer IP and fills in the hostname below."
                },
                new ComponentFormField
                {
                    Key = "wg-host",
                    Label = "Public Hostname / IP",
                    YamlPath = "",
                    Type = FormFieldType.Text,
                    StoreAsSecret = true,
                    SecretName = "WG_HOST",
                    KubernetesSecretName = "wg-easy-env",
                    KubernetesSecretNamespace = "wg-easy",
                    ManifestPlaceholder = "%%WG_HOST%%",
                    Placeholder = "vpn.example.com",
                    HelpText = "The public address WireGuard clients connect to, and the web-UI hostname. Enter a DNS hostname (recommended) that resolves to the selected gateway's LoadBalancer IP — the gateway IP is auto-filled only as a starting point, but a hostname is preferred so clients aren't pinned to a literal IP. It's substituted into the web-UI HTTPRoute automatically."
                },
                new ComponentFormField
                {
                    Key = "admin-password",
                    Label = "Admin Password",
                    YamlPath = "",
                    Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    SecretName = "PASSWORD_HASH",
                    KubernetesSecretName = "wg-easy-env",
                    KubernetesSecretNamespace = "wg-easy",
                    BcryptOnSync = true,
                    HelpText = "Plain-text password stored in vault. EntKube automatically bcrypt-hashes it (cost 12) when syncing to the wg-easy-env K8s Secret as PASSWORD_HASH."
                },
                new ComponentFormField
                {
                    Key = "wg-allowed-ips",
                    Label = "Allowed IPs (cluster CIDRs)",
                    YamlPath = "",
                    Type = FormFieldType.Text,
                    StoreAsSecret = true,
                    SecretName = "WG_ALLOWED_IPS",
                    ManifestPlaceholder = "%%WG_ALLOWED_IPS%%",
                    DefaultValue = "100.96.0.0/11,10.250.0.0/16,100.64.0.0/13",
                    Placeholder = "100.96.0.0/11,10.250.0.0/16,100.64.0.0/13",
                    HelpText = "Comma-separated CIDRs that VPN clients route through the tunnel — must cover your cluster's Pod, Service, and node networks. The Service CIDR (where Cluster DNS lives) MUST be included or in-cluster name resolution fails over the VPN."
                },
                new ComponentFormField
                {
                    Key = "wg-dns",
                    Label = "Cluster DNS (CoreDNS ClusterIP)",
                    YamlPath = "",
                    Type = FormFieldType.Text,
                    StoreAsSecret = true,
                    SecretName = "WG_DNS",
                    ManifestPlaceholder = "%%WG_DNS%%",
                    DefaultValue = "100.64.0.10",
                    Placeholder = "100.64.0.10",
                    HelpText = "The in-cluster DNS (CoreDNS/kube-dns) Service ClusterIP, so VPN clients can resolve *.svc.cluster.local. Usually the .10 address of your Service CIDR (e.g. 100.64.0.10). Its CIDR must also appear in Allowed IPs above."
                }
            ]
        },

        new CatalogEntry
        {
            Key = "tailscale-operator",
            DisplayName = "Tailscale Operator",
            Description = "The official Tailscale Kubernetes Operator. Exposes Kubernetes services and Ingresses directly into your Tailscale network — no manual WireGuard config, no self-hosted coordination server. ACLs and access policies are managed in the Tailscale admin console. Supports both L3 (per-service proxy pods) and L7 (Ingress with HTTPS).",
            Icon = "bi-shield-lock",
            Category = "Networking",
            HelmRepoUrl = "https://pkgs.tailscale.com/helmcharts",
            HelmChartName = "tailscale-operator",
            HelmChartVersion = "1.82.0",
            DefaultNamespace = "tailscale",
            DefaultReleaseName = "tailscale-operator",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "oauth-client-id",
                    Label = "OAuth Client ID",
                    YamlPath = "oauth.clientId",
                    Type = FormFieldType.Text,
                    Placeholder = "kXXXXXXXXXXXXX",
                    HelpText = "Create an OAuth client in the Tailscale admin console (Settings → OAuth clients) with 'Devices: Read/Write' and 'Routes: Read/Write' scopes. Tag it with the same tag you put in defaultTags below."
                },
                new ComponentFormField
                {
                    Key = "oauth-client-secret",
                    Label = "OAuth Client Secret",
                    YamlPath = "oauth.clientSecret",
                    Type = FormFieldType.Password,
                    Placeholder = "tskey-client-...",
                    HelpText = "The secret key shown once when you create the OAuth client."
                },
                new ComponentFormField
                {
                    Key = "operator-hostname",
                    Label = "Operator hostname",
                    YamlPath = "operatorConfig.hostname",
                    Type = FormFieldType.Text,
                    DefaultValue = "k8s-operator",
                    Placeholder = "k8s-operator",
                    HelpText = "Device name the operator registers itself under in your tailnet."
                },
                new ComponentFormField
                {
                    Key = "default-tags",
                    Label = "Default device tags",
                    YamlPath = "operatorConfig.defaultTags",
                    Type = FormFieldType.Text,
                    DefaultValue = "tag:k8s",
                    Placeholder = "tag:k8s",
                    HelpText = "Comma-separated Tailscale ACL tags applied to every proxy device. The OAuth client must have permission to apply these tags."
                },
                new ComponentFormField
                {
                    Key = "cpu-request",
                    Label = "CPU Request",
                    YamlPath = "resources.requests.cpu",
                    Type = FormFieldType.Text,
                    DefaultValue = "100m"
                },
                new ComponentFormField
                {
                    Key = "memory-request",
                    Label = "Memory Request",
                    YamlPath = "resources.requests.memory",
                    Type = FormFieldType.Text,
                    DefaultValue = "128Mi"
                }
            ],
            DefaultValues = """
                oauth:
                  clientId: ""
                  clientSecret: ""

                operatorConfig:
                  hostname: "k8s-operator"
                  defaultTags:
                    - "tag:k8s"

                resources:
                  requests:
                    cpu: 100m
                    memory: 128Mi
                  limits:
                    memory: 256Mi
                """
        },

        new CatalogEntry
        {
            Key = "headscale",
            DisplayName = "Headscale (VPN coordinator)",
            Description = "Self-hosted Tailscale control plane. Runs the coordination server that nodes register against. Once installed, create a subnet router to advertise the cluster service CIDR into your VPN — so Tailscale clients can reach in-cluster services without a per-service tunnel.",
            Icon = "bi-shield-lock-fill",
            Category = "Networking",
            ComponentType = "Manifest",
            HelmRepoUrl = "",
            HelmChartName = "",
            DefaultNamespace = "headscale",
            DefaultReleaseName = "headscale",
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "server-url",
                    Label = "Public server URL",
                    YamlPath = "headscale:server-url",
                    Type = FormFieldType.Text,
                    Placeholder = "https://headscale.example.com",
                    StoreAsSecret = true,
                    ManifestPlaceholder = "%%SERVER_URL%%",
                    HelpText = "The public HTTPS URL where headscale is reachable. Must match the hostname you expose via an external route — headscale uses this in coordination messages and magic-DNS records."
                },
                new ComponentFormField
                {
                    Key = "base-domain",
                    Label = "Magic DNS base domain",
                    YamlPath = "headscale:base-domain",
                    Type = FormFieldType.Text,
                    Placeholder = "ts.example.com",
                    DefaultValue = "ts.internal",
                    StoreAsSecret = true,
                    ManifestPlaceholder = "%%BASE_DOMAIN%%",
                    HelpText = "Suffix for Magic DNS hostnames assigned to nodes. Nodes become reachable as <machine>.<user>.<base-domain> within the VPN."
                },
                new ComponentFormField
                {
                    Key = "cluster-issuer",
                    Label = "TLS cluster issuer",
                    YamlPath = "headscale:cluster-issuer",
                    Type = FormFieldType.ClusterIssuer,
                    StoreAsSecret = true,
                    HelpText = "cert-manager ClusterIssuer used to issue the TLS certificate for the public server URL."
                },
                new ComponentFormField
                {
                    Key = "api-key",
                    Label = "API key",
                    YamlPath = "headscale:api-key",
                    Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    HelpText = "Generated after first install by clicking 'Generate API Key' in the VPN tab. Leave blank for initial install."
                }
            ],
            DefaultValues = """
                apiVersion: v1
                kind: Namespace
                metadata:
                  name: headscale
                  labels:
                    app.kubernetes.io/managed-by: entkube
                ---
                apiVersion: v1
                kind: PersistentVolumeClaim
                metadata:
                  name: headscale-data
                  namespace: headscale
                spec:
                  accessModes: [ReadWriteOnce]
                  resources:
                    requests:
                      storage: 2Gi
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: headscale-config
                  namespace: headscale
                data:
                  config.yaml: |
                    server_url: %%SERVER_URL%%
                    listen_addr: 0.0.0.0:8080
                    grpc_listen_addr: 127.0.0.1:50443
                    metrics_listen_addr: 0.0.0.0:9090
                    noise:
                      private_key_path: /var/lib/headscale/noise_private.key
                    prefixes:
                      v4: 100.64.0.0/10
                      v6: fd7a:115c:a1e0::/48
                      allocation: sequential
                    derp:
                      server:
                        enabled: false
                        region_id: 999
                        region_code: "headscale"
                        region_name: "Headscale Embedded DERP"
                        stun_listen_addr: "0.0.0.0:3478"
                      urls:
                        - https://controlplane.tailscale.com/derpmap/default
                      paths: []
                      auto_update_enabled: true
                      update_frequency: 24h
                    database:
                      type: sqlite
                      sqlite:
                        path: /var/lib/headscale/db.sqlite
                        write_ahead_log: true
                    policy:
                      mode: database
                    dns:
                      magic_dns: true
                      base_domain: %%BASE_DOMAIN%%
                      nameservers:
                        global:
                          - 1.1.1.1
                          - 1.0.0.1
                    log:
                      level: info
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: headscale-policy
                  namespace: headscale
                data:
                  policy.hujson: |
                    {
                      "groups": {
                        "group:infra": [],
                        "group:admin": []
                      },
                      "acls": [
                        {"action": "accept", "src": ["*"], "dst": ["*:*"]}
                      ]
                    }
                ---
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: headscale
                  namespace: headscale
                  labels:
                    app: headscale
                    app.kubernetes.io/managed-by: entkube
                spec:
                  replicas: 1
                  selector:
                    matchLabels:
                      app: headscale
                  template:
                    metadata:
                      labels:
                        app: headscale
                    spec:
                      securityContext:
                        fsGroup: 999
                      containers:
                      - name: headscale
                        image: ghcr.io/juanfont/headscale:0.23.0
                        args: ["serve"]
                        ports:
                        - name: http
                          containerPort: 8080
                        - name: metrics
                          containerPort: 9090
                        volumeMounts:
                        - name: data
                          mountPath: /var/lib/headscale
                        - name: config
                          mountPath: /etc/headscale/config.yaml
                          subPath: config.yaml
                        - name: policy
                          mountPath: /etc/headscale/policy.hujson
                          subPath: policy.hujson
                        livenessProbe:
                          httpGet:
                            path: /health
                            port: 8080
                          initialDelaySeconds: 15
                          periodSeconds: 30
                        resources:
                          requests:
                            cpu: 100m
                            memory: 128Mi
                          limits:
                            memory: 256Mi
                      volumes:
                      - name: data
                        persistentVolumeClaim:
                          claimName: headscale-data
                      - name: config
                        configMap:
                          name: headscale-config
                      - name: policy
                        configMap:
                          name: headscale-policy
                ---
                apiVersion: v1
                kind: Service
                metadata:
                  name: headscale
                  namespace: headscale
                  labels:
                    app: headscale
                    app.kubernetes.io/managed-by: entkube
                spec:
                  selector:
                    app: headscale
                  ports:
                  - name: http
                    port: 80
                    targetPort: 8080
                  - name: grpc
                    port: 50443
                    targetPort: 50443
                """
        },

        new CatalogEntry
        {
            Key = "submariner-broker",
            DisplayName = "Submariner Broker",
            Description = "Coordination hub for the Submariner cluster mesh. Stores cluster and endpoint metadata so gateway pods can discover each other. Install on exactly one cluster per mesh — this cluster does not carry data-plane traffic.",
            Icon = "bi-diagram-3",
            Category = "Networking",
            HelmRepoUrl = "https://submariner.io/charts",
            HelmChartName = "submariner-k8s-broker",
            HelmChartVersion = "0.17.0",
            DefaultNamespace = "submariner-k8s-broker",
            DefaultReleaseName = "submariner-broker",
            DefaultValues = "",
            FormFields = []
        },

        new CatalogEntry
        {
            Key = "submariner-gateway",
            DisplayName = "Submariner Gateway",
            Description = "IPsec data-plane gateway for the Submariner cluster mesh. Install on every cluster that joins the mesh (including the broker host). Handles encrypted pod-to-pod and service-to-service traffic between clusters via Libreswan.",
            Icon = "bi-hdd-network",
            Category = "Networking",
            HelmRepoUrl = "https://submariner.io/charts",
            HelmChartName = "submariner-operator",
            HelmChartVersion = "0.17.0",
            DefaultNamespace = "submariner-operator",
            DefaultReleaseName = "submariner",
            DefaultValues = """
                submariner:
                  cableDriver: libreswan
                """,
            FormFields =
            [
                new ComponentFormField
                {
                    Key = "broker-api-server",
                    Label = "Broker API Server URL",
                    YamlPath = "broker.server",
                    Type = FormFieldType.Text,
                    Placeholder = "https://<broker-cluster-api>:6443"
                },
                new ComponentFormField
                {
                    Key = "broker-token",
                    Label = "Broker Token",
                    YamlPath = "broker.token",
                    Type = FormFieldType.Password,
                    StoreAsSecret = true
                },
                new ComponentFormField
                {
                    Key = "cluster-cidr",
                    Label = "Pod CIDR",
                    YamlPath = "submariner.clusterCidr",
                    Type = FormFieldType.Text,
                    Placeholder = "e.g. 10.244.0.0/16"
                },
                new ComponentFormField
                {
                    Key = "service-cidr",
                    Label = "Service CIDR",
                    YamlPath = "submariner.serviceCidr",
                    Type = FormFieldType.Text,
                    Placeholder = "e.g. 10.96.0.0/12"
                },
                new ComponentFormField
                {
                    Key = "cable-driver",
                    Label = "Cable Driver",
                    YamlPath = "submariner.cableDriver",
                    Type = FormFieldType.Select,
                    DefaultValue = "libreswan",
                    Options = ["libreswan", "wireguard"]
                }
            ]
        },

        // ── OpenStack platform (for EntKube-provisioned clusters) ──
        // These make a freshly-provisioned OpenStack cluster fully functional: a CNI so
        // nodes go Ready, the cloud-controller-manager for node lifecycle + Octavia
        // LoadBalancers, and the Cinder CSI driver for dynamic PVCs. CCM + Cinder CSI read
        // an in-cluster "cloud-config" Secret (cloud.conf) that ClusterProvisioningService
        // writes into kube-system from the OpenStack application credential it mints, so no
        // credential fields are needed here.

        new CatalogEntry
        {
            Key = "cilium",
            DisplayName = "Cilium (CNI)",
            Description = "eBPF-based CNI providing pod networking, network policy, and load balancing. The default container network for EntKube-provisioned clusters; installable standalone on any cluster that has no CNI yet.",
            Icon = "bi-bezier2",
            Category = "Networking",
            HelmRepoUrl = "https://helm.cilium.io/",
            HelmChartName = "cilium",
            HelmChartVersion = "1.16.5",
            DefaultNamespace = "kube-system",
            DefaultReleaseName = "cilium",
            InstallTimeout = "15m0s",
            DetectionCrds = ["ciliumnetworkpolicies.cilium.io"],
            DefaultValues = """
                ipam:
                  mode: kubernetes
                """,
            FormFields = []
        },

        new CatalogEntry
        {
            Key = "openstack-ccm",
            DisplayName = "OpenStack Cloud Controller Manager",
            Description = "External cloud-controller-manager for OpenStack: initializes nodes, wires Service type=LoadBalancer to Octavia, and manages node lifecycle. Reads the in-cluster 'cloud-config' Secret written during provisioning.",
            Icon = "bi-hdd-network",
            Category = "Networking",
            HelmRepoUrl = "https://kubernetes.github.io/cloud-provider-openstack",
            HelmChartName = "openstack-cloud-controller-manager",
            HelmChartVersion = "2.31.1",
            DefaultNamespace = "kube-system",
            DefaultReleaseName = "openstack-ccm",
            InstallTimeout = "15m0s",
            DetectionResource = new DetectionResource("apps", "v1", "daemonsets", "openstack-cloud-controller-manager"),
            DefaultValues = """
                # Reference the cloud-config Secret created by EntKube provisioning
                # (do not let the chart create/overwrite it).
                secret:
                  enabled: true
                  create: false
                  name: cloud-config
                # Run on control-plane nodes before the CNI/other add-ons are up.
                tolerations:
                  - key: node.cloudprovider.kubernetes.io/uninitialized
                    value: "true"
                    effect: NoSchedule
                  - key: node-role.kubernetes.io/control-plane
                    effect: NoSchedule
                  - key: node-role.kubernetes.io/master
                    effect: NoSchedule
                """,
            FormFields = []
        },

        new CatalogEntry
        {
            Key = "openstack-cinder-csi",
            DisplayName = "OpenStack Cinder CSI",
            Description = "CSI driver backing dynamic PersistentVolumeClaims with OpenStack Cinder block volumes. Installs a default StorageClass so ordinary RWO PVCs just work. Reads the in-cluster 'cloud-config' Secret written during provisioning.",
            Icon = "bi-hdd-stack",
            Category = "Storage",
            HelmRepoUrl = "https://kubernetes.github.io/cloud-provider-openstack",
            HelmChartName = "openstack-cinder-csi",
            HelmChartVersion = "2.31.1",
            DefaultNamespace = "kube-system",
            DefaultReleaseName = "cinder-csi",
            InstallTimeout = "15m0s",
            Dependencies = ["openstack-ccm"],
            DetectionCrds = ["volumesnapshotclasses.snapshot.storage.k8s.io"],
            DefaultValues = """
                secret:
                  enabled: true
                  create: false
                  name: cloud-config
                storageClass:
                  enabled: true
                  delete:
                    isDefault: true
                    allowVolumeExpansion: true
                  retain:
                    isDefault: false
                    allowVolumeExpansion: true
                """,
            FormFields = []
        },

        new CatalogEntry
        {
            Key = "cubefs",
            DisplayName = "CubeFS",
            Description = "Cloud-native distributed storage providing an S3-compatible object gateway (buckets) and shared/RWX filesystem volumes. Layered on the Cinder block StorageClass, it gives a single portable S3 story on any OpenStack — independent of whether the cloud exposes Ceph RGW or Swift. Note: this is a distributed system you operate (master/meta/data nodes); size and tune per environment.",
            Icon = "bi-hdd-stack-fill",
            Category = "Storage",
            HelmRepoUrl = "https://cubefs.github.io/cubefs-helm",
            HelmChartName = "cubefs",
            HelmChartVersion = "3.5.0",
            DefaultNamespace = "cubefs-system",
            DefaultReleaseName = "cubefs",
            InstallTimeout = "20m0s",
            Dependencies = ["openstack-cinder-csi"],
            DetectionCrds = ["cubefsclusters.cubefs.io"],
            DefaultValues = """
                # CubeFS component toggles. Data/meta nodes are backed by the default
                # (Cinder) StorageClass; tune replica counts, disk paths and capacity per
                # environment before production use.
                component:
                  master: true
                  metanode: true
                  datanode: true
                  objectnode: true   # the S3 gateway EntKube storage links consume
                  client: false
                  csi: true
                """,
            FormFields = []
        }
    ];

    /// <summary>
    /// Returns a mapping of subchart release names to their parent catalog key.
    /// For example: {"plugin-barman-cloud" → "cloudnative-pg"}.
    /// Used during scan to identify releases that are subcharts of a parent.
    /// </summary>
    public static Dictionary<string, string> GetSubchartParents()
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogEntry entry in Entries)
        {
            foreach (ComponentFormField field in entry.FormFields)
            {
                if (field.YamlPath.StartsWith("subchart:", StringComparison.Ordinal))
                {
                    string subchartName = field.YamlPath["subchart:".Length..];
                    result[subchartName] = entry.Key;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Looks up a catalog entry by its key. Returns null if not found.
    /// </summary>
    public static CatalogEntry? GetByKey(string key)
    {
        return Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the catalog entry for a stored component. Components registered from
    /// the catalog store the catalog Key as their name, so <see cref="GetByKey"/>
    /// matches directly. Scan-imported components store the Helm release name instead
    /// (e.g. "cnpg" rather than "cloudnative-pg"), so we fall back to
    /// <see cref="FindByRelease"/> — matching on release/chart name. This keeps title,
    /// icon, form fields, install timeout, and other catalog-derived behaviour
    /// consistent regardless of how the component was added. Returns null if unknown.
    /// </summary>
    public static CatalogEntry? ResolveForComponent(string componentName, string? helmChartName)
    {
        return GetByKey(componentName) ?? FindByRelease(componentName, helmChartName);
    }

    /// <summary>
    /// Finds a catalog entry that matches a discovered Helm release by checking
    /// the release name against the catalog Key, DefaultReleaseName, and HelmChartName.
    /// </summary>
    public static CatalogEntry? FindByRelease(string releaseName, string? chartName)
    {
        // First try exact matches on Key, DefaultReleaseName, or HelmChartName == releaseName.

        CatalogEntry? exact = Entries.FirstOrDefault(e =>
            string.Equals(e.Key, releaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.DefaultReleaseName, releaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.HelmChartName, releaseName, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        // Match by chart name. When a single entry owns the chart this is unambiguous;
        // when several share it (e.g. "istio" and "istio-internal" both use "gateway"),
        // disambiguate by the external/internal qualifier in the release name so custom
        // release names resolve to the correct entry instead of just the first one listed.

        if (!string.IsNullOrEmpty(chartName))
        {
            List<CatalogEntry> chartMatches = Entries
                .Where(e => string.Equals(e.HelmChartName, chartName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (chartMatches.Count == 1)
            {
                return chartMatches[0];
            }

            if (chartMatches.Count > 1)
            {
                CatalogEntry? qualified = chartMatches.FirstOrDefault(e => MatchesByChartQualifier(releaseName, e));

                if (qualified is not null)
                {
                    return qualified;
                }

                // Ambiguous chart with no qualifier hint — fall through to partial matching
                // rather than guessing at the first sibling.
            }
        }

        // Fall back to partial matching — the release name might contain the
        // catalog's DefaultReleaseName or Key as a substring, or vice versa.
        // For example, release "cloudnative-pg-cnpg" matching entry with Key "cloudnative-pg".

        return Entries.FirstOrDefault(e =>
            (!string.IsNullOrEmpty(e.DefaultReleaseName)
                && (releaseName.Contains(e.DefaultReleaseName, StringComparison.OrdinalIgnoreCase)
                    || e.DefaultReleaseName.Contains(releaseName, StringComparison.OrdinalIgnoreCase)))
            || releaseName.Contains(e.Key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Distinguishing qualifiers for catalog entries that share a Helm chart. The Istio
    /// external and internal gateways are both the "gateway" chart, separated only by these
    /// words in their Key/DefaultReleaseName. Real deployments pick custom release names
    /// (e.g. "istio-gw-external"), so matching falls back to this qualifier.
    /// </summary>
    private static readonly string[] ChartQualifiers = ["external", "internal"];

    /// <summary>
    /// True when <paramref name="releaseName"/> carries the same external/internal qualifier
    /// as <paramref name="entry"/> (via its Key or DefaultReleaseName) and none of the
    /// conflicting qualifiers — so "istio-gw-internal" matches the internal entry but not the
    /// external one. Returns false for entries that don't carry a qualifier at all.
    /// </summary>
    private static bool MatchesByChartQualifier(string releaseName, CatalogEntry entry)
    {
        string? entryQualifier = ChartQualifiers.FirstOrDefault(q =>
            entry.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (entry.DefaultReleaseName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        if (entryQualifier is null)
        {
            return false;
        }

        bool releaseHasEntryQualifier = releaseName.Contains(entryQualifier, StringComparison.OrdinalIgnoreCase);
        bool releaseHasConflictingQualifier = ChartQualifiers.Any(q =>
            !string.Equals(q, entryQualifier, StringComparison.OrdinalIgnoreCase)
            && releaseName.Contains(q, StringComparison.OrdinalIgnoreCase));

        return releaseHasEntryQualifier && !releaseHasConflictingQualifier;
    }

    /// <summary>
    /// Checks whether a component (by name and optional chart/release info) matches
    /// a given catalog entry. This handles cases where the Helm release name on
    /// the cluster differs from the catalog's DefaultReleaseName — e.g. a release
    /// named "istio-ingress" matching catalog entry with DefaultReleaseName "istio-ingress-external".
    /// </summary>
    public static bool IsComponentMatch(string componentName, CatalogEntry entry,
        string? helmChartName = null, string? releaseName = null)
    {
        // Direct match on Key or DefaultReleaseName.
        // Intentionally does NOT match componentName against entry.HelmChartName —
        // that path would cause "cert-manager" to match the letsencrypt-issuer entry.
        // HelmChartName matching is handled below via the component's own HelmChartName field.

        if (string.Equals(componentName, entry.Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(componentName, entry.DefaultReleaseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Match by the component's Helm chart name (set during import from catalog enrichment).
        // For example, component with HelmChartName "gateway" matches catalog entry with HelmChartName "gateway".

        if (!string.IsNullOrEmpty(helmChartName)
            && string.Equals(helmChartName, entry.HelmChartName, StringComparison.OrdinalIgnoreCase))
        {
            // Chart name alone is ambiguous when multiple catalog entries share the same chart
            // (e.g. "istio" and "istio-internal" both use the "gateway" chart).
            // Disambiguate using the release name when both sides have one.
            if (!string.IsNullOrEmpty(releaseName) && !string.IsNullOrEmpty(entry.DefaultReleaseName))
            {
                return string.Equals(releaseName, entry.DefaultReleaseName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(releaseName, entry.Key, StringComparison.OrdinalIgnoreCase)
                    // Real deployments use custom release names (e.g. "istio-gw-external") that
                    // don't equal the catalog default, so fall back to the external/internal
                    // qualifier that distinguishes the sibling entries.
                    || MatchesByChartQualifier(releaseName, entry);
            }
            return true;
        }

        // Match by the component's ReleaseName if stored separately from Name.

        if (!string.IsNullOrEmpty(releaseName)
            && (string.Equals(releaseName, entry.Key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(releaseName, entry.DefaultReleaseName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a list of component names (with optional chart names) to their catalog keys.
    /// For example, if "cnpg" is installed, this returns "cloudnative-pg" (the catalog key).
    /// Components that don't match any catalog entry keep their original name.
    /// This ensures dependency checks work correctly for imported components.
    /// </summary>
    public static List<string> ResolveInstalledKeys(IEnumerable<(string Name, string? HelmChartName, string? ReleaseName)> components)
    {
        List<string> resolved = [];

        foreach ((string name, string? chartName, string? releaseName) in components)
        {
            // Prefer a direct key/releaseName match to avoid HelmChartName collisions.
            // Example: letsencrypt-issuer has HelmChartName="cert-manager", so a fuzzy
            // first-match would incorrectly resolve it to the cert-manager catalog entry.
            CatalogEntry? match =
                Entries.FirstOrDefault(e =>
                    string.Equals(name, e.Key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, e.DefaultReleaseName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(releaseName)
                        && (string.Equals(releaseName, e.Key, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(releaseName, e.DefaultReleaseName, StringComparison.OrdinalIgnoreCase))))
                ?? Entries.FirstOrDefault(e => IsComponentMatch(name, e, chartName, releaseName));

            resolved.Add(match?.Key ?? name);
        }

        return resolved;
    }

    /// <summary>
    /// Returns catalog entries grouped by category, in the order they appear.
    /// </summary>
    public static IReadOnlyList<IGrouping<string, CatalogEntry>> GetByCategory()
    {
        return Entries.GroupBy(e => e.Category).ToList();
    }

    /// <summary>
    /// Checks which dependencies are satisfied and which are missing for a given
    /// catalog entry, based on the components already present on the cluster.
    /// Returns a result describing the status of each dependency.
    /// </summary>
    public static DependencyCheckResult CheckDependencies(CatalogEntry entry, IEnumerable<string> installedComponentNames)
    {
        HashSet<string> installed = new(installedComponentNames, StringComparer.OrdinalIgnoreCase);
        List<string> missingDirect = [];
        List<DependencyRequirement> missingOneOf = [];

        // Check direct dependencies — all must be present.

        foreach (string dep in entry.Dependencies)
        {
            if (!installed.Contains(dep))
            {
                missingDirect.Add(dep);
            }
        }

        // Check one-of dependencies — at least one option must be present.

        foreach (DependencyRequirement req in entry.RequiresOneOf)
        {
            bool satisfied = req.Options.Any(opt => installed.Contains(opt));
            if (!satisfied)
            {
                missingOneOf.Add(req);
            }
        }

        return new DependencyCheckResult
        {
            IsSatisfied = missingDirect.Count == 0 && missingOneOf.Count == 0,
            MissingDependencies = missingDirect,
            MissingOneOfRequirements = missingOneOf
        };
    }

    /// <summary>
    /// Creates a ComponentRegistration from a catalog entry, pre-filling all
    /// the Helm details. The operator can then adjust the values before installing.
    /// </summary>
    public static ComponentRegistration ToRegistration(CatalogEntry entry)
    {
        return new ComponentRegistration
        {
            Name = entry.Key,
            ComponentType = entry.ComponentType,
            Namespace = entry.DefaultNamespace,
            HelmRepoUrl = entry.HelmRepoUrl,
            HelmChartName = entry.HelmChartName,
            HelmChartVersion = entry.HelmChartVersion,
            ReleaseName = entry.DefaultReleaseName ?? entry.Key,
            HelmValues = entry.DefaultValues
        };
    }
}

/// <summary>
/// Result of checking whether a catalog entry's dependencies are met
/// given the set of currently installed components on a cluster.
/// </summary>
public class DependencyCheckResult
{
    /// <summary>True if all dependencies are satisfied.</summary>
    public bool IsSatisfied { get; init; }

    /// <summary>Direct dependency keys that are not yet present.</summary>
    public IReadOnlyList<string> MissingDependencies { get; init; } = [];

    /// <summary>One-of requirements where none of the options are present.</summary>
    public IReadOnlyList<DependencyRequirement> MissingOneOfRequirements { get; init; } = [];
}
