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
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
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

        builder.Services.AddHttpClient("default", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient();
        builder.Services.AddScoped<Services.InverterApi.Adapters.HuaweiAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.FoxEssAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.SolisAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.GoodWeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.DeyeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.IInverterApiAdapterFactory, Services.InverterApi.InverterApiAdapterFactory>();
        builder.Services.AddScoped<Services.InverterApi.Common.IAdapterDiagnostics, Services.InverterApi.Common.AdapterDiagnostics>();
        builder.Services.AddScoped<Services.InverterApi.Common.IConnectionHealthChecker, Services.InverterApi.Common.ConnectionHealthChecker>();
        builder.Services.AddScoped<Services.Authorization.IInstallationAccessHelper, Services.Authorization.InstallationAccessHelper>();
        builder.Services.AddScoped<Services.Performance.IPerformanceMetricsService, Services.Performance.PerformanceMetricsService>();
        builder.Services.AddScoped<Services.AlertRules.IAlertRulesEngine, Services.AlertRules.AlertRulesEngine>();
        builder.Services.AddScoped<Services.LineApi.ILineMessagingService, Services.LineApi.LineMessagingService>();
        builder.Services.AddScoped<Services.Weather.IWeatherService, Services.Weather.WeatherService>();
        builder.Services.AddMemoryCache();
        builder.Services.AddValidatorsFromAssemblyContaining<Startup>();
        builder.Services.AddScoped<BackgroundJobs.SolarDataSyncJob>();
        builder.Services.AddScoped<BackgroundJobs.SystemHealthMonitorJob>();

        // ── Localization ──
        builder.Services.AddLocalization(opt => opt.ResourcesPath = "Resources");
        builder.Services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(opt =>
        {
            var supported = new[] { "en", "th" };
            opt.SetDefaultCulture("en")
                .AddSupportedCultures(supported)
                .AddSupportedUICultures(supported);
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

        RecurringJob.AddOrUpdate<BackgroundJobs.SolarDataSyncJob>(
            "sync-production-data", job => job.ExecuteAsync(), syncCron);
        RecurringJob.AddOrUpdate<BackgroundJobs.SystemHealthMonitorJob>(
            "check-system-health", job => job.ExecuteAsync(), healthCron);
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
            catch
            {
                // Try next candidate
            }
        }

        // All failed — throw a helpful error so the admin knows what to fix
        throw new InvalidOperationException(
            "Could not find a writable directory for DataProtection keys. Tried: " +
            string.Join(", ", candidates) +
            ". Grant the IIS app-pool identity modify permission on one of these paths, " +
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
            log("DB: Seed complete (admin@solarapp.com / Admin@123456)");
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

        var sb = new System.Text.StringBuilder();

        // Build one big idempotent batch
        foreach (var (table, column, sql) in upgrades)
        {
            sb.AppendLine($@"
IF NOT EXISTS (SELECT 1 FROM sys.columns
    WHERE Name = N'{column}' AND Object_ID = Object_ID(N'[{table}]'))
BEGIN {sql}; END");
        }

        sb.AppendLine(@"
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
END");

        foreach (var (name, table, cols) in indexUpgrades)
        {
            sb.AppendLine($@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}')
    AND EXISTS (SELECT 1 FROM sys.tables WHERE name = '{table}')
BEGIN CREATE INDEX [{name}] ON [{table}]({cols}); END");
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            #pragma warning disable EF1002 // All values are compile-time constants
            await context.Database.ExecuteSqlRawAsync(sb.ToString());
            #pragma warning restore EF1002
            sw.Stop();
            log($"DB: Schema upgrades + indexes applied in one batch ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            log($"DB: Schema batch failed: {ex.Message}");
            // Don't throw — app should still start with current schema
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
                ? Results.Json(new { success = false, message = "No results" })
                : Results.Json(new { success = true, lat = result.Value.Lat, lng = result.Value.Lng, displayName = result.Value.DisplayName });
        }).RequireAuthorization();

        // Refresh device info / inverter metadata from cloud for an installation
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
                return Results.Json(new { success = false, message = "Installation not found" });

            if (string.IsNullOrEmpty(installation.PlantId))
                return Results.Json(new { success = false, message = "No Plant/Station ID configured" });

            try
            {
                var adapter = factory.GetAdapter(installation.InverterBrand);
                var credentials = new Services.InverterApi.Models.ApiCredentials
                {
                    ApiKey = credProtector.Unprotect(installation.CloudApiKey),
                    ApiSecret = credProtector.Unprotect(installation.CloudApiSecret),
                    Username = credProtector.Unprotect(installation.CloudUsername),
                    Password = credProtector.Unprotect(installation.CloudPassword),
                    PlantId = installation.PlantId
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
                return Results.Json(new { success = false, message = $"Sync error: {ex.Message}" });
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
                    return Results.Json(new { success = false, message = "Invalid brand" });

                var apiKey    = form["apiKey"].ToString();
                var apiSecret = form["apiSecret"].ToString();
                var username  = form["username"].ToString();
                var password  = form["password"].ToString();
                var plantId   = form["plantId"].ToString();

                // Fill any blank field from the stored installation credentials.
                // Empty input from the form = "use the saved value" (same as Save).
                if (int.TryParse(form["installationId"].ToString(), out var installId) && installId > 0)
                {
                    var inst = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                        .FirstOrDefaultAsync(db.Installations, i => i.Id == installId);
                    if (inst != null)
                    {
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
                    }
                }

                var credentials = new Services.InverterApi.Models.ApiCredentials
                {
                    ApiKey = apiKey,
                    ApiSecret = apiSecret,
                    Username = username,
                    Password = password,
                    PlantId = plantId
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
                return Results.Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }).RequireAuthorization();
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
        });
    }

    private static string MaskConnectionString(string? cs)
    {
        if (string.IsNullOrEmpty(cs)) return "(empty)";
        return System.Text.RegularExpressions.Regex.Replace(
            cs, @"(Password|Pwd)\s*=\s*[^;]+", "$1=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
