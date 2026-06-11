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

    /// <summary>Default namespace where this should be deployed.</summary>
    public required string DefaultNamespace { get; init; }

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
            Description = "Configures a ClusterIssuer that uses Let's Encrypt to automatically issue and renew TLS certificates. Supports HTTP-01 (ingress) and DNS-01 (Azure DNS) challenge solvers — both can be active simultaneously.",
            Icon = "bi-lock",
            Category = "Certificate Management",
            HelmRepoUrl = "https://charts.jetstack.io",
            HelmChartName = "letsencrypt-issuer",
            DefaultNamespace = "cert-manager",
            DefaultReleaseName = "letsencrypt-issuer",
            Dependencies = ["cert-manager"],
            ComponentType = "Manifest",
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
                new ComponentFormField
                {
                    Key = "dns-zone", Label = "DNS-01 Hosted Zone",
                    YamlPath = "spec.acme.solvers.0.dns01.azureDNS.hostedZoneName",
                    Type = FormFieldType.Text,
                    Placeholder = "example.com",
                    HelpText = "Azure DNS zone name (e.g. example.com)"
                },
                new ComponentFormField
                {
                    Key = "dns-resource-group", Label = "DNS Resource Group",
                    YamlPath = "spec.acme.solvers.0.dns01.azureDNS.resourceGroupName",
                    Type = FormFieldType.Text,
                    Placeholder = "my-dns-rg",
                    HelpText = "Azure resource group containing the DNS zone"
                },
                new ComponentFormField
                {
                    Key = "dns-subscription-id", Label = "DNS Subscription ID",
                    YamlPath = "spec.acme.solvers.0.dns01.azureDNS.subscriptionID",
                    Type = FormFieldType.Text,
                    Placeholder = "00000000-0000-0000-0000-000000000000",
                    HelpText = "Azure subscription ID containing the DNS zone"
                },
                new ComponentFormField
                {
                    Key = "dns-tenant-id", Label = "DNS Tenant ID",
                    YamlPath = "spec.acme.solvers.0.dns01.azureDNS.tenantID",
                    Type = FormFieldType.Text,
                    Placeholder = "00000000-0000-0000-0000-000000000000",
                    HelpText = "Azure AD tenant ID for the service principal"
                },
                new ComponentFormField
                {
                    Key = "dns-client-id", Label = "DNS Client ID",
                    YamlPath = "spec.acme.solvers.0.dns01.azureDNS.clientID",
                    Type = FormFieldType.Text,
                    Placeholder = "00000000-0000-0000-0000-000000000000",
                    HelpText = "Service principal (app registration) client ID"
                },
                new ComponentFormField
                {
                    Key = "dns-client-secret", Label = "DNS Client Secret",
                    YamlPath = "",
                    Type = FormFieldType.Password,
                    StoreAsSecret = true,
                    SecretName = "azuredns-client-secret",
                    KubernetesSecretName = "azuredns-config",
                    KubernetesSecretNamespace = "cert-manager",
                    HelpText = "Service principal client secret — stored in vault and synced to K8s Secret 'azuredns-config'"
                }
            ],
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
                    solvers:
                      - dns01:
                          azureDNS:
                            hostedZoneName: example.com
                            resourceGroupName: my-dns-rg
                            subscriptionID: 00000000-0000-0000-0000-000000000000
                            tenantID: 00000000-0000-0000-0000-000000000000
                            clientID: 00000000-0000-0000-0000-000000000000
                            environment: AzurePublicCloud
                            clientSecretSecretRef:
                              name: azuredns-config
                              key: azuredns-client-secret
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
            FormFields =
            [
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
                  auth_enabled: false
                  commonConfig:
                    replication_factor: 1
                  storage:
                    type: filesystem
                  useTestSchema: true

                singleBinary:
                  replicas: 1
                  persistence:
                    enabled: true
                    size: 10Gi
                  resources:
                    requests:
                      cpu: 100m
                      memory: 256Mi

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
            FormFields = [],
            DefaultValues = ""
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
            Dependencies = ["rabbitmq-cluster-operator", "cert-manager"],
            FormFields = [],
            DefaultValues = ""
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
            DefaultValues = ""
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
            Description = "WireGuard VPN server with web UI. Routes WireGuard UDP 51820 through the existing Istio external gateway via an Envoy UDP proxy (EnvoyFilter), and exposes the web UI as an HTTPRoute on the same gateway. A dedicated LoadBalancer Service shares the Istio gateway's existing external IP (MetalLB: allow-shared-ip; cloud providers: set loadBalancerIP). All resources are lifecycle-managed — uninstall removes everything including the LB rule.",
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
                # Internal ClusterIP — target for both the Envoy UDP proxy and the HTTPRoute web UI.
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
                              value: "10.96.0.10"
                            - name: WG_ALLOWED_IPS
                              value: "10.96.0.0/12,10.244.0.0/16"
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
                # EnvoyFilter: adds a UDP proxy listener on 51820 to the Istio gateway pods.
                # %%WG_GATEWAY%% is substituted at apply-time with the release name of the
                # gateway chosen in the component's FormFields (e.g. istio-ingress-external).
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
                    - applyTo: LISTENER
                      match:
                        context: GATEWAY
                      patch:
                        operation: ADD
                        value:
                          name: udp_0.0.0.0_51820
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
                                cluster: outbound|51820||wg-easy-svc.wg-easy.svc.cluster.local
                                idle_timeout: 90s
                ---
                # Dedicated LoadBalancer Service that exposes UDP 51820 on the SAME external IP
                # as the Istio gateway, so no new LB IP is provisioned.
                #
                # MetalLB (on-prem): 'allow-shared-ip' tells MetalLB to assign the same IP
                # as the istio-ingress-external Service. No other changes needed.
                #
                # Cloud providers (AKS/GKE/EKS): set spec.loadBalancerIP to the Istio
                # gateway's existing external IP. Most cloud LB implementations will add
                # the UDP rule to the same backend pool rather than allocating a new IP.
                #   Azure example annotation: service.beta.kubernetes.io/azure-pip-name: <pip-name>
                #   GKE: set loadBalancerIP below
                #
                # This Service is a proper manifest resource — 'kubectl delete' on uninstall
                # removes it cleanly and the LB rule is released automatically.
                apiVersion: v1
                kind: Service
                metadata:
                  name: istio-ingress-wireguard
                  namespace: istio-system
                  annotations:
                    metallb.universe.tf/allow-shared-ip: %%WG_GATEWAY%%
                    # Uncomment and set for cloud providers:
                    # service.beta.kubernetes.io/azure-pip-name: "<your-pip-name>"
                spec:
                  type: LoadBalancer
                  # loadBalancerIP: ""  # set to the Istio gateway's existing external IP for cloud providers
                  selector:
                    app: %%WG_GATEWAY%%
                  ports:
                    - name: wireguard
                      port: 51820
                      targetPort: 51820
                      protocol: UDP
                ---
                # HTTPRoute: exposes the wg-easy web UI through the Istio external gateway.
                # Update 'hostnames' to match your WG_HOST value (the gateway's external DNS name).
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
                    - "vpn.example.com"
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
                    Placeholder = "vpn.example.com",
                    HelpText = "Auto-detected from the selected gateway's LoadBalancer IP. You can override this with a DNS hostname — just make sure it resolves to the same IP. Also update the HTTPRoute hostnames in the advanced YAML editor to match."
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
                }
            ]
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
    /// Finds a catalog entry that matches a discovered Helm release by checking
    /// the release name against the catalog Key, DefaultReleaseName, and HelmChartName.
    /// </summary>
    public static CatalogEntry? FindByRelease(string releaseName, string? chartName)
    {
        // First try exact matches on Key, DefaultReleaseName, or HelmChartName.

        CatalogEntry? exact = Entries.FirstOrDefault(e =>
            string.Equals(e.Key, releaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.DefaultReleaseName, releaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.HelmChartName, releaseName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(chartName) && string.Equals(e.HelmChartName, chartName, StringComparison.OrdinalIgnoreCase)));

        if (exact is not null)
        {
            return exact;
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
                    || string.Equals(releaseName, entry.Key, StringComparison.OrdinalIgnoreCase);
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
