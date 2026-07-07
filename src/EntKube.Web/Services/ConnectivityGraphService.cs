using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;

namespace EntKube.Web.Services;

/// <summary>
/// Builds an app's connectivity graph for a single environment: the ports it
/// exposes, who reaches it (ingress), what it reaches inside the cluster
/// (internal egress — other apps, managed databases/caches/queues/storage), and
/// what it reaches outside (external egress).
///
/// The graph is assembled by <em>merging inference with declaration</em>:
///   • Inferred — derived on every build from existing config: Service manifests
///     (exposed ports), managed-resource bindings (internal egress), and routes
///     (ingress). Requires no up-front data entry.
///   • Declared — persisted <see cref="AppServicePort"/> / <see cref="ConnectivityRule"/>
///     / <see cref="ExternalDependency"/> rows a user authored as deliberate
///     least-privilege intent.
///
/// This service is read-only in Phase 1 (topology view). Later phases layer a
/// pre-apply analyzer over the same graph, then NetworkPolicy / Istio
/// AuthorizationPolicy generation.
/// </summary>
public class ConnectivityGraphService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8sFactory,
    ILogger<ConnectivityGraphService> logger)
{
    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public async Task<ConnectivityGraph> BuildGraphAsync(Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ConnectivityGraph graph = new();

        // The app's deployments in this environment, with everything we infer from.
        List<AppDeployment> deployments = await db.AppDeployments
            .AsNoTracking()
            .Include(d => d.Manifests)
            .Include(d => d.DatabaseBindings)
            .Include(d => d.CacheBindings)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .ToListAsync(ct);

        List<Guid> deploymentIds = deployments.Select(d => d.Id).ToList();
        graph.Namespaces = deployments
            .Select(d => d.Namespace)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        // ── Exposed ports (inferred from Service manifests + declared overrides) ──
        Dictionary<string, ServicePortNode> ports = new(StringComparer.OrdinalIgnoreCase);
        foreach (AppDeployment dep in deployments)
        {
            foreach (DeploymentManifest manifest in dep.Manifests.Where(m =>
                         string.Equals(m.Kind, "Service", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (ServicePortNode node in ParseServicePorts(manifest, dep.Namespace))
                {
                    ports[$"{node.Namespace}/{node.ServiceName}:{node.Port}/{node.Protocol}"] = node;
                }
            }
        }

        List<AppServicePort> declaredPorts = await db.AppServicePorts
            .AsNoTracking()
            .Where(p => p.AppId == appId && p.EnvironmentId == environmentId)
            .ToListAsync(ct);
        foreach (AppServicePort p in declaredPorts)
        {
            string key = $"{p.Namespace}/{p.ServiceName}:{p.Port}/{p.Protocol}";
            ports[key] = new ServicePortNode
            {
                ServiceName = p.ServiceName,
                Namespace = p.Namespace,
                Port = p.Port,
                TargetPort = p.TargetPort,
                Protocol = p.Protocol.ToString().ToUpperInvariant(),
                PortName = p.PortName,
                AppProtocol = p.AppProtocol,
                Source = ConnectivitySource.Declared
            };
        }
        graph.ExposedPorts = ports.Values
            .OrderBy(p => p.ServiceName).ThenBy(p => p.Port)
            .ToList();

        // ── Ingress (who reaches this app) — inferred from routes ──
        // L7 HTTP/TLS routes.
        var l7Routes = await (
            from r in db.AppDeploymentRoutes.AsNoTracking()
            join ar in db.AppRoutes.AsNoTracking() on r.AppRouteId equals ar.Id
            where deploymentIds.Contains(r.AppDeploymentId)
            select new { ar.Hostname, r.ServiceName, r.ServicePort, r.PathPrefix, r.IsEnabled })
            .ToListAsync(ct);
        foreach (var r in l7Routes)
        {
            graph.Ingress.Add(new ConnectivityEdge
            {
                PeerLabel = r.Hostname,
                PeerKind = "Internet (HTTPS)",
                Icon = "bi-globe",
                Target = $"{r.ServiceName}:{r.ServicePort}",
                Port = r.ServicePort,
                Protocol = "TCP",
                AppProtocol = "http",
                Source = ConnectivitySource.Inferred,
                Detail = r.PathPrefix,
                IsEnabled = r.IsEnabled
            });
        }

        // Raw L4 (TCP/UDP) routes.
        List<AppL4Route> l4Routes = await db.AppL4Routes
            .AsNoTracking()
            .Where(r => deploymentIds.Contains(r.AppDeploymentId))
            .ToListAsync(ct);
        foreach (AppL4Route r in l4Routes)
        {
            graph.Ingress.Add(new ConnectivityEdge
            {
                PeerLabel = $"Port {r.ExternalPort}/{r.Protocol.ToString().ToLowerInvariant()}",
                PeerKind = $"Internet ({r.Protocol.ToString().ToUpperInvariant()})",
                Icon = "bi-ethernet",
                Target = $"{r.ServiceName}:{r.ServicePort}",
                Port = r.ServicePort,
                Protocol = r.Protocol.ToString().ToUpperInvariant(),
                Source = ConnectivitySource.Inferred,
                IsEnabled = r.IsEnabled
            });
        }

        // ── Internal egress (managed backing services this app binds to) ──
        foreach (AppDeployment dep in deployments)
        {
            foreach (DatabaseBinding b in dep.DatabaseBindings)
            {
                (string label, int port) = b switch
                {
                    { MongoDatabaseId: not null } => ("MongoDB", 27017),
                    _ => ("PostgreSQL", 5432)
                };

                // A registered (non-CNPG) database can live off-cluster, so an in-cluster
                // egress allow-rule wouldn't reach it. Treat it as external egress so the
                // generator warns rather than silently generating a rule that blocks it.
                bool offCluster = b.RegisteredPostgresDatabaseId is not null;
                ConnectivityEdge edge = new()
                {
                    PeerLabel = label,
                    PeerKind = offCluster ? "Registered database (may be off-cluster)" : "Managed database",
                    Icon = "bi-database",
                    Port = port,
                    Protocol = "TCP",
                    Source = ConnectivitySource.Inferred,
                    Detail = $"secret {b.KubernetesSecretName}",
                    IsEnabled = b.SyncEnabled
                };
                if (offCluster)
                {
                    graph.ExternalEgress.Add(edge);
                }
                else
                {
                    graph.InternalEgress.Add(edge);
                }
            }
            foreach (CacheBinding b in dep.CacheBindings)
            {
                graph.InternalEgress.Add(new ConnectivityEdge
                {
                    PeerLabel = "Redis",
                    PeerKind = "Managed cache",
                    Icon = "bi-lightning-charge",
                    Port = 6379,
                    Protocol = "TCP",
                    Source = ConnectivitySource.Inferred,
                    IsEnabled = b.SyncEnabled
                });
            }
        }

        // Messaging + storage bindings are not exposed as navigations on AppDeployment;
        // query them by deployment id.
        List<MessagingBinding> messaging = await db.MessagingBindings
            .AsNoTracking()
            .Where(b => b.AppDeploymentId != Guid.Empty && deploymentIds.Contains(b.AppDeploymentId))
            .ToListAsync(ct);
        foreach (MessagingBinding b in messaging)
        {
            string? detail = b.QueueName is not null || b.ExchangeName is not null
                ? string.Join(" / ", new[] { b.QueueName, b.ExchangeName }.Where(s => !string.IsNullOrEmpty(s)))
                : null;
            graph.InternalEgress.Add(new ConnectivityEdge
            {
                PeerLabel = "RabbitMQ",
                PeerKind = "Managed messaging",
                Icon = "bi-send",
                Port = 5672,
                Protocol = "TCP",
                Source = ConnectivitySource.Inferred,
                Detail = detail,
                IsEnabled = b.SyncEnabled
            });
        }

        List<StorageBinding> storage = await db.StorageBindings
            .AsNoTracking()
            .Where(b => b.AppDeploymentId != null && deploymentIds.Contains(b.AppDeploymentId!.Value))
            .ToListAsync(ct);
        foreach (StorageBinding b in storage)
        {
            // Object storage is frequently off-cluster; surface it as external egress
            // so a deny-egress policy would visibly threaten it.
            graph.ExternalEgress.Add(new ConnectivityEdge
            {
                PeerLabel = "Object storage",
                PeerKind = "Managed storage (S3)",
                Icon = "bi-bucket",
                Port = 443,
                Protocol = "TCP",
                AppProtocol = "https",
                Source = ConnectivitySource.Inferred,
                IsEnabled = b.SyncEnabled
            });
        }

        // ── Declared connectivity rules (deliberate intent) ──
        List<ConnectivityRule> rules = await db.ConnectivityRules
            .AsNoTracking()
            .Include(r => r.PeerApp)
            .Where(r => r.AppId == appId && r.EnvironmentId == environmentId)
            .ToListAsync(ct);
        foreach (ConnectivityRule rule in rules)
        {
            ConnectivityEdge edge = new()
            {
                PeerLabel = DescribePeer(rule),
                PeerKind = rule.PeerType == ConnectivityPeerType.External ? "External host" : $"{rule.PeerType}",
                Icon = PeerIcon(rule.PeerType),
                Port = rule.Port,
                Protocol = rule.Protocol.ToString().ToUpperInvariant(),
                AppProtocol = rule.AppProtocol,
                Source = ConnectivitySource.Declared,
                Detail = rule.Description,
                IsEnabled = rule.IsEnabled
            };

            if (rule.Direction == ConnectivityDirection.Ingress)
            {
                graph.Ingress.Add(edge);
            }
            else if (rule.PeerType is ConnectivityPeerType.External or ConnectivityPeerType.Cidr)
            {
                graph.ExternalEgress.Add(edge);
            }
            else
            {
                graph.InternalEgress.Add(edge);
            }
        }

        // ── External dependencies (declared internet egress) ──
        List<ExternalDependency> externals = await db.ExternalDependencies
            .AsNoTracking()
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .ToListAsync(ct);
        foreach (ExternalDependency d in externals)
        {
            graph.ExternalEgress.Add(new ConnectivityEdge
            {
                PeerLabel = d.Host,
                PeerKind = "External host",
                Icon = "bi-cloud-arrow-up",
                Target = $"{d.Host}:{d.Port}",
                Port = d.Port,
                Protocol = d.Protocol.ToString().ToUpperInvariant(),
                AppProtocol = d.Tls ? "tls" : null,
                Source = ConnectivitySource.Declared,
                Detail = d.Description,
                IsEnabled = true
            });
        }

        return graph;
    }

    // ── Pre-apply analyzer ────────────────────────────────────────────────────

    /// <summary>
    /// Statically analyze the connectivity graph against the environment's
    /// NetworkPolicies and least-privilege best practice, producing findings the
    /// customer can act on <em>before</em> anything is applied. Pure prediction —
    /// no cluster access.
    /// </summary>
    public async Task<List<ConnectivityFinding>> AnalyzeAsync(Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        ConnectivityGraph graph = await BuildGraphAsync(appId, environmentId, ct);
        return await AnalyzeAsync(graph, appId, environmentId, ct);
    }

    /// <summary>
    /// Analyze an already-built graph. Callers that have already built the graph
    /// (e.g. the topology view) should use this to avoid rebuilding it.
    /// </summary>
    public async Task<List<ConnectivityFinding>> AnalyzeAsync(
        ConnectivityGraph graph, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<AppNetworkPolicy> policies = await db.AppNetworkPolicies
            .AsNoTracking()
            .Where(p => p.AppId == appId && p.EnvironmentId == environmentId)
            .ToListAsync(ct);
        List<ConnectivityRule> declaredRules = await db.ConnectivityRules
            .AsNoTracking()
            .Include(r => r.PeerApp)
            .Where(r => r.AppId == appId && r.EnvironmentId == environmentId)
            .ToListAsync(ct);

        bool hasDenyAll = policies.Any(p => p.PolicyType == AppNetworkPolicyType.DenyAll);
        bool hasAllowIngress = policies.Any(p => p.PolicyType == AppNetworkPolicyType.AllowFromIngress);
        bool hasAnyPolicy = policies.Count > 0;

        List<ConnectivityFinding> findings = [];

        // Route ingress edges carry a "svc:port" backend target we can validate.
        // Only enabled routes matter — the generator skips disabled ones, so the
        // analyzer must too, or the issues panel contradicts the generated policy.
        List<ConnectivityEdge> routeTargets = graph.Ingress
            .Where(e => e.IsEnabled && e.Target is not null && e.PeerKind.StartsWith("Internet", StringComparison.Ordinal))
            .ToList();

        // ── 1) Route targets a Service/port that isn't exposed ──
        if (routeTargets.Count > 0 && graph.ExposedPorts.Count == 0)
        {
            findings.Add(new ConnectivityFinding(FindingSeverity.Warning, "Broken dependency",
                "Route targets can't be verified",
                "Routes are configured but no Service manifests were found in this environment, so their backends can't be validated.",
                "Attach the Service manifest(s) to a deployment in this environment."));
        }
        else
        {
            foreach (ConnectivityEdge e in routeTargets)
            {
                string[] parts = e.Target!.Split(':');
                string svc = parts[0];
                _ = int.TryParse(parts.ElementAtOrDefault(1), out int port);
                bool exact = graph.ExposedPorts.Any(p => string.Equals(p.ServiceName, svc, StringComparison.OrdinalIgnoreCase) && p.Port == port);
                if (exact)
                {
                    continue;
                }

                List<int> svcPorts = graph.ExposedPorts
                    .Where(p => string.Equals(p.ServiceName, svc, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Port).ToList();
                if (svcPorts.Count > 0)
                {
                    findings.Add(new ConnectivityFinding(FindingSeverity.Error, "Broken dependency",
                        $"Route port mismatch → {e.Target}",
                        $"The route “{e.PeerLabel}” sends traffic to {svc}:{port}, but that Service only exposes port(s) {string.Join(", ", svcPorts)}. Requests will fail.",
                        $"Point the route at an exposed port ({string.Join(", ", svcPorts)}), or add port {port} to the Service.",
                        e.PeerLabel));
                }
                else
                {
                    findings.Add(new ConnectivityFinding(FindingSeverity.Error, "Broken dependency",
                        $"Route backend not exposed → {e.Target}",
                        $"The route “{e.PeerLabel}” targets a Service “{svc}” that is not exposed in this environment. Requests will 503.",
                        $"Add a Service named “{svc}” exposing port {port}, or fix the route’s target service.",
                        e.PeerLabel));
                }
            }
        }

        // ── 2) DenyAll blocks required egress ──
        List<ConnectivityEdge> egress = graph.InternalEgress.Concat(graph.ExternalEgress)
            .Where(e => e.IsEnabled).ToList();
        if (hasDenyAll && egress.Count > 0)
        {
            string peers = string.Join(", ", egress.Select(e => e.PeerLabel).Distinct().Take(6));
            findings.Add(new ConnectivityFinding(FindingSeverity.Error, "Policy conflict",
                "Deny-all policy will block required outbound traffic",
                $"A DenyAll NetworkPolicy denies all egress, but this app depends on outbound access to: {peers}. No egress allow-rule exists, so these connections will break.",
                "Add egress allow-rules for these dependencies (Phase 3 generates them from this graph), or relax the DenyAll policy."));
        }

        // ── 3) DenyAll blocks inbound routes ──
        if (hasDenyAll && routeTargets.Count > 0 && !hasAllowIngress)
        {
            findings.Add(new ConnectivityFinding(FindingSeverity.Error, "Policy conflict",
                "Deny-all policy will block inbound routes",
                "A DenyAll NetworkPolicy denies all ingress and there is no “Allow from ingress” policy, but this app has external route(s). Inbound requests will be dropped.",
                "Add an “Allow from ingress” NetworkPolicy for this environment in Governance."));
        }

        // ── 4) FQDN egress is not enforceable by plain NetworkPolicy ──
        List<string> fqdnEgress = graph.ExternalEgress
            .Where(e => e.PeerKind == "External host")
            .Select(e => e.PeerLabel).Distinct().ToList();
        if (fqdnEgress.Count > 0)
        {
            findings.Add(new ConnectivityFinding(FindingSeverity.Warning, "Enforceability",
                "Internet egress needs Istio or an FQDN-capable CNI",
                $"Outbound access to {string.Join(", ", fqdnEgress.Take(6))} is by hostname. Plain Kubernetes NetworkPolicy can’t match FQDNs — enforcing this needs an Istio ServiceEntry or a CNI with FQDN egress (e.g. Cilium).",
                "Confirm the target clusters run Istio egress or an FQDN-capable CNI before relying on egress enforcement."));
        }

        // ── 5) No baseline at all (least privilege) ──
        if (!hasAnyPolicy && (graph.ExposedPorts.Count > 0 || graph.Ingress.Count > 0))
        {
            findings.Add(new ConnectivityFinding(FindingSeverity.Info, "Least privilege",
                "No network policy baseline",
                "This app exposes traffic but has no NetworkPolicy in this environment, so its namespace accepts connections from anywhere.",
                "Add a default-deny baseline plus explicit allows for the edges above."));
        }

        // ── 6) Broad declared rules ──
        foreach (ConnectivityRule r in declaredRules.Where(r => r.IsEnabled))
        {
            bool broadPeer = r.PeerType == ConnectivityPeerType.Namespace && string.IsNullOrWhiteSpace(r.PeerNamespace);
            bool anyPort = r.Port is null;
            if (!broadPeer && !anyPort)
            {
                continue;
            }

            string label = DescribePeer(r);
            string what = (anyPort, broadPeer) switch
            {
                (true, true) => "allows any port and targets all namespaces",
                (true, false) => "allows any port",
                _ => "targets all namespaces"
            };
            findings.Add(new ConnectivityFinding(FindingSeverity.Warning, "Least privilege",
                $"Broad rule → {label}",
                $"The rule to “{label}” {what}. Broad rules defeat least-privilege.",
                "Narrow the rule to a specific peer and port.", label));
        }

        return findings.OrderBy(f => f.Severity).ThenBy(f => f.Category).ToList();
    }

    // ── CRUD for declared intent ──────────────────────────────────────────────

    public async Task<List<ConnectivityRule>> GetRulesAsync(Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ConnectivityRules
            .AsNoTracking()
            .Include(r => r.PeerApp)
            .Where(r => r.AppId == appId && r.EnvironmentId == environmentId)
            .OrderBy(r => r.Direction).ThenBy(r => r.PeerType)
            .ToListAsync(ct);
    }

    public async Task<ConnectivityRule> AddRuleAsync(ConnectivityRule rule, CancellationToken ct = default)
    {
        if (rule.PeerType == ConnectivityPeerType.App && rule.PeerAppId is null)
        {
            throw new InvalidOperationException("Select a peer app for an app-to-app rule.");
        }
        if (rule.Port is int rp && rp is < 1 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }
        // Validate the free-form peer fields — they end up verbatim in generated
        // NetworkPolicy/AuthorizationPolicy YAML, so anything invalid here would
        // produce a broken (or, if crafted, a widened) manifest.
        if (!string.IsNullOrWhiteSpace(rule.PeerNamespace) && !IsValidDnsLabel(rule.PeerNamespace))
        {
            throw new InvalidOperationException("Peer namespace must be a valid Kubernetes namespace name.");
        }
        if (rule.PeerType == ConnectivityPeerType.Cidr)
        {
            if (string.IsNullOrWhiteSpace(rule.PeerCidr) || !System.Net.IPNetwork.TryParse(rule.PeerCidr, out _))
            {
                throw new InvalidOperationException("Enter a valid CIDR block (e.g. 10.0.0.0/16).");
            }
        }
        if (rule.PeerType == ConnectivityPeerType.Selector)
        {
            Dictionary<string, string>? sel = ParseSelector(rule.PeerSelector);
            if (sel is null || sel.Count == 0)
            {
                throw new InvalidOperationException("Selector must be a JSON object of labels, e.g. {\"app\":\"api\"}.");
            }
            if (sel.Any(kv => !IsValidLabelKey(kv.Key) || !IsValidLabelValue(kv.Value)))
            {
                throw new InvalidOperationException("Selector contains invalid label keys or values.");
            }
        }

        rule.Id = Guid.NewGuid();
        rule.Source = ConnectivitySource.Declared;
        rule.CreatedAt = DateTime.UtcNow;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.ConnectivityRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.ConnectivityRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<List<ExternalDependency>> GetExternalDependenciesAsync(Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ExternalDependencies
            .AsNoTracking()
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .OrderBy(d => d.Host)
            .ToListAsync(ct);
    }

    public async Task<ExternalDependency> AddExternalDependencyAsync(ExternalDependency dep, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dep.Host))
        {
            throw new InvalidOperationException("Host is required.");
        }
        dep.Host = dep.Host.Trim();
        if (!IsValidExternalHost(dep.Host))
        {
            throw new InvalidOperationException("Host must be a valid hostname (e.g. api.stripe.com or *.stripe.com).");
        }
        if (dep.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        dep.Id = Guid.NewGuid();
        dep.Source = ConnectivitySource.Declared;
        dep.CreatedAt = DateTime.UtcNow;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        db.ExternalDependencies.Add(dep);
        await db.SaveChangesAsync(ct);
        return dep;
    }

    public async Task DeleteExternalDependencyAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.ExternalDependencies.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Apps in the same customer as <paramref name="appId"/>, excluding itself — candidate peers.</summary>
    public async Task<List<App>> GetCandidatePeerAppsAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        App? app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null)
        {
            return [];
        }
        return await db.Apps
            .AsNoTracking()
            .Where(a => a.CustomerId == app.CustomerId && a.Id != appId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
    }

    // ── NetworkPolicy generation (Phase 3) ─────────────────────────────────────

    private const string IstioIngressNamespace = "istio-system";

    /// <summary>
    /// Generate the least-privilege NetworkPolicy set for this app in this
    /// environment, one plan per target namespace: a default-deny baseline, an
    /// always-on DNS egress allow, plus ingress/egress allow policies derived from
    /// the connectivity graph and declared rules. Anything that NetworkPolicy
    /// cannot express (FQDN egress, unresolvable peers) is returned as a warning,
    /// not silently dropped. Pure generation — nothing is applied.
    /// </summary>
    public async Task<List<ConnectivityPolicyPlan>> GenerateNetworkPoliciesAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        ConnectivityGraph graph = await BuildGraphAsync(appId, environmentId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        App? app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        string prefix = Sanitize(app?.Name ?? "app");

        List<ConnectivityRule> declaredRules = await db.ConnectivityRules
            .AsNoTracking()
            .Where(r => r.AppId == appId && r.EnvironmentId == environmentId && r.IsEnabled)
            .ToListAsync(ct);
        List<ExternalDependency> externalDeps = await db.ExternalDependencies
            .AsNoTracking()
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .ToListAsync(ct);

        // Resolve namespaces of any app-typed peers so we can select them precisely.
        Dictionary<Guid, List<string>> peerNs = await ResolvePeerNamespacesAsync(
            db, declaredRules.Where(r => r.PeerType == ConnectivityPeerType.App && r.PeerAppId is not null)
                .Select(r => r.PeerAppId!.Value),
            environmentId, ct);

        List<(KubernetesCluster Cluster, string Namespace)> targets =
            await ResolveAppTargetsAsync(db, appId, environmentId, ct);
        List<string> namespaces = targets.Select(t => t.Namespace)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (namespaces.Count == 0)
        {
            return [];
        }

        // Build the direction-split rule set once (same graph for every namespace).
        List<NpRule> ingress = [];
        List<NpRule> egress = [];
        List<string> warnings = [];

        // A NetworkPolicy ingress rule and an Istio AuthorizationPolicy match the
        // port the destination POD listens on (the container/targetPort), not the
        // Service port a route points at. Map service:port back to its targetPort.
        int? PodPort(ConnectivityEdge e)
        {
            string? svc = e.Target?.Split(':').FirstOrDefault();
            ServicePortNode? match = graph.ExposedPorts.FirstOrDefault(p =>
                svc is not null
                && string.Equals(p.ServiceName, svc, StringComparison.OrdinalIgnoreCase)
                && p.Port == e.Port);
            return match?.TargetPort ?? e.Port;
        }

        // Inferred ingress (routes) → allow from the ingress gateway namespace.
        foreach (ConnectivityEdge e in graph.Ingress.Where(x => x.Source == ConnectivitySource.Inferred && x.IsEnabled))
        {
            ingress.Add(new NpRule([new NpPeer("ns", IstioIngressNamespace, null)], PodPort(e), e.Protocol, e.PeerLabel));
        }

        // Inferred in-cluster egress (managed DB/cache/queue) → allow to any in-cluster pod on that port.
        foreach (ConnectivityEdge e in graph.InternalEgress.Where(x => x.Source == ConnectivitySource.Inferred && x.IsEnabled))
        {
            egress.Add(new NpRule([new NpPeer("nsAny", null, null)], e.Port, e.Protocol, e.PeerLabel));
        }

        // Inferred external egress (e.g. object storage) — not expressible by NetworkPolicy.
        foreach (ConnectivityEdge e in graph.ExternalEgress.Where(x => x.Source == ConnectivitySource.Inferred && x.IsEnabled))
        {
            warnings.Add($"Egress to “{e.PeerLabel}” ({e.PeerKind}) can’t be enforced by NetworkPolicy — needs an Istio ServiceEntry (Phase 4).");
        }

        // Declared rules → translate the peer to a NetworkPolicy selector.
        foreach (ConnectivityRule r in declaredRules)
        {
            List<NpPeer>? peers = TranslatePeer(r, peerNs, warnings);
            if (peers is null || peers.Count == 0)
            {
                continue;
            }
            NpRule rule = new(peers, r.Port, r.Protocol.ToString().ToUpperInvariant(), DescribePeer(r));
            if (r.Direction == ConnectivityDirection.Ingress)
            {
                ingress.Add(rule);
            }
            else
            {
                egress.Add(rule);
            }
        }

        // L7 authorization sources (inbound identity) for Istio AuthorizationPolicy.
        List<AuthzRule> authz = [];
        foreach (ConnectivityEdge e in graph.Ingress.Where(x => x.Source == ConnectivitySource.Inferred && x.IsEnabled))
        {
            authz.Add(new AuthzRule([IstioIngressNamespace], [], PodPort(e), e.PeerLabel));
        }
        foreach (ConnectivityRule r in declaredRules.Where(r => r.Direction == ConnectivityDirection.Ingress))
        {
            (List<string> Namespaces, List<string> IpBlocks)? src = TranslateAuthzSource(r, peerNs);
            if (src is null)
            {
                warnings.Add($"Ingress rule from “{DescribePeer(r)}” can’t be expressed as an Istio identity source.");
                continue;
            }
            authz.Add(new AuthzRule(src.Value.Namespaces, src.Value.IpBlocks, r.Port, DescribePeer(r)));
        }

        // The workload selector to target — the app's Service pod selector, when known.
        Dictionary<string, string>? workloadSelector = graph.ExposedPorts
            .Select(p => p.Selector)
            .FirstOrDefault(s => s is { Count: > 0 });

        // Per-cluster capability: does the mesh exist? (drives ServiceEntry egress + AuthorizationPolicy).
        List<Guid> targetClusterIds = targets.Select(t => t.Cluster.Id).Distinct().ToList();
        List<ClusterComponent> comps = await db.ClusterComponents
            .AsNoTracking()
            .Where(c => targetClusterIds.Contains(c.ClusterId))
            .ToListAsync(ct);

        // Other apps' deployments in the same clusters — used to warn that the
        // namespace-wide default-deny will also govern co-located apps.
        var coTenantDeployments = await db.AppDeployments
            .AsNoTracking()
            .Where(d => targetClusterIds.Contains(d.ClusterId) && d.AppId != appId)
            .Select(d => new { d.ClusterId, d.Namespace })
            .ToListAsync(ct);

        List<ConnectivityPolicyPlan> plans = [];
        foreach ((KubernetesCluster cluster, string ns) in targets)
        {
            bool hasIstio = comps.Any(c => c.ClusterId == cluster.Id
                && c.Status == ComponentStatus.Installed
                && ((c.Name?.Contains("istio", StringComparison.OrdinalIgnoreCase) ?? false)
                    || (c.HelmChartName?.Contains("istio", StringComparison.OrdinalIgnoreCase) ?? false)));

            ConnectivityPolicyPlan plan = new()
            {
                Namespace = ns,
                ClusterId = cluster.Id,
                ClusterName = cluster.Name,
                HasIstio = hasIstio,
                Warnings = warnings.Distinct().ToList()
            };

            // These NetworkPolicies target the whole namespace (podSelector: {}). Warn
            // if another app also runs there, since the default-deny will govern it too.
            if (coTenantDeployments.Any(d => d.ClusterId == cluster.Id
                    && string.Equals(d.Namespace, ns, StringComparison.OrdinalIgnoreCase)))
            {
                plan.Warnings.Add($"Namespace “{ns}” on {cluster.Name} also hosts other app deployment(s). These NetworkPolicies apply to the whole namespace (podSelector: {{}}), so the default-deny will also restrict those workloads.");
            }

            plan.Policies.Add(new GeneratedPolicy
            {
                Name = $"{prefix}-default-deny",
                Kind = "NetworkPolicy",
                Summary = "Default-deny baseline (all ingress + egress)",
                Yaml = BuildDefaultDeny($"{prefix}-default-deny", ns)
            });
            plan.Policies.Add(new GeneratedPolicy
            {
                Name = $"{prefix}-allow-dns",
                Kind = "NetworkPolicy",
                Summary = "Allow DNS egress (kube-system :53) — required or everything breaks",
                Yaml = BuildDnsAllow($"{prefix}-allow-dns", ns)
            });
            if (ingress.Count > 0)
            {
                plan.Policies.Add(new GeneratedPolicy
                {
                    Name = $"{prefix}-allow-ingress",
                    Kind = "NetworkPolicy",
                    Summary = $"Allow {ingress.Count} inbound path(s)",
                    Yaml = BuildAllowPolicy($"{prefix}-allow-ingress", ns, "Ingress", ingress)
                });
            }
            if (egress.Count > 0)
            {
                plan.Policies.Add(new GeneratedPolicy
                {
                    Name = $"{prefix}-allow-egress",
                    Kind = "NetworkPolicy",
                    Summary = $"Allow {egress.Count} outbound path(s)",
                    Yaml = BuildAllowPolicy($"{prefix}-allow-egress", ns, "Egress", egress)
                });
            }

            // L7 identity enforcement → Istio AuthorizationPolicy (inbound), only where a mesh exists.
            if (hasIstio && authz.Count > 0)
            {
                if (workloadSelector is { Count: > 0 })
                {
                    plan.Policies.Add(new GeneratedPolicy
                    {
                        Name = $"{prefix}-authz",
                        Kind = "AuthorizationPolicy",
                        Summary = $"L7 allow — {authz.Count} inbound identity rule(s)",
                        Yaml = BuildAuthorizationPolicy($"{prefix}-authz", ns, workloadSelector, authz)
                    });
                    plan.Notes.Add("AuthorizationPolicy enforces only on workloads in the mesh (Istio sidecar or ambient). If your pods are sidecar-less, enable injection/ambient for it to take effect.");
                }
                else
                {
                    // Without a workload selector an ALLOW policy would apply to — and
                    // lock down — every pod in the namespace. Skip it rather than misfire.
                    plan.Warnings.Add("L7 AuthorizationPolicy not generated: this app's Service has no pod selector, so an ALLOW policy would apply to (and lock down) every workload in the namespace. Add a Service with a selector to enable L7 authorization.");
                }
            }

            // Internet egress → Istio ServiceEntry when the mesh is present; otherwise it can't be enforced.
            if (externalDeps.Count > 0)
            {
                if (hasIstio)
                {
                    foreach (ExternalDependency d in externalDeps)
                    {
                        string seName = $"{prefix}-egress-{Sanitize(d.Host)}";
                        plan.Policies.Add(new GeneratedPolicy
                        {
                            Name = seName,
                            Kind = "ServiceEntry",
                            Summary = $"Allow egress to {d.Host}:{d.Port}",
                            Yaml = BuildServiceEntry(seName, ns, d)
                        });
                    }
                    plan.Notes.Add("ServiceEntry registers these external hosts with the mesh so egress can reach them. To actively BLOCK undeclared internet egress, the mesh must run outboundTrafficPolicy: REGISTRY_ONLY.");
                }
                else
                {
                    foreach (ExternalDependency d in externalDeps)
                    {
                        plan.Warnings.Add($"Internet egress to {d.Host}:{d.Port} can’t be enforced on {cluster.Name} — no Istio mesh detected (NetworkPolicy can’t match FQDNs).");
                    }
                }
            }

            plans.Add(plan);
        }

        return plans;
    }

    /// <summary>
    /// Apply the generated NetworkPolicy set to each (cluster, namespace) target
    /// for this app in the environment via kubectl. Returns one result per target.
    /// </summary>
    public async Task<List<(string Target, bool Success, string Output)>> ApplyNetworkPoliciesAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        List<ConnectivityPolicyPlan> plans = await GenerateNetworkPoliciesAsync(appId, environmentId, ct);
        if (plans.Count == 0)
        {
            return [("(no targets)", false, "No deployments found for this app in this environment.")];
        }

        // The plans already carry their (ClusterId, Namespace); load just the clusters
        // once (kubeconfig is materialized on load) rather than re-resolving targets.
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<Guid> clusterIds = plans.Select(p => p.ClusterId).Distinct().ToList();
        Dictionary<Guid, KubernetesCluster> clusters = await db.KubernetesClusters
            .Where(c => clusterIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        List<(string Target, bool Success, string Output)> results = [];
        foreach (ConnectivityPolicyPlan plan in plans)
        {
            if (!clusters.TryGetValue(plan.ClusterId, out KubernetesCluster? cluster))
            {
                continue;
            }
            string target = $"{cluster.Name}/{plan.Namespace}";
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                results.Add((target, false, "Cluster has no kubeconfig configured."));
                continue;
            }
            try
            {
                // Reuse the shared kubectl wrapper (handles temp files, HOME/cache, throws on failure).
                await k8sFactory.ApplyManifestAsync(plan.CombinedYaml, cluster.Kubeconfig, ct);
                logger.LogInformation("Connectivity policies applied to {Cluster}/{Namespace}", cluster.Name, plan.Namespace);
                results.Add((target, true, "applied"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply connectivity policies to {Cluster}/{Namespace}", cluster.Name, plan.Namespace);
                results.Add((target, false, ex.Message));
            }
        }

        return results.Count == 0
            ? [("(no targets)", false, "No applicable targets resolved.")]
            : results;
    }

    private static async Task<List<(KubernetesCluster Cluster, string Namespace)>> ResolveAppTargetsAsync(
        ApplicationDbContext db, Guid appId, Guid environmentId, CancellationToken ct)
    {
        string? locked = await db.AppEnvironments
            .Where(ae => ae.AppId == appId && ae.EnvironmentId == environmentId)
            .Select(ae => ae.Namespace)
            .FirstOrDefaultAsync(ct);

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .ToListAsync(ct);

        return deployments
            .Select(d => (Cluster: d.Cluster!, Namespace: string.IsNullOrWhiteSpace(locked) ? d.Namespace : locked!))
            .Where(t => t.Cluster is not null && !string.IsNullOrWhiteSpace(t.Namespace))
            .DistinctBy(t => (t.Cluster.Id, t.Namespace))
            .ToList();
    }

    private static async Task<Dictionary<Guid, List<string>>> ResolvePeerNamespacesAsync(
        ApplicationDbContext db, IEnumerable<Guid> peerAppIds, Guid environmentId, CancellationToken ct)
    {
        List<Guid> ids = peerAppIds.Distinct().ToList();
        Dictionary<Guid, List<string>> result = new();
        if (ids.Count == 0)
        {
            return result;
        }

        Dictionary<Guid, string?> locked = (await db.AppEnvironments
            .Where(ae => ids.Contains(ae.AppId) && ae.EnvironmentId == environmentId)
            .Select(ae => new { ae.AppId, ae.Namespace })
            .ToListAsync(ct))
            .ToDictionary(x => x.AppId, x => x.Namespace);

        var deps = await db.AppDeployments
            .Where(d => ids.Contains(d.AppId) && d.EnvironmentId == environmentId)
            .Select(d => new { d.AppId, d.Namespace })
            .ToListAsync(ct);

        foreach (Guid id in ids)
        {
            if (locked.TryGetValue(id, out string? ns) && !string.IsNullOrWhiteSpace(ns))
            {
                result[id] = [ns];
            }
            else
            {
                List<string> nss = deps.Where(d => d.AppId == id).Select(d => d.Namespace)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (nss.Count > 0)
                {
                    result[id] = nss;
                }
            }
        }
        return result;
    }

    private static List<NpPeer>? TranslatePeer(
        ConnectivityRule r, Dictionary<Guid, List<string>> peerNs, List<string> warnings)
    {
        switch (r.PeerType)
        {
            case ConnectivityPeerType.App:
                if (r.PeerAppId is Guid pid && peerNs.TryGetValue(pid, out List<string>? nss) && nss.Count > 0)
                {
                    return nss.Select(n => new NpPeer("ns", n, null)).ToList();
                }
                warnings.Add($"Rule to app peer can’t be enforced — the peer app has no namespace in this environment yet.");
                return null;

            case ConnectivityPeerType.Namespace:
                if (string.IsNullOrWhiteSpace(r.PeerNamespace))
                {
                    warnings.Add("A namespace rule with no namespace was skipped.");
                    return null;
                }
                return [new NpPeer("ns", r.PeerNamespace, null)];

            case ConnectivityPeerType.Cidr:
                if (string.IsNullOrWhiteSpace(r.PeerCidr))
                {
                    return null;
                }
                return [new NpPeer("ipblock", r.PeerCidr, null)];

            case ConnectivityPeerType.Selector:
                Dictionary<string, string>? labels = ParseSelector(r.PeerSelector);
                if (labels is null || labels.Count == 0)
                {
                    warnings.Add("A selector rule with an unparseable selector was skipped.");
                    return null;
                }
                return [new NpPeer("pod", null, labels)];

            case ConnectivityPeerType.Ingress:
                return [new NpPeer("ns", IstioIngressNamespace, null)];

            default:
                return null;
        }
    }

    // ── Input validators for user-authored peer fields (defense against broken/injected YAML) ──

    private static readonly System.Text.RegularExpressions.Regex DnsLabel =
        new("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LabelValue =
        new("^[A-Za-z0-9]([-A-Za-z0-9_.]*[A-Za-z0-9])?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LabelKey =
        new("^[A-Za-z0-9]([-A-Za-z0-9_./]*[A-Za-z0-9])?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ExternalHost =
        new(@"^(\*\.)?([A-Za-z0-9]([-A-Za-z0-9]*[A-Za-z0-9])?\.)*[A-Za-z0-9]([-A-Za-z0-9]*[A-Za-z0-9])?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidDnsLabel(string s) => s.Length <= 63 && DnsLabel.IsMatch(s);
    private static bool IsValidLabelValue(string s) => s.Length == 0 || (s.Length <= 63 && LabelValue.IsMatch(s));
    private static bool IsValidLabelKey(string s) => s.Length is > 0 and <= 316 && LabelKey.IsMatch(s);
    private static bool IsValidExternalHost(string s) => s.Length <= 253 && ExternalHost.IsMatch(s);

    private static Dictionary<string, string>? ParseSelector(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    // ── YAML builders (string style consistent with AppGovernanceService) ──

    private static string BuildDefaultDeny(string name, string ns) => string.Join("\n",
        "apiVersion: networking.k8s.io/v1",
        "kind: NetworkPolicy",
        "metadata:",
        $"  name: {name}",
        $"  namespace: {ns}",
        "  labels:",
        "    app.kubernetes.io/managed-by: entkube",
        "    entkube.io/connectivity: baseline",
        "spec:",
        "  podSelector: {}",
        "  policyTypes:",
        "    - Ingress",
        "    - Egress");

    private static string BuildDnsAllow(string name, string ns) => string.Join("\n",
        "apiVersion: networking.k8s.io/v1",
        "kind: NetworkPolicy",
        "metadata:",
        $"  name: {name}",
        $"  namespace: {ns}",
        "  labels:",
        "    app.kubernetes.io/managed-by: entkube",
        "    entkube.io/connectivity: allow",
        "spec:",
        "  podSelector: {}",
        "  policyTypes:",
        "    - Egress",
        "  egress:",
        "    - to:",
        "        - namespaceSelector:",
        "            matchLabels:",
        "              kubernetes.io/metadata.name: kube-system",
        "      ports:",
        "        - protocol: UDP",
        "          port: 53",
        "        - protocol: TCP",
        "          port: 53");

    private static (List<string> Namespaces, List<string> IpBlocks)? TranslateAuthzSource(
        ConnectivityRule r, Dictionary<Guid, List<string>> peerNs)
    {
        switch (r.PeerType)
        {
            case ConnectivityPeerType.App:
                if (r.PeerAppId is Guid pid && peerNs.TryGetValue(pid, out List<string>? nss) && nss.Count > 0)
                {
                    return (nss, []);
                }
                return null;
            case ConnectivityPeerType.Namespace:
                return string.IsNullOrWhiteSpace(r.PeerNamespace) ? null : ([r.PeerNamespace], []);
            case ConnectivityPeerType.Ingress:
                return ([IstioIngressNamespace], []);
            case ConnectivityPeerType.Cidr:
                return string.IsNullOrWhiteSpace(r.PeerCidr) ? null : ([], [r.PeerCidr]);
            default:
                // Pod selectors aren't a source identity in AuthorizationPolicy.
                return null;
        }
    }

    private static string BuildAuthorizationPolicy(
        string name, string ns, Dictionary<string, string>? selector, List<AuthzRule> rules)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine("apiVersion: security.istio.io/v1");
        sb.AppendLine("kind: AuthorizationPolicy");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    app.kubernetes.io/managed-by: entkube");
        sb.AppendLine("    entkube.io/connectivity: authz");
        sb.AppendLine("spec:");
        if (selector is { Count: > 0 })
        {
            sb.AppendLine("  selector:");
            sb.AppendLine("    matchLabels:");
            foreach ((string k, string v) in selector)
            {
                sb.AppendLine($"      {k}: {v}");
            }
        }
        sb.AppendLine("  action: ALLOW");
        sb.AppendLine("  rules:");
        foreach (AuthzRule rule in rules)
        {
            sb.AppendLine($"    # {rule.Label}");
            sb.AppendLine("    - from:");
            sb.AppendLine("        - source:");
            if (rule.Namespaces.Count > 0)
            {
                sb.AppendLine("            namespaces:");
                foreach (string n in rule.Namespaces)
                {
                    sb.AppendLine($"              - {n}");
                }
            }
            if (rule.IpBlocks.Count > 0)
            {
                sb.AppendLine("            ipBlocks:");
                foreach (string b in rule.IpBlocks)
                {
                    sb.AppendLine($"              - {b}");
                }
            }
            if (rule.Port is int port)
            {
                sb.AppendLine("      to:");
                sb.AppendLine("        - operation:");
                sb.AppendLine("            ports:");
                sb.AppendLine($"              - \"{port}\"");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildServiceEntry(string name, string ns, ExternalDependency d)
    {
        bool wildcard = d.Host.StartsWith('*');
        string proto = d.Tls || d.Port == 443 ? "TLS" : d.Port == 80 ? "HTTP" : "TCP";
        string resolution = wildcard ? "NONE" : "DNS";
        return string.Join("\n",
            "apiVersion: networking.istio.io/v1",
            "kind: ServiceEntry",
            "metadata:",
            $"  name: {name}",
            $"  namespace: {ns}",
            "  labels:",
            "    app.kubernetes.io/managed-by: entkube",
            "    entkube.io/connectivity: egress",
            "spec:",
            "  hosts:",
            $"    - {d.Host}",
            "  exportTo:",
            "    - \".\"",
            "  location: MESH_EXTERNAL",
            $"  resolution: {resolution}",
            "  ports:",
            $"    - number: {d.Port}",
            $"      name: {proto.ToLowerInvariant()}-{d.Port}",
            $"      protocol: {proto}");
    }

    private static string BuildAllowPolicy(string name, string ns, string direction, List<NpRule> rules)
    {
        string keyword = direction == "Ingress" ? "from" : "to";
        System.Text.StringBuilder sb = new();
        sb.AppendLine("apiVersion: networking.k8s.io/v1");
        sb.AppendLine("kind: NetworkPolicy");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    app.kubernetes.io/managed-by: entkube");
        sb.AppendLine("    entkube.io/connectivity: allow");
        sb.AppendLine("spec:");
        sb.AppendLine("  podSelector: {}");
        sb.AppendLine("  policyTypes:");
        sb.AppendLine($"    - {direction}");
        sb.AppendLine($"  {direction.ToLowerInvariant()}:");
        foreach (NpRule rule in rules)
        {
            sb.AppendLine($"    # {rule.Label}");
            sb.AppendLine($"    - {keyword}:");
            foreach (NpPeer peer in rule.Peers)
            {
                AppendPeer(sb, peer);
            }
            if (rule.Port is int port)
            {
                sb.AppendLine("      ports:");
                sb.AppendLine($"        - protocol: {rule.Protocol}");
                sb.AppendLine($"          port: {port}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendPeer(System.Text.StringBuilder sb, NpPeer peer)
    {
        switch (peer.Kind)
        {
            case "nsAny":
                sb.AppendLine("        - namespaceSelector: {}");
                break;
            case "ns":
                sb.AppendLine("        - namespaceSelector:");
                sb.AppendLine("            matchLabels:");
                sb.AppendLine($"              kubernetes.io/metadata.name: {peer.Value}");
                break;
            case "ipblock":
                sb.AppendLine("        - ipBlock:");
                sb.AppendLine($"            cidr: {peer.Value}");
                break;
            case "pod":
                sb.AppendLine("        - podSelector:");
                sb.AppendLine("            matchLabels:");
                foreach ((string k, string v) in peer.Labels ?? [])
                {
                    sb.AppendLine($"              {k}: {v}");
                }
                break;
        }
    }

    private static string Sanitize(string name)
    {
        string s = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        if (s.Length == 0 || !char.IsLetter(s[0]))
        {
            s = "app-" + s;
        }
        return s.Length > 50 ? s[..50].Trim('-') : s;
    }

    private sealed record NpPeer(string Kind, string? Value, Dictionary<string, string>? Labels);
    private sealed record NpRule(List<NpPeer> Peers, int? Port, string Protocol, string Label);
    private sealed record AuthzRule(List<string> Namespaces, List<string> IpBlocks, int? Port, string Label);

    /// <summary>Parse the ports out of a Kubernetes Service manifest's YAML.</summary>
    private List<ServicePortNode> ParseServicePorts(DeploymentManifest manifest, string ns)
    {
        List<ServicePortNode> result = [];
        try
        {
            ServiceDoc? doc = YamlDeserializer.Deserialize<ServiceDoc>(manifest.YamlContent);
            if (doc?.spec?.ports is null)
            {
                return result;
            }

            foreach (ServicePortYaml port in doc.spec.ports)
            {
                string protocol = string.IsNullOrEmpty(port.protocol) ? "TCP" : port.protocol.ToUpperInvariant();
                result.Add(new ServicePortNode
                {
                    ServiceName = doc.metadata?.name ?? manifest.Name,
                    Namespace = doc.metadata?.@namespace ?? ns,
                    Port = port.port,
                    TargetPort = int.TryParse(port.targetPort?.ToString(), out int tp) ? tp : null,
                    Protocol = protocol,
                    PortName = port.name,
                    AppProtocol = port.appProtocol ?? InferAppProtocol(port.name),
                    Selector = doc.spec.selector,
                    Source = ConnectivitySource.Inferred
                });
            }
        }
        catch (Exception ex)
        {
            // A malformed manifest shouldn't sink the whole graph — note it and move on.
            logger.LogDebug(ex, "Failed to parse Service manifest {Name} for connectivity ports", manifest.Name);
        }

        return result;
    }

    private static string? InferAppProtocol(string? portName) => portName?.ToLowerInvariant() switch
    {
        "http" or "http2" or "web" => "http",
        "https" => "https",
        "grpc" => "grpc",
        _ => null
    };

    private static string DescribePeer(ConnectivityRule rule) => rule.PeerType switch
    {
        ConnectivityPeerType.App => rule.PeerApp?.Name ?? "App",
        ConnectivityPeerType.Namespace => $"ns/{rule.PeerNamespace}",
        ConnectivityPeerType.Selector => rule.PeerSelector ?? "selector",
        ConnectivityPeerType.Cidr => rule.PeerCidr ?? "CIDR",
        ConnectivityPeerType.Ingress => "Ingress gateway",
        ConnectivityPeerType.External => rule.PeerNamespace ?? "External host",
        _ => "peer"
    };

    private static string PeerIcon(ConnectivityPeerType type) => type switch
    {
        ConnectivityPeerType.App => "bi-box",
        ConnectivityPeerType.Namespace => "bi-diagram-2",
        ConnectivityPeerType.Selector => "bi-tags",
        ConnectivityPeerType.Cidr => "bi-hdd-network",
        ConnectivityPeerType.Ingress => "bi-globe",
        ConnectivityPeerType.External => "bi-cloud-arrow-up",
        _ => "bi-question-circle"
    };

    // ── Minimal YAML shapes for a Service manifest ──
    private sealed class ServiceDoc
    {
        public ServiceMeta? metadata { get; set; }
        public ServiceSpec? spec { get; set; }
    }

    private sealed class ServiceMeta
    {
        public string? name { get; set; }
        public string? @namespace { get; set; }
    }

    private sealed class ServiceSpec
    {
        public List<ServicePortYaml>? ports { get; set; }
        public Dictionary<string, string>? selector { get; set; }
    }

    private sealed class ServicePortYaml
    {
        public string? name { get; set; }
        public int port { get; set; }
        public object? targetPort { get; set; }
        public string? protocol { get; set; }
        public string? appProtocol { get; set; }
    }
}

// ── View models (in-memory graph consumed by the Connectivity tab) ──

/// <summary>An app's connectivity graph for one environment.</summary>
public class ConnectivityGraph
{
    public List<string> Namespaces { get; set; } = [];

    /// <summary>Ports this app exposes (Service objects).</summary>
    public List<ServicePortNode> ExposedPorts { get; set; } = [];

    /// <summary>Who reaches this app (north-south + declared ingress).</summary>
    public List<ConnectivityEdge> Ingress { get; set; } = [];

    /// <summary>What this app reaches inside the cluster (apps + managed services).</summary>
    public List<ConnectivityEdge> InternalEgress { get; set; } = [];

    /// <summary>What this app reaches outside the cluster (internet).</summary>
    public List<ConnectivityEdge> ExternalEgress { get; set; } = [];

    public bool IsEmpty =>
        ExposedPorts.Count == 0 && Ingress.Count == 0 &&
        InternalEgress.Count == 0 && ExternalEgress.Count == 0;
}

/// <summary>A single exposed port node.</summary>
public class ServicePortNode
{
    public required string ServiceName { get; set; }
    public string? Namespace { get; set; }
    public int Port { get; set; }
    public int? TargetPort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string? PortName { get; set; }
    public string? AppProtocol { get; set; }

    /// <summary>The Service's pod selector — used to target the workload in an Istio AuthorizationPolicy.</summary>
    public Dictionary<string, string>? Selector { get; set; }

    public ConnectivitySource Source { get; set; }
}

/// <summary>The generated enforcement set (NetworkPolicy + Istio ServiceEntry) for a single target.</summary>
public class ConnectivityPolicyPlan
{
    public required string Namespace { get; set; }
    public Guid ClusterId { get; set; }
    public string ClusterName { get; set; } = "";

    /// <summary>Whether an Istio mesh was detected on this cluster (drives ServiceEntry egress).</summary>
    public bool HasIstio { get; set; }

    public List<GeneratedPolicy> Policies { get; set; } = [];

    /// <summary>Things that can't be enforced here (FQDN egress with no mesh, unresolvable peers).</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Informational caveats about enforcement (e.g. REGISTRY_ONLY needed to actually block).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>All docs joined into one multi-doc manifest for preview/apply.</summary>
    public string CombinedYaml => string.Join("\n---\n", Policies.Select(p => p.Yaml));
}

/// <summary>One generated Kubernetes policy document.</summary>
public class GeneratedPolicy
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string Yaml { get; set; }
    public string? Summary { get; set; }
}

/// <summary>Severity of an analyzer finding, ordered most-serious first.</summary>
public enum FindingSeverity
{
    /// <summary>Will break traffic once applied.</summary>
    Error,

    /// <summary>Likely a problem, or not enforceable as-is.</summary>
    Warning,

    /// <summary>Least-privilege / hardening suggestion.</summary>
    Info
}

/// <summary>A single predicted issue with the connectivity + policy configuration.</summary>
public record ConnectivityFinding(
    FindingSeverity Severity,
    string Category,
    string Title,
    string Detail,
    string? SuggestedFix = null,
    string? RelatedLabel = null);

/// <summary>A directed edge (ingress or egress) rendered on the topology view.</summary>
public class ConnectivityEdge
{
    public required string PeerLabel { get; set; }
    public string PeerKind { get; set; } = "";
    public string Icon { get; set; } = "bi-arrow-left-right";
    public string? Target { get; set; }
    public int? Port { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string? AppProtocol { get; set; }
    public ConnectivitySource Source { get; set; }
    public string? Detail { get; set; }
    public bool IsEnabled { get; set; } = true;
}
