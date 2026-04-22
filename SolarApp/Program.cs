var logFile = Path.Combine(AppContext.BaseDirectory, "startup-debug.log");
File.WriteAllText(logFile, ""); // clear old log

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
    Console.WriteLine(line);
    try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
}

Log("=== SolarApp Starting ===");

try
{
    var builder = WebApplication.CreateBuilder(args);
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "";

    if (string.IsNullOrEmpty(connectionString))
    {
        Log("FATAL: No connection string! Add 'DefaultConnection' to appsettings.json");
        return;
    }

    Log($"SQL Server: {connectionString.Split(';')[0]}");

    // ── Register all services ──
    SolarApp.Startup.ConfigureServices(builder);
    Log("Services registered");

    // ── Hangfire (optional) ──
    var hangfireOk = false;
    try
    {
        SolarApp.Startup.ConfigureHangfire(builder, connectionString);
        hangfireOk = true;
        Log("Hangfire registered");
    }
    catch (Exception ex)
    {
        Log($"Hangfire skipped: {ex.Message}");
    }

    var app = builder.Build();
    Log("App built OK");

    // ── Self-healing database init ──
    await SolarApp.Startup.InitializeDatabaseAsync(app.Services, Log);

    // ── Middleware ──
    app.UseDeveloperExceptionPage();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    if (hangfireOk)
    {
        try { SolarApp.Startup.ConfigureHangfireDashboard(app); Log("Hangfire jobs OK"); }
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
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nFATAL: {ex.Message}");
    Console.WriteLine($"Log: {logFile}");
    Console.ResetColor();
}
