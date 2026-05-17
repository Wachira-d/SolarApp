// ══════════════════════════════════════════════════════════════
// SolarApp - Program.cs
// Defence-in-depth startup logging: writes to 4 places so that SOMETHING
// reaches the operator even if file perms, DB, or console are broken.
//   1. Console (picked up by IIS if stdoutLogEnabled=true in web.config)
//   2. <publish>/logs/startup-debug.log  (if perms allow)
//   3. %TEMP%/solarapp-startup.log       (always writable by app-pool user)
//   4. Database table StartupLogs        (best effort, after DB init)
//   5. Windows Event Log                 (last resort)
// ══════════════════════════════════════════════════════════════

var logFiles = new List<string>();
var logBuffer = new List<string>();
string? dbConnectionString = null;

void Log(string msg)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}";
    logBuffer.Add(line);
    Console.WriteLine(line);

    // Try every known log target. Each one is wrapped in try/catch so a
    // failure in one doesn't stop the others.
    foreach (var path in logFiles)
    {
        try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
    }
}

// ── Set up file log targets ──
// Target 1: next to the app (nicest for devs)
try
{
    var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logDir);
    var p = Path.Combine(logDir, "startup-debug.log");
    File.WriteAllText(p, ""); // truncate; proves we have write perms
    logFiles.Add(p);
}
catch { }

// Target 2: %TEMP% (always writable under IIS app-pool identity)
try
{
    var tempLog = Path.Combine(Path.GetTempPath(), $"solarapp-startup-{DateTime.Now:yyyyMMdd}.log");
    File.AppendAllText(tempLog, $"--- Session start {DateTime.Now:O} ---{Environment.NewLine}");
    logFiles.Add(tempLog);
}
catch { }

Log("=== SolarApp Starting ===");
Log($"Log targets: {(logFiles.Count == 0 ? "CONSOLE ONLY (no file perms!)" : string.Join(" | ", logFiles))}");
Log($"BaseDir: {AppContext.BaseDirectory}");
Log($"Env: {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "(not set = Production)"}");
Log($"User: {Environment.UserName}");
Log($"Machine: {Environment.MachineName}");
Log($"OS: {Environment.OSVersion}");
Log($"AppDomain dir: {AppDomain.CurrentDomain.BaseDirectory}");

try
{
    var builder = WebApplication.CreateBuilder(args);
    dbConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "";

    if (string.IsNullOrEmpty(dbConnectionString))
    {
        Log("FATAL: No 'DefaultConnection' in appsettings.json!");
        return;
    }

    Log($"SQL: {dbConnectionString.Split(';')[0]}...");

    // ── Services ──
    Log("Configuring services...");
    SolarApp.Startup.ConfigureServices(builder);
    Log("Services OK");

    // Report which external auth providers were actually registered
    foreach (var kv in SolarApp.Startup.AuthProviderStatus)
    {
        Log($"  Auth provider {kv.Key}: {(kv.Value ? "REGISTERED" : "not configured (skipped)")}");
    }

    // ── Hangfire (optional) ──
    var hangfireOk = false;
    try
    {
        SolarApp.Startup.ConfigureHangfire(builder, dbConnectionString);
        hangfireOk = true;
        Log("Hangfire OK");
    }
    catch (Exception ex)
    {
        Log($"Hangfire skipped: {ex.Message}");
    }

    Log("Building app...");
    var app = builder.Build();
    Log("Build OK");

    // ── Database ──
    Log("Initializing database...");
    await SolarApp.Startup.InitializeDatabaseAsync(app.Services, Log);
    Log("Database init complete");

    // ── Middleware ──
    Log("Configuring middleware...");
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        Log("  • UseDeveloperExceptionPage (dev only)");
    }
    else
    {
        app.UseExceptionHandler("/Error");
        Log("  • UseExceptionHandler (production)");
    }

    // Always enforce HTTPS in production (previously opt-in, now default ON)
    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
        app.UseHsts();
        Log("  • UseHttpsRedirection + UseHsts (production)");
    }
    else if (app.Configuration.GetValue("EnforceHttps", false))
    {
        app.UseHttpsRedirection();
        app.UseHsts();
        Log("  • UseHttpsRedirection + UseHsts (config override)");
    }

    app.UseStaticFiles();
    Log("  • UseStaticFiles");

    var locOpt = app.Services.GetService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>>();
    if (locOpt != null)
    {
        app.UseRequestLocalization(locOpt.Value);
        Log("  • UseRequestLocalization");
    }

    app.UseRouting();
    Log("  • UseRouting");

    app.UseAuthentication();
    Log("  • UseAuthentication");

    app.UseAuthorization();
    Log("  • UseAuthorization");

    if (hangfireOk)
    {
        try { SolarApp.Startup.ConfigureHangfireDashboard(app); Log("  • Hangfire dashboard OK"); }
        catch (Exception ex) { Log($"  ⚠ Hangfire dashboard skipped: {ex.Message}"); }
    }

    Log("Mapping endpoints...");
    app.MapRazorPages();
    Log("  • MapRazorPages");
    app.MapControllers();
    Log("  • MapControllers");
    app.MapHub<SolarApp.Hubs.DashboardHub>("/hubs/dashboard");
    Log("  • MapHub<DashboardHub>");
    SolarApp.Startup.MapApiEndpoints(app);
    Log("  • MapApiEndpoints");
    SolarApp.Startup.MapDiagnosticEndpoint(app, logFiles.FirstOrDefault());
    Log("  • MapDiagnosticEndpoint");

    // Flush accumulated startup log to DB — best effort
    try { await FlushStartupLogToDbAsync(dbConnectionString!, logBuffer); Log("  • StartupLogs DB write OK"); }
    catch (Exception ex) { Log($"  ⚠ StartupLogs DB write skipped: {ex.Message}"); }

    Log("=== SolarApp Ready ===");
    app.Run();
}
catch (Exception ex)
{
    Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
    Log($"Stack: {ex.StackTrace}");
    if (ex.InnerException != null)
    {
        Log($"Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        Log($"InnerStack: {ex.InnerException.StackTrace}");
    }

    // Try every possible place to surface this error
    // 1. Database (if we have connection string)
    if (!string.IsNullOrEmpty(dbConnectionString))
    {
        try { await FlushStartupLogToDbAsync(dbConnectionString, logBuffer, isError: true); } catch { }
    }
    // 2. Windows Event Log
    try
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.EventLog.WriteEntry("Application",
                "SolarApp startup failure:\n" + string.Join("\n", logBuffer.TakeLast(30)),
                System.Diagnostics.EventLogEntryType.Error, 1001);
        }
    }
    catch { }
}

// ══════════════════════════════════════════════════════════════
//  Startup-log persistence to the database
// ══════════════════════════════════════════════════════════════
static async Task FlushStartupLogToDbAsync(string connectionString, IReadOnlyList<string> lines, bool isError = false)
{
    using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
    await conn.OpenAsync();

    // Create table if missing (self-healing)
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StartupLogs')
            BEGIN
                CREATE TABLE [StartupLogs] (
                    [Id] bigint IDENTITY(1,1) PRIMARY KEY,
                    [Timestamp] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    [MachineName] nvarchar(100) NULL,
                    [IsError] bit NOT NULL DEFAULT 0,
                    [LogText] nvarchar(max) NOT NULL
                );
                CREATE INDEX IX_StartupLogs_Timestamp ON [StartupLogs]([Timestamp] DESC);
            END";
        await cmd.ExecuteNonQueryAsync();
    }

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "INSERT INTO [StartupLogs]([MachineName],[IsError],[LogText]) VALUES (@m, @e, @t)";
        cmd.Parameters.AddWithValue("@m", Environment.MachineName);
        cmd.Parameters.AddWithValue("@e", isError);
        cmd.Parameters.AddWithValue("@t", string.Join("\n", lines));
        await cmd.ExecuteNonQueryAsync();
    }
}
