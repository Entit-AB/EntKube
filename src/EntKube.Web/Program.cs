using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Authorization;
using EntKube.Web.Client.Pages;
using EntKube.Web.Components;
using EntKube.Web.Components.Account;
using EntKube.Web.Data;
using EntKube.Web.Services;

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

        string databaseProvider = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "Sqlite";
        string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        // Register both AddDbContext (for Identity/migrations — needs derived type mapping)
        // and a DbContext factory for Blazor Server concurrency safety.
        // A single scoped DbContext is NOT safe in Blazor Server because multiple
        // async operations (different components) can run concurrently on the same circuit.

        switch (databaseProvider)
        {
            case "Postgres":
                builder.Services.AddDbContext<ApplicationDbContext, PostgresApplicationDbContext>(options =>
                    options.UseNpgsql(connectionString));
                builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
                    _ => new DelegatingDbContextFactory(() =>
                    {
                        DbContextOptionsBuilder<PostgresApplicationDbContext> opts = new();
                        opts.UseNpgsql(connectionString);
                        return new PostgresApplicationDbContext(opts.Options);
                    }));
                break;

            case "SqlServer":
                builder.Services.AddDbContext<ApplicationDbContext, SqlServerApplicationDbContext>(options =>
                    options.UseSqlServer(connectionString));
                builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
                    _ => new DelegatingDbContextFactory(() =>
                    {
                        DbContextOptionsBuilder<SqlServerApplicationDbContext> opts = new();
                        opts.UseSqlServer(connectionString);
                        return new SqlServerApplicationDbContext(opts.Options);
                    }));
                break;

            default: // "Sqlite"
                builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
                    options.UseSqlite(connectionString));
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
            .AddSignInManager()
            .AddDefaultTokenProviders();

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("HasTenantAccess", policy =>
                policy.Requirements.Add(new HasTenantAccessRequirement()));
        });
        builder.Services.AddScoped<IAuthorizationHandler, HasTenantAccessHandler>();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
        builder.Services.AddHttpClient();
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
        builder.Services.AddScoped<ComponentLifecycleService>();
        builder.Services.AddScoped<ExternalRouteService>();
        builder.Services.AddScoped<AppRouteService>();
        builder.Services.AddScoped<DatabaseService>();
        builder.Services.AddScoped<CnpgService>();
        builder.Services.AddScoped<MongoService>();
        builder.Services.AddScoped<RegisteredPostgresService>();
        builder.Services.AddScoped<IKubernetesClientFactory, KubernetesClientFactory>();
        builder.Services.AddScoped<OpenStackS3Service>();
        builder.Services.AddScoped<StorageService>();
        builder.Services.AddScoped<StorageBrowserService>();
        builder.Services.AddScoped<ComponentScanService>();
        builder.Services.AddScoped<KeycloakService>();
        builder.Services.AddScoped<RabbitMQService>();
        builder.Services.AddScoped<RedisService>();
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
        builder.Services.AddScoped<BackupService>();
        builder.Services.AddScoped<VpnService>();
        builder.Services.AddScoped<KyvernoPolicyService>();
        builder.Services.AddScoped<KedaScalerService>();
        builder.Services.AddScoped<SecretExpiryService>();
        builder.Services.AddScoped<CustomerNotificationService>();

        builder.Services.AddScoped<AppGovernanceService>();
        builder.Services.AddScoped<GitOperationsService>();
        builder.Services.AddScoped<GitRepositoryService>();
        builder.Services.AddScoped<CustomerGitService>();
        builder.Services.AddScoped<AppOfAppsService>();
        builder.Services.AddSingleton<GitSyncService>();
        builder.Services.AddScoped<GitWebhookService>();
        builder.Services.AddHostedService<DeploymentSyncService>();
        builder.Services.AddHostedService<ExternalRouteHealthService>();
        builder.Services.AddHostedService<AlertSyncService>();
        builder.Services.AddHostedService<AlertEscalationService>();
        builder.Services.AddHostedService<UptimeTrackingService>();
        builder.Services.AddHostedService<HeadscaleCertSyncService>();
        builder.Services.AddHostedService<SecretExpiryNotificationService>();
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
        if (app.Environment.IsDevelopment())
        {
            app.UseWebAssemblyDebugging();
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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

        app.Run();
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
