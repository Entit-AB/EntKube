using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Authorization;
using EntKube.Web.Client.Pages;
using EntKube.Web.Components;
using EntKube.Web.Components.Account;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using StackExchange.Redis;

namespace EntKube.Web;

public class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddAuthenticationStateSerialization();

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        // The app runs behind the Caddy reverse proxy which terminates TLS. Honor the
        // X-Forwarded-Proto/For headers so the app knows the original request was HTTPS —
        // otherwise Kestrel sees plain HTTP and cookies with the default SameAsRequest policy
        // (the antiforgery cookie, auth cookies) never get the Secure attribute (finding L3).
        // This is the real fix: once the forwarded scheme is honored, those cookies become
        // Secure automatically. (We deliberately do NOT set antiforgery SecurePolicy = Always —
        // that makes the antiforgery system *throw* on any non-SSL request, which would break
        // internal/direct-HTTP traffic and health probes that render antiforgery-bearing pages.)
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The middleware only applies X-Forwarded-* when the immediate peer is a trusted
            // source. Kestrel is only reachable via the Caddy proxy (network-isolated), and the
            // proxy's IP isn't fixed (container networking), so trust all sources by adding the
            // 0.0.0.0/0 and ::/0 ranges. (Merely clearing the lists does NOT trust all — the peer
            // then matches nothing and the headers are ignored.) If Kestrel could ever be reached
            // directly, replace these with the proxy's specific subnet to prevent header spoofing.
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Any, 0));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.IPv6Any, 0));
        });

        // Force the antiforgery cookie to Secure (finding L3). The default SameAsRequest policy
        // was observed NOT to mark it Secure even when the forwarded scheme is HTTPS, so set it
        // explicitly. This is safe because every real request arrives via Caddy as HTTPS (see
        // ForwardedHeaders above); Always only throws for genuinely non-SSL requests, which the
        // network-isolated deployment does not serve. Keep health probes on a non-rendering
        // endpoint (they hit the pod over plain HTTP and must not trigger antiforgery).
        builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);

        // Strong HSTS: 1 year, all subdomains, preload-eligible — replaces the framework's
        // 30-day default with no includeSubDomains/preload (security finding L5).
        builder.Services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
            options.Preload = true;
        });

        // Persist DataProtection keys to a stable location so antiforgery/auth cookies
        // survive container restarts. Without this the keys live in the container's
        // ephemeral filesystem (/app/.aspnet/DataProtection-Keys) and are regenerated on
        // every restart, invalidating all existing cookies and forcing every user to
        // re-authenticate after each deploy. The path should be a mounted volume in
        // containerized deployments (e.g. mount a volume at /app/keys).
        string keyRingPath = builder.Configuration.GetValue<string>("DataProtection:KeyPath")
            ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
        Directory.CreateDirectory(keyRingPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName("EntKube");

        // The database provider is selected based on the "DatabaseProvider" config value.
        // Supported values: "Sqlite" (default for local dev), "Postgres", "SqlServer".
        // Each provider uses its own connection string and DbContext subclass so that
        // migrations are generated and applied independently per provider.

        string? configuredProvider = builder.Configuration.GetValue<string>("DatabaseProvider");
        string databaseProvider = configuredProvider ?? "Sqlite";
        string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        // Fail loudly on the silent Sqlite fallback in Production. appsettings.Production.json is
        // gitignored (it can carry secrets), so a clean CI-built image ships only appsettings.json —
        // whose DatabaseProvider default is "Sqlite". If a Production deployment neither bakes in
        // appsettings.Production.json nor injects the config via environment variables, the app would
        // otherwise start quietly against a throwaway Sqlite file at Data/app.db ("can't find its
        // database") instead of the real Postgres/SqlServer. Turn that into an immediate startup error.
        // The trigger is precise: DatabaseProvider unset *anywhere* (configuredProvider is null). Setting
        // DatabaseProvider=Sqlite explicitly opts into Sqlite in Production without tripping this.
        if (builder.Environment.IsProduction()
            && configuredProvider is null
            && string.Equals(databaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DatabaseProvider is not configured in the Production environment, so the app fell back to the " +
                "Sqlite dev default (DataSource=Data/app.db). This usually means appsettings.Production.json is " +
                "missing from the image AND no environment variables were injected. Supply the database settings via " +
                "environment variables (DatabaseProvider=Postgres and ConnectionStrings__DefaultConnection=\"Host=...\") " +
                "or a mounted appsettings.Production.json. To intentionally run Sqlite in Production, set " +
                "DatabaseProvider=Sqlite explicitly.");
        }

        // Transparently decrypts a registered cluster's kubeconfig from the vault when the
        // cluster is materialized, so consumers can keep reading cluster.Kubeconfig even
        // though it is no longer a database column. Registered as singletons and attached
        // to every DbContext below (both the derived contexts and the factory-created ones).
        builder.Services.AddSingleton<KubeconfigResolver>();
        builder.Services.AddSingleton<KubeconfigMaterializationInterceptor>();

        // Register both AddDbContext (for Identity/migrations — needs derived type mapping)
        // and a DbContext factory for Blazor Server concurrency safety.
        // A single scoped DbContext is NOT safe in Blazor Server because multiple
        // async operations (different components) can run concurrently on the same circuit.

        switch (databaseProvider)
        {
            case "Postgres":
                builder.Services.AddDbContext<ApplicationDbContext, PostgresApplicationDbContext>((sp, options) =>
                    options.UseNpgsql(connectionString)
                        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
                        .AddInterceptors(sp.GetRequiredService<KubeconfigMaterializationInterceptor>()));
                builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
                    sp => new DelegatingDbContextFactory(() =>
                    {
                        DbContextOptionsBuilder<PostgresApplicationDbContext> opts = new();
                        opts.UseNpgsql(connectionString)
                            .AddInterceptors(sp.GetRequiredService<KubeconfigMaterializationInterceptor>());
                        return new PostgresApplicationDbContext(opts.Options);
                    }));
                break;

            case "SqlServer":
                builder.Services.AddDbContext<ApplicationDbContext, SqlServerApplicationDbContext>((sp, options) =>
                    options.UseSqlServer(connectionString)
                        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
                        .AddInterceptors(sp.GetRequiredService<KubeconfigMaterializationInterceptor>()));
                builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
                    sp => new DelegatingDbContextFactory(() =>
                    {
                        DbContextOptionsBuilder<SqlServerApplicationDbContext> opts = new();
                        opts.UseSqlServer(connectionString)
                            .AddInterceptors(sp.GetRequiredService<KubeconfigMaterializationInterceptor>());
                        return new SqlServerApplicationDbContext(opts.Options);
                    }));
                break;

            default: // "Sqlite"
                builder.Services.AddDbContextFactory<ApplicationDbContext>((sp, options) =>
                    options.UseSqlite(connectionString)
                        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
                        .AddInterceptors(sp.GetRequiredService<KubeconfigMaterializationInterceptor>()));
                break;
        }

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = true;
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager<TrackingSignInManager>()
            .AddDefaultTokenProviders();

        // Live "online now" presence for the admin users page, fed by a
        // per-circuit handler. Defaults to a process-local in-memory backend;
        // set "Presence:Provider" to "Redis" (with ConnectionStrings:Redis) for
        // cross-instance presence in a multi-instance/HA deployment.
        string presenceProvider = builder.Configuration.GetValue<string>("Presence:Provider") ?? "InMemory";
        if (string.Equals(presenceProvider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            string redisConnection = builder.Configuration.GetConnectionString("Redis")
                ?? builder.Configuration.GetValue<string>("Presence:Redis:Configuration")
                ?? throw new InvalidOperationException(
                    "Presence:Provider is 'Redis' but no Redis connection string was found " +
                    "(set ConnectionStrings:Redis or Presence:Redis:Configuration).");
            builder.Services.AddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(redisConnection));
            builder.Services.AddSingleton<IPresenceTracker, RedisPresenceTracker>();
            builder.Services.AddHostedService<PresenceHeartbeatService>();
        }
        else
        {
            builder.Services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
        }
        builder.Services.AddScoped<CircuitHandler, PresenceCircuitHandler>();

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("HasTenantAccess", policy =>
                policy.Requirements.Add(new HasTenantAccessRequirement()));
        });
        builder.Services.AddScoped<IAuthorizationHandler, HasTenantAccessHandler>();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
        builder.Services.AddHttpClient();
        builder.Services.AddScoped<ToastService>();
        builder.Services.AddScoped<TenantService>();
        builder.Services.AddScoped<UserAccessService>();
        builder.Services.AddScoped<UserManagementService>();
        builder.Services.AddScoped<TenantRoleService>();

        // Vault encryption: the root key is loaded from configuration.
        // In production this should come from a secure source (env var, key vault, etc.)
        string rootKeyBase64 = builder.Configuration.GetValue<string>("Vault:RootKey")
            ?? throw new InvalidOperationException("Vault:RootKey must be configured (32-byte base64-encoded key).");
        byte[] rootKey = Convert.FromBase64String(rootKeyBase64);
        builder.Services.AddSingleton(new VaultEncryptionService(rootKey));
        builder.Services.AddSingleton<DeploymentStatusNotifier>();
        builder.Services.AddScoped<VaultService>();
        builder.Services.AddScoped<DockerRegistryService>();
        builder.Services.AddScoped<DeploymentService>();
        builder.Services.AddScoped<CustomerAccessService>();
        builder.Services.AddScoped<KubernetesOperationsService>();
        builder.Services.AddScoped<NodeManagementService>();
        builder.Services.AddScoped<PrometheusService>();
        // Telemetry engine: the self-built Lucene/S3 segment engine is the sole backend for logs, traces,
        // and RUM. OTLP/RUM writes go through SegmentTelemetryStore (no per-request DB connection — the
        // Postgres "too many clients" failure that motivated this cannot occur), and queries run over the
        // Lucene index unioned across the active index and the sealed segments on object storage. Metrics
        // are served by Prometheus (apps write there directly). There is no longer a Postgres telemetry
        // store or a per-cluster telemetry DB.
        SegmentEngineOptions segmentOptions = new()
        {
            DataPath = builder.Configuration.GetValue<string>("Telemetry:DataPath") ?? "/app/Data/telemetry",
            RollMaxDocs = builder.Configuration.GetValue<long?>("Telemetry:SegmentMaxDocs") ?? 1_000_000,
            RollMaxAge = TimeSpan.FromMinutes(builder.Configuration.GetValue<int?>("Telemetry:SegmentMaxAgeMinutes") ?? 60),
            RetentionDays = builder.Configuration.GetValue<int?>("Telemetry:RetentionDays") ?? 90,
        };
        builder.Services.AddSingleton(segmentOptions);
        // Per-tenant setting for which StorageLink backs a tenant's telemetry (edited in the tenant's
        // telemetry settings UI) + the per-tenant blob-store factory that resolves it.
        builder.Services.AddSingleton<TelemetryStorageSettingService>();
        builder.Services.AddSingleton<TenantBlobStoreFactory>();
        // Telemetry is TENANT-SCOPED: one segment manager per (tenant, signal), created lazily on first
        // ingest/query, each with its own active index, catalog partition, and object storage — no tenant's
        // logs/traces/RUM ever share a segment or a bucket with another's. The registries hold them.
        builder.Services.AddSingleton(sp => new SegmentManagerRegistry<LogSegmentManager>(tenantId =>
            new LogSegmentManager(tenantId,
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<TenantBlobStoreFactory>().CreateFor(tenantId),
                segmentOptions, sp.GetRequiredService<ILogger<LogSegmentManager>>())));
        builder.Services.AddSingleton(sp => new SegmentManagerRegistry<SpanSegmentManager>(tenantId =>
            new SpanSegmentManager(tenantId,
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<TenantBlobStoreFactory>().CreateFor(tenantId),
                segmentOptions, sp.GetRequiredService<ILogger<SpanSegmentManager>>())));
        builder.Services.AddSingleton(sp => new SegmentManagerRegistry<RumSegmentManager>(tenantId =>
            new RumSegmentManager(tenantId,
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<TenantBlobStoreFactory>().CreateFor(tenantId),
                segmentOptions, sp.GetRequiredService<ILogger<RumSegmentManager>>())));
        // Trace-summary index — pre-aggregated per-trace partials fed from span ingest for a fast trace list.
        builder.Services.AddSingleton(sp => new SegmentManagerRegistry<TraceSummarySegmentManager>(tenantId =>
            new TraceSummarySegmentManager(tenantId,
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<TenantBlobStoreFactory>().CreateFor(tenantId),
                segmentOptions, sp.GetRequiredService<ILogger<TraceSummarySegmentManager>>())));
        builder.Services.AddSingleton<ITelemetryIngest, SegmentTelemetryStore>();
        builder.Services.AddScoped<ILogBackend, SegmentLogService>();
        builder.Services.AddScoped<ITraceQueryService, SegmentTraceService>();
        builder.Services.AddScoped<IRumQueryService, SegmentRumService>();
        builder.Services.AddScoped<IMetricsQuery, PromMetricsService>();
        // One seal/retention loop per signal; each iterates that signal's live per-tenant managers.
        builder.Services.AddHostedService(sp => new SegmentSealService(
            sp.GetRequiredService<SegmentManagerRegistry<LogSegmentManager>>(), segmentOptions,
            sp.GetRequiredService<ILogger<SegmentSealService>>()));
        builder.Services.AddHostedService(sp => new SegmentSealService(
            sp.GetRequiredService<SegmentManagerRegistry<SpanSegmentManager>>(), segmentOptions,
            sp.GetRequiredService<ILogger<SegmentSealService>>()));
        builder.Services.AddHostedService(sp => new SegmentSealService(
            sp.GetRequiredService<SegmentManagerRegistry<RumSegmentManager>>(), segmentOptions,
            sp.GetRequiredService<ILogger<SegmentSealService>>()));
        builder.Services.AddHostedService(sp => new SegmentSealService(
            sp.GetRequiredService<SegmentManagerRegistry<TraceSummarySegmentManager>>(), segmentOptions,
            sp.GetRequiredService<ILogger<SegmentSealService>>()));
        builder.Services.AddSingleton<IngestTokenService>();
        builder.Services.AddSingleton<IngestRateLimiter>();
        // Real User Monitoring: resolves per-site public keys for the public browser ingest endpoint.
        builder.Services.AddSingleton<RumSiteService>();
        builder.Services.AddScoped<ClusterTenantResolver>();
        // Native telemetry alerting: rules evaluated over logs/spans → incidents via the existing pipeline.
        builder.Services.AddScoped<TelemetryAlertRuleService>();
        builder.Services.AddScoped<DashboardService>();
        builder.Services.AddScoped<IncidentDispatcher>();
        builder.Services.AddHostedService<TelemetryAlertEvaluator>();
        // Backend-agnostic log facade: routes each cluster to the segment engine (when it has data)
        // or Loki. The log viewers inject this instead of LokiService.
        builder.Services.AddScoped<LogQueryService>();
        builder.Services.AddScoped<ComponentLifecycleService>();
        builder.Services.AddScoped<ExternalRouteService>();
        builder.Services.AddScoped<AppRouteService>();
        builder.Services.AddScoped<AppL4RouteService>();
        builder.Services.AddScoped<ConnectivityGraphService>();
        builder.Services.AddScoped<IngressDashboardService>();
        builder.Services.AddScoped<DatabaseService>();
        builder.Services.AddScoped<CnpgService>();
        builder.Services.AddScoped<MongoService>();
        builder.Services.AddScoped<RegisteredPostgresService>();
        builder.Services.AddScoped<IKubernetesClientFactory, KubernetesClientFactory>();
        builder.Services.AddScoped<OpenStackKeystoneClient>();
        builder.Services.AddScoped<OpenStackS3Service>();
        builder.Services.AddScoped<OpenStackComputeService>();
        builder.Services.AddScoped<ClusterProvisioningService>();
        builder.Services.AddScoped<StorageService>();
        builder.Services.AddScoped<StorageLinkClientFactory>();
        builder.Services.AddScoped<StorageBrowserService>();
        builder.Services.AddScoped<ComponentScanService>();
        builder.Services.AddScoped<DeploymentImportService>();
        builder.Services.AddScoped<KeycloakService>();
        builder.Services.AddScoped<RabbitMQService>();
        builder.Services.AddScoped<RedisService>();
        builder.Services.AddScoped<KafkaService>();
        builder.Services.AddScoped<HarborService>();
        builder.Services.AddScoped<TailscaleService>();
        builder.Services.AddScoped<HeadscaleService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<IncidentService>();
        builder.Services.AddScoped<NotificationService>();
        builder.Services.AddScoped<NotificationProviderConfigService>();
        builder.Services.AddScoped<RemediationService>();
        builder.Services.AddScoped<OnCallService>();
        builder.Services.AddScoped<AlertRoutingService>();
        builder.Services.AddScoped<LokiService>();
        builder.Services.AddScoped<MimirService>();
        builder.Services.AddScoped<TempoService>();
        builder.Services.AddScoped<BackupService>();
        builder.Services.AddScoped<VpnService>();
        builder.Services.AddScoped<KyvernoPolicyService>();
        builder.Services.AddScoped<KedaScalerService>();
        builder.Services.AddScoped<SecretExpiryService>();
        builder.Services.AddScoped<IncidentCorrelationService>();
        builder.Services.AddScoped<StormSuppressionService>();
        builder.Services.AddScoped<ErrorBudgetService>();
        builder.Services.AddScoped<AdvisorStateService>();
        builder.Services.AddScoped<AdvisorDigestConfigService>();
        builder.Services.AddScoped<OperationsAdvisorService>();
        builder.Services.AddScoped<CustomerNotificationService>();

        builder.Services.AddScoped<ComponentInstallOrchestrator>();
        builder.Services.AddScoped<CatalogComponentRegistrar>();
        builder.Services.AddScoped<ClusterBlueprintService>();
        builder.Services.AddScoped<BlueprintFromClusterService>();
        builder.Services.AddScoped<AppGovernanceService>();
        builder.Services.AddScoped<GitOperationsService>();
        builder.Services.AddScoped<GitRepositoryService>();
        builder.Services.AddScoped<CustomerGitService>();
        builder.Services.AddScoped<AppOfAppsService>();
        builder.Services.AddSingleton<GitSyncService>();
        builder.Services.AddScoped<GitWebhookService>();
        builder.Services.AddHostedService<DeploymentSyncService>();
        builder.Services.AddHostedService<ExternalRouteHealthService>();
        builder.Services.AddHostedService<AppL4RouteHealthService>();
        builder.Services.AddHostedService<AlertSyncService>();
        builder.Services.AddHostedService<AlertEscalationService>();
        builder.Services.AddHostedService<UptimeTrackingService>();
        builder.Services.AddHostedService<KeycloakBackupSchedulerService>();
        builder.Services.AddHostedService<AdvisorScanService>();
        builder.Services.AddHostedService<ResourceUsageCollectorService>();
        builder.Services.AddHostedService<HeadscaleCertSyncService>();
        builder.Services.AddHostedService<SecretExpiryNotificationService>();
        builder.Services.AddHostedService<ObservedSecretRefreshService>();
        builder.Services.AddHostedService<MessagingStatusPollingService>();
        builder.Services.AddHostedService<BootstrapRunnerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<GitSyncService>());

        builder.Services.AddHttpClient("Notifications", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Dedicated client for external route health probes — short timeout,
        // follows redirects, ignores SSL cert errors for internal/self-signed endpoints.
        builder.Services.AddHttpClient("RouteHealth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

        // Headscale REST API client — each call sets a per-request BaseAddress and Bearer token.
        builder.Services.AddHttpClient("HeadscaleApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

        // Harbor API client: cookies must be disabled so Harbor's gorilla/csrf middleware
        // does not require a CSRF token (CSRF is only enforced when a session cookie is present).
        builder.Services.AddHttpClient("HarborApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseCookies = false
        });

        WebApplication app = builder.Build();

        // Apply pending migrations automatically on startup.
        // In a containerized or deployed environment the database may not be
        // immediately reachable, so we retry with exponential backoff.

        using (IServiceScope scope = app.Services.CreateScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            MigrateWithRetry(db, app.Logger);
            await EnsureAppEnvironmentNamespaceAsync(db, app.Logger);
            await EnsureDeploymentRouteClusterAppliedAtAsync(db, app.Logger);
            await EnsureDeploymentRouteRewritePathAsync(db, app.Logger);
            await EnsureNotificationChannelFiltersAsync(db, app.Logger);
            await EnsureRumSiteAppIdAsync(db, app.Logger);
            await EnsureClusterKubeconfigsMigratedAsync(db, scope.ServiceProvider, app.Logger);
            await EnsureImportedTlsSecretsAsCertificatesAsync(scope.ServiceProvider, app.Logger);

            RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleManager.RoleExistsAsync("Admin"))
                await roleManager.CreateAsync(new IdentityRole("Admin"));

            // If Seed:AdminEmail is set in config, ensure that user gets the Admin role.
            // Useful for bootstrapping a fresh environment or recovering access.
            string? seedAdminEmail = builder.Configuration.GetValue<string>("Seed:AdminEmail");
            if (!string.IsNullOrWhiteSpace(seedAdminEmail))
            {
                UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                ApplicationUser? adminUser = await userManager.FindByEmailAsync(seedAdminEmail);
                if (adminUser is not null && !await userManager.IsInRoleAsync(adminUser, "Admin"))
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                    app.Logger.LogInformation("Granted Admin role to seeded user {Email}.", seedAdminEmail);
                }
            }
        }

        // Configure the HTTP request pipeline.

        // Must run first: apply X-Forwarded-* from the reverse proxy so the correct scheme/host
        // is visible to HTTPS redirection, HSTS, and cookie Secure decisions downstream (L3).
        app.UseForwardedHeaders();

        // Global security response headers on every response (static assets, errors, and pages).
        // CSP keeps 'unsafe-inline' for scripts/styles because the head renders an inline
        // <script type="importmap"> (Blazor's <ImportMap/>) and the app uses inline style
        // attributes — a nonce-less strict script-src would break module loading. It still
        // restricts scripts to same-origin (blocking attacker-hosted script injection), forbids
        // objects, and locks base-uri/frame-ancestors/form-action (findings L4 + L6). Set
        // Security:EnableCsp=false or override Security:ContentSecurityPolicy to tune per-env.
        // CSP on by default in production; off in Development (avoids dotnet-watch hot-reload
        // friction). Set Security:EnableCsp explicitly to override either way.
        bool enableCsp = app.Configuration.GetValue<bool?>("Security:EnableCsp") ?? !app.Environment.IsDevelopment();
        string cspPolicy = app.Configuration["Security:ContentSecurityPolicy"] ?? string.Join("; ",
            "default-src 'self'",
            "base-uri 'self'",
            "object-src 'none'",
            "frame-ancestors 'self'",
            "frame-src 'self'",
            "form-action 'self'",
            "img-src 'self' data:",
            "font-src 'self' data:",
            "style-src 'self' 'unsafe-inline'",
            // blob: + worker-src for Monaco's web workers (self-hosted under wwwroot/lib/monaco-editor).
            "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' blob:",
            "worker-src 'self' blob:",
            "connect-src 'self'");

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary h = context.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                if (enableCsp) h["Content-Security-Policy"] = cspPolicy;
                return Task.CompletedTask;
            });
            await next();
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseWebAssemblyDebugging();
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<EntKube.Web.Components.App>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(Client._Imports).Assembly);

        // Add additional endpoints required by the Identity /Account Razor components.
        app.MapAdditionalIdentityEndpoints();

        // Backup download — streams a gzip-compressed JSON bundle of the full app state.
        // Requires the Admin role; the bundle contains plaintext secrets so access is restricted.
        app.MapGet("/api/admin/backup", async (BackupService backupService, HttpContext httpContext) =>
        {
            System.Security.Claims.ClaimsPrincipal user = httpContext.User;
            if (user.Identity?.IsAuthenticated != true || !user.IsInRole("Admin"))
                return Results.Forbid();

            string performedBy = user.Identity?.Name ?? user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown";
            byte[] data = await backupService.ExportAsync(performedBy);
            string filename = $"entkube-backup-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.json.gz";
            return Results.File(data, "application/gzip", filename);
        }).RequireAuthorization();

        // Backup restore — accepts a multipart form upload of a .json.gz (or plain .json) bundle.
        app.MapPost("/api/admin/restore", [Microsoft.AspNetCore.Mvc.RequestSizeLimit(50 * 1024 * 1024)]
        async (HttpRequest request, BackupService backupService, HttpContext httpContext) =>
        {
            System.Security.Claims.ClaimsPrincipal user = httpContext.User;
            if (user.Identity?.IsAuthenticated != true || !user.IsInRole("Admin"))
                return Results.Forbid();

            if (!request.HasFormContentType)
                return Results.Redirect("/admin/backup?error=Invalid+request+format.");

            IFormFile? bundle = request.Form.Files["bundle"];
            if (bundle is null || bundle.Length == 0)
                return Results.Redirect("/admin/backup?error=No+file+uploaded.");

            bool wipe = request.Form.TryGetValue("wipe", out Microsoft.Extensions.Primitives.StringValues wipeVal)
                && wipeVal == "true";

            try
            {
                await using Stream stream = bundle.OpenReadStream();
                await backupService.ImportAsync(stream, wipe);
                return Results.Redirect("/admin/backup?success=Restore+completed+successfully.");
            }
            catch (Exception ex)
            {
                // Walk the full exception chain so EF inner exceptions (constraint violations, etc.) are visible.
                var msg = new System.Text.StringBuilder();
                for (Exception? e = ex; e != null; e = e.InnerException)
                {
                    if (msg.Length > 0) msg.Append(" → ");
                    msg.Append(e.Message);
                }
                return Results.Redirect($"/admin/backup?error={Uri.EscapeDataString(msg.ToString())}");
            }
        }).RequireAuthorization().DisableAntiforgery();

        // Git webhook endpoint — receives push events from GitHub, Azure DevOps, etc.
        // Route: POST /api/git/webhook/{tenantSlug}
        // No authentication required (public endpoint); signature verification is
        // optional and done per-repo via the vault-stored WEBHOOK_SECRET.
        app.MapPost("/api/git/webhook/{tenantSlug}", async (
            string tenantSlug,
            HttpContext httpContext,
            GitWebhookService webhookService,
            CancellationToken ct) =>
        {
            using StreamReader reader = new(httpContext.Request.Body);
            string body = await reader.ReadToEndAsync(ct);
            string? signature = httpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();

            string result = await webhookService.HandleAsync(tenantSlug, body, signature, ct);
            return Results.Ok(result);
        });

        // OTLP/JSON ingest — the OpenTelemetry Collector's otlphttp exporter (encoding: json) pushes
        // logs to /v1/logs and traces to /v1/traces. Authenticated by a per-cluster HMAC ingest token
        // (Bearer / X-EntKube-Ingest-Key), NOT user identity; the token binds (tenantId, clusterId)
        // which are stamped onto every row. OtlpIngest.ReadAsync handles token/gzip/size-cap/parse and
        // returns 503 (telemetry off), 401 (bad token), 413 (too large), or 400 (unparseable). The
        // collector retries on 5xx and drops on 4xx.
        app.MapPost("/ingest/otlp/v1/logs", async (
            HttpContext httpContext, ITelemetryIngest telemetry, IngestTokenService tokens,
            IngestRateLimiter rateLimiter, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            ILogger log = loggerFactory.CreateLogger("OtlpIngest");
            OtlpIngest.Result r = await OtlpIngest.ReadAsync(httpContext, telemetry, tokens, rateLimiter, log, ct);
            if (r.Error is not null) return r.Error;

            using System.Text.Json.JsonDocument doc = r.Doc!;
            List<LogIngestRecord> records;
            try
            {
                records = OtlpLogsParser.Parse(doc);
            }
            catch (Exception ex)
            {
                // Malformed payload — 400 so the collector DROPS it, never a 500 (which it retries forever).
                log.LogWarning(ex, "Failed to parse OTLP logs payload.");
                return Results.BadRequest();
            }
            try
            {
                await telemetry.WriteLogsAsync(r.TenantId, r.ClusterId, records, ct);
            }
            catch (Exception ex)
            {
                // Timestamps are clamped into the partitioned range on write, so the atomic-COPY "no
                // partition" wedge can't happen; a failure here is a genuine DB/transient issue. Return
                // 500 so the collector retries (it buffers) rather than dropping the batch.
                log.LogError(ex, "Failed to persist OTLP logs batch.");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            return Results.Json(new { });
        }).DisableAntiforgery();

        app.MapPost("/ingest/otlp/v1/traces", async (
            HttpContext httpContext, ITelemetryIngest telemetry, IngestTokenService tokens,
            IngestRateLimiter rateLimiter, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            ILogger log = loggerFactory.CreateLogger("OtlpIngest");
            OtlpIngest.Result r = await OtlpIngest.ReadAsync(httpContext, telemetry, tokens, rateLimiter, log, ct);
            if (r.Error is not null) return r.Error;

            using System.Text.Json.JsonDocument doc = r.Doc!;
            List<SpanIngestRecord> spans;
            try
            {
                spans = OtlpTracesParser.Parse(doc);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to parse OTLP traces payload.");
                return Results.BadRequest();
            }
            try
            {
                await telemetry.WriteSpansAsync(r.TenantId, r.ClusterId, spans, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to persist OTLP traces batch.");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            return Results.Json(new { });
        }).DisableAntiforgery();

        // NB: there is no /ingest/otlp/v1/metrics endpoint — app metrics go straight to Prometheus (apps
        // remote-write / are scraped), and EntKube only visualizes them via PromQL (PromMetricsService).

        // The first-party RUM snippet — embedded as <script src="…/rum/v1/rum.js" data-key="…">. Static,
        // cacheable, no per-site templating (the key comes from data-key, the ingest origin from the src).
        app.MapGet("/rum/v1/rum.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(RumSnippet.Js, "application/javascript; charset=utf-8");
        });

        // Public RUM ingest — the browser snippet POSTs a compact JSON beacon (text/plain to avoid a CORS
        // preflight, or application/json) to /ingest/rum/v1/{publicKey}. UNLIKE the OTLP endpoints there is
        // no secret token (the key ships in client JS); abuse is bounded by the site's Origin allow-list,
        // the per-site rate limiter, and sampling. 503 (off) / 401 (unknown/disabled key) / 403 (origin) /
        // 429 (rate) / 413 (too large) / 400 (unparseable) / 204 (accepted, incl. sampled-out).
        app.MapMethods("/ingest/rum/v1/{key}", ["OPTIONS"], async (
            HttpContext ctx, string key, RumSiteService sites, CancellationToken ct) =>
        {
            // CORS preflight (fetch fallback). Beacons are "simple" requests and never reach here.
            RumSiteInfo? site = await sites.ResolveAsync(key, ct);
            string? origin = ctx.Request.Headers.Origin;
            bool allowed = site is { IsEnabled: true } && RumIngest.OriginAllowed(site.Origins, origin);
            if (allowed && origin is not null)
            {
                SetRumCors(ctx, origin);
                ctx.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
                ctx.Response.Headers.AccessControlAllowHeaders = "content-type";
                ctx.Response.Headers.AccessControlMaxAge = "600";
            }
            return Results.NoContent();
        }).DisableAntiforgery();

        app.MapPost("/ingest/rum/v1/{key}", async (
            HttpContext httpContext, string key, ITelemetryIngest telemetry, RumSiteService sites,
            IngestRateLimiter rateLimiter, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            ILogger log = loggerFactory.CreateLogger("RumIngest");
            RumIngest.Result r = await RumIngest.ReadAsync(httpContext, key, telemetry, sites, rateLimiter, log, ct);
            if (r.AllowOrigin is not null) SetRumCors(httpContext, r.AllowOrigin);
            if (r.Error is not null) return r.Error;
            if (r.Doc is null) return Results.NoContent();   // sampled out — accepted, not stored

            using System.Text.Json.JsonDocument doc = r.Doc;
            RumIngestParser.RumBatch batch;
            try
            {
                batch = RumIngestParser.Parse(doc);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to parse RUM payload.");
                return Results.BadRequest();
            }
            try
            {
                await telemetry.WriteRumPageViewsAsync(r.TenantId, r.SiteId, batch.PageViews, ct);
                await telemetry.WriteRumErrorsAsync(r.TenantId, r.SiteId, batch.Errors, ct);
                await telemetry.WriteRumResourcesAsync(r.TenantId, r.SiteId, batch.Resources, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to persist RUM batch.");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            return Results.NoContent();
        }).DisableAntiforgery();

        app.Run();
    }

    private static void SetRumCors(HttpContext ctx, string origin)
    {
        ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.Vary = "Origin";
    }

    /// <summary>
    /// Applies all pending EF Core migrations, retrying with exponential backoff.
    /// Throws on final failure so the container exits and the orchestrator restarts it
    /// rather than running with a broken schema.
    /// </summary>
    // One-time idempotent repair: the AddAppEnvironmentNamespace migration was recorded
    // with an empty Up() so the column was never actually created. This adds it if missing.
    private static async Task EnsureAppEnvironmentNamespaceAsync(DbContext db, ILogger logger)
    {
        try
        {
            string? provider = db.Database.ProviderName;
            string? sql = null;
            if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                sql = "ALTER TABLE \"AppEnvironments\" ADD COLUMN IF NOT EXISTS \"Namespace\" text";
            else if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
                sql = "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'AppEnvironments') AND name = N'Namespace')" +
                      " ALTER TABLE [AppEnvironments] ADD [Namespace] nvarchar(max) NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
                sql = "ALTER TABLE \"AppEnvironments\" ADD COLUMN \"Namespace\" TEXT";

            if (sql is not null)
                await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema repair for AppEnvironments.Namespace skipped or failed.");
        }
    }

    private static async Task EnsureDeploymentRouteClusterAppliedAtAsync(DbContext db, ILogger logger)
    {
        try
        {
            string? provider = db.Database.ProviderName;
            string? sql = null;
            if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                sql = "ALTER TABLE \"AppDeploymentRoutes\" ADD COLUMN IF NOT EXISTS \"ClusterAppliedAt\" timestamp with time zone NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
                sql = "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'AppDeploymentRoutes') AND name = N'ClusterAppliedAt')" +
                      " ALTER TABLE [AppDeploymentRoutes] ADD [ClusterAppliedAt] datetimeoffset NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
                sql = "ALTER TABLE \"AppDeploymentRoutes\" ADD COLUMN \"ClusterAppliedAt\" TEXT NULL";

            if (sql is not null)
                await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema repair for AppDeploymentRoutes.ClusterAppliedAt skipped or failed.");
        }
    }

    private static async Task EnsureDeploymentRouteRewritePathAsync(DbContext db, ILogger logger)
    {
        try
        {
            string? provider = db.Database.ProviderName;
            string? sql = null;
            if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                sql = "ALTER TABLE \"AppDeploymentRoutes\" ADD COLUMN IF NOT EXISTS \"RewritePath\" character varying(200) NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
                sql = "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'AppDeploymentRoutes') AND name = N'RewritePath')" +
                      " ALTER TABLE [AppDeploymentRoutes] ADD [RewritePath] nvarchar(200) NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
                sql = "ALTER TABLE \"AppDeploymentRoutes\" ADD COLUMN \"RewritePath\" TEXT NULL";

            if (sql is not null)
                await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema repair for AppDeploymentRoutes.RewritePath skipped or failed.");
        }
    }

    // Adds RumSites.AppId (nullable) so RUM sites can be owned by an app and scoped to that app's
    // customer in the portal. Idempotent per provider; a nullable column needs no default/backfill.
    private static async Task EnsureRumSiteAppIdAsync(DbContext db, ILogger logger)
    {
        try
        {
            string? provider = db.Database.ProviderName;
            string? sql = null;
            if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                sql = "ALTER TABLE \"RumSites\" ADD COLUMN IF NOT EXISTS \"AppId\" uuid NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
                sql = "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'RumSites') AND name = N'AppId')" +
                      " ALTER TABLE [RumSites] ADD [AppId] uniqueidentifier NULL";
            else if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
                sql = "ALTER TABLE \"RumSites\" ADD COLUMN \"AppId\" TEXT NULL";

            if (sql is not null)
                await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema repair for RumSites.AppId skipped or failed.");
        }
    }

    private static async Task EnsureNotificationChannelFiltersAsync(DbContext db, ILogger logger)
    {
        try
        {
            string? provider = db.Database.ProviderName;
            if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"NotificationChannels\" ADD COLUMN IF NOT EXISTS \"AcknowledgeFilter\" character varying(30) NOT NULL DEFAULT 'All'");
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"NotificationChannels\" ADD COLUMN IF NOT EXISTS \"FiringFilter\" character varying(30) NOT NULL DEFAULT 'All'");
            }
            else if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'NotificationChannels') AND name = N'AcknowledgeFilter')" +
                    " ALTER TABLE [NotificationChannels] ADD [AcknowledgeFilter] nvarchar(30) NOT NULL DEFAULT 'All'");
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'NotificationChannels') AND name = N'FiringFilter')" +
                    " ALTER TABLE [NotificationChannels] ADD [FiringFilter] nvarchar(30) NOT NULL DEFAULT 'All'");
            }
            else if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"NotificationChannels\" ADD COLUMN \"AcknowledgeFilter\" TEXT NOT NULL DEFAULT 'All'");
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"NotificationChannels\" ADD COLUMN \"FiringFilter\" TEXT NOT NULL DEFAULT 'All'");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema repair for NotificationChannels filters skipped or failed (columns may already exist).");
        }
    }

    /// <summary>
    /// One-time data migration: moves each registered cluster's plaintext kubeconfig out of the
    /// legacy <c>KubernetesClusters.Kubeconfig</c> column and into the encrypted tenant vault
    /// (as a <see cref="VaultSecretType.Kubeconfig"/> secret), setting each cluster's
    /// <c>KubeconfigSecretId</c>. Once every cluster has been migrated, the legacy column is dropped.
    ///
    /// This runs in application code (not the EF migration) because encryption requires the tenant's
    /// unsealed DEK. It is idempotent: on a database whose column has already been dropped it is a
    /// no-op, and it only drops the column after confirming no cluster still holds an unmigrated
    /// plaintext kubeconfig — so a partial failure keeps the column for the next startup.
    /// </summary>
    // One-time/idempotent: secrets imported before TLS detection existed are sitting in the
    // vault as separate opaque keys (tls.crt + tls.key). Merge each such group into a single
    // readable Certificate secret so it shows expiry and syncs as kubernetes.io/tls.
    private static async Task EnsureImportedTlsSecretsAsCertificatesAsync(
        IServiceProvider services, ILogger logger)
    {
        try
        {
            VaultService vault = services.GetRequiredService<VaultService>();
            int converted = await vault.ConvertImportedTlsSecretsToCertificatesAsync();
            if (converted > 0)
            {
                logger.LogInformation("Merged {Count} imported TLS secret(s) into certificate secrets.", converted);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TLS-secret to certificate backfill failed; leaving secrets as-is.");
        }
    }

    private static async Task EnsureClusterKubeconfigsMigratedAsync(
        ApplicationDbContext db, IServiceProvider services, ILogger logger)
    {
        string? provider = db.Database.ProviderName;

        try
        {
            // If the legacy column is already gone (fresh DB or previously migrated), nothing to do.
            if (!await LegacyKubeconfigColumnExistsAsync(db, provider))
            {
                return;
            }

            List<(Guid Id, Guid TenantId, string? ContextName, string ApiServerUrl, string Kubeconfig)> rows =
                await ReadLegacyKubeconfigsAsync(db, provider);

            if (rows.Count > 0)
            {
                VaultService vault = services.GetRequiredService<VaultService>();
                logger.LogInformation("Migrating {Count} cluster kubeconfig(s) into the vault.", rows.Count);

                foreach ((Guid id, Guid tenantId, string? contextName, string apiServerUrl, string kubeconfig) in rows)
                {
                    try
                    {
                        KubeconfigBundle bundle = new()
                        {
                            ConfigYaml = kubeconfig,
                            ContextName = contextName,
                            ApiServerUrl = apiServerUrl,
                        };

                        (bool ok, string? error, _) = await vault.SetClusterKubeconfigAsync(
                            tenantId, id, bundle, updatedBy: "system:migration");
                        if (!ok)
                        {
                            logger.LogWarning("Kubeconfig migration for cluster {ClusterId} failed: {Error}", id, error);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Kubeconfig migration for cluster {ClusterId} threw.", id);
                    }
                }
            }

            // Only drop the legacy column once nothing is left unmigrated.
            long remaining = await CountUnmigratedKubeconfigsAsync(db, provider);
            if (remaining == 0)
            {
                await db.Database.ExecuteSqlRawAsync(DropLegacyKubeconfigColumnSql(provider));
                logger.LogInformation("Dropped legacy plaintext KubernetesClusters.Kubeconfig column after vault migration.");
            }
            else
            {
                logger.LogWarning(
                    "{Count} cluster kubeconfig(s) remain unmigrated; keeping the legacy column for the next startup.",
                    remaining);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cluster kubeconfig vault migration skipped or failed.");
        }
    }

    private static async Task<bool> LegacyKubeconfigColumnExistsAsync(ApplicationDbContext db, string? provider)
    {
        // CAST to BIGINT so the scalar reads back as long on every provider — SQL Server's COUNT(*)
        // is int, which would otherwise fail SqlQueryRaw<long> and (being caught) silently skip the
        // whole migration, leaving clusters unmigrated and the legacy column in place. EF Core maps a
        // scalar SqlQuery result to a column named "Value", so the projection must be aliased as such.
        string sql = provider switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM information_schema.columns WHERE table_name = 'KubernetesClusters' AND column_name = 'Kubeconfig'",
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                "SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM sys.columns WHERE object_id = OBJECT_ID(N'KubernetesClusters') AND name = N'Kubeconfig'",
            _ => // Sqlite
                "SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM pragma_table_info('KubernetesClusters') WHERE name = 'Kubeconfig'",
        };

        return await db.Database.SqlQueryRaw<long>(sql).SingleAsync() > 0;
    }

    private static async Task<long> CountUnmigratedKubeconfigsAsync(ApplicationDbContext db, string? provider)
    {
        // A cluster is "unmigrated" if it still has a plaintext kubeconfig but no vault secret yet.
        string col = provider == "Microsoft.EntityFrameworkCore.SqlServer" ? "[{0}]" : "\"{0}\"";
        string table = string.Format(col, "KubernetesClusters");
        string kubeconfig = string.Format(col, "Kubeconfig");
        string secretId = string.Format(col, "KubeconfigSecretId");
        // CAST to BIGINT + alias "Value" for cross-provider scalar typing (see LegacyKubeconfigColumnExistsAsync).
        string sql = $"SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM {table} WHERE {kubeconfig} IS NOT NULL AND {secretId} IS NULL";

        return await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    private static string DropLegacyKubeconfigColumnSql(string? provider) => provider switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => "ALTER TABLE [KubernetesClusters] DROP COLUMN [Kubeconfig]",
        _ => "ALTER TABLE \"KubernetesClusters\" DROP COLUMN \"Kubeconfig\"",
    };

    private static async Task<List<(Guid, Guid, string?, string, string)>> ReadLegacyKubeconfigsAsync(
        ApplicationDbContext db, string? provider)
    {
        string col = provider == "Microsoft.EntityFrameworkCore.SqlServer" ? "[{0}]" : "\"{0}\"";
        string sql =
            $"SELECT {string.Format(col, "Id")}, {string.Format(col, "TenantId")}, " +
            $"{string.Format(col, "ContextName")}, {string.Format(col, "ApiServerUrl")}, " +
            $"{string.Format(col, "Kubeconfig")} FROM {string.Format(col, "KubernetesClusters")} " +
            $"WHERE {string.Format(col, "Kubeconfig")} IS NOT NULL " +
            $"AND {string.Format(col, "KubeconfigSecretId")} IS NULL";

        List<(Guid, Guid, string?, string, string)> rows = [];

        System.Data.Common.DbConnection conn = db.Database.GetDbConnection();
        bool wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await conn.OpenAsync();
        }

        try
        {
            using System.Data.Common.DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using System.Data.Common.DbDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        finally
        {
            if (wasClosed)
            {
                await conn.CloseAsync();
            }
        }

        return rows;
    }

    private static void MigrateWithRetry(DbContext db, ILogger logger)
    {
        int maxRetries = 10;
        int delayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                IEnumerable<string> pending = db.Database.GetPendingMigrations().ToList();
                if (pending.Any())
                    logger.LogInformation("Applying {Count} pending migration(s): {Migrations}",
                        pending.Count(), string.Join(", ", pending));

                db.Database.Migrate();
                logger.LogInformation("Database migrations applied successfully.");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                logger.LogWarning(ex, "Migration attempt {Attempt}/{Max} failed. Retrying in {Delay}ms...",
                    attempt, maxRetries, delayMs);
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 16_000);
            }
        }

        // Final attempt — let the exception propagate so the container exits cleanly
        // and the orchestrator restarts it rather than serving traffic with a bad schema.
        logger.LogCritical("All {Max} migration attempts failed. Terminating.", maxRetries);
        db.Database.Migrate();
    }
}

/// <summary>
/// A simple IDbContextFactory adapter that uses a delegate to create DbContext instances.
/// Needed because AddDbContextFactory doesn't support derived DbContext types the way
/// AddDbContext does with its TContext/TImplementation overload.
/// </summary>
file sealed class DelegatingDbContextFactory(Func<ApplicationDbContext> factory) : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => factory();
}
