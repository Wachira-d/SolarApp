using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.Implementations;
using SolarApp.Services.Interfaces;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Adapters;
using SolarApp.Services.LineApi;
using SolarApp.BackgroundJobs;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Identity
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Services
builder.Services.AddScoped<IInstallationService, InstallationService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
builder.Services.AddScoped<IAlertService, AlertService>();

// Inverter API adapters
builder.Services.AddHttpClient();
builder.Services.AddScoped<HuaweiAdapter>();
builder.Services.AddScoped<FoxEssAdapter>();
builder.Services.AddScoped<SolisAdapter>();
builder.Services.AddScoped<GoodWeAdapter>();
builder.Services.AddScoped<DeyeAdapter>();
builder.Services.AddScoped<IInverterApiAdapterFactory, InverterApiAdapterFactory>();

// LINE Messaging
builder.Services.AddScoped<ILineMessagingService, LineMessagingService>();

// Background Jobs
builder.Services.AddHostedService<SolarDataSyncJob>();
builder.Services.AddHostedService<SystemHealthMonitorJob>();

builder.Services.AddRazorPages();

var app = builder.Build();

// Seed database
using (var scope = app.Services.CreateScope())
{
    await DbInitializer.SeedAsync(scope.ServiceProvider);
}

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
app.MapRazorPages();

app.Run();
