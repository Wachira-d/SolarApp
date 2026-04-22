using FluentValidation;
using FluentValidation.AspNetCore;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
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

// ── Serilog bootstrap ──
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Host.UseSerilog((context, services, config) => config
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName());

    // ── Database (MS SQL Server) ──
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<AuditableEntityInterceptor>();
    builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
    {
        var interceptor = sp.GetRequiredService<AuditableEntityInterceptor>();
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
               .AddInterceptors(interceptor);
    });

    // ── Identity ──
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

    // ── FluentValidation ──
    builder.Services.AddFluentValidationAutoValidation();
    builder.Services.AddValidatorsFromAssemblyContaining<Program>();

    // ── Services ──
    builder.Services.AddScoped<IInstallationService, InstallationService>();
    builder.Services.AddScoped<IDashboardService, DashboardService>();
    builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
    builder.Services.AddScoped<IAlertService, AlertService>();
    builder.Services.AddScoped<IReportExportService, ReportExportService>();

    // ── Inverter API adapters ──
    builder.Services.AddHttpClient();
    builder.Services.AddScoped<HuaweiAdapter>();
    builder.Services.AddScoped<FoxEssAdapter>();
    builder.Services.AddScoped<SolisAdapter>();
    builder.Services.AddScoped<GoodWeAdapter>();
    builder.Services.AddScoped<DeyeAdapter>();
    builder.Services.AddScoped<IInverterApiAdapterFactory, InverterApiAdapterFactory>();

    // ── LINE Messaging ──
    builder.Services.AddScoped<ILineMessagingService, LineMessagingService>();

    // ── Hangfire ──
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection"),
            new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.FromSeconds(15),
                UseRecommendedIsolationLevel = true,
                SchemaName = "Hangfire"
            }));
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 5);
        options.Queues = new[] { "critical", "default", "low" };
    });

    // ── Hangfire job classes ──
    builder.Services.AddScoped<SolarDataSyncJob>();
    builder.Services.AddScoped<SystemHealthMonitorJob>();

    builder.Services.AddRazorPages();

    var app = builder.Build();

    // ── Seed database ──
    using (var scope = app.Services.CreateScope())
    {
        await DbInitializer.SeedAsync(scope.ServiceProvider);
    }

    // ── Serilog request logging ──
    app.UseSerilogRequestLogging();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    // ── Hangfire Dashboard (Admin only) ──
    app.UseHangfireDashboard(
        app.Configuration.GetValue("Hangfire:DashboardPath", "/hangfire")!,
        new DashboardOptions
        {
            Authorization = new[] { new HangfireAdminAuthorizationFilter() }
        });

    // ── Register recurring Hangfire jobs ──
    RecurringJob.AddOrUpdate<SolarDataSyncJob>(
        "sync-production-data",
        job => job.ExecuteAsync(),
        "*/5 * * * *");  // Every 5 minutes

    RecurringJob.AddOrUpdate<SystemHealthMonitorJob>(
        "check-system-health",
        job => job.ExecuteAsync(),
        "*/2 * * * *");  // Every 2 minutes

    app.MapRazorPages();

    // ── Minimal API endpoints for report export ──
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

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
