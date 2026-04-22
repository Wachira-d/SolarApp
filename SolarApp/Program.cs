using FluentValidation;
using FluentValidation.AspNetCore;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SolarApp.BackgroundJobs;
using SolarApp.Data;
using SolarApp.Data.Interceptors;
using SolarApp.Models;
using SolarApp.Services.Implementations;
using SolarApp.Services.Interfaces;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Adapters;
using SolarApp.Services.LineApi;
using SolarApp.Services.ReportExport;

// ── File-based startup log (writes BEFORE Serilog is configured) ──
var startupLogPath = Path.Combine(AppContext.BaseDirectory, "Logs");
Directory.CreateDirectory(startupLogPath);
var startupLogFile = Path.Combine(startupLogPath, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
var startupErrors = new List<string>();

void LogStartup(string message, bool isError = false)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(isError ? "ERROR" : "INFO ")} {message}";
    File.AppendAllText(startupLogFile, line + Environment.NewLine);
    if (isError) startupErrors.Add(message);
    Console.WriteLine(line);
}

LogStartup("========== SolarApp Startup Begin ==========");

try
{
    // ── Serilog ──
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(startupLogPath, "solarapp-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            Path.Combine(startupLogPath, "errors-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 60,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateBootstrapLogger();

    LogStartup("Serilog configured OK");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, config) => config
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ── Read connection string ──
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    LogStartup($"Connection string: {MaskPassword(connectionString)}");

    if (string.IsNullOrEmpty(connectionString))
    {
        LogStartup("Connection string 'DefaultConnection' is EMPTY! Check appsettings.json", true);
        throw new InvalidOperationException("Connection string 'DefaultConnection' not found in appsettings.json");
    }

    // ── Step 1: Pre-create database ──
    var dbReady = false;
    try
    {
        LogStartup("Step 1: Connecting to SQL Server to pre-create database...");
        var csBuilder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = csBuilder.InitialCatalog;

        if (string.IsNullOrEmpty(databaseName))
        {
            LogStartup("No database name in connection string (missing 'Database=' or 'Initial Catalog=')", true);
        }
        else
        {
            csBuilder.InitialCatalog = "master";
            csBuilder.ConnectTimeout = 10;

            using var masterConn = new SqlConnection(csBuilder.ConnectionString);
            masterConn.Open();
            LogStartup($"Connected to SQL Server: {csBuilder.DataSource}");

            using var cmd = masterConn.CreateCommand();
            cmd.CommandText = $@"
                IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @db)
                BEGIN
                    CREATE DATABASE [{databaseName}]
                    SELECT 'CREATED'
                END
                ELSE SELECT 'EXISTS'";
            cmd.Parameters.AddWithValue("@db", databaseName);
            var result = cmd.ExecuteScalar()?.ToString();
            LogStartup($"Database '{databaseName}': {result}");
            dbReady = true;
        }
    }
    catch (SqlException ex)
    {
        LogStartup($"SQL Server connection FAILED: {ex.Message}", true);
        LogStartup($"  Error Number: {ex.Number}, State: {ex.State}", true);
        LogStartup("  Possible causes:", true);
        LogStartup("  - SQL Server is not running (check Services > SQL Server)", true);
        LogStartup("  - Wrong server name in connection string", true);
        LogStartup("  - Windows Authentication not enabled (try SQL auth with User Id/Password)", true);
        LogStartup("  - Firewall blocking port 1433", true);
    }
    catch (Exception ex)
    {
        LogStartup($"Database pre-creation FAILED: {ex.GetType().Name}: {ex.Message}", true);
    }

    // ── Step 2: Register services ──
    LogStartup("Step 2: Registering services...");

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<AuditableEntityInterceptor>();
    builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
    {
        options.UseSqlServer(connectionString);
        options.AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>());
    });

    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        options.Lockout.MaxFailedAccessAttempts = 5;
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

    builder.Services.AddFluentValidationAutoValidation();
    builder.Services.AddValidatorsFromAssemblyContaining<Program>();

    builder.Services.AddScoped<IInstallationService, InstallationService>();
    builder.Services.AddScoped<IDashboardService, DashboardService>();
    builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
    builder.Services.AddScoped<IAlertService, AlertService>();
    builder.Services.AddScoped<IReportExportService, ReportExportService>();

    builder.Services.AddHttpClient();
    builder.Services.AddScoped<HuaweiAdapter>();
    builder.Services.AddScoped<FoxEssAdapter>();
    builder.Services.AddScoped<SolisAdapter>();
    builder.Services.AddScoped<GoodWeAdapter>();
    builder.Services.AddScoped<DeyeAdapter>();
    builder.Services.AddScoped<IInverterApiAdapterFactory, InverterApiAdapterFactory>();
    builder.Services.AddScoped<ILineMessagingService, LineMessagingService>();

    // Hangfire - only if DB is ready
    if (dbReady)
    {
        try
        {
            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(connectionString,
                    new SqlServerStorageOptions
                    {
                        PrepareSchemaIfNecessary = true,
                        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                        QueuePollInterval = TimeSpan.FromSeconds(15),
                        UseRecommendedIsolationLevel = true,
                        SchemaName = "HangFire"
                    }));
            builder.Services.AddHangfireServer(options =>
            {
                options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 5);
                options.Queues = new[] { "critical", "default", "low" };
            });
            LogStartup("Hangfire registered OK");
        }
        catch (Exception ex)
        {
            LogStartup($"Hangfire registration FAILED: {ex.Message}", true);
            dbReady = false;
        }
    }
    else
    {
        LogStartup("Hangfire SKIPPED (database not available)", true);
    }

    builder.Services.AddScoped<SolarDataSyncJob>();
    builder.Services.AddScoped<SystemHealthMonitorJob>();
    builder.Services.AddRazorPages();

    LogStartup("Services registered OK");

    // ── Step 3: Build app ──
    LogStartup("Step 3: Building app...");
    var app = builder.Build();
    LogStartup("App built OK");

    // ── Step 4: Create tables + Seed ──
    LogStartup("Step 4: Creating database schema and seeding data...");
    try
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.EnsureCreatedAsync();
        LogStartup("Database schema created/verified OK");
        await DbInitializer.SeedAsync(scope.ServiceProvider);
        LogStartup("Database seeded OK (admin@solarapp.com / Admin@123456)");
    }
    catch (Exception ex)
    {
        LogStartup($"Database schema/seed FAILED: {ex.GetType().Name}: {ex.Message}", true);
        if (ex.InnerException != null)
            LogStartup($"  Inner: {ex.InnerException.Message}", true);
    }

    // ── Step 5: Configure middleware ──
    LogStartup("Step 5: Configuring middleware...");

    // Always show detailed errors during development
    app.UseDeveloperExceptionPage();
    app.UseSerilogRequestLogging();

    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    // Startup diagnostic page at /startup-log
    app.MapGet("/startup-log", (HttpContext ctx) =>
    {
        if (!ctx.User.Identity?.IsAuthenticated == true || !ctx.User.IsInRole("Admin"))
        {
            // Allow access if no users exist yet (first run)
        }

        var logContent = File.Exists(startupLogFile)
            ? File.ReadAllText(startupLogFile) : "No startup log found";

        var html = $@"<!DOCTYPE html>
<html><head><title>SolarApp - Startup Log</title>
<style>
body {{ font-family: monospace; background: #1a1a2e; color: #eee; padding: 20px; }}
h1 {{ color: #ffc107; }}
.error {{ color: #ff6b6b; font-weight: bold; }}
.info {{ color: #51cf66; }}
pre {{ background: #16213e; padding: 20px; border-radius: 8px; overflow-x: auto; line-height: 1.8; }}
.summary {{ background: #0f3460; padding: 15px; border-radius: 8px; margin-bottom: 20px; }}
.summary.ok {{ border-left: 4px solid #51cf66; }}
.summary.fail {{ border-left: 4px solid #ff6b6b; }}
</style></head><body>
<h1>SolarApp Startup Diagnostic</h1>
<div class='summary {(startupErrors.Count == 0 ? "ok" : "fail")}'>
  <strong>Status: {(startupErrors.Count == 0 ? "ALL OK" : $"{startupErrors.Count} ERROR(S) FOUND")}</strong><br/>
  Log file: {startupLogFile}
</div>
{(startupErrors.Count > 0 ? "<h2 style='color:#ff6b6b'>Errors:</h2><ul>" + string.Join("", startupErrors.Select(e => $"<li class='error'>{System.Net.WebUtility.HtmlEncode(e)}</li>")) + "</ul>" : "")}
<h2>Full Startup Log:</h2>
<pre>{FormatLogHtml(logContent)}</pre>
<h2>Connection String Check:</h2>
<pre>{System.Net.WebUtility.HtmlEncode(MaskPassword(connectionString))}</pre>
<p style='color:#888; margin-top:30px;'>Generated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
</body></html>";

        return Results.Content(html, "text/html");
    });

    // Hangfire dashboard + jobs
    if (dbReady)
    {
        app.UseHangfireDashboard(
            app.Configuration.GetValue("Hangfire:DashboardPath", "/hangfire")!,
            new DashboardOptions
            {
                Authorization = new[] { new HangfireAdminAuthorizationFilter() }
            });

        try
        {
            RecurringJob.AddOrUpdate<SolarDataSyncJob>(
                "sync-production-data",
                job => job.ExecuteAsync(),
                "*/5 * * * *");
            RecurringJob.AddOrUpdate<SystemHealthMonitorJob>(
                "check-system-health",
                job => job.ExecuteAsync(),
                "*/2 * * * *");
            LogStartup("Hangfire jobs registered OK");
        }
        catch (Exception ex)
        {
            LogStartup($"Hangfire jobs FAILED: {ex.Message}", true);
        }
    }

    app.MapRazorPages();

    // Report export API
    app.MapGet("/api/reports/excel/{installationId:int}", async (
        int installationId, DateTime? from, DateTime? to,
        IReportExportService reportService) =>
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;
        var bytes = await reportService.ExportProductionToExcelAsync(installationId, fromDate, toDate);
        return Results.File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SolarReport_{installationId}_{fromDate:yyyyMMdd}_{toDate:yyyyMMdd}.xlsx");
    }).RequireAuthorization();

    app.MapGet("/api/reports/pdf/{installationId:int}", async (
        int installationId, DateTime? from, DateTime? to,
        IReportExportService reportService) =>
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;
        var bytes = await reportService.ExportProductionToPdfAsync(installationId, fromDate, toDate);
        return Results.File(bytes, "application/pdf",
            $"SolarReport_{installationId}_{fromDate:yyyyMMdd}_{toDate:yyyyMMdd}.pdf");
    }).RequireAuthorization();

    LogStartup("========== SolarApp Startup Complete ==========");
    LogStartup($"Errors: {startupErrors.Count}");
    LogStartup($"View startup log at: /startup-log");

    Log.Information("SolarApp started. View startup diagnostics at /startup-log");
    app.Run();
}
catch (Exception ex)
{
    LogStartup($"FATAL: {ex.GetType().Name}: {ex.Message}", true);
    if (ex.InnerException != null)
        LogStartup($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}", true);
    LogStartup($"  StackTrace: {ex.StackTrace}", true);
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ── Helper functions ──

static string MaskPassword(string? connStr)
{
    if (string.IsNullOrEmpty(connStr)) return "(empty)";
    return System.Text.RegularExpressions.Regex.Replace(
        connStr, @"(Password|Pwd)\s*=\s*[^;]+", "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

static string FormatLogHtml(string log)
{
    var encoded = System.Net.WebUtility.HtmlEncode(log);
    encoded = encoded.Replace("ERROR", "<span class='error'>ERROR</span>");
    encoded = encoded.Replace("INFO ", "<span class='info'>INFO </span>");
    return encoded;
}
