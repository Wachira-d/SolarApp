using FluentValidation;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp;

public class Startup
{
    /// <summary>
    /// Which external auth providers were registered at startup and therefore
    /// have active callback handlers (/signin-microsoft, /signin-line).
    /// Read by Program.cs after ConfigureServices and logged to startup log.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> AuthProviderStatus =>
        _authProviderStatus ?? new Dictionary<string, bool>();
    private static Dictionary<string, bool>? _authProviderStatus;

    // ══════════════════════════════════════════════════════════════
    //  SERVICE REGISTRATION
    // ══════════════════════════════════════════════════════════════

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        // A background service that throws must never stop the whole app.
        // .NET's default (StopHost) took the entire site down when the
        // self-keepalive ping timed out — one optional ping killing a
        // production web app is never the trade-off we want.  Failures are
        // logged; the site keeps serving.
        builder.Services.Configure<HostOptions>(o =>
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        builder.Services.AddHttpContextAccessor();
        // Automatic audit trail for user-driven changes to domain entities
        // (Installation, Customer, Partner, AlertRule, settings, …).  The
        // interceptor self-excludes background-job writes and high-volume
        // tables, so it never floods AuditLog.
        builder.Services.AddScoped<Data.Interceptors.AuditableEntityInterceptor>();
        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
            options.AddInterceptors(sp.GetRequiredService<Data.Interceptors.AuditableEntityInterceptor>());
        });

        builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false;

            // Password policy
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;

            // Lockout protects against brute-force
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

            // User settings — require unique email
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            // Security hardening
            options.Cookie.HttpOnly = true;                                   // JS cannot read
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax; // Lax allows top-level nav from email links
        });

        // ─── External login (Microsoft/Office 365 + LINE) ───
        // Config precedence: DB SystemSettings > appsettings.json > empty
        // Admins configure these via /Admin/ExternalAuth; secrets stored encrypted.
        var authBuilder = builder.Services.AddAuthentication();
        var (msClientId, msClientSecret, msTenantId, lineClientId, lineClientSecret) =
            ReadExternalAuthConfig(builder);

        var msRegistered = false;
        var lineRegistered = false;

        if (!string.IsNullOrWhiteSpace(msClientId) && !string.IsNullOrWhiteSpace(msClientSecret))
        {
            authBuilder.AddMicrosoftAccount("Microsoft", options =>
            {
                options.ClientId = msClientId;
                options.ClientSecret = msClientSecret;
                options.CallbackPath = "/signin-microsoft";
                options.SaveTokens = true;

                // If a specific tenant ID is configured, use that tenant's endpoint
                // (single-tenant app). Otherwise use /common (multi-tenant + personal).
                // Fixes AADSTS50194 when the app is registered as single-tenant.
                if (!string.IsNullOrWhiteSpace(msTenantId))
                {
                    options.AuthorizationEndpoint =
                        $"https://login.microsoftonline.com/{msTenantId}/oauth2/v2.0/authorize";
                    options.TokenEndpoint =
                        $"https://login.microsoftonline.com/{msTenantId}/oauth2/v2.0/token";
                }

                // Request profile + email scopes so we can create a local user
                options.Scope.Add("User.Read");
            });
            msRegistered = true;
        }

        if (!string.IsNullOrWhiteSpace(lineClientId) && !string.IsNullOrWhiteSpace(lineClientSecret))
        {
            authBuilder.AddOAuth("LINE", "LINE", options =>
            {
                options.ClientId = lineClientId;
                options.ClientSecret = lineClientSecret;
                options.CallbackPath = "/signin-line";
                options.AuthorizationEndpoint = "https://access.line.me/oauth2/v2.1/authorize";
                options.TokenEndpoint = "https://api.line.me/oauth2/v2.1/token";
                options.UserInformationEndpoint = "https://api.line.me/v2/profile";
                options.SaveTokens = true;
                options.Scope.Add("profile");
                options.Scope.Add("openid");
                // Only add 'email' if the LINE Login channel has email permission enabled
                // options.Scope.Add("email");

                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "userId");
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "displayName");
                options.ClaimActions.MapJsonKey("urn:line:picture", "pictureUrl");

                options.Events = new Microsoft.AspNetCore.Authentication.OAuth.OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);

                        using var response = await context.Backchannel.SendAsync(request,
                            System.Net.Http.HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
                        response.EnsureSuccessStatusCode();

                        using var payload = System.Text.Json.JsonDocument.Parse(
                            await response.Content.ReadAsStringAsync());
                        context.RunClaimActions(payload.RootElement);
                    }
                };
            });
            lineRegistered = true;
        }

        // Expose registration status so startup logs can tell the operator whether
        // /signin-microsoft and /signin-line callbacks are active.
        _authProviderStatus = new Dictionary<string, bool>
        {
            ["Microsoft (/signin-microsoft)"] = msRegistered,
            ["LINE (/signin-line)"] = lineRegistered
        };

        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
        });

        // DataProtection — used for encrypting API credentials at rest
        // Pin DataProtection keys to a fixed directory so startup-time decryption
        // (raw ADO.NET path) and runtime (DI path) use the same key ring.
        // Priority: SOLARAPP_KEYS_PATH env > %ProgramData%\SolarApp\dp-keys >
        //           %TEMP%\SolarApp-keys (last resort).
        // Default %ProgramData% is writable by IIS AppPool identities by default.
        // Avoid BaseDirectory — wwwroot is usually read-only under IIS.
        var keysDir = ResolveKeysDirectory();
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(keysDir)
            .SetApplicationName("SolarApp");
        builder.Services.AddSingleton<Services.Security.ICredentialProtector, Services.Security.CredentialProtector>();
        builder.Services.AddScoped<Services.DeviceImport.IDeviceImporter, Services.DeviceImport.DeviceImporter>();
        builder.Services.AddSingleton<Services.Geocoding.IGeocodingService, Services.Geocoding.NominatimGeocodingService>();

        builder.Services.AddScoped<Services.Interfaces.IInstallationService, Services.Implementations.InstallationService>();
        builder.Services.AddScoped<Services.Interfaces.IDashboardService, Services.Implementations.DashboardService>();
        builder.Services.AddScoped<Services.Interfaces.IMaintenanceService, Services.Implementations.MaintenanceService>();
        builder.Services.AddScoped<Services.Interfaces.IAlertService, Services.Implementations.AlertService>();
        builder.Services.AddScoped<Services.ReportExport.IReportExportService, Services.ReportExport.ReportExportService>();
        builder.Services.AddScoped<Services.FileUpload.IFileUploadService, Services.FileUpload.FileUploadService>();
        builder.Services.AddScoped<Services.Admin.IUserAdminQueryService, Services.Admin.UserAdminQueryService>();
        builder.Services.AddScoped<Services.Admin.IUserAdminCommandService, Services.Admin.UserAdminCommandService>();

        // ── Global HttpClient hardening (applies to EVERY client, named or
        // bare, that any adapter resolves from IHttpClientFactory) ──
        // The vendor clouds (neapi.hoymiles, *.fusionsolar.huawei, semsportal,
        // soliscloud, …) sit behind load balancers whose IPs rotate.  With
        // the framework default (PooledConnectionLifetime = infinite) a
        // long-lived process pins the first resolved IP forever, so when the
        // vendor drains a node our syncs hang/timeout until the app restarts.
        // Recycling pooled connections every 2 min forces periodic DNS
        // re-resolution — pure reliability, no behaviour change.
        // A 100s ceiling is a safety net for the bare CreateClient() path
        // (Deye/FoxESS/GoodWe/Solis) which previously had NO timeout at all.
        builder.Services.AddTransient<Services.InverterApi.Common.ResilientHttpHandler>();
        builder.Services.AddTransient<Services.InverterApi.Common.VendorRateLimitHandler>();
        builder.Services.ConfigureHttpClientDefaults(b =>
        {
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                // Cap concurrent sockets per vendor host so a burst of
                // parallel installations can't exhaust ephemeral ports.
                MaxConnectionsPerServer = 20,
            });
            // Pipeline order (outermost → innermost):
            //   ResilientHttpHandler  — retry transient failures
            //   VendorRateLimitHandler — token-bucket throttle per vendor host
            //   SocketsHttpHandler    — the actual connection
            // Retry is outermost so each RETRY attempt also passes the rate
            // gate (consuming a token per real request, never over-budget).
            b.AddHttpMessageHandler(sp =>
                sp.GetRequiredService<Services.InverterApi.Common.ResilientHttpHandler>());
            b.AddHttpMessageHandler(sp =>
                sp.GetRequiredService<Services.InverterApi.Common.VendorRateLimitHandler>());
            b.SetHandlerLifetime(TimeSpan.FromMinutes(5));
        });

        builder.Services.AddHttpClient("default", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        // Bare default client: give it a real timeout ceiling (was infinite).
        builder.Services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(100);
        });
        builder.Services.AddScoped<Services.InverterApi.Adapters.HuaweiAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.FoxEssAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.SolisAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.GoodWeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.DeyeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.HoymilesAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.SigenergyAdapter>();
        builder.Services.AddScoped<Services.InverterApi.IInverterApiAdapterFactory, Services.InverterApi.InverterApiAdapterFactory>();
        builder.Services.AddScoped<Services.InverterApi.Common.IAdapterDiagnostics, Services.InverterApi.Common.AdapterDiagnostics>();
        builder.Services.AddScoped<Services.InverterApi.Common.IConnectionHealthChecker, Services.InverterApi.Common.ConnectionHealthChecker>();
        builder.Services.AddScoped<Services.Authorization.IInstallationAccessHelper, Services.Authorization.InstallationAccessHelper>();
        builder.Services.AddScoped<Services.Authorization.IWorkOrderAccessHelper, Services.Authorization.WorkOrderAccessHelper>();
        builder.Services.AddScoped<Services.WorkOrderTemplates.IWorkOrderTemplateService, Services.WorkOrderTemplates.WorkOrderTemplateService>();
        builder.Services.AddScoped<Services.Performance.IPerformanceMetricsService, Services.Performance.PerformanceMetricsService>();
        builder.Services.AddScoped<Services.AlertRules.IAlertRulesEngine, Services.AlertRules.AlertRulesEngine>();
        builder.Services.AddScoped<Services.LineApi.ILineMessagingService, Services.LineApi.LineMessagingService>();
        builder.Services.AddScoped<Services.Notifications.INotificationRouteService, Services.Notifications.NotificationRouteService>();
        builder.Services.AddScoped<Services.Weather.IWeatherService, Services.Weather.WeatherService>();
        builder.Services.AddMemoryCache();
        builder.Services.AddValidatorsFromAssemblyContaining<Startup>();
        // Self-keepalive: pings /ping every 4 min to prevent IIS idle shutdown
        builder.Services.AddHostedService<BackgroundJobs.SelfKeepAliveService>();

        builder.Services.AddScoped<BackgroundJobs.SolarDataSyncJob>();
        builder.Services.AddScoped<BackgroundJobs.SystemHealthMonitorJob>();
        builder.Services.AddScoped<BackgroundJobs.WorkOrderReminderJob>();
        builder.Services.AddScoped<BackgroundJobs.DailySummaryJob>();
        builder.Services.AddScoped<BackgroundJobs.PredictiveDiagnosticsJob>();
        builder.Services.AddScoped<BackgroundJobs.DataRetentionJob>();
        builder.Services.AddScoped<Services.PredictiveDiagnostics.PredictiveDiagnosticsEngine>();
        builder.Services.AddScoped<Services.PredictiveDiagnostics.AlertDiagnosticsEnricher>();

        // ── Huawei NBI rate gate tunables ──
        // Defaults to 65s spacing, which keeps us safely below Huawei's
        // 1 req/min per-account ceiling. Override via appsettings:
        //   "InverterApis": { "HuaweiMinSpacingSeconds": 70, "HuaweiBackoffSeconds": 120 }
        Services.InverterApi.Common.HuaweiRateGate.MinSpacing = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("InverterApis:HuaweiMinSpacingSeconds", 65));
        Services.InverterApi.Common.HuaweiRateGate.ExtendOnRateLimit = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("InverterApis:HuaweiBackoffSeconds", 120));
        Services.InverterApi.Common.HuaweiRateGate.MaxAcceptableWait = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("InverterApis:HuaweiMaxAcceptableWaitSeconds", 30));

        // ── Localization ──
        // Default culture is Thai because the primary users are field technicians
        // in Thailand. Users can still switch to English via the language menu.
        builder.Services.AddLocalization(opt => opt.ResourcesPath = "Resources");
        builder.Services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(opt =>
        {
            // Force Thai locale to use the Gregorian calendar.  .NET's default
            // th-TH culture uses ThaiBuddhistCalendar, which formats 2025 as
            // 2568 (year + 543) in ToString("yyyy") / ToString("dd/MM/yyyy").
            // That confuses users (every UI date suddenly shows the wrong
            // year) and breaks date-range queries that compare the displayed
            // year against the DB which stores Gregorian DateTimes.
            var thai = new System.Globalization.CultureInfo("th-TH");
            thai.DateTimeFormat.Calendar = new System.Globalization.GregorianCalendar();
            var english = new System.Globalization.CultureInfo("en-US");

            var supported = new[] { thai, english };
            opt.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(thai);
            opt.SupportedCultures = supported;
            opt.SupportedUICultures = supported;
            opt.FallBackToParentCultures = true;
        });

        // ── SignalR for real-time dashboard updates ──
        builder.Services.AddSignalR();

        builder.Services.AddRazorPages()
            .AddViewLocalization()
            .AddDataAnnotationsLocalization();
        builder.Services.AddControllers();
    }

    public static void ConfigureHangfire(WebApplicationBuilder builder, string connectionString)
    {
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                PrepareSchemaIfNecessary = true,
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.FromSeconds(15),
                UseRecommendedIsolationLevel = true,
                SchemaName = "HangFire"
            }));
        builder.Services.AddHangfireServer(options =>
        {
            options.WorkerCount = 5;
            options.Queues = new[] { "critical", "default", "low" };
        });
    }

    public static void ConfigureHangfireDashboard(WebApplication app)
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new BackgroundJobs.HangfireAdminAuthorizationFilter() }
        });

        // Load cron schedules from SystemSettings (fallback to recommended defaults)
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var syncCron = GetSetting(ctx, "Schedule.DataSync", "*/15 * * * *");
        var healthCron = GetSetting(ctx, "Schedule.HealthMonitor", "*/5 * * * *");
        var reminderCron = GetSetting(ctx, "Schedule.WorkOrderReminders", "*/30 * * * *");

        RecurringJob.AddOrUpdate<BackgroundJobs.SolarDataSyncJob>(
            "sync-production-data", job => job.ExecuteAsync(), syncCron);
        RecurringJob.AddOrUpdate<BackgroundJobs.SystemHealthMonitorJob>(
            "check-system-health", job => job.ExecuteAsync(), healthCron);
        RecurringJob.AddOrUpdate<BackgroundJobs.WorkOrderReminderJob>(
            "workorder-reminders", job => job.ExecuteAsync(), reminderCron);

        var dailySummaryCron = GetSetting(ctx, "Schedule.DailySummary", "0 11 * * *"); // 18:00 Thailand (UTC+7)
        RecurringJob.AddOrUpdate<BackgroundJobs.DailySummaryJob>(
            "daily-summary-report", job => job.ExecuteAsync(), dailySummaryCron);

        var predictiveCron = GetSetting(ctx, "Schedule.PredictiveDiagnostics", "0 1 * * *"); // 08:00 Thailand (UTC+7)
        RecurringJob.AddOrUpdate<BackgroundJobs.PredictiveDiagnosticsJob>(
            "predictive-diagnostics", job => job.ExecuteAsync(), predictiveCron);

        // Nightly retention sweep at 18:00 UTC = 01:00 Thailand (lowest traffic).
        var retentionCron = GetSetting(ctx, "Schedule.DataRetention", "0 18 * * *");
        RecurringJob.AddOrUpdate<BackgroundJobs.DataRetentionJob>(
            "data-retention", job => job.ExecuteAsync(), retentionCron);
    }

    /// <summary>
    /// Reads external-auth credentials (Microsoft + LINE) with precedence:
    ///   1. Database SystemSettings (configured via /Admin/ExternalAuth)
    ///   2. appsettings.json Authentication:{Provider}:ClientId/ClientSecret
    /// Secrets in the DB are DataProtection-encrypted.
    /// Must run before the app is built, so it opens a short-lived DB scope.
    /// Fails gracefully if the DB isn't ready (returns config-only values).
    /// </summary>
    /// <summary>
    /// Finds a directory we can actually write DataProtection keys to.
    /// Tries, in order: SOLARAPP_KEYS_PATH env var, %ProgramData%\SolarApp\dp-keys,
    /// %TEMP%\SolarApp-keys. Returns the first one that works.
    /// Under IIS, wwwroot is typically read-only so we must NOT pick AppContext.BaseDirectory.
    /// </summary>
    public static DirectoryInfo ResolveKeysDirectory()
    {
        var candidates = new List<string>();
        var envPath = Environment.GetEnvironmentVariable("SOLARAPP_KEYS_PATH");
        if (!string.IsNullOrWhiteSpace(envPath)) candidates.Add(envPath);

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SolarApp", "dp-keys"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "SolarApp-keys"));

        var attemptErrors = new List<string>();
        foreach (var path in candidates)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                dir.Create();
                // Prove writability
                var testFile = Path.Combine(path, ".write-test");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
                return dir;
            }
            catch (Exception ex)
            {
                // Record per-candidate failure so the final error message can
                // tell the admin exactly which paths failed and why.
                attemptErrors.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // All failed — throw a helpful error so the admin knows what to fix
        throw new InvalidOperationException(
            "Could not find a writable directory for DataProtection keys. Tried:\n  - " +
            string.Join("\n  - ", attemptErrors) +
            "\nGrant the IIS app-pool identity modify permission on one of these paths, " +
            "or set environment variable SOLARAPP_KEYS_PATH to a writable folder.");
    }

    private static (string? MsId, string? MsSecret, string? MsTenantId, string? LineId, string? LineSecret)
        ReadExternalAuthConfig(WebApplicationBuilder builder)
    {
        // appsettings fallback first
        var msId = builder.Configuration["Authentication:Microsoft:ClientId"];
        var msSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
        var msTenant = builder.Configuration["Authentication:Microsoft:TenantId"];
        var lineId = builder.Configuration["Authentication:LINE:ClientId"];
        var lineSecret = builder.Configuration["Authentication:LINE:ClientSecret"];

        try
        {
            var cs = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs)) return (msId, msSecret, msTenant, lineId, lineSecret);

            // Use raw ADO.NET — no DI container needed, no EF mapping risk.
            // Fail silently if table doesn't exist yet (fresh install).
            using var conn = new SqlConnection(cs);
            conn.Open();

            // Verify SystemSettings table exists; if not, skip DB lookup
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE name='SystemSettings') THEN 1 ELSE 0 END";
                if ((int)(check.ExecuteScalar() ?? 0) == 0)
                    return (msId, msSecret, msTenant, lineId, lineSecret);
            }

            string? ReadRaw(string key)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT [Value] FROM [SystemSettings] WHERE [Key] = @k";
                cmd.Parameters.AddWithValue("@k", key);
                return cmd.ExecuteScalar() as string;
            }

            var dbMsId = ReadRaw("Auth.Microsoft.ClientId");
            var dbMsSecret = ReadRaw("Auth.Microsoft.ClientSecret");
            var dbMsTenant = ReadRaw("Auth.Microsoft.TenantId");
            var dbLineId = ReadRaw("Auth.LINE.ClientId");
            var dbLineSecret = ReadRaw("Auth.LINE.ClientSecret");

            // Decrypt secrets if encrypted (prefix "enc:"). DataProtection is done
            // lazily via a local provider so we don't need the app's DI.
            string? TryDecrypt(string? raw)
            {
                if (string.IsNullOrEmpty(raw)) return raw;
                if (!raw.StartsWith("enc:")) return raw; // legacy plaintext
                try
                {
                    var dpProvider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider
                        .Create(ResolveKeysDirectory(),
                            configuration => configuration.SetApplicationName("SolarApp"));
                    var protector = dpProvider.CreateProtector("SolarApp.Credentials.v1");
                    return protector.Unprotect(raw["enc:".Length..]);
                }
                catch
                {
                    // Keys don't exist on this machine — cannot decrypt. Skip silently.
                    return null;
                }
            }

            return (
                dbMsId ?? msId,
                TryDecrypt(dbMsSecret) ?? msSecret,
                dbMsTenant ?? msTenant,
                dbLineId ?? lineId,
                TryDecrypt(dbLineSecret) ?? lineSecret
            );
        }
        catch
        {
            // DB not reachable at startup — fall back to config file only.
            // Never throw; app MUST be able to start without external auth.
            return (msId, msSecret, msTenant, lineId, lineSecret);
        }
    }

    private static string GetSetting(ApplicationDbContext ctx, string key, string defaultValue)
    {
        try
        {
            var setting = ctx.SystemSettings.FirstOrDefault(s => s.Key == key);
            if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
                return setting.Value;

            // Seed default if missing
            ctx.SystemSettings.Add(new Models.SystemSetting
            {
                Key = key,
                Value = defaultValue,
                Description = key == "Schedule.DataSync"
                    ? "Cron expression for inverter data sync job"
                    : "Cron expression for system health monitor job"
            });
            ctx.SaveChanges();
            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SELF-HEALING DATABASE INITIALIZATION
    // ══════════════════════════════════════════════════════════════

    public static async Task InitializeDatabaseAsync(IServiceProvider services, Action<string> log)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connectionString = context.Database.GetConnectionString()!;

        // ── Step A: Ensure SQL Server is reachable ──
        log("DB: Testing SQL Server connection...");
        if (!await TestSqlServerAsync(connectionString, log))
        {
            log("DB: SQL Server not reachable. Will try to create database anyway...");
        }

        // ── Step B: Ensure database exists ──
        log("DB: Ensuring database exists...");
        await EnsureDatabaseExistsAsync(connectionString, log);

        // ── Step C: Ensure all tables exist ──
        log("DB: Checking tables...");
        var tablesOk = await CheckTablesExistAsync(context, log);

        if (!tablesOk)
        {
            log("DB: Tables missing or incomplete. Rebuilding schema...");

            // Drop via raw SQL (more reliable than EnsureDeleted)
            try
            {
                var cs = context.Database.GetConnectionString()!;
                var csb = new SqlConnectionStringBuilder(cs);
                var dbName = csb.InitialCatalog;
                csb.InitialCatalog = "master";
                using var conn = new SqlConnection(csb.ConnectionString);
                await conn.OpenAsync();
                using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = $@"
                    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db)
                    BEGIN
                        ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{dbName}];
                    END";
                dropCmd.Parameters.AddWithValue("@db", dbName);
                await dropCmd.ExecuteNonQueryAsync();
                log("DB: Dropped old database via SQL");

                // Recreate empty database
                using var createCmd = conn.CreateCommand();
                createCmd.CommandText = $"CREATE DATABASE [{dbName}]";
                await createCmd.ExecuteNonQueryAsync();
                log("DB: Created fresh database");
            }
            catch (Exception ex)
            {
                log($"DB: Raw drop/create failed: {ex.Message}. Trying EnsureDeleted...");
                await context.Database.EnsureDeletedAsync();
            }

            // Create all tables
            var created = await context.Database.EnsureCreatedAsync();
            log($"DB: EnsureCreated result: {created}");

            // Verify tables again
            tablesOk = await CheckTablesExistAsync(context, log);
            if (!tablesOk)
            {
                // Last resort: raw SQL to verify what happened
                log("DB: WARNING - Tables still not created. Checking raw schema...");
                await DumpTableListAsync(context, log);
            }
        }

        // ── Step D: Seed data ──
        log("DB: Seeding data...");
        try
        {
            await DbInitializer.SeedAsync(scope.ServiceProvider);
            // Never log the admin password.  Set SEED_ADMIN_PASSWORD (env) or
            // Seed:AdminPassword (config) to provision it securely; change it
            // on first login regardless.
            log("DB: Seed complete (admin user: admin@solarapp.com — set SEED_ADMIN_PASSWORD to control the seed password).");
        }
        catch (Exception ex)
        {
            log($"DB: Seed failed: {ex.Message}");
            if (ex.InnerException != null)
                log($"DB: Seed inner: {ex.InnerException.Message}");
        }

        // ── Step E: Schema migrations (add missing columns without drop) ──
        log("DB: Running schema upgrades...");
        await RunSchemaUpgradesAsync(context, log);

        // ── Step F: Final verification ──
        log("DB: Running final verification...");
        await VerifyDatabaseAsync(context, log);
    }

    /// <summary>
    /// Adds missing columns to existing tables without dropping data.
    /// Each upgrade uses IF NOT EXISTS so it's idempotent.
    /// </summary>
    private static async Task RunSchemaUpgradesAsync(ApplicationDbContext context, Action<string> log)
    {
        // PERFORMANCE: previously we ran each upgrade in its own ExecuteSqlRawAsync.
        // With ~20+ upgrades × ~3 sec per round-trip this took a full minute on
        // every startup even when nothing needed to change. IIS recycles apps
        // during long startups which caused interleaved logs and flaky 503s.
        //
        // Now: single batched SQL. Each IF NOT EXISTS check is a local query
        // on sys.columns / sys.tables / sys.indexes (microseconds). Entire
        // script runs in ~500 ms on a warm SQL Server.

        var upgrades = new[]
        {
            (Table: "Installations", Column: "InverterSizeKw",
             Sql: "ALTER TABLE [Installations] ADD [InverterSizeKw] decimal(10,3) NOT NULL DEFAULT 0"),
            (Table: "Installations", Column: "CloudApiBaseUrl",
             Sql: "ALTER TABLE [Installations] ADD [CloudApiBaseUrl] nvarchar(500) NULL"),
            (Table: "Equipment", Column: "RatingKw",
             Sql: "ALTER TABLE [Equipment] ADD [RatingKw] decimal(10,3) NULL"),
            (Table: "Equipment", Column: "RatingWatts",
             Sql: "ALTER TABLE [Equipment] ADD [RatingWatts] int NULL"),
            (Table: "MaintenanceRecords", Column: "Category",
             Sql: "ALTER TABLE [MaintenanceRecords] ADD [Category] nvarchar(100) NULL"),
            (Table: "MaintenanceRecords", Column: "Location",
             Sql: "ALTER TABLE [MaintenanceRecords] ADD [Location] nvarchar(200) NULL"),
            (Table: "MaintenanceRecords", Column: "AttachmentUrls",
             Sql: "ALTER TABLE [MaintenanceRecords] ADD [AttachmentUrls] nvarchar(max) NULL"),
            (Table: "CleaningRecords", Column: "BeforePhotos",
             Sql: "ALTER TABLE [CleaningRecords] ADD [BeforePhotos] nvarchar(max) NULL"),
            (Table: "CleaningRecords", Column: "AfterPhotos",
             Sql: "ALTER TABLE [CleaningRecords] ADD [AfterPhotos] nvarchar(max) NULL"),
            (Table: "WorkOrders", Column: "ReminderSentAt",
             Sql: "ALTER TABLE [WorkOrders] ADD [ReminderSentAt] datetime2 NULL"),

            // Service-scope flags. Defaults preserve existing behaviour: every
            // legacy row becomes Full O&M with all five services enabled.
            (Table: "Installations", Column: "ServiceLevel",
             Sql: "ALTER TABLE [Installations] ADD [ServiceLevel] int NOT NULL DEFAULT 0"),
            (Table: "Installations", Column: "IncludesMonitoring",
             Sql: "ALTER TABLE [Installations] ADD [IncludesMonitoring] bit NOT NULL DEFAULT 1"),
            (Table: "Installations", Column: "IncludesCleaning",
             Sql: "ALTER TABLE [Installations] ADD [IncludesCleaning] bit NOT NULL DEFAULT 1"),
            (Table: "Installations", Column: "IncludesPreventiveMaintenance",
             Sql: "ALTER TABLE [Installations] ADD [IncludesPreventiveMaintenance] bit NOT NULL DEFAULT 1"),
            (Table: "Installations", Column: "IncludesCorrectiveMaintenance",
             Sql: "ALTER TABLE [Installations] ADD [IncludesCorrectiveMaintenance] bit NOT NULL DEFAULT 1"),
            (Table: "Installations", Column: "IncludesWarrantyService",
             Sql: "ALTER TABLE [Installations] ADD [IncludesWarrantyService] bit NOT NULL DEFAULT 1"),
            (Table: "Installations", Column: "ServiceNotes",
             Sql: "ALTER TABLE [Installations] ADD [ServiceNotes] nvarchar(1000) NULL"),

            // Cloud-connection per-installation overrides
            (Table: "Installations", Column: "ConnectionMethod",
             Sql: "ALTER TABLE [Installations] ADD [ConnectionMethod] int NOT NULL DEFAULT 0"),
            (Table: "Installations", Column: "SyncIntervalMinutes",
             Sql: "ALTER TABLE [Installations] ADD [SyncIntervalMinutes] int NOT NULL DEFAULT 15"),
            (Table: "Installations", Column: "NextSyncDueAt",
             Sql: "ALTER TABLE [Installations] ADD [NextSyncDueAt] datetime2 NULL"),

            // Customer LINE-alert routing
            (Table: "Installations", Column: "LineGroupId",
             Sql: "ALTER TABLE [Installations] ADD [LineGroupId] nvarchar(100) NULL"),
            (Table: "Installations", Column: "NotifyOnGridLoss",
             Sql: "ALTER TABLE [Installations] ADD [NotifyOnGridLoss] bit NOT NULL DEFAULT 1"),
            (Table: "Installations", Column: "NotifyOnApiFailure",
             Sql: "ALTER TABLE [Installations] ADD [NotifyOnApiFailure] bit NOT NULL DEFAULT 0"),
            (Table: "Installations", Column: "NotifyOnLowProduction",
             Sql: "ALTER TABLE [Installations] ADD [NotifyOnLowProduction] bit NOT NULL DEFAULT 0"),
            (Table: "Installations", Column: "NotifyOnCleaningDue",
             Sql: "ALTER TABLE [Installations] ADD [NotifyOnCleaningDue] bit NOT NULL DEFAULT 0"),
            (Table: "Installations", Column: "NotifyOnWarrantyExpiry",
             Sql: "ALTER TABLE [Installations] ADD [NotifyOnWarrantyExpiry] bit NOT NULL DEFAULT 0"),

            // Customer-managed notification refinements
            (Table: "Installations", Column: "QuietHoursStart",
             Sql: "ALTER TABLE [Installations] ADD [QuietHoursStart] time NULL"),
            (Table: "Installations", Column: "QuietHoursEnd",
             Sql: "ALTER TABLE [Installations] ADD [QuietHoursEnd] time NULL"),
            (Table: "Installations", Column: "NotifyOnRecovery",
             Sql: "ALTER TABLE [Installations] ADD [NotifyOnRecovery] bit NOT NULL DEFAULT 1"),

            // Battery storage opt-in.  Default 0 (no battery); a backfill
            // stage below flips it on for legacy rows that already carry
            // battery metadata so we don't lose existing hybrid sites.
            (Table: "Installations", Column: "HasBattery",
             Sql: "ALTER TABLE [Installations] ADD [HasBattery] bit NOT NULL DEFAULT 0"),
        };

        var indexUpgrades = new (string Name, string Table, string Columns)[]
        {
            ("IX_SystemAlerts_AcknowledgedByUserId", "SystemAlerts", "[AcknowledgedByUserId]"),
            ("IX_SystemAlerts_Severity", "SystemAlerts", "[Severity]"),
            ("IX_ServiceTickets_ReportedByUserId", "ServiceTickets", "[ReportedByUserId]"),
            ("IX_ServiceTickets_AssignedToUserId", "ServiceTickets", "[AssignedToUserId]"),
            ("IX_MaintenanceRecords_PerformedByUserId", "MaintenanceRecords", "[PerformedByUserId]"),
            ("IX_MaintenanceRecords_Status", "MaintenanceRecords", "[Status]"),
            ("IX_MaintenanceRecords_InstallationId_ScheduledDate", "MaintenanceRecords", "[InstallationId], [ScheduledDate]"),
            ("IX_WarrantyRecords_EquipmentId", "WarrantyRecords", "[EquipmentId]"),
            ("IX_WarrantyRecords_WarrantyType", "WarrantyRecords", "[WarrantyType]"),
            ("IX_CleaningRecords_PerformedByUserId", "CleaningRecords", "[PerformedByUserId]"),
            ("IX_CleaningRecords_Status", "CleaningRecords", "[Status]"),
        };

        // NOTE: column upgrades are NOT batched into one script any more.
        // A single 30-statement batch shares one command timeout, so one slow
        // or blocked ALTER took the whole batch down with it and NONE of the
        // other 29 columns got applied — and the log couldn't say which
        // statement was at fault.  They are now executed individually below,
        // each idempotent and independently timed.

        // ── Execute in stages so a single failure doesn't abort the rest ──
        // Stage 1: ALTER TABLE column additions (idempotent IF EXISTS / NOT EXISTS).
        // Stage 2: CREATE TABLE for the new tables, each in its own statement so
        //          we can pinpoint exactly which one failed.
        // Stage 3: CREATE INDEX for ancillary indexes.

        var stages = new List<(string Name, string Sql)>();

        // Stage 1 (column ALTERs) is no longer a stage — see the individual
        // per-column loop below, which replaced the single batched script.

        // Stage 1b: one-time backfill of HasBattery for legacy rows.  Runs
        // after the column exists.  Idempotent + safe: it only flips rows
        // that are still 0 AND carry battery metadata (capacity / count) or
        // are Hybrid systems.  Once an operator unchecks battery on a site
        // (which also nulls the metadata), this won't re-flip it.
        stages.Add(("BackfillHasBattery", @"
IF EXISTS (SELECT 1 FROM sys.columns
    WHERE Name = N'HasBattery' AND Object_ID = Object_ID(N'[Installations]'))
BEGIN
    UPDATE [Installations]
    SET [HasBattery] = 1
    WHERE [HasBattery] = 0
      AND ( ([BatteryCapacityKwh] IS NOT NULL AND [BatteryCapacityKwh] > 0)
            OR ([BatteryCount] IS NOT NULL AND [BatteryCount] > 0)
            OR [SystemType] = 3 );  -- 3 = SystemType.Hybrid
END"));

        // Stage 2: each table CREATE in its own stage. Failures in one don't block others.
        stages.Add(("CreateTable:AlertRules", @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AlertRules')
BEGIN
    CREATE TABLE [AlertRules] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(300) NULL,
        [Metric] int NOT NULL,
        [Operator] int NOT NULL,
        [Threshold] decimal(14,4) NOT NULL,
        [ConsecutiveChecks] int NOT NULL DEFAULT 1,
        [Severity] int NOT NULL,
        [AlertType] int NOT NULL,
        [InstallationId] int NULL,
        [BrandScope] int NULL,
        [IsEnabled] bit NOT NULL DEFAULT 1,
        [CooldownMinutes] int NOT NULL DEFAULT 60,
        [LastFiredAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [CreatedByUserId] nvarchar(450) NULL,
        [UpdatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_AlertRules_Installations FOREIGN KEY ([InstallationId])
            REFERENCES [Installations]([Id]) ON DELETE CASCADE,
        CONSTRAINT FK_AlertRules_AspNetUsers FOREIGN KEY ([CreatedByUserId])
            REFERENCES [AspNetUsers]([Id]) ON DELETE SET NULL
    );
    CREATE INDEX IX_AlertRules_IsEnabled ON [AlertRules]([IsEnabled]);
    CREATE INDEX IX_AlertRules_InstallationId ON [AlertRules]([InstallationId]);
    CREATE INDEX IX_AlertRules_Metric ON [AlertRules]([Metric]);
END"));

        stages.Add(("CreateTable:WorkOrders", @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrders')
BEGIN
    CREATE TABLE [WorkOrders] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [WorkOrderNumber] nvarchar(20) NOT NULL,
        [Type] int NOT NULL,
        [Status] int NOT NULL DEFAULT 0,
        [InstallationId] int NULL,
        [ExternalSiteName] nvarchar(200) NULL,
        [ExternalSiteAddress] nvarchar(500) NULL,
        [ExternalCustomerName] nvarchar(100) NULL,
        [ExternalCustomerPhone] nvarchar(50) NULL,
        [ExternalLatitude] float NULL,
        [ExternalLongitude] float NULL,
        [CustomerId] int NULL,
        [ScheduledDate] datetime2 NOT NULL,
        [ScheduledTime] time NULL,
        [EstimatedDurationMinutes] int NOT NULL DEFAULT 120,
        [ActualStartTime] datetime2 NULL,
        [ActualEndTime] datetime2 NULL,
        [RecurrenceMonths] int NULL,
        [ParentWorkOrderId] int NULL,
        [AssignedTeam] nvarchar(200) NULL,
        [AssignedToUserId] nvarchar(450) NULL,
        [CreatedByUserId] nvarchar(450) NULL,
        [Title] nvarchar(200) NOT NULL,
        [Description] nvarchar(max) NULL,
        [Scope] nvarchar(max) NULL,
        [SpecialInstructions] nvarchar(max) NULL,
        [LaborCost] decimal(12,2) NOT NULL DEFAULT 0,
        [MaterialCost] decimal(12,2) NOT NULL DEFAULT 0,
        [TravelCost] decimal(12,2) NOT NULL DEFAULT 0,
        [OtherCost] decimal(12,2) NOT NULL DEFAULT 0,
        [CostNotes] nvarchar(max) NULL,
        [BeforePhotos] nvarchar(max) NULL,
        [AfterPhotos] nvarchar(max) NULL,
        [Attachments] nvarchar(max) NULL,
        [CompletionNotes] nvarchar(max) NULL,
        [CustomerSignature] nvarchar(max) NULL,
        [CustomerSigned] bit NOT NULL DEFAULT 0,
        [CustomerRating] int NULL,
        [CustomerFeedback] nvarchar(max) NULL,
        [ServiceTicketId] int NULL,
        [ReminderSentAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [UpdatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX IX_WorkOrders_Number ON [WorkOrders]([WorkOrderNumber]);
    CREATE INDEX IX_WorkOrders_Status ON [WorkOrders]([Status]);
    CREATE INDEX IX_WorkOrders_ScheduledDate ON [WorkOrders]([ScheduledDate]);
    CREATE INDEX IX_WorkOrders_AssignedToUserId ON [WorkOrders]([AssignedToUserId]);
    CREATE INDEX IX_WorkOrders_Type ON [WorkOrders]([Type]);
    CREATE INDEX IX_WorkOrders_InstallationId ON [WorkOrders]([InstallationId]);
END"));

        stages.Add(("CreateTable:WorkOrderItems", @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderItems')
AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrders')
BEGIN
    CREATE TABLE [WorkOrderItems] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [WorkOrderId] int NOT NULL,
        [SortOrder] int NOT NULL DEFAULT 0,
        [Description] nvarchar(300) NOT NULL,
        [IsCompleted] bit NOT NULL DEFAULT 0,
        [CompletedAt] datetime2 NULL,
        [CompletedByUserId] nvarchar(450) NULL,
        [Notes] nvarchar(max) NULL,
        [Photos] nvarchar(max) NULL,
        CONSTRAINT FK_WorkOrderItems_WorkOrders FOREIGN KEY ([WorkOrderId])
            REFERENCES [WorkOrders]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX IX_WorkOrderItems_WorkOrderId_SortOrder ON [WorkOrderItems]([WorkOrderId], [SortOrder]);
END"));

        stages.Add(("CreateTable:WorkOrderPhotos", @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderPhotos')
AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrders')
BEGIN
    CREATE TABLE [WorkOrderPhotos] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [WorkOrderId] int NOT NULL,
        [Category] int NOT NULL,
        [Timing] int NOT NULL DEFAULT 1,
        [Description] nvarchar(500) NULL,
        [FilePath] nvarchar(500) NOT NULL,
        [ContentType] nvarchar(100) NULL,
        [FileSizeBytes] bigint NOT NULL DEFAULT 0,
        [Latitude] float NULL,
        [Longitude] float NULL,
        [UploadedByUserId] nvarchar(450) NULL,
        [UploadedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [SortOrder] int NOT NULL DEFAULT 0,
        CONSTRAINT FK_WorkOrderPhotos_WorkOrders FOREIGN KEY ([WorkOrderId])
            REFERENCES [WorkOrders]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX IX_WorkOrderPhotos_WO_Cat_Timing ON [WorkOrderPhotos]([WorkOrderId], [Category], [Timing]);
END"));

        stages.Add(("CreateTable:WorkOrderTypeTemplates", @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderTypeTemplates')
BEGIN
    CREATE TABLE [WorkOrderTypeTemplates] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [Type] int NOT NULL,
        [Title] nvarchar(200) NOT NULL DEFAULT '',
        [ChecklistItems] nvarchar(max) NOT NULL DEFAULT '',
        [UpdatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [UpdatedByUserId] nvarchar(450) NULL
    );
    CREATE UNIQUE INDEX IX_WorkOrderTypeTemplates_Type ON [WorkOrderTypeTemplates]([Type]);
END"));

        stages.Add(("CreateTable:NotificationRoutes", @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NotificationRoutes')
BEGIN
    CREATE TABLE [NotificationRoutes] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [InstallationId] int NULL,
        [EventType] int NOT NULL,
        [LineId] nvarchar(200) NOT NULL,
        [Label] nvarchar(100) NULL,
        [IsEnabled] bit NOT NULL DEFAULT 1,
        [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [CreatedByUserId] nvarchar(450) NULL,
        CONSTRAINT FK_NotificationRoutes_Installations FOREIGN KEY ([InstallationId])
            REFERENCES [Installations]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX IX_NotificationRoutes_EventType ON [NotificationRoutes]([EventType]);
    CREATE INDEX IX_NotificationRoutes_InstallationId ON [NotificationRoutes]([InstallationId]);
    CREATE INDEX IX_NotificationRoutes_IsEnabled ON [NotificationRoutes]([IsEnabled]);
END"));

        // Stage 3: ancillary indexes — one stage PER INDEX.  CREATE INDEX on a
        // table with real data can genuinely take tens of seconds on SQL
        // Express; batching them meant one slow index timed out the whole
        // script, rolled back, and left every index uncreated — so the same
        // slow statement was retried from scratch on every single startup.
        // Individually, the fast ones stick and only the slow one retries.
        foreach (var (name, table, cols) in indexUpgrades)
        {
            var bareCols = cols.Replace("[", "").Replace("]", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var colExistsClauses = string.Join(" AND ", bareCols.Select(c =>
                $"EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'{c}' AND Object_ID = Object_ID(N'[{table}]'))"));
            stages.Add(($"Index:{name}", $@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}')
    AND EXISTS (SELECT 1 FROM sys.tables WHERE name = '{table}')
    AND {colExistsClauses}
BEGIN CREATE INDEX [{name}] ON [{table}]({cols}); END"));
        }

        // ── Bounded execution ────────────────────────────────────────
        // Schema work runs on the startup path, so it must NEVER be able to
        // hold the app hostage.  Two guards:
        //   1. SET LOCK_TIMEOUT — an ALTER needs a schema-modification lock;
        //      if the previous app instance (or a running Hangfire job) still
        //      holds a lock on the table, the statement would otherwise wait
        //      the full command timeout.  Fail in 5s instead of 30s.
        //   2. A total budget — once spent we stop trying and let the app
        //      start.  Missing columns are logged loudly; the per-column
        //      retry below is best-effort, not a startup requirement.
        var schemaBudget = TimeSpan.FromSeconds(90);
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var previousTimeout = context.Database.GetCommandTimeout();
        context.Database.SetCommandTimeout(20);

        const string lockGuard = "SET LOCK_TIMEOUT 5000;\n";

        var failedStages = new List<string>();
        var budgetExhausted = false;

        // ── Read the existing schema ONCE ────────────────────────────
        // Catalog lookups are pathologically slow on this deployment
        // (~2.5s per sys.columns probe, vs ~20ms for a normal table query),
        // so probing each column separately cost 75s+ and blew the timeout —
        // to answer "already present" every single time.  One query returns
        // the whole picture, after which almost every upgrade is a pure
        // in-memory skip with no database round-trip at all.
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaKnown = false;
        try
        {
            #pragma warning disable EF1002
            var rows = await context.Database.SqlQueryRaw<string>(
                lockGuard +
                "SELECT t.name + '.' + c.name AS Value " +
                "FROM sys.columns c INNER JOIN sys.tables t ON c.object_id = t.object_id")
                .ToListAsync();
            #pragma warning restore EF1002
            foreach (var r in rows)
                if (!string.IsNullOrEmpty(r)) existingColumns.Add(r);
            schemaKnown = true;
            log($"DB: Schema snapshot loaded ({existingColumns.Count} columns) in {totalSw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            // Fall back to per-statement guarded ALTERs (slower, still correct).
            log($"DB: Schema snapshot unavailable ({ex.Message}) — falling back to per-column checks.");
        }

        // ── Column upgrades ──────────────────────────────────────────
        // Statements stay individually executed so one slow/blocked ALTER
        // cannot stop the rest, and the log names the exact failure.  Each
        // is still guarded server-side, so the snapshot is an optimisation
        // and never the sole source of truth.
        var applied = new List<string>();
        var columnFailures = new List<string>();
        var skippedColumns = 0;
        foreach (var (table, column, sql) in upgrades)
        {
            // Already present → nothing to do, and no round-trip to prove it.
            if (schemaKnown && existingColumns.Contains($"{table}.{column}"))
                continue;

            if (budgetExhausted || totalSw.Elapsed > schemaBudget)
            {
                budgetExhausted = true;
                skippedColumns++;
                continue;
            }
            try
            {
                #pragma warning disable EF1002
                await context.Database.ExecuteSqlRawAsync(lockGuard + $@"
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = N'{table}')
AND NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE Name = N'{column}' AND Object_ID = Object_ID(N'[{table}]'))
BEGIN {sql}; END");
                #pragma warning restore EF1002
                applied.Add($"{table}.{column}");
                log($"DB: Column upgrade applied: {table}.{column}");
            }
            catch (Exception ex)
            {
                columnFailures.Add($"{table}.{column}: {ex.Message}");
                log($"DB: Column upgrade {table}.{column} FAILED: {ex.Message}");
            }
        }
        if (applied.Count > 0)
            log($"DB: Applied {applied.Count} column upgrade(s): {string.Join(", ", applied)}");
        if (columnFailures.Count > 0)
            log($"DB: {columnFailures.Count} column upgrade(s) failed — pages using them may throw. " +
                $"Manual SQL needed for: {string.Join(" | ", columnFailures.Select(f => f.Split(':')[0]))}");
        if (skippedColumns > 0)
            log($"DB: Skipped {skippedColumns} column upgrade(s) — schema budget spent. Restart when the database is idle.");

        // Same trick for indexes: one catalog read, then skip the ones that
        // already exist without paying a round-trip each.
        var existingIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexesKnown = false;
        try
        {
            #pragma warning disable EF1002
            var idxRows = await context.Database.SqlQueryRaw<string>(
                lockGuard + "SELECT name AS Value FROM sys.indexes WHERE name IS NOT NULL")
                .ToListAsync();
            #pragma warning restore EF1002
            foreach (var r in idxRows)
                if (!string.IsNullOrEmpty(r)) existingIndexes.Add(r);
            indexesKnown = true;
        }
        catch { /* fall through to the guarded CREATE INDEX statements */ }

        foreach (var (stageName, stageSql) in stages)
        {
            if (string.IsNullOrWhiteSpace(stageSql)) continue;
            // "Index:<name>" stages are skippable when the index is present.
            if (indexesKnown && stageName.StartsWith("Index:", StringComparison.Ordinal)
                && existingIndexes.Contains(stageName["Index:".Length..]))
                continue;
            if (totalSw.Elapsed > schemaBudget)
            {
                budgetExhausted = true;
                log($"DB: Schema budget ({schemaBudget.TotalSeconds:0}s) exhausted — skipping stage '{stageName}' so the app can start.");
                continue;
            }
            try
            {
                #pragma warning disable EF1002
                await context.Database.ExecuteSqlRawAsync(lockGuard + stageSql);
                #pragma warning restore EF1002
            }
            catch (Exception ex)
            {
                failedStages.Add($"{stageName}: {ex.Message}");
                log($"DB: Schema stage '{stageName}' FAILED: {ex.Message}");
            }
        }

        totalSw.Stop();
        // Restore the ambient command timeout — the 20s cap was only for the
        // schema phase; normal app queries keep the provider default.
        context.Database.SetCommandTimeout(previousTimeout);

        if (budgetExhausted)
        {
            log($"DB: Schema upgrades TIMED OUT after {totalSw.ElapsedMilliseconds} ms and were cut short so the app could start. " +
                "This usually means the database was busy or locked (e.g. the previous app instance's jobs were still running). " +
                "Restart once the database is idle to finish any remaining changes.");
        }
        else if (failedStages.Count == 0)
        {
            log($"DB: Schema upgrades applied successfully ({stages.Count} stage(s), {totalSw.ElapsedMilliseconds} ms)");
        }
        else
        {
            log($"DB: Schema upgrades completed with {failedStages.Count} failure(s) in {totalSw.ElapsedMilliseconds} ms. " +
                "See Admin → Startup Logs for details.");
        }
    }

    private static async Task<bool> TestSqlServerAsync(string connectionString, Action<string> log)
    {
        try
        {
            var csb = new SqlConnectionStringBuilder(connectionString);
            csb.ConnectTimeout = 5;
            using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();
            log($"DB: Connected to {csb.DataSource}, database: {csb.InitialCatalog}");
            return true;
        }
        catch (SqlException ex)
        {
            log($"DB: Connection failed - Error {ex.Number}: {ex.Message}");

            // Self-diagnose common issues
            switch (ex.Number)
            {
                case -1:
                case 2:
                    log("DB: FIX -> SQL Server is not running. Start it: Services > 'SQL Server (MSSQLSERVER)' > Start");
                    log("DB: FIX -> Or check server name in connection string. Common names: localhost, .\\SQLEXPRESS, (localdb)\\mssqllocaldb");
                    break;
                case 4060:
                    log("DB: FIX -> Database doesn't exist yet. Will be auto-created.");
                    break;
                case 18456:
                    log("DB: FIX -> Login failed. If using Trusted_Connection=True, ensure your Windows user has SQL access.");
                    log("DB: FIX -> Or use SQL auth: User Id=sa;Password=YourPassword");
                    break;
                case 233:
                    log("DB: FIX -> Named pipes/TCP not enabled. Enable TCP/IP in SQL Server Configuration Manager.");
                    break;
                default:
                    log($"DB: FIX -> Check SQL Server error number {ex.Number} for details");
                    break;
            }
            return false;
        }
    }

    private static async Task EnsureDatabaseExistsAsync(string connectionString, Action<string> log)
    {
        try
        {
            var csb = new SqlConnectionStringBuilder(connectionString);
            var dbName = csb.InitialCatalog;

            if (string.IsNullOrEmpty(dbName))
            {
                log("DB: ERROR - No database name in connection string!");
                return;
            }

            // Connect to master to create database
            csb.InitialCatalog = "master";
            csb.ConnectTimeout = 10;

            using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @db)
                BEGIN
                    CREATE DATABASE [{dbName}];
                    SELECT 'CREATED';
                END
                ELSE SELECT 'EXISTS';";
            cmd.Parameters.AddWithValue("@db", dbName);

            var result = (await cmd.ExecuteScalarAsync())?.ToString();
            log($"DB: Database '{dbName}' -> {result}");
        }
        catch (Exception ex)
        {
            log($"DB: Could not ensure database exists: {ex.Message}");
        }
    }

    private static async Task<bool> CheckTablesExistAsync(ApplicationDbContext context, Action<string> log)
    {
        var requiredTables = new[]
        {
            "AspNetUsers", "AspNetRoles", "AspNetUserRoles",
            "Installations", "Customers", "Partners", "Equipment",
            "EnergyData", "SystemAlerts", "MaintenanceRecords",
            "ServiceTickets", "SystemSettings"
        };

        var missingTables = new List<string>();

        foreach (var table in requiredTables)
        {
            try
            {
                #pragma warning disable EF1002
                await context.Database.ExecuteSqlRawAsync($"SELECT TOP 0 * FROM [{table}]");
                #pragma warning restore EF1002
            }
            catch
            {
                missingTables.Add(table);
            }
        }

        if (missingTables.Count == 0)
        {
            log($"DB: All {requiredTables.Length} tables verified OK");
            return true;
        }

        log($"DB: Missing {missingTables.Count} tables: {string.Join(", ", missingTables)}");
        return false;
    }

    private static async Task DumpTableListAsync(ApplicationDbContext context, Action<string> log)
    {
        try
        {
            var tables = new List<string>();
            using var cmd = context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
            await context.Database.OpenConnectionAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));

            log($"DB: Existing tables ({tables.Count}): {(tables.Count > 0 ? string.Join(", ", tables) : "NONE")}");
        }
        catch (Exception ex)
        {
            log($"DB: Could not list tables: {ex.Message}");
        }
    }

    private static async Task VerifyDatabaseAsync(ApplicationDbContext context, Action<string> log)
    {
        try
        {
            var userCount = await context.Users.CountAsync();
            var roleCount = await context.Roles.CountAsync();
            var settingCount = await context.SystemSettings.CountAsync();
            log($"DB: Verification OK - Users: {userCount}, Roles: {roleCount}, Settings: {settingCount}");
        }
        catch (Exception ex)
        {
            log($"DB: Verification FAILED: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  API ENDPOINTS
    // ══════════════════════════════════════════════════════════════

    public static void MapApiEndpoints(WebApplication app)
    {
        // ─── Health / keep-alive endpoint ───
        // Safe for anonymous access: returns app status + DB connectivity + last
        // Hangfire job run. External uptime monitors (UptimeRobot, Windows
        // Task Scheduler, etc.) ping this to keep the IIS process alive, so
        // background jobs (LINE alerts, sync) keep running with no users online.
        app.MapGet("/health", async (Data.ApplicationDbContext db) =>
        {
            var info = new
            {
                status = "ok",
                time = DateTime.UtcNow,
                appUptime = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
                version = typeof(Startup).Assembly.GetName().Version?.ToString() ?? "unknown",
                dbConnected = await db.Database.CanConnectAsync(),
                installations = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .CountAsync(db.Installations)
            };
            return Results.Json(info);
        }).AllowAnonymous();

        // Minimal liveness probe — no DB, no auth. For load balancers.
        app.MapGet("/ping", () => Results.Text("pong")).AllowAnonymous();

        // Customer lookup by ID (used by Installation Edit page to populate inline edit fields).
        // Access control: Admin sees any; Partner sees only customers they have an installation
        // with; everyone else (incl. Customer role on someone else's data) is denied.
        app.MapGet("/api/customers/{id:int}", async (
            int id,
            ApplicationDbContext db,
            UserManager<Models.ApplicationUser> userManager,
            System.Security.Claims.ClaimsPrincipal principal) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user == null) return Results.Unauthorized();

            var isAdmin = await userManager.IsInRoleAsync(user, "Admin");
            if (!isAdmin)
            {
                // Partner: must share at least one installation with this customer
                if (user.PartnerId.HasValue)
                {
                    var partnerSharesCustomer = await db.Installations.AnyAsync(i =>
                        i.CustomerId == id && i.PartnerId == user.PartnerId);
                    if (!partnerSharesCustomer) return Results.Forbid();
                }
                else
                {
                    // Customer can only see themselves
                    var ownCustomerId = await db.Customers
                        .Where(c => c.UserId == user.Id)
                        .Select(c => (int?)c.Id)
                        .FirstOrDefaultAsync();
                    if (ownCustomerId != id) return Results.Forbid();
                }
            }

            var c = await db.Customers
                .Where(c => c.Id == id && c.IsActive)
                .Select(c => new { c.Id, c.FirstName, c.LastName, c.Phone, c.Email, c.Address })
                .FirstOrDefaultAsync();
            return c == null ? Results.NotFound() : Results.Json(c);
        }).RequireAuthorization();

        // Reverse-geocode lat/lng → address (Nominatim/OSM)
        app.MapGet("/api/geocode/reverse", async (double lat, double lng, string? lang,
            Services.Geocoding.IGeocodingService geocoder) =>
        {
            if (Math.Abs(lat) < 0.0001 && Math.Abs(lng) < 0.0001)
                return Results.BadRequest(new { error = "Coordinates (0,0) look empty — please provide valid lat/lng" });
            var address = await geocoder.ReverseAsync(lat, lng, lang);
            return address == null
                ? Results.Json(new { success = false, address = (string?)null,
                    message = "Reverse geocoding failed (Nominatim unreachable or no result)" })
                : Results.Json(new { success = true, address });
        }).RequireAuthorization();

        // Forward geocode (search) for the admin search box
        app.MapGet("/api/geocode/search", async (string q,
            Services.Geocoding.IGeocodingService geocoder) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length < 3)
                return Results.BadRequest(new { error = "Query must be at least 3 characters" });
            var result = await geocoder.SearchAsync(q);
            return result == null
                ? Results.Json(Services.Api.ApiResponse.NotFound("No results"))
                : Results.Json(new { success = true, lat = result.Value.Lat, lng = result.Value.Lng, displayName = result.Value.DisplayName });
        }).RequireAuthorization();

        // Refresh device info / inverter metadata from cloud for an installation
        // Send a one-off test message to the LINE channel configured on the
        // installation (LineGroupId override → falls back to Customer.LineUserId).
        // Operator-only, so the admin can verify the recipient resolves and
        // the channel token is valid without waiting for a real alert.
        // Diagnostic: dump raw Hoymiles historical responses for a site
        // so we can finally see what each endpoint actually returns.
        // Reveals whether responses are JSON / binary / 404 / wrong shape
        // — when standard backfill keeps returning 0 records.
        // Admin-only because it exposes raw API payload.
        app.MapGet("/api/installations/{id:int}/hoymiles-probe", async (
            int id,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<Models.ApplicationUser> userManager,
            Data.ApplicationDbContext db,
            Services.InverterApi.IInverterApiAdapterFactory adapterFactory,
            Services.Security.ICredentialProtector protector) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user == null) return Results.Unauthorized();
            if (!await userManager.IsInRoleAsync(user, "Admin")) return Results.Forbid();

            var inst = await db.Installations.FirstOrDefaultAsync(i => i.Id == id);
            if (inst == null) return Results.NotFound();
            if (inst.InverterBrand != Models.Enums.InverterBrand.Hoymiles)
                return Results.BadRequest(new { error = "Hoymiles only" });

            var adapter = adapterFactory.GetAdapter(inst.InverterBrand);
            if (adapter is not Services.InverterApi.Adapters.HoymilesAdapter h)
                return Results.BadRequest(new { error = "adapter is not HoymilesAdapter" });

            var creds = new Services.InverterApi.Models.ApiCredentials
            {
                ApiKey = protector.Unprotect(inst.CloudApiKey),
                ApiSecret = protector.Unprotect(inst.CloudApiSecret),
                Username = protector.Unprotect(inst.CloudUsername),
                Password = protector.Unprotect(inst.CloudPassword),
                PlantId = inst.PlantId,
                ApiBaseUrl = inst.CloudApiBaseUrl,
            };
            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1).ToString("yyyy-MM-dd");
            var probes = await h.ProbeHistoricalEndpointsAsync(creds, monthStart, today.Year);
            return Results.Json(new
            {
                brand = "Hoymiles",
                plantId = inst.PlantId,
                today = today.ToString("yyyy-MM-dd"),
                // Version stamp — bump this string whenever the probe
                // payload shape changes so the operator can tell at a
                // glance whether the running backend matches the latest
                // UI expectation.  When the UI sees a value other than
                // its own expectation, it tells the operator to redeploy.
                probeVersion = "v5-2026-05-29-month-merge",
                probes,
            });
        }).RequireAuthorization();

        // FoxESS endpoint probe — same idea as the Hoymiles one: ask the
        // vendor cloud directly which endpoints/parameters it accepts and
        // report its own errno + msg, instead of inferring from the spec.
        app.MapGet("/api/installations/{id:int}/foxess-probe", async (
            int id,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<Models.ApplicationUser> userManager,
            Data.ApplicationDbContext db,
            Services.InverterApi.IInverterApiAdapterFactory adapterFactory,
            Services.Security.ICredentialProtector protector) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user == null) return Results.Unauthorized();
            if (!await userManager.IsInRoleAsync(user, "Admin")) return Results.Forbid();

            var inst = await db.Installations.FirstOrDefaultAsync(i => i.Id == id);
            if (inst == null) return Results.NotFound();
            if (inst.InverterBrand != Models.Enums.InverterBrand.FoxESS)
                return Results.BadRequest(new { error = "FoxESS only" });

            var adapter = adapterFactory.GetAdapter(inst.InverterBrand);
            if (adapter is not Services.InverterApi.Adapters.FoxEssAdapter f)
                return Results.BadRequest(new { error = "adapter is not FoxEssAdapter" });

            var creds = new Services.InverterApi.Models.ApiCredentials
            {
                ApiKey = protector.Unprotect(inst.CloudApiKey),
                ApiSecret = protector.Unprotect(inst.CloudApiSecret),
                Username = protector.Unprotect(inst.CloudUsername),
                Password = protector.Unprotect(inst.CloudPassword),
                PlantId = inst.PlantId,
                ApiBaseUrl = inst.CloudApiBaseUrl,
            };
            var probes = await f.ProbeEndpointsAsync(creds);
            return Results.Json(new
            {
                brand = "FoxESS",
                plantId = string.IsNullOrWhiteSpace(inst.PlantId) ? "(not set)" : inst.PlantId,
                hasApiKey = !string.IsNullOrEmpty(inst.CloudApiKey),
                serverTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                probeVersion = "v1-2026-08-04-foxess",
                probes,
            });
        }).RequireAuthorization();

        // Brand-agnostic backfill probe.  Runs the live adapter end-to-end
        // (TestConnection → GetPlantInfo → GetHistorical for current month
        // / last 30d / last 90d / current year) and reports per-step
        // ApiResult.Kind, Message, Duration, and a small sample of the
        // records returned.  This is what we show on the Performance →
        // Detail page for non-Hoymiles brands so the operator can tell at
        // a glance whether the backfill button is going to do anything.
        //
        // Admin-only — exposes plant id, sample dates, and the technical
        // error message from the vendor cloud.
        app.MapGet("/api/installations/{id:int}/backfill-probe", async (
            int id,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<Models.ApplicationUser> userManager,
            Data.ApplicationDbContext db,
            Services.InverterApi.IInverterApiAdapterFactory adapterFactory,
            Services.Security.ICredentialProtector protector) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user == null) return Results.Unauthorized();
            if (!await userManager.IsInRoleAsync(user, "Admin")) return Results.Forbid();

            var inst = await db.Installations.FirstOrDefaultAsync(i => i.Id == id);
            if (inst == null) return Results.NotFound();

            var adapter = adapterFactory.GetAdapter(inst.InverterBrand);
            var creds = new Services.InverterApi.Models.ApiCredentials
            {
                ApiKey = protector.Unprotect(inst.CloudApiKey),
                ApiSecret = protector.Unprotect(inst.CloudApiSecret),
                Username = protector.Unprotect(inst.CloudUsername),
                Password = protector.Unprotect(inst.CloudPassword),
                PlantId = inst.PlantId,
                ApiBaseUrl = inst.CloudApiBaseUrl,
            };

            var today = SolarApp.Services.SiteTime.Today;
            var steps = new List<object>();

            // ── Step 1: TestConnection ────────────────────────────────
            var conn = await adapter.TestConnectionResultAsync(creds);
            steps.Add(new
            {
                step = "TestConnection",
                ok = conn.IsSuccess,
                kind = conn.Kind.ToString(),
                message = conn.Message,
                durationMs = (int)conn.Duration.TotalMilliseconds,
                httpStatus = conn.HttpStatusCode,
                technical = conn.TechnicalDetail,
            });

            // ── Step 2: GetPlantInfo ──────────────────────────────────
            var plant = await adapter.GetPlantInfoResultAsync(creds);
            steps.Add(new
            {
                step = "GetPlantInfo",
                ok = plant.IsSuccess,
                kind = plant.Kind.ToString(),
                message = plant.Message,
                durationMs = (int)plant.Duration.TotalMilliseconds,
                plant = plant.Data == null ? null : new
                {
                    plantId = plant.Data.PlantId,
                    plantName = plant.Data.PlantName,
                    capacityKw = plant.Data.CapacityKw,
                },
                technical = plant.TechnicalDetail,
            });

            // ── Step 3+: GetHistorical at 2 windows ───────────────────
            // Two windows are enough: last30d shows recent fetch behaviour,
            // currentYear shows whether the adapter can pull bulk history.
            // We deliberately do NOT enable HuaweiRateGate.DiagnosticBypass
            // here — bypassing would let the probe fire ~10 rapid month
            // calls and earn an extended 407 cooldown that breaks the real
            // background sync for normal users.
            var windows = new (string Label, DateTime From, DateTime To)[]
            {
                ("last30d",     today.AddDays(-30), today),
                ("currentYear", new DateTime(today.Year, 1, 1), today),
            };
            foreach (var (label, from, to) in windows)
            {
                var hist = await adapter.GetHistoricalResultAsync(creds, from, to);
                var rows = hist.Data ?? new List<Services.InverterApi.Models.EnergyRecord>();
                steps.Add(new
                {
                    step = $"GetHistorical({label})",
                    ok = hist.IsSuccess,
                    kind = hist.Kind.ToString(),
                    message = hist.Message,
                    durationMs = (int)hist.Duration.TotalMilliseconds,
                    fromDate = from.ToString("yyyy-MM-dd"),
                    toDate = to.ToString("yyyy-MM-dd"),
                    recordCount = rows.Count,
                    nonZeroDays = rows.Count(r => r.EnergyGenerationKwh > 0m),
                    totalKwh = rows.Sum(r => r.EnergyGenerationKwh),
                    sampleFirst = rows.Take(3).Select(r => new
                    {
                        d = r.Timestamp.ToString("yyyy-MM-dd"),
                        kwh = r.EnergyGenerationKwh,
                    }).ToArray(),
                    sampleLast = rows.OrderByDescending(r => r.Timestamp).Take(3).Select(r => new
                    {
                        d = r.Timestamp.ToString("yyyy-MM-dd"),
                        kwh = r.EnergyGenerationKwh,
                    }).ToArray(),
                    technical = hist.TechnicalDetail,
                });
            }

            // ── Brand-specific diagnostic chips ──────────────────────
            // Huawei's NBI throttle is enforced PER ACCOUNT process-wide,
            // so a probe that ran a few seconds after TestConnection will
            // see the historical calls defer (0ms response, 0 records).
            // Surface the rate-gate state so the operator can tell apart
            // "API returned no rows" from "rate gate hadn't released".
            object? extra = null;
            if (inst.InverterBrand == Models.Enums.InverterBrand.Huawei)
            {
                var key = Services.InverterApi.Common.HuaweiRateGate
                    .AccountKey(creds.Username);
                var waitSec = Services.InverterApi.Common.HuaweiRateGate
                    .GetRequiredWait(key).TotalSeconds;
                extra = new
                {
                    huaweiRateGate = new
                    {
                        cooldownRemainingSeconds = Math.Round(waitSec, 1),
                        explanation = waitSec > 1
                            ? $"Huawei NBI is rate-limited PER ACCOUNT to ~1 call/minute. " +
                              $"GetHistorical calls in this probe were likely deferred " +
                              $"because of {waitSec:0}s cooldown left.  Real Hangfire sync " +
                              $"still works — it just spaces calls over time."
                            : "Rate gate is clear — GetHistorical results above are real.",
                    },
                };
            }

            return Results.Json(new
            {
                brand = inst.InverterBrand.ToString(),
                plantId = inst.PlantId,
                today = today.ToString("yyyy-MM-dd"),
                probeVersion = "v3-2026-05-29-goodwe-discover",
                steps,
                extra,
            });
        }).RequireAuthorization();


        // operator can verify (or copy) what's stored without resetting it.
        // Strictly Admin-only — Partners with edit access can still SAVE new
        // credentials but should not be able to read existing ones back in
        // clear text.  Every reveal is logged so the admin audit trail
        // shows who looked at what and when.
        // Manually pull historical daily-energy data from the inverter
        // brand's cloud and upsert into [EnergyData].  Useful when:
        //   * Site was added late and we missed months of production
        //   * Auto-backfill hit rate-limits and gave up early
        //   * Vendor's portal shows numbers the customer wants reflected
        //   * Bad rows from earlier adapter bugs need replacement
        // Returns counts so the UI can show a progress summary.
        //
        // Query params:
        //   days       1 — 1095, default 365
        //   cleanFirst true → delete every existing row in [from, to]
        //              before writing the API result.  Use this to
        //              recover from inflated readings produced by the
        //              old sum-fallback bug.  Default false (safer:
        //              upsert-merge).
        app.MapPost("/api/installations/{id:int}/backfill", async (
            int id,
            int? days,
            bool? cleanFirst,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Data.ApplicationDbContext db,
            Services.InverterApi.IInverterApiAdapterFactory adapterFactory,
            Services.Security.ICredentialProtector protector,
            ILogger<Program> logger) =>
        {
            if (!await access.CanAccessAsync(id, user)) return Results.Forbid();
            var inst = await db.Installations
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (inst == null) return Results.NotFound();
            if (string.IsNullOrEmpty(inst.PlantId))
                return Results.Json(new { success = false, message = "No PlantId — set it first" });

            var requestedDays = Math.Clamp(days ?? 365, 1, 365 * 3);
            var toDate = DateTime.UtcNow.Date;
            var fromDate = toDate.AddDays(-requestedDays);

            var creds = new Services.InverterApi.Models.ApiCredentials
            {
                ApiKey = protector.Unprotect(inst.CloudApiKey),
                ApiSecret = protector.Unprotect(inst.CloudApiSecret),
                Username = protector.Unprotect(inst.CloudUsername),
                Password = protector.Unprotect(inst.CloudPassword),
                PlantId = inst.PlantId,
                ApiBaseUrl = inst.CloudApiBaseUrl,
            };

            List<Services.InverterApi.Models.EnergyRecord> historical;
            string? diagnostic = null;
            string? authMessage = null;
            bool authOk = false;
            try
            {
                var adapter = adapterFactory.GetAdapter(inst.InverterBrand);

                // Pre-flight: did login + a baseline API call actually work?
                // When historical returns empty list it's usually an auth
                // problem (token expired, password reset on the vendor
                // portal, region wrong) — but the adapter swallows the
                // exception and returns empty.  TestConnectionAsync uses
                // the same login path AND a real endpoint, so its result
                // tells us whether the credentials work at all.
                try
                {
                    var test = await adapter.TestConnectionAsync(creds);
                    authOk = test.Success;
                    authMessage = test.Message;
                }
                catch (Exception authEx)
                {
                    authOk = false;
                    authMessage = $"TestConnection threw {authEx.GetType().Name}: {authEx.Message}";
                }

                historical = await adapter.GetHistoricalDataAsync(creds, fromDate, toDate);

                if (historical.Count == 0 && !authOk)
                    diagnostic = "Auth check failed — " + (authMessage ?? "no detail");
                else if (historical.Count == 0)
                    diagnostic = $"Adapter returned empty list but auth check OK ({authMessage}). " +
                                 "The vendor cloud may not have data for this window, or the per-brand historical endpoint " +
                                 "is unsupported on this account.  Check server log for adapter-specific WARN entries.";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backfill failed for installation {Id}", id);
                return Results.Json(new
                {
                    success = false,
                    message = $"{ex.GetType().Name}: {ex.Message}",
                    inner = ex.InnerException?.Message,
                    diagnostic = "Adapter threw an unhandled exception — see server log",
                });
            }

            // Sanity ceiling — reject implausibly high rows from a buggy
            // adapter so backfill never poisons the dataset with values it
            // would then refuse to lower (monotonic guard).
            var plausibleMax = inst.SystemCapacityKw > 0
                ? inst.SystemCapacityKw * 18m
                : decimal.MaxValue;
            int rejected = 0;
            historical = historical.Where(h =>
            {
                if (h.EnergyGenerationKwh > plausibleMax)
                {
                    logger.LogWarning(
                        "Backfill rejected implausible {Energy} kWh on {Date} for {Site} (cap {Cap} kWp, max {Max})",
                        h.EnergyGenerationKwh, h.Timestamp.Date, inst.SiteName, inst.SystemCapacityKw, plausibleMax);
                    rejected++;
                    return false;
                }
                return true;
            }).ToList();

            if (historical.Count == 0)
                return Results.Json(new {
                    success = false,
                    message = $"ดึงไม่ได้ — adapter คืน 0 records (rejected {rejected} implausible)",
                    inserted = 0, updated = 0, deleted = 0, rejected,
                    fromDate = fromDate.ToString("yyyy-MM-dd"),
                    toDate = toDate.ToString("yyyy-MM-dd"),
                    authOk,
                    authMessage,
                    diagnostic,
                    brand = inst.InverterBrand.ToString(),
                    plantId = inst.PlantId,
                });

            // Clean-first mode: wipe every existing row in the requested
            // window so the API result becomes the source of truth.
            int deleted = 0;
            if (cleanFirst == true)
            {
                var doomed = await db.EnergyData
                    .Where(e => e.InstallationId == id
                        && e.Granularity == Models.Enums.DataGranularity.Daily
                        && e.Timestamp >= fromDate && e.Timestamp <= toDate)
                    .ToListAsync();
                deleted = doomed.Count;
                db.EnergyData.RemoveRange(doomed);
                await db.SaveChangesAsync();
            }

            // Batch-load remaining existing dates so we know insert vs update
            var minDate = historical.Min(h => h.Timestamp.Date);
            var maxDate = historical.Max(h => h.Timestamp.Date);
            var existing = await db.EnergyData
                .Where(e => e.InstallationId == id
                    && e.Granularity == Models.Enums.DataGranularity.Daily
                    && e.Timestamp >= minDate && e.Timestamp <= maxDate)
                .ToDictionaryAsync(e => e.Timestamp.Date, e => e);

            int inserted = 0, updated = 0;
            foreach (var h in historical)
            {
                if (existing.TryGetValue(h.Timestamp.Date, out var row))
                {
                    if (row.EnergyGenerationKwh != h.EnergyGenerationKwh) updated++;
                    row.EnergyGenerationKwh = h.EnergyGenerationKwh;
                    if (h.GridExportKwh > 0) row.GridExportKwh = h.GridExportKwh;
                    if (h.GridImportKwh > 0) row.GridImportKwh = h.GridImportKwh;
                }
                else
                {
                    db.EnergyData.Add(new Models.EnergyData
                    {
                        InstallationId = id,
                        Timestamp = h.Timestamp.Date,
                        Granularity = Models.Enums.DataGranularity.Daily,
                        EnergyGenerationKwh = h.EnergyGenerationKwh,
                        GridExportKwh = h.GridExportKwh,
                        GridImportKwh = h.GridImportKwh,
                    });
                    inserted++;
                }
            }
            await db.SaveChangesAsync();

            return Results.Json(new
            {
                success = true,
                message = cleanFirst == true
                    ? $"Cleaned + backfilled — deleted {deleted}, inserted {inserted}, updated {updated}, rejected {rejected} implausible"
                    : $"Backfilled {historical.Count} days — inserted {inserted}, updated {updated}, rejected {rejected} implausible",
                inserted, updated, deleted, rejected,
                fromDate = fromDate.ToString("yyyy-MM-dd"),
                toDate = toDate.ToString("yyyy-MM-dd"),
            });
        }).RequireAuthorization();

        app.MapGet("/api/installations/{id:int}/credential/{field}", async (
            int id,
            string field,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<Models.ApplicationUser> userManager,
            Data.ApplicationDbContext db,
            Services.Security.ICredentialProtector protector,
            ILogger<Program> logger) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user == null) return Results.Unauthorized();
            if (!await userManager.IsInRoleAsync(user, "Admin"))
                return Results.Forbid();

            var inst = await db.Installations.FirstOrDefaultAsync(i => i.Id == id);
            if (inst == null) return Results.NotFound();

            string? encrypted = field switch
            {
                "apiKey"    => inst.CloudApiKey,
                "apiSecret" => inst.CloudApiSecret,
                "username"  => inst.CloudUsername,
                "password"  => inst.CloudPassword,
                _           => null,
            };
            if (field is null
                || (field != "apiKey" && field != "apiSecret"
                    && field != "username" && field != "password"))
                return Results.BadRequest(new { error = "Unknown field" });
            if (string.IsNullOrEmpty(encrypted))
                return Results.Json(new { value = "", note = "(empty)" });

            string? plain;
            try { plain = protector.Unprotect(encrypted); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to decrypt {Field} for installation {Id}", field, id);
                return Results.Json(new { value = "", note = "(decrypt failed — may be plaintext or wrong key)" });
            }

            // Audit trail — the same AuditableEntityInterceptor doesn't catch
            // pure reads, so emit our own row.
            db.Set<Models.AuditLog>().Add(new Models.AuditLog
            {
                UserId = user.Id,
                UserName = user.UserName,
                Action = "Reveal",
                EntityType = "Installation." + field,
                EntityId = id.ToString(),
                Timestamp = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            return Results.Json(new { value = plain ?? "" });
        }).RequireAuthorization();

        app.MapPost("/api/installations/{id:int}/notify-test", async (
            int id,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Data.ApplicationDbContext db,
            Services.LineApi.ILineMessagingService line) =>
        {
            if (!await access.CanAccessAsync(id, user)) return Results.Forbid();
            var install = await db.Installations
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (install == null) return Results.NotFound();

            var recipient = Services.LineApi.CustomerAlertMessageBuilder.PickRecipient(install);
            if (string.IsNullOrWhiteSpace(recipient))
                return Results.Json(new { success = false, message = "ไม่มี LineGroupId หรือ Customer.LineUserId ที่จะส่ง — กรอกในการตั้งค่าก่อน" });

            var msg = $"🧪 ทดสอบการแจ้งเตือนจากระบบ SolarApp\n\n" +
                      $"📍 ไซต์: {install.SiteName}\n" +
                      $"🕐 เวลา: {DateTime.UtcNow.ToLocalTime():dd/MM/yyyy HH:mm}\n\n" +
                      $"ถ้าคุณได้รับข้อความนี้ = การตั้งค่าถูกต้อง ✅";
            try
            {
                await line.SendMessageAsync(recipient, msg);
                return Results.Json(new { success = true, message = $"ส่งทดสอบแล้วไปยัง {recipient[..Math.Min(8, recipient.Length)]}…" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"ส่งล้มเหลว: {ex.Message}" });
            }
        }).RequireAuthorization();

        app.MapPost("/api/installations/{id:int}/refresh-devices", async (
            int id,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Services.DeviceImport.IDeviceImporter importer) =>
        {
            if (!await access.CanAccessAsync(id, user)) return Results.Forbid();
            var result = await importer.ImportAsync(id);
            return Results.Json(new
            {
                success = result.Success,
                message = result.Message,
                devicesFetched = result.DevicesFetched,
                equipmentCreated = result.EquipmentCreated,
                equipmentUpdated = result.EquipmentUpdated,
                installationNote = result.InstallationUpdate
            });
        }).RequireAuthorization();

        // Delete all equipment for an installation (useful before re-importing)
        app.MapDelete("/api/installations/{id:int}/equipment", async (
            int id,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Data.ApplicationDbContext db) =>
        {
            if (!await access.CanAccessAsync(id, user)) return Results.Forbid();
            var equipment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .ToListAsync(db.Equipment.Where(e => e.InstallationId == id));
            if (!equipment.Any())
                return Results.Json(new { success = true, message = "No equipment to delete", deleted = 0 });
            db.Equipment.RemoveRange(equipment);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true, message = $"Deleted {equipment.Count} equipment record(s)", deleted = equipment.Count });
        }).RequireAuthorization();

        app.MapGet("/api/reports/excel/{installationId:int}", async (
            int installationId, DateTime? from, DateTime? to,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Services.ReportExport.IReportExportService svc) =>
        {
            if (!await access.CanAccessAsync(installationId, user))
                return Results.Forbid();
            var f = from ?? DateTime.UtcNow.AddMonths(-1);
            var t = to ?? DateTime.UtcNow;
            return Results.File(await svc.ExportProductionToExcelAsync(installationId, f, t),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Report_{installationId}.xlsx");
        }).RequireAuthorization();

        app.MapGet("/api/reports/pdf/{installationId:int}", async (
            int installationId, DateTime? from, DateTime? to,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Services.ReportExport.IReportExportService svc) =>
        {
            if (!await access.CanAccessAsync(installationId, user))
                return Results.Forbid();
            var f = from ?? DateTime.UtcNow.AddMonths(-1);
            var t = to ?? DateTime.UtcNow;
            return Results.File(await svc.ExportProductionToPdfAsync(installationId, f, t),
                "application/pdf", $"Report_{installationId}.pdf");
        }).RequireAuthorization();

        // Manually sync data from cloud for a specific installation
        app.MapPost("/api/installations/{id:int}/sync", async (
            int id,
            System.Security.Claims.ClaimsPrincipal user,
            Services.Authorization.IInstallationAccessHelper access,
            Data.ApplicationDbContext db,
            Services.InverterApi.IInverterApiAdapterFactory factory,
            Services.Security.ICredentialProtector credProtector) =>
        {
            if (!await access.CanAccessAsync(id, user))
                return Results.Forbid();
            var installation = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.Installations, i => i.Id == id);
            if (installation == null)
                return Results.Json(Services.Api.ApiResponse.NotFound("Installation not found"));

            if (string.IsNullOrEmpty(installation.PlantId))
                return Results.Json(Services.Api.ApiResponse.ValidationError("No Plant/Station ID configured"));

            try
            {
                var adapter = factory.GetAdapter(installation.InverterBrand);
                var credentials = new Services.InverterApi.Models.ApiCredentials
                {
                    ApiKey = credProtector.Unprotect(installation.CloudApiKey),
                    ApiSecret = credProtector.Unprotect(installation.CloudApiSecret),
                    Username = credProtector.Unprotect(installation.CloudUsername),
                    Password = credProtector.Unprotect(installation.CloudPassword),
                    PlantId = installation.PlantId,
                    ApiBaseUrl = installation.CloudApiBaseUrl
                };

                var realtimeResult = await adapter.GetRealtimeResultAsync(credentials);
                if (realtimeResult.IsApiFailure)
                {
                    return Results.Json(new
                    {
                        success = false,
                        kind = realtimeResult.Kind.ToString(),
                        message = $"[{realtimeResult.Kind}] {realtimeResult.Message}",
                        detail = realtimeResult.TechnicalDetail,
                        durationMs = (int)realtimeResult.Duration.TotalMilliseconds,
                        httpStatus = realtimeResult.HttpStatusCode
                    });
                }
                var realtime = realtimeResult.Data!;

                // 1. Save realtime snapshot
                db.EnergyData.Add(new Models.EnergyData
                {
                    InstallationId = installation.Id,
                    Timestamp = DateTime.UtcNow,
                    Granularity = Models.Enums.DataGranularity.Realtime,
                    PowerGenerationKw = realtime.CurrentPowerKw,
                    EnergyGenerationKwh = realtime.TodayEnergyKwh,
                    GridExportKwh = realtime.GridExportKw ?? 0,
                    GridImportKwh = realtime.GridImportKw ?? 0,
                    BatterySocPercent = realtime.BatterySocPercent
                });

                // 2. Upsert today's daily aggregate (used by Dashboard/Reports charts)
                var today = DateTime.UtcNow.Date;
                var todayRecord = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .FirstOrDefaultAsync(db.EnergyData,
                        e => e.InstallationId == installation.Id
                          && e.Granularity == Models.Enums.DataGranularity.Daily
                          && e.Timestamp.Date == today);

                if (todayRecord == null)
                {
                    db.EnergyData.Add(new Models.EnergyData
                    {
                        InstallationId = installation.Id,
                        Timestamp = today,
                        Granularity = Models.Enums.DataGranularity.Daily,
                        EnergyGenerationKwh = realtime.TodayEnergyKwh,
                        GridExportKwh = realtime.GridExportKw ?? 0,
                        GridImportKwh = realtime.GridImportKw ?? 0,
                        SelfConsumptionKwh = Math.Max(0, realtime.TodayEnergyKwh - (realtime.GridExportKw ?? 0)),
                        BatterySocPercent = realtime.BatterySocPercent
                    });
                }
                else
                {
                    todayRecord.EnergyGenerationKwh = realtime.TodayEnergyKwh;
                    if (realtime.GridExportKw.HasValue) todayRecord.GridExportKwh = realtime.GridExportKw.Value;
                    if (realtime.GridImportKw.HasValue) todayRecord.GridImportKwh = realtime.GridImportKw.Value;
                    todayRecord.SelfConsumptionKwh = Math.Max(0, realtime.TodayEnergyKwh - todayRecord.GridExportKwh);
                    todayRecord.BatterySocPercent = realtime.BatterySocPercent;
                }

                // 3. Fetch last 30 days historical + save if missing
                var histFrom = today.AddDays(-30);
                var historicalRecords = await adapter.GetHistoricalDataAsync(credentials, histFrom, today.AddDays(-1));
                var addedHistorical = 0;
                foreach (var h in historicalRecords)
                {
                    var exists = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                        .AnyAsync(db.EnergyData,
                            e => e.InstallationId == installation.Id
                              && e.Granularity == Models.Enums.DataGranularity.Daily
                              && e.Timestamp.Date == h.Timestamp.Date);
                    if (!exists)
                    {
                        db.EnergyData.Add(new Models.EnergyData
                        {
                            InstallationId = installation.Id,
                            Timestamp = h.Timestamp.Date,
                            Granularity = Models.Enums.DataGranularity.Daily,
                            EnergyGenerationKwh = h.EnergyGenerationKwh,
                            GridExportKwh = h.GridExportKwh,
                            GridImportKwh = h.GridImportKwh,
                            SelfConsumptionKwh = h.SelfConsumptionKwh,
                            BatteryChargeKwh = h.BatteryChargeKwh,
                            BatteryDischargeKwh = h.BatteryDischargeKwh
                        });
                        addedHistorical++;
                    }
                }

                // 4. Fetch alarms (last 7 days)
                var alarmRecords = await adapter.GetAlarmsAsync(credentials, today.AddDays(-7), DateTime.UtcNow);
                var addedAlarms = 0;
                foreach (var a in alarmRecords)
                {
                    var exists = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                        .AnyAsync(db.SystemAlerts,
                            x => x.InstallationId == installation.Id
                              && x.Title == a.AlarmName
                              && x.OccurredAt == a.OccurredAt);
                    if (!exists)
                    {
                        db.SystemAlerts.Add(new Models.SystemAlert
                        {
                            InstallationId = installation.Id,
                            AlertType = Models.Enums.AlertType.Error,
                            Severity = a.Severity switch
                            {
                                1 => Models.Enums.AlertSeverity.Critical,
                                2 => Models.Enums.AlertSeverity.High,
                                3 => Models.Enums.AlertSeverity.Medium,
                                _ => Models.Enums.AlertSeverity.Low
                            },
                            Title = a.AlarmName,
                            Message = $"{a.Description ?? a.AlarmName} (Device: {a.DeviceName ?? "N/A"})",
                            OccurredAt = a.OccurredAt,
                            ResolvedAt = a.ClearedAt,
                            IsNotified = a.ClearedAt.HasValue
                        });
                        addedAlarms++;
                    }
                }

                // LastOnlineAt reflects cloud reachability, not device producing state.
                // We got here via a successful API call, so update regardless of
                // device-level online/offline reporting.
                installation.LastOnlineAt = DateTime.UtcNow;
                if (installation.Status == Models.Enums.InstallationStatus.Offline)
                    installation.Status = Models.Enums.InstallationStatus.Active;

                await db.SaveChangesAsync();

                return Results.Json(new
                {
                    success = true,
                    message = $"Synced! Realtime + {addedHistorical} daily records + {addedAlarms} alarms",
                    data = new
                    {
                        timestamp = realtime.Timestamp,
                        currentPowerKw = realtime.CurrentPowerKw,
                        todayEnergyKwh = realtime.TodayEnergyKwh,
                        totalEnergyKwh = realtime.TotalEnergyKwh,
                        gridExportKw = realtime.GridExportKw,
                        gridImportKw = realtime.GridImportKw,
                        batterySocPercent = realtime.BatterySocPercent,
                        batteryPowerKw = realtime.BatteryPowerKw,
                        isOnline = realtime.IsOnline,
                        historicalDaysAdded = addedHistorical,
                        alarmsAdded = addedAlarms
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(Services.Api.ApiResponse.Fail($"Sync error: {ex.Message}", "SYNC_ERROR"));
            }
        }).RequireAuthorization();

        // Test inverter cloud API connection.
        // If installationId is supplied, missing credential fields are back-filled
        // from the stored (encrypted) values for that installation. This lets the
        // Edit page test without the user re-typing saved credentials.
        app.MapPost("/api/test-inverter-connection", async (
            HttpRequest req,
            Services.InverterApi.IInverterApiAdapterFactory factory,
            Data.ApplicationDbContext db,
            Services.Security.ICredentialProtector credProtector) =>
        {
            try
            {
                var form = await req.ReadFormAsync();
                if (!Enum.TryParse<Models.Enums.InverterBrand>(form["brand"].ToString(), out var brand))
                    return Results.Json(Services.Api.ApiResponse.ValidationError("Invalid brand"));

                // Trim every pasted credential.  Copy-paste from a vendor
                // portal very often carries a trailing space/newline, which
                // silently breaks signature-based auth (the value is signed
                // with the whitespace but sent without it) — and made Test
                // Connection fail while a saved-then-synced value worked,
                // because Save already trims.
                var apiKey    = form["apiKey"].ToString().Trim();
                var apiSecret = form["apiSecret"].ToString().Trim();
                var username  = form["username"].ToString().Trim();
                var password  = form["password"].ToString().Trim();
                var plantId   = form["plantId"].ToString().Trim();
                var apiBaseUrl = form["apiBaseUrl"].ToString().Trim();
                // ConnectionMethod is sent by the UI dropdown.  When provided,
                // adapters honor it instead of auto-detecting from which
                // fields are populated — auto-detect produces the wrong
                // mode when stored credentials linger from a previous attempt.
                Models.Enums.ConnectionMethod? connectionMethod = null;
                if (Enum.TryParse<Models.Enums.ConnectionMethod>(
                        form["connectionMethod"].ToString(), ignoreCase: true, out var cm))
                    connectionMethod = cm;

                // Fill any blank field from the stored installation credentials.
                // Empty input from the form = "use the saved value" (same as Save).
                if (int.TryParse(form["installationId"].ToString(), out var installId) && installId > 0)
                {
                    var inst = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                        .FirstOrDefaultAsync(db.Installations, i => i.Id == installId);
                    if (inst != null)
                    {
                        // Access control: verify caller can access this installation.
                        // A null principal must be REJECTED, not skipped — the old
                        // `if (currentUser != null)` let an unauthenticated edge case
                        // fall through to back-fill stored (decrypted) credentials.
                        var userManager = req.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser>>();
                        var currentUser = await userManager.GetUserAsync(req.HttpContext.User);
                        if (currentUser == null)
                            return Results.Json(Services.Api.ApiResponse.AccessDenied("Not authenticated."));
                        var isAdmin = await userManager.IsInRoleAsync(currentUser, "Admin");
                        var isPartnerMatch = currentUser.PartnerId.HasValue && inst.PartnerId == currentUser.PartnerId;
                        if (!isAdmin && !isPartnerMatch)
                            return Results.Json(Services.Api.ApiResponse.AccessDenied("Access denied to this installation"));
                        if (string.IsNullOrEmpty(apiKey))
                            apiKey = credProtector.Unprotect(inst.CloudApiKey) ?? "";
                        if (string.IsNullOrEmpty(apiSecret))
                            apiSecret = credProtector.Unprotect(inst.CloudApiSecret) ?? "";
                        if (string.IsNullOrEmpty(username))
                            username = credProtector.Unprotect(inst.CloudUsername) ?? "";
                        if (string.IsNullOrEmpty(password))
                            password = credProtector.Unprotect(inst.CloudPassword) ?? "";
                        if (string.IsNullOrEmpty(plantId))
                            plantId = inst.PlantId ?? "";
                        if (string.IsNullOrEmpty(apiBaseUrl))
                            apiBaseUrl = inst.CloudApiBaseUrl ?? "";
                        // Fall back to stored ConnectionMethod when the UI
                        // didn't send one (e.g. older Create page).
                        connectionMethod ??= inst.ConnectionMethod;
                    }
                }

                var credentials = new Services.InverterApi.Models.ApiCredentials
                {
                    ApiKey = apiKey,
                    ApiSecret = apiSecret,
                    Username = username,
                    Password = password,
                    PlantId = plantId,
                    ApiBaseUrl = string.IsNullOrEmpty(apiBaseUrl) ? null : apiBaseUrl,
                    ConnectionMethod = connectionMethod,
                };

                var adapter = factory.GetAdapter(brand);
                var testResult = await adapter.TestConnectionResultAsync(credentials);

                if (!testResult.IsSuccess)
                    return Results.Json(new {
                        success = false,
                        message = testResult.Message,
                        kind = testResult.Kind.ToString(),
                        detail = testResult.TechnicalDetail,
                        durationMs = (int)testResult.Duration.TotalMilliseconds,
                        plantInfo = (object?)null, realtime = (object?)null
                    });

                // Pull actual data to verify
                var plantResult = await adapter.GetPlantInfoResultAsync(credentials);
                var realtimeResult = await adapter.GetRealtimeResultAsync(credentials);
                var devicesResult = await adapter.GetDevicesResultAsync(credentials);
                var plant = plantResult.Data;
                var realtime = realtimeResult.Data;
                var devices = devicesResult.Data ?? new();

                // If the plant lookup said "wrong PlantId" we should FAIL the test —
                // returning realtime data for a mismatched plant is a lie.
                if (plantResult.Kind == Services.InverterApi.Models.ApiResultKind.PlantNotFound)
                {
                    return Results.Json(new
                    {
                        success = false,
                        kind = plantResult.Kind.ToString(),
                        message = testResult.Message + " — BUT: " + plantResult.Message,
                        detail = "Fix the Plant/Station ID in the installation settings, then re-test.",
                        durationMs = (int)(testResult.Duration + plantResult.Duration).TotalMilliseconds,
                        plantInfo = (object?)null,
                        realtime = (object?)null
                    });
                }

                var message = testResult.Message
                    + (realtimeResult.Kind == Services.InverterApi.Models.ApiResultKind.DeviceOffline
                        ? " | Note: API works, but device is offline/sleeping."
                        : "");

                return Results.Json(new
                {
                    success = realtimeResult.IsSuccess,
                    kind = realtimeResult.Kind.ToString(),
                    durationMs = (int)(testResult.Duration + realtimeResult.Duration).TotalMilliseconds,
                    message,
                    plantInfo = plant == null ? null : new
                    {
                        plant.PlantId,
                        plant.PlantName,
                        plant.CapacityKw,
                        plant.Address
                    },
                    realtime = realtime == null ? null : new
                    {
                        realtime.Timestamp,
                        realtime.CurrentPowerKw,
                        realtime.TodayEnergyKwh,
                        realtime.TotalEnergyKwh,
                        realtime.GridExportKw,
                        realtime.GridImportKw,
                        realtime.BatterySocPercent,
                        realtime.IsOnline
                    },
                    deviceCount = devices.Count,
                    devices = devices.Take(5).Select(d => new { d.DeviceId, d.DeviceName, d.Model, d.IsOnline })
                });
            }
            catch (Exception ex)
            {
                return Results.Json(Services.Api.ApiResponse.Fail($"Error: {ex.Message}", "INTERNAL_ERROR"));
            }
        // Admin/Partner only — this endpoint issues an outbound request to a
        // caller-supplied apiBaseUrl, so a Customer must never reach it (SSRF).
        }).RequireAuthorization(p => p.RequireRole("Admin", "Partner"));
    }

    // ══════════════════════════════════════════════════════════════
    //  DIAGNOSTIC PAGE
    // ══════════════════════════════════════════════════════════════

    public static void MapDiagnosticEndpoint(WebApplication app, string? logFile)
    {
        app.MapGet("/startup-log", async (ApplicationDbContext db) =>
        {
            var log = File.Exists(logFile) ? await File.ReadAllTextAsync(logFile) : "No log found";
            var dbStatus = "Unknown";
            var tableList = "";

            try
            {
                var tables = new List<string>();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
                await db.Database.OpenConnectionAsync();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

                var userCount = await db.Users.CountAsync();
                var roleCount = await db.Roles.CountAsync();

                dbStatus = $"OK - {tables.Count} tables, {userCount} users, {roleCount} roles";
                tableList = string.Join(", ", tables);
            }
            catch (Exception ex)
            {
                dbStatus = $"ERROR: {ex.Message}";
            }

            return Results.Content($@"<!DOCTYPE html>
<html><head><title>SolarApp Diagnostic</title>
<style>
body {{ font-family: 'Segoe UI', monospace; background: #0d1117; color: #c9d1d9; padding: 30px; max-width: 1000px; margin: auto; }}
h1 {{ color: #ffc107; border-bottom: 2px solid #30363d; padding-bottom: 10px; }}
h2 {{ color: #58a6ff; margin-top: 25px; }}
.card {{ background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 20px; margin: 15px 0; }}
.ok {{ border-left: 4px solid #3fb950; }}
.err {{ border-left: 4px solid #f85149; }}
pre {{ background: #0d1117; padding: 15px; border-radius: 6px; overflow-x: auto; font-size: 13px; line-height: 1.6; white-space: pre-wrap; }}
.label {{ color: #8b949e; min-width: 120px; display: inline-block; }}
</style></head>
<body>
<h1>SolarApp Diagnostic</h1>

<h2>Database Status</h2>
<div class='card {(dbStatus.StartsWith("OK") ? "ok" : "err")}'>
<p><span class='label'>Status:</span> <strong>{dbStatus}</strong></p>
<p><span class='label'>Tables:</span> {(string.IsNullOrEmpty(tableList) ? "None" : tableList)}</p>
<p><span class='label'>ConnString:</span> {MaskConnectionString(db.Database.GetConnectionString())}</p>
</div>

<h2>Startup Log</h2>
<div class='card'>
<pre>{System.Net.WebUtility.HtmlEncode(log)}</pre>
</div>

<h2>System Info</h2>
<div class='card'>
<p><span class='label'>Runtime:</span> {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}</p>
<p><span class='label'>OS:</span> {System.Runtime.InteropServices.RuntimeInformation.OSDescription}</p>
<p><span class='label'>Time:</span> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
<p><span class='label'>Log file:</span> {logFile}</p>
</div>
</body></html>", "text/html");
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Admin" });
    }

    private static string MaskConnectionString(string? cs)
    {
        if (string.IsNullOrEmpty(cs)) return "(empty)";
        return System.Text.RegularExpressions.Regex.Replace(
            cs, @"(Password|Pwd)\s*=\s*[^;]+", "$1=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
