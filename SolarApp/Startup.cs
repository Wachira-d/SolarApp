using FluentValidation;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp;

public class Startup
{
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
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
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
        });

        builder.Services.AddScoped<Services.Interfaces.IInstallationService, Services.Implementations.InstallationService>();
        builder.Services.AddScoped<Services.Interfaces.IDashboardService, Services.Implementations.DashboardService>();
        builder.Services.AddScoped<Services.Interfaces.IMaintenanceService, Services.Implementations.MaintenanceService>();
        builder.Services.AddScoped<Services.Interfaces.IAlertService, Services.Implementations.AlertService>();
        builder.Services.AddScoped<Services.ReportExport.IReportExportService, Services.ReportExport.ReportExportService>();
        builder.Services.AddScoped<Services.FileUpload.IFileUploadService, Services.FileUpload.FileUploadService>();

        builder.Services.AddHttpClient();
        builder.Services.AddScoped<Services.InverterApi.Adapters.HuaweiAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.FoxEssAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.SolisAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.GoodWeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.DeyeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.IInverterApiAdapterFactory, Services.InverterApi.InverterApiAdapterFactory>();
        builder.Services.AddScoped<Services.InverterApi.Common.IAdapterDiagnostics, Services.InverterApi.Common.AdapterDiagnostics>();
        builder.Services.AddScoped<Services.LineApi.ILineMessagingService, Services.LineApi.LineMessagingService>();
        builder.Services.AddValidatorsFromAssemblyContaining<Startup>();
        builder.Services.AddScoped<BackgroundJobs.SolarDataSyncJob>();
        builder.Services.AddScoped<BackgroundJobs.SystemHealthMonitorJob>();
        builder.Services.AddRazorPages();
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
        var upgrades = new[]
        {
            // Installation: inverter size in kW (per unit)
            (Table: "Installations", Column: "InverterSizeKw",
             Sql: "ALTER TABLE [Installations] ADD [InverterSizeKw] decimal(10,3) NOT NULL DEFAULT 0"),

            // Equipment: per-unit power rating
            (Table: "Equipment", Column: "RatingKw",
             Sql: "ALTER TABLE [Equipment] ADD [RatingKw] decimal(10,3) NULL"),
            (Table: "Equipment", Column: "RatingWatts",
             Sql: "ALTER TABLE [Equipment] ADD [RatingWatts] int NULL"),

            // Maintenance: category, location, photos, attachments
            (Table: "MaintenanceRecords", Column: "Category",
             Sql: "ALTER TABLE [MaintenanceRecords] ADD [Category] nvarchar(100) NULL"),
            (Table: "MaintenanceRecords", Column: "Location",
             Sql: "ALTER TABLE [MaintenanceRecords] ADD [Location] nvarchar(200) NULL"),
            (Table: "MaintenanceRecords", Column: "AttachmentUrls",
             Sql: "ALTER TABLE [MaintenanceRecords] ADD [AttachmentUrls] nvarchar(max) NULL"),

            // Cleaning: multi-photo support
            (Table: "CleaningRecords", Column: "BeforePhotos",
             Sql: "ALTER TABLE [CleaningRecords] ADD [BeforePhotos] nvarchar(max) NULL"),
            (Table: "CleaningRecords", Column: "AfterPhotos",
             Sql: "ALTER TABLE [CleaningRecords] ADD [AfterPhotos] nvarchar(max) NULL"),
        };

        foreach (var (table, column, sql) in upgrades)
        {
            try
            {
                var exists = await context.Database.ExecuteSqlRawAsync(
                    $@"IF NOT EXISTS (SELECT 1 FROM sys.columns
                         WHERE Name = N'{column}' AND Object_ID = Object_ID(N'[{table}]'))
                       BEGIN
                         {sql};
                       END");
                log($"DB: Schema upgrade '{table}.{column}' applied/skipped");
            }
            catch (Exception ex)
            {
                log($"DB: Schema upgrade '{table}.{column}' failed: {ex.Message}");
            }
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
                await context.Database.ExecuteSqlRawAsync($"SELECT TOP 0 * FROM [{table}]");
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
        app.MapGet("/api/reports/excel/{installationId:int}", async (
            int installationId, DateTime? from, DateTime? to,
            Services.ReportExport.IReportExportService svc) =>
        {
            var f = from ?? DateTime.UtcNow.AddMonths(-1);
            var t = to ?? DateTime.UtcNow;
            return Results.File(await svc.ExportProductionToExcelAsync(installationId, f, t),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Report_{installationId}.xlsx");
        }).RequireAuthorization();

        app.MapGet("/api/reports/pdf/{installationId:int}", async (
            int installationId, DateTime? from, DateTime? to,
            Services.ReportExport.IReportExportService svc) =>
        {
            var f = from ?? DateTime.UtcNow.AddMonths(-1);
            var t = to ?? DateTime.UtcNow;
            return Results.File(await svc.ExportProductionToPdfAsync(installationId, f, t),
                "application/pdf", $"Report_{installationId}.pdf");
        }).RequireAuthorization();

        // Manually sync data from cloud for a specific installation
        app.MapPost("/api/installations/{id:int}/sync", async (
            int id,
            Data.ApplicationDbContext db,
            Services.InverterApi.IInverterApiAdapterFactory factory) =>
        {
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
                    ApiKey = installation.CloudApiKey,
                    ApiSecret = installation.CloudApiSecret,
                    Username = installation.CloudUsername,
                    Password = installation.CloudPassword,
                    PlantId = installation.PlantId
                };

                var realtime = await adapter.GetRealtimeDataAsync(credentials);
                if (realtime == null)
                    return Results.Json(new { success = false, message = "Could not fetch data from cloud" });

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

                installation.LastOnlineAt = realtime.IsOnline ? DateTime.UtcNow : installation.LastOnlineAt;
                if (realtime.IsOnline && installation.Status == Models.Enums.InstallationStatus.Offline)
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

        // Test inverter cloud API connection
        app.MapPost("/api/test-inverter-connection", async (
            HttpRequest req,
            Services.InverterApi.IInverterApiAdapterFactory factory) =>
        {
            try
            {
                var form = await req.ReadFormAsync();
                if (!Enum.TryParse<Models.Enums.InverterBrand>(form["brand"].ToString(), out var brand))
                    return Results.Json(new { success = false, message = "Invalid brand" });

                var credentials = new Services.InverterApi.Models.ApiCredentials
                {
                    ApiKey = form["apiKey"].ToString(),
                    ApiSecret = form["apiSecret"].ToString(),
                    Username = form["username"].ToString(),
                    Password = form["password"].ToString(),
                    PlantId = form["plantId"].ToString()
                };

                var adapter = factory.GetAdapter(brand);
                var (success, message) = await adapter.TestConnectionAsync(credentials);

                if (!success)
                    return Results.Json(new { success, message, plantInfo = (object?)null, realtime = (object?)null });

                // Pull actual data to verify
                var plant = await adapter.GetPlantInfoAsync(credentials);
                var realtime = await adapter.GetRealtimeDataAsync(credentials);
                var devices = await adapter.GetDevicesAsync(credentials);

                return Results.Json(new
                {
                    success = true,
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
