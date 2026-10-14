using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How widely a cluster's kubeconfig travels through the code, as a number that may only
/// come down.
///
/// <para><b>What this found.</b> Every one of the eighteen methods on
/// <c>IKubernetesClientFactory</c> takes a <c>string kubeconfig</c>. So any code that wants
/// to apply a manifest, read a pod or create a namespace must first load the cluster row and
/// pull the credential out of it — and 534 places do. That is why
/// <c>KubernetesCluster</c> is read by 31 services across module boundaries: not because they
/// want a cluster's name, but because the client factory's signature makes every caller a
/// custodian of its credentials.</para>
///
/// <para><b>Why it matters twice over.</b> As security, a credential that passes through
/// twelve modules has twelve places it can be logged, cached or mishandled. As architecture,
/// no module can stop depending on Fleet's cluster table while it needs the kubeconfig out of
/// it — so this is also a hard blocker on the decomposition, and one
/// <c>docs/decomposition.md</c> did not account for when it was written.</para>
///
/// <para><b>The fix is not in this test.</b> It is to give the factory methods a cluster id
/// and let them resolve the credential themselves, inside Fleet, so it never reaches a
/// caller. That is a 534-site change and belongs in its own piece of work. Until then this
/// stops the number growing, which is the cheap half of the job.</para>
/// </summary>
public class ClusterCredentialCustodyTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Places that take a cluster's credential out of the row, measured 2026-10-07.
    ///
    /// <para><b>223 and falling</b>, from 508 once the count was corrected (below):
    /// <c>RedisService</c> 9, <c>CnpgService</c> 48, <c>ElasticsearchService</c> 42,
    /// <c>RabbitMQService</c> 37, <c>MongoService</c> 47, then <c>KafkaService</c> 15 and
    /// <c>RegisteredPostgresService</c> 16. None of those seven injects
    /// <see cref="IKubernetesClientFactory"/> any more, which is the clearest evidence a service
    /// is done: it cannot reach the raw-credential API even if a later change wanted to.</para>
    ///
    /// <para><c>TrustBundleService</c> and <c>CertificateDistributionService</c> each kept a local
    /// copy of kubectl plumbing purely because <c>ApplyManifestAsync</c> discarded kubectl's
    /// output and they log it. The factory now returns it, so both copies are gone. Their
    /// remaining sites are the change gate's: <c>PlannedClusterChange</c> <em>requires</em> a
    /// kubeconfig, so every gated call site must hold one. Fixing that is the next unit and needs
    /// a diff capability on the seam — the gate shells out to <c>kubectl diff</c>, which is not
    /// among the factory's eighteen methods.</para>
    ///
    /// <para>By module: Fleet 86 (unremarkable — it owns clusters, and
    /// <c>KubernetesOperationsService</c> alone is 56), Catalog 52, Telemetry 28, Delivery 26,
    /// Connectivity 23, DataServices 18, then single figures elsewhere.</para>
    ///
    /// <para><b>What the remaining sites actually do</b>, classified per site rather than per file —
    /// two earlier attempts to plan this inferred a site's purpose from its file's contents and were
    /// wrong both times. Of the 275 measured a day earlier: a <b>reachability guard</b> 96
    /// (<c>IsNullOrWhiteSpace(cluster.Kubeconfig)</c>, which is not use of the credential at all —
    /// <c>ForAsync</c> returning null already answers that question), handed to <b>one of the
    /// service's own helpers</b> 70, <b>building an SDK client</b> 31, a <b>local assignment</b> 16,
    /// the <b>gate's model</b> 16, and an actual <b>factory call</b> 13.</para>
    ///
    /// <para>The sixteen that went next were the first two categories meeting in one place: the
    /// routing, gateway, L4 and mesh paths of <c>KubernetesOperationsService</c> shared eight
    /// private helpers that each took a <c>string kubeconfig</c> only because their callers had
    /// one — <c>ApplyRawYamlAsync</c> alone was the funnel for ten applies. Converting the helpers
    /// rather than the public methods moved every one of their callers at once, and turned each
    /// caller's reachability guard into the resolution that answers it. See
    /// <c>RoutingClusterCallTests</c>, which is also the first coverage this service has had of
    /// what it sends a cluster: the kubectl invocation used to sit behind a <c>private static</c>
    /// method, so the seam and the coverage had to arrive together.</para>
    ///
    /// <para>So the bulk is not exotic transports. It is guards that become a resolution, and
    /// private helpers whose signatures take a credential because their callers had one. Both are
    /// the ordinary conversion this work has been doing.</para>
    ///
    /// <para><b>Helm is on the seam now, and it turned out to need two operations rather than a
    /// general passthrough — because five of the eight invocations do not touch a cluster at all.</b>
    /// <c>helm repo add</c>, <c>repo update</c> and <c>registry login</c> fetch an index over HTTP
    /// and write a local cache; two of them were being handed <c>--kubeconfig</c> anyway, which is
    /// why every census of this counted them. Checked rather than reasoned about: both succeed
    /// with a malformed kubeconfig and with a path that does not exist.</para>
    ///
    /// <para><b>helm is 8 invocations in 2 files</b>, not the 97 an earlier count suggested — that
    /// figure was credential sites in files that merely mention helm. The two files are
    /// <c>KubernetesOperationsService</c> and <c>ComponentLifecycleService</c>, which between them
    /// hold <b>72 of the 259</b> and use the SDK, their own process spawning and helm. That one
    /// file, now 56, is still the centre of gravity of everything left: the YAML-deployment path
    /// (apply, prune, delete-from-cluster), the four helm invocations, and eighteen remaining
    /// places that build an SDK client of their own.</para>
    ///
    /// <para><b>The change gate comes last, not next.</b> <c>PlannedClusterChange</c> requires a
    /// kubeconfig, so every gated call site holds one — but the gate is invoked from inside
    /// <see cref="IKubernetesClientFactory"/>, and not one of its eighteen methods receives a
    /// cluster identity to put there instead. Moving the gating up into the seam early would let
    /// every unconverted caller bypass the acknowledgment entirely, which is a safety regression
    /// rather than a refactor. The gate can only change once its callers already route through
    /// the seam.</para>
    ///
    /// <para><b>211, after five services that already had a tenant.</b> <c>VeleroService</c>,
    /// <c>DriftAdoptionService</c>, <c>StalwartDnsService</c> and <c>OpenLdapService</c> reached
    /// zero; <c>KyvernoPolicyService</c> went 9 to 6, keeping only its helm and own-process paths.
    /// One was more than custody: <c>VeleroService.ResolveKubeconfigAsync</c> was a private copy
    /// of <c>ForAsync</c>'s own query, same predicate and all, returning the credential instead of
    /// a client.</para>
    ///
    /// <para><b>None of the five closed a cross-tenant hole</b>, and it is worth saying so rather
    /// than letting a reader assume otherwise. <c>StalwartDnsService</c> looked like one — it
    /// fetched its cluster by id alone — but the component supplying that id was itself fetched
    /// with <c>c.Cluster.TenantId == tenantId</c> three lines above, so nothing was reachable. The
    /// seam makes the scoping local instead of inherited, which is worth having and is not a
    /// vulnerability fixed.</para>
    ///
    /// <para><b>200, and the reachable bucket is now empty of conversions.</b>
    /// <c>StalwartService</c> (5), <c>KeycloakService</c> (2), <c>StorageService</c>'s egress
    /// relay (2) and <c>IngressDashboardService</c> (2) went, which leaves
    /// <see cref="CustodyTriageTests"/> at three — two of them <c>ClusterClient</c>'s own and one
    /// a <em>write</em>. So every one of the 197 that remain is there because its method has no
    /// tenant, not because nobody has got to it.</para>
    ///
    /// <para><b>⚠️ This number was 202 on main for one merge.</b> Two PRs lowered it
    /// independently, their conflict was resolved by taking one side, and the result claimed 202
    /// against a tree measuring 200. Nothing was lost — both sets of conversions landed — but the
    /// baseline was a fiction for as long as it stood, and this test is the only reason anyone
    /// knew. <b>A ratchet constant has no correct merge resolution: it has to be re-measured.</b>
    /// See <see cref="The_custody_baseline_does_not_carry_slack"/>, which is the half of the
    /// ratchet that exists for exactly this and nothing else.</para>
    ///
    /// <para><b>183 — the first fall after the sweep decision, and the largest single one.</b>
    /// <c>HeadscaleService</c> went 17 → 0. Not because anything new was built: the decision
    /// (docs/decomposition.md §4.0.1.2) is that a background sweep acts as <em>each tenant in
    /// turn</em>, so <c>HeadscaleCertSyncService</c> now loops tenants and
    /// <c>HeadscaleService</c>'s 29 public methods take the tenant its callers always had. Its
    /// <c>GetComponentAsync</c> funnel — thirteen internal callers — asks Catalog tenant-first,
    /// and the credential comes from the seam.</para>
    ///
    /// <para>That is what the 197 were waiting for. They were never a refactor; they were this
    /// question.</para>
    ///
    /// <para><b>181 — two of <c>PrometheusService</c>'s twenty, and deliberately only two.</b>
    /// Converting all seventeen of its public methods at once was attempted and abandoned: each
    /// signature threaded surfaced callers that had not been counted, and the error count rose as
    /// often as it fell. So the service now has <em>two</em> resolution paths — the old
    /// <c>ResolvedPrometheusInfo</c> (a kubeconfig string, reached by cluster id alone, still used
    /// by fifteen methods) and <c>ReachablePrometheus</c> (an <c>IClusterClient</c>, tenant-first)
    /// — and methods move across one at a time, each step compiling and tested.</para>
    ///
    /// <para>When the last caller of the old path goes, it and the <c>clientPool</c> field go with
    /// it, and this number drops by the remaining eighteen in one step.</para>
    ///
    /// <para><b>179 — step 2, the aggregate pair.</b> <c>GetAppMetricsAsync</c> and
    /// <c>GetCustomerMetricsAsync</c> both fan out over several clusters through
    /// <c>AggregateMetricsAsync</c>, whose tuple carried a <c>string Kubeconfig</c> — so every
    /// producer of that tuple had to read one. It carries a cluster id now and the aggregate
    /// resolves each through the seam, which is why two sites went for two methods.</para>
    ///
    /// <para>An unreachable cluster is <em>skipped</em> there rather than failing the whole
    /// answer, because the aggregate sums across a tenant's clusters — but <c>anySuccess</c> still
    /// decides whether the result is a failure, so one that cannot be reached cannot masquerade
    /// as a zero.</para>
    ///
    /// <para><c>PrometheusService</c> is at 16 of its original 20. Thirteen methods remain on the
    /// old by-cluster-id path; each moves with its callers, one step at a time.</para>
    ///
    /// <para><b>176 — step 3, the two monitoring panels.</b>
    /// <c>GetRabbitMQMetricsAsync</c>, <c>GetKeycloakMetricsAsync</c> and the
    /// <c>DiagnoseServiceMonitorScrapeAsync</c> they share. <c>PrometheusService</c> is at 13 of
    /// its original 20, with nine callers of the old by-cluster-id resolution left.</para>
    ///
    /// <para>Three steps in, the rate is steady and so is the method: 183 → 181 → 179 → 176,
    /// each step compiling and tested, after one attempt at all seventeen methods at once that never
    /// compiled at all.</para>
    ///
    /// <para><b>169 — step 4, the ClusterMonitoring subtree.</b> Seven methods at once, which is
    /// more than the earlier steps took, because they share exactly one plumbing act: giving
    /// <c>ClusterMonitoring</c> a <c>TenantId</c> from <c>ClusterDetail</c>, which then reaches
    /// <c>AlertPanel</c>, <c>AlertRulesPanel</c> and <c>ScrapeTargetsPanel</c>. Splitting them
    /// would have meant adding a parameter in one commit and using it in another.</para>
    ///
    /// <para><c>PrometheusService</c> is at 6 of its original 20 and has <b>two</b> callers of the
    /// old by-cluster-id resolution left — <c>GetDeploymentMetricsAsync</c> and the
    /// <c>GetMetricRangeAsync</c>/<c>GetLabelValuesAsync</c> pair behind <c>IMetricsQuery</c>. When
    /// those go, the old record and the <c>clientPool</c> field go with them.</para>
    ///
    /// <para><c>ClusterHealthPanel.razor</c> deleted — 225 lines, rendered by nothing, and one of
    /// <c>GetClusterHealthAsync</c>'s four callers.</para>
    ///
    /// <para><b>167 — step 5, the deployment metrics.</b> <c>GetDeploymentMetricsAsync</c> used to
    /// take a deployment id and nothing else: it loaded that deployment's cluster by the id the
    /// row carried and read the kubeconfig off it, so the id was the entire authorization. The id
    /// is now an input — the cluster id goes into <c>ForAsync</c>, which is what compares it
    /// against the asking tenant.</para>
    ///
    /// <para>Its second caller, <c>RolloutService.MeasureAsync</c>, had no tenant, so
    /// <c>RolloutWatcherService</c> became the fourth sweep this programme has turned into a tenant
    /// loop — the ninth of the twenty-six that loops tenants at all: the due rollouts are
    /// selected per tenant through <c>Deployment.App.Customer.TenantId</c> rather than
    /// installation-wide. That also retired the lookup <c>MeasureAsync</c> did for its error
    /// budget, which asked the deployment to name the tenant that was about to authorize it.</para>
    ///
    /// <para><c>PrometheusService</c> is at 4 of its original 20, with <b>one</b> old-path caller
    /// left: the <c>GetMetricRangeAsync</c>/<c>GetLabelValuesAsync</c> pair behind
    /// <c>IMetricsQuery</c>. When it goes, so do <c>ResolvedPrometheusInfo</c>, the
    /// <c>string kubeconfig</c> overload of <c>WithServiceAsync</c>, and the four sites here.</para>
    ///
    /// <para><b>160 — step 6, and PrometheusService is at zero.</b> The range query and the label
    /// lookup were the last two on the old resolution and the widest-reaching: every dashboard
    /// panel, the metrics explorer's dropdowns, the cost report, the resource-usage sweep and the
    /// ingress traffic sparkline. <c>IMetricsQuery</c>'s five methods all take a tenant now, which
    /// reached five pages; <c>ResourceUsageCollectorService</c> stopped flattening its
    /// <c>(tenant, cluster)</c> pairs to cluster ids, which is the move that turns a check back
    /// into a tautology.</para>
    ///
    /// <para>Then the old path went: <c>ResolvedPrometheusInfo</c>, <c>ResolvePrometheusInfoAsync</c>,
    /// the <c>string kubeconfig</c> overload of <c>WithServiceAsync</c>, <c>CreateK8sClient</c> and
    /// the <c>clientPool</c> field. Pooling is not lost — <c>ClusterClient</c> holds it now, and
    /// DI supplies the same singleton.</para>
    ///
    /// <para><c>CostReportService</c> came with it and left the list too: it had the tenant at the
    /// top of <c>GetTenantReportAsync</c> and dropped it through five private helpers, the last of
    /// which read a kubeconfig by cluster id to count load balancers. Those helpers are the
    /// "private funnel" shape this programme keeps finding — convert the funnel, not the public
    /// method.</para>
    ///
    /// <para><b>165 — and the rollback with it.</b> Giving the watcher a tenant made its two
    /// remaining sites <em>reachable</em> in <see cref="CustodyTriageTests"/>'s sense — a
    /// credential read in a method that now knows its tenant — so that ratchet failed, correctly:
    /// the conversion had created the easy case rather than taking it. The rollback moved onto a
    /// new seam verb, <c>UndoDeploymentsAsync</c>, and <c>RolloutWatcherService</c> left the
    /// file list.</para>
    ///
    /// <para><b>140 — the resource tree, and the gate stops needing a credential.</b>
    /// <c>KubernetesOperationsService</c> 46 → 26. Nine actions on <c>ResourceTreePanel</c> —
    /// scale, restart, delete, logs, containers, events, YAML, cronjob suspend — each took a
    /// deployment id and nothing else, loaded that deployment's cluster and read the kubeconfig
    /// off it. One <c>TenantId</c> on that panel reached all of them, the way
    /// <c>ClusterMonitoring</c>'s did in #182.</para>
    ///
    /// <para><b>What unblocked the mutating half.</b> These mutate through the k8s SDK, not
    /// through the seam, so the gate cannot be raised for them from inside the factory — they
    /// have to ask. And asking meant holding a kubeconfig, because
    /// <c>PlannedClusterChange.Kubeconfig</c> is <c>required</c>. So <c>PlannedChange</c> was
    /// added: everything about the operation and nothing about the credential, completed by the
    /// seam in <c>IClusterClient.AcknowledgeAsync</c>. The gate's invariant is untouched — a
    /// <c>PlannedClusterChange</c> still cannot exist without a credential — but a caller no
    /// longer needs one to ask for an acknowledgment. That requirement was the whole reason a
    /// dozen methods here read a cluster row at all.</para>
    ///
    /// <para><c>GetResourceYamlAsync</c> needed a new seam read instead (<c>kubectl get -o
    /// yaml</c>): <c>GetJsonAsync</c> lists a kind and returns JSON, and re-serialising JSON for
    /// a human to read reorders and requotes it. <c>TriggerCronJobAsync</c> is the one action
    /// left on the old prologue — <c>kubectl create job --from=cronjob/x</c> is a verb the seam
    /// does not have, the same situation <c>ChangeVerb.Rollback</c> was in.</para>
    ///
    /// <para><b>125 — the rest of the deployment-level reads, and three dead ends.</b>
    /// <c>KubernetesOperationsService</c> 26 → 11.</para>
    ///
    /// <para><b>Eight of the fifteen came from deleting code.</b> <c>DeletePodAsync</c>,
    /// <c>ScaleDeploymentAsync</c> and <c>GetPodsAsync</c> had no caller but a test each. The
    /// first two were superseded by the generic <c>DeleteResourceAsync</c> and
    /// <c>ScaleWorkloadAsync</c> that <c>ResourceTreePanel</c> actually calls — converted in #185
    /// — and the pod list by the telemetry backends. Their <c>PodInfo</c>/<c>ContainerInfo</c>
    /// DTOs stay: <c>StorageService</c>, <c>NodeManagementService</c> and <c>ClusterNodes</c> use
    /// them. Deleting a credential site beats converting one, and this is the third time the
    /// cheapest conversion in a file turned out to be a deletion.</para>
    ///
    /// <para>Converted: <c>GetLiveResourcesAsync</c> (the resource tree's own inventory, and the
    /// widest-reaching of the four), <c>RestartDeploymentAsync</c>, <c>ListNamespacesAsync</c>.
    /// The restart is the public API's endpoint — it already checked that the <em>app</em> belongs
    /// to the token's tenant, and the seam now checks the <em>cluster</em>'s, which is a different
    /// row, so a deployment pointing at someone else's cluster is refused rather than restarted.
    /// <c>ListNamespacesAsync</c> reads no cluster row at all now: the seam answers the same
    /// question the row lookup used to.</para>
    ///
    /// <para><c>DeploymentSyncService</c> is the fifth sweep turned into a tenant loop — it
    /// selected every deployment in the installation whose cluster had a kubeconfig, then read
    /// each one's live resources by id.</para>
    ///
    /// <para>What is left in the file is <b>11</b>: <c>TriggerCronJobAsync</c> (3, needs a seam
    /// verb for <c>kubectl create job --from=cronjob/x</c>), the two cluster-scoped route helpers
    /// <c>GetServicesInNamespaceAsync</c>/<c>GetEndpointsForServiceAsync</c> (4), and four
    /// one-site private helpers on the apply path.</para>
    ///
    /// <para><b>118 — CA trust and autoscalers, both files to zero.</b> These are the first two
    /// of the four that held <em>both</em> a tautological seam call and credential sites, which is
    /// why they were left until the two could move together: threading a tenant in makes the
    /// credential reads newly inexcusable in the same step.</para>
    ///
    /// <para><c>KedaScalerService</c>'s target resolution stopped returning
    /// <c>KubernetesCluster</c> rows. Including them materialized a kubeconfig through the vault
    /// interceptor for every target — on a <em>scan</em>, which only ever needed the cluster's
    /// name — and then put a credential within reach of every caller. It returns ids and names
    /// now, which is what the seam wants.</para>
    ///
    /// <para><c>TrustBundleService</c> needed a seam gap closed first: the trust Bundle CR is
    /// cluster-scoped, and <c>DeleteManifestAsync</c> always emitted <c>-n {ns}</c>, which kubectl
    /// rejects when empty. <c>ResourceExistsAsync</c> had read an empty <c>ns</c> as "omit the
    /// flag" all along and documented the convention; delete now does too. That gap was the whole
    /// reason this service kept its own kubectl with its own copy of the credential.</para>
    ///
    /// <para>⚠️ And the exemption predicted in <see cref="ClusterChangeGateCoverageTests"/> expired
    /// exactly as written: moving the acknowledgment onto <c>IClusterClient</c> made the file stop
    /// matching that metric's "acknowledges" test, so it arrived in the ungated list and the count
    /// went 12 → 13. The fix was not to widen the regex — the private kubectl runner had become
    /// <b>dead code</b>, 39 lines of it, so deleting it took the file out of the list honestly.</para>
    ///
    /// <para><b>111 — the certificate distributor, and the last of the four.</b> It writes a
    /// certificate and often its private key into namespaces across a tenant's clusters, and it
    /// did so by resolving targets into <c>KubernetesCluster</c> rows: a decrypted kubeconfig per
    /// target, passed on to the namespace listing, the acknowledgment and the apply, with the
    /// tenant each was checked against read off the row itself.</para>
    ///
    /// <para>It needed the same seam gap closed one method further along: listing Namespaces by
    /// label through <c>GetJsonAsync</c> needed an empty namespace to mean <em>cluster-scoped</em>,
    /// as <c>ResourceExistsAsync</c> and (since #191) the delete already do. Three methods now
    /// share one convention, and the names come out of JSON instead of <c>-o name</c> lines.</para>
    ///
    /// <para>Both this service and <c>TrustBundleService</c> also stopped taking
    /// <c>IClusterChangeGate</c> at all. They reach it through <c>IClusterClient</c>, which is the
    /// point of <c>PlannedChange</c>: a caller that mutates through kubectl can still raise the
    /// acknowledgment without ever holding a credential. Their private kubectl runners — 37 and
    /// 39 lines — were dead afterwards.</para>
    ///
    /// <para><b>105 — <c>ComponentLifecycleService</c> at zero, and it took the "cannot" with
    /// it.</b> This file held the install path: six credential sites and three tautological seam
    /// calls, and a kubeconfig written to disk for the whole duration of every helm run.</para>
    ///
    /// <para>Two of its reads were only ever guards — the subchart loops read the credential to
    /// check it existed, which is a question the seam answers for the right tenant. Two became
    /// seam calls that already existed: the image-pull Secret through <c>ReplaceSecretAsync</c>
    /// (not an apply — these Secrets carry no last-applied annotation, so an apply MERGES and a
    /// revoked registry credential would survive), and the load-balancer address through
    /// <c>GetJsonAsync</c>.</para>
    ///
    /// <para>The last two needed <b>new seam verbs</b>, and that is the finding: the file's own
    /// comment said the kubeconfig had to stay for "kubectl helpers the seam cannot express", and
    /// the gate-coverage baseline listed them among four that "genuinely cannot move". They were
    /// an apply from a URL and a log tail for one container of a multi-container pod — a missing
    /// parameter and a missing operation, not an impossibility. Third time this programme has
    /// found a "cannot" that was a gap (see also #191's cluster-scoped delete and #192's list).</para>
    ///
    /// <para><b>92 — node management, the largest single file left.</b> Thirteen sites, and
    /// twelve of them existed for one reason: a pair of <c>LoadClusterAsync</c> helpers that
    /// loaded the cluster row and checked its <c>Kubeconfig</c> column. "May this tenant hold
    /// that cluster's credential" and "does a credential exist" are the same question, and the
    /// seam answers both — no node operation here read anything else off the row.</para>
    ///
    /// <para>The thirteenth was <c>kubectl drain</c>, which needed the fourth new seam verb of
    /// this run. A drain is neither a patch nor a delete of one object: it respects
    /// PodDisruptionBudgets, waits for evictions, and has its own handling for DaemonSet pods and
    /// local storage — all of it in kubectl rather than the API. <c>--timeout=5m</c> is carried
    /// across deliberately, because a drain blocked by a budget otherwise waits forever inside a
    /// request.</para>
    ///
    /// <para>The acknowledgment it used to raise took the kubeconfig purely to satisfy
    /// <c>PlannedClusterChange</c>; it is a <c>PlannedChange</c> through the seam now, which is
    /// what let the row go entirely. Cordon, uncordon, drain, label and taint are all authorized
    /// as the asking tenant, from one <c>TenantId</c> on <c>ClusterNodes</c>.</para>
    ///
    /// <para><b>82 — the discovery scans.</b> <c>ComponentScanService</c> is what reads a
    /// cluster to find out what is installed on it: helm release secrets across every namespace,
    /// manifest-installed components by CRD presence, Gateway API CRDs, and the routes a
    /// component already exposes. Five methods, each taking a <c>KubernetesCluster</c> row and
    /// reading its kubeconfig; they take <c>(tenantId, clusterId)</c> now and the row is not
    /// needed at all, because the only other thing they read off it was its id.</para>
    ///
    /// <para>Two of the ten sites went with a method that had <b>no caller anywhere</b> —
    /// <c>CheckCertManagerGatewayApiAsync</c>, which checked for a cert-manager feature gate. The
    /// fourth dead method this programme has deleted rather than converted.</para>
    ///
    /// <para><c>ImportReleaseAsync</c> and its adoption path also stopped reading
    /// <c>cluster.TenantId</c> for the four tenant-scoped calls they make — Kyverno discovery,
    /// RabbitMQ discovery and two vault extractions. Those were not counted here, because a
    /// tenant read off a row is the <em>other</em> ratchet, but they are the same mistake and
    /// they went in the same change.</para>
    ///
    /// <para><b>72 — <c>KubernetesOperationsService</c> down to its last one.</b> Ten of the
    /// eleven that were left: the cronjob trigger, the two route-panel reads, the management
    /// toggle with its live-manifest refresh, and the Kyverno hook's guard.</para>
    ///
    /// <para>Two more seam verbs, both narrow: <c>CreateJobFromCronJobAsync</c> (the template
    /// lives on the cluster, so there is nothing for EntKube to apply and reconstructing it would
    /// be a guess at what the CronJob says) and <c>GetResourceJsonAsync</c>, the sibling of the
    /// YAML read for callers that parse rather than display.</para>
    ///
    /// <para>⚠️ <see cref="CustodyTriageTests"/> went 3 → 5 in the middle of this, because
    /// <c>GetEndpointsForServiceAsync</c> had its <em>signature</em> converted and its body left
    /// alone — a tenant parameter above two credential reads. Exactly what that ratchet is for,
    /// and the second time in this programme it has caught a half-finished method.</para>
    ///
    /// <para><b>The one left is the prune path</b>, and it is a design decision rather than a
    /// conversion. <c>DeletePrunedResourceAsync</c> maps cleanly onto the seam's delete — but the
    /// seam acknowledges each delete, and pruning is a consequence of an apply the operator has
    /// already acknowledged. Asking again, once per pruned resource, would be the opposite of
    /// what <c>ReplaceSecretAsync</c> exists to avoid. It needs a verb that asks once for the
    /// set, which deserves its own change rather than being buried in this one.</para>
    ///
    /// <summary>
    /// <b>70 in 25 files.</b> <c>DockerRegistryService</c> left both this list and
    /// <see cref="ClusterChangeGateCoverageTests"/>'s in one change, which is the shape §5.4
    /// predicted: its two credential reads existed only to write a kubeconfig to disk for a
    /// <c>kubectl apply -f</c> it ran itself. Through <c>EnsureNamespaceAsync</c> +
    /// <c>ReplaceSecretAsync</c> the credential never leaves the seam and the write is
    /// acknowledged — and its private process runner was deleted rather than moved, because
    /// nothing else in the file used it.
    ///
    /// <para>The tenant comes from the panel that triggers the sync rather than from the
    /// credential's own vault row. That distinction is the whole value: a tenant read off the row
    /// being acted on cannot decide whether this cluster may be reached.</para>
    /// </summary>
    /// <summary>
    /// <b>66 in 23 files.</b> <c>ReleaseVolumeGuard</c> and <c>ComponentUpgradeRunner</c> left
    /// together, because they were one holding: the runner read the kubeconfig in order to hand it
    /// to the guard, and the guard wrote it to disk for its own <c>kubectl</c>. The runner hands a
    /// client now and the guard takes one, so the credential never leaves the seam — and the guard
    /// stopped being invisible to the change gate at the same time
    /// (<see cref="ClusterChangeGateCoverageTests"/> 3 → 2).
    ///
    /// <para>Worth recording as a shape: <b>a caller that passes a credential to another service
    /// makes two files hold one credential.</b> Neither could be converted alone — converting the
    /// guard first would have left the runner reading a kubeconfig to pass nothing, and converting
    /// the runner first would have left the guard with no way to reach a cluster.</para>
    /// </summary>
    private const int BaselineOccurrences = 66;

    /// <summary>
    /// Files doing so. A file that has stopped should not be able to start again quietly.
    ///
    /// <para>36. <c>VaultService</c> left the list first, which was the first time a file had.
    /// Then <c>VeleroService</c>, <c>DriftAdoptionService</c>, <c>StalwartDnsService</c>,
    /// <c>OpenLdapService</c>, <c>StalwartService</c>, <c>KeycloakService</c> and
    /// <c>IngressDashboardService</c> — every one because it already had a tenant, which is the
    /// only thing that decides whether a conversion is possible at all.</para>
    ///
    /// <para><c>ClusterEgressRelay</c> left too, and differently: it took a kubeconfig as a
    /// parameter rather than reading one, so it never appeared in this count. It now takes an
    /// <c>IClusterClient</c>, which is what let <c>StorageService</c>'s relay path go.</para>
    ///
    /// <para>34. <c>RolloutWatcherService</c> stopped reading <c>deployment.Cluster.Kubeconfig</c>
    /// to hand to a rollback, so it no longer includes the cluster row at all.</para>
    ///
    /// <para>32. <c>PrometheusService</c> — which held twenty, the largest block in the whole
    /// count — and <c>CostReportService</c> with it.</para>
    ///
    /// <para>30. <c>KedaScalerService</c> and <c>TrustBundleService</c>.</para>
    ///
    /// <para>29. <c>CertificateDistributionService</c>.</para>
    ///
    /// <para>28. <c>ComponentLifecycleService</c> — the install path, and the last file in this
    /// count that also carried tautological seam calls.</para>
    ///
    /// <para>27. <c>NodeManagementService</c>.</para>
    ///
    /// <para>26. <c>ComponentScanService</c>.</para>
    /// </summary>
    private const int BaselineFiles = 23;

    private static (int Occurrences, int Files) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        int occurrences = 0, files = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                              || p.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            int n = CodeOccurrences(File.ReadAllText(path));

            if (n > 0)
            {
                occurrences += n;
                files++;
            }
        }

        return (occurrences, files);
    }

    /// <summary>
    /// Occurrences in code, ignoring comments and doc prose.
    ///
    /// <para>Nine of the 284 this used to report were sentences about the credential rather than
    /// uses of it — including one in a comment this very test's author had written, which had to
    /// be reworded to get the number down. A security metric that a paragraph can move is a
    /// metric nobody should trust, so prose is now excluded and the figure means what it says.</para>
    /// </summary>
    private static int CodeOccurrences(string text)
    {
        int n = 0;

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            n += Credential.Matches(line).Count;
        }

        return n;
    }

    /// <summary>
    /// The credential property itself, and not the things that merely look like it.
    ///
    /// <para>The lookahead excludes <c>.KubeconfigSecretId</c> and anything else that
    /// continues the identifier; the lookbehind excludes
    /// <c>VaultSecretType.Kubeconfig</c>, which names a kind of secret rather than holding
    /// one. Both were counted by the first version of this test.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Credential =
        new(@"(?<!VaultSecretType)\.Kubeconfig(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    [Fact]
    public void The_cluster_credential_does_not_spread_any_further()
    {
        (int occurrences, int files) = Measure();

        occurrences.Should().BeLessThanOrEqualTo(BaselineOccurrences,
            "a new place handling a raw kubeconfig is a new place it can be logged or cached, "
            + "and one more caller that cannot stop depending on Fleet's cluster table. Ask "
            + "the client factory to act on a cluster id instead, so the credential stays "
            + "inside Fleet");

        files.Should().BeLessThanOrEqualTo(BaselineFiles,
            "a file that did not handle cluster credentials should not start");
    }

    [Fact]
    public void The_custody_baseline_does_not_carry_slack()
    {
        (int occurrences, int files) = Measure();

        occurrences.Should().Be(BaselineOccurrences,
            $"this has been reduced to {occurrences} — bank it by lowering the baseline, or "
            + "the slack lets the credential spread back for free");

        files.Should().Be(BaselineFiles, $"now {files} files; lower the baseline to match");
    }
}
