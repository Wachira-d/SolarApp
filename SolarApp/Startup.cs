using FluentValidation;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp;

public class Startup
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

        // ── Database ──
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString);
        });

        // ── Identity ──
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

        // ── Core Services ──
        builder.Services.AddScoped<Services.Interfaces.IInstallationService, Services.Implementations.InstallationService>();
        builder.Services.AddScoped<Services.Interfaces.IDashboardService, Services.Implementations.DashboardService>();
        builder.Services.AddScoped<Services.Interfaces.IMaintenanceService, Services.Implementations.MaintenanceService>();
        builder.Services.AddScoped<Services.Interfaces.IAlertService, Services.Implementations.AlertService>();
        builder.Services.AddScoped<Services.ReportExport.IReportExportService, Services.ReportExport.ReportExportService>();

        // ── Inverter API ──
        builder.Services.AddHttpClient();
        builder.Services.AddScoped<Services.InverterApi.Adapters.HuaweiAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.FoxEssAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.SolisAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.GoodWeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.Adapters.DeyeAdapter>();
        builder.Services.AddScoped<Services.InverterApi.IInverterApiAdapterFactory, Services.InverterApi.InverterApiAdapterFactory>();

        // ── LINE ──
        builder.Services.AddScoped<Services.LineApi.ILineMessagingService, Services.LineApi.LineMessagingService>();

        // ── Validators ──
        builder.Services.AddValidatorsFromAssemblyContaining<Startup>();

        // ── Background jobs ──
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
            .UseSqlServerStorage(connectionString,
                new SqlServerStorageOptions
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

        RecurringJob.AddOrUpdate<BackgroundJobs.SolarDataSyncJob>(
            "sync-production-data", job => job.ExecuteAsync(), "*/5 * * * *");
        RecurringJob.AddOrUpdate<BackgroundJobs.SystemHealthMonitorJob>(
            "check-system-health", job => job.ExecuteAsync(), "*/2 * * * *");
    }

    public static async Task InitializeDatabaseAsync(IServiceProvider services, Action<string> log)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Check if tables exist by testing a known table
        var canConnect = await context.Database.CanConnectAsync();
        log($"Database CanConnect: {canConnect}");

        if (canConnect)
        {
            try
            {
                // Test if AspNetUsers table exists
                await context.Database.ExecuteSqlRawAsync("SELECT TOP 1 Id FROM AspNetUsers");
                log("Tables already exist");
            }
            catch
            {
                // Tables don't exist - delete and recreate
                log("Tables missing! Dropping and recreating database...");
                await context.Database.EnsureDeletedAsync();
                log("Old database deleted");
                await context.Database.EnsureCreatedAsync();
                log("Database recreated with all tables");
            }
        }
        else
        {
            await context.Database.EnsureCreatedAsync();
            log("Database created from scratch");
        }

        await DbInitializer.SeedAsync(scope.ServiceProvider);
        log("Database seeded (admin@solarapp.com / Admin@123456)");
    }

    public static bool TryCreateDatabase(string connectionString, Action<string> log)
    {
        try
        {
            var csBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            var dbName = csBuilder.InitialCatalog;
            csBuilder.InitialCatalog = "master";
            csBuilder.ConnectTimeout = 10;

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(csBuilder.ConnectionString);
            conn.Open();
            log($"Connected to SQL Server: {csBuilder.DataSource}");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @db)
                BEGIN CREATE DATABASE [{dbName}] SELECT 'CREATED' END ELSE SELECT 'EXISTS'";
            cmd.Parameters.AddWithValue("@db", dbName);
            var result = cmd.ExecuteScalar()?.ToString();
            log($"Database '{dbName}': {result}");
            return true;
        }
        catch (Exception ex)
        {
            log($"SQL Server ERROR: {ex.Message}");
            log("Check: 1) SQL Server running? 2) Connection string correct? 3) Auth method?");
            return false;
        }
    }
}
