// ══════════════════════════════════════════════════════════════
// SolarApp - Program.cs
// Designed to NEVER crash silently. Logs everything to stdout
// (captured by IIS via web.config stdoutLogEnabled="true")
// ══════════════════════════════════════════════════════════════

string? logFile = null;

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
    Console.WriteLine(line);
    if (logFile != null)
        try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
}

// Try to set up file logging (may fail under IIS due to permissions)
try
{
    var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logDir);
    logFile = Path.Combine(logDir, "startup-debug.log");
    File.WriteAllText(logFile, "");
}
catch
{
    // File logging unavailable - stdout only (IIS captures via web.config)
    logFile = null;
}

Log("=== SolarApp Starting ===");
Log($"BaseDir: {AppContext.BaseDirectory}");
Log($"Env: {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "(not set = Production)"}");
Log($"User: {Environment.UserName}");
Log($"OS: {Environment.OSVersion}");

try
{
    var builder = WebApplication.CreateBuilder(args);
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "";

    if (string.IsNullOrEmpty(connectionString))
    {
        Log("FATAL: No 'DefaultConnection' in appsettings.json!");
        return;
    }

    Log($"SQL: {connectionString.Split(';')[0]}...");

    // ── Services ──
    SolarApp.Startup.ConfigureServices(builder);
    Log("Services OK");

    // ── Hangfire (optional) ──
    var hangfireOk = false;
    try
    {
        SolarApp.Startup.ConfigureHangfire(builder, connectionString);
        hangfireOk = true;
        Log("Hangfire OK");
    }
    catch (Exception ex)
    {
        Log($"Hangfire skipped: {ex.Message}");
    }

    var app = builder.Build();
    Log("Build OK");

    // ── Database ──
    await SolarApp.Startup.InitializeDatabaseAsync(app.Services, Log);

    // ── Middleware ──
    app.UseDeveloperExceptionPage();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    if (hangfireOk)
    {
        try { SolarApp.Startup.ConfigureHangfireDashboard(app); Log("Hangfire dashboard OK"); }
        catch (Exception ex) { Log($"Hangfire dashboard skipped: {ex.Message}"); }
    }

    app.MapRazorPages();
    SolarApp.Startup.MapApiEndpoints(app);
    SolarApp.Startup.MapDiagnosticEndpoint(app, logFile);

    Log("=== SolarApp Ready ===");
    app.Run();
}
catch (Exception ex)
{
    Log($"FATAL: {ex}");
}
