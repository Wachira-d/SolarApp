// ── Minimal Program.cs: catches ALL errors including DLL load failures ──

var logFile = Path.Combine(AppContext.BaseDirectory, "startup-debug.log");
var errors = new List<string>();

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
    Console.WriteLine(line);
    try { File.AppendAllText(logFile, line + "\n"); } catch { }
}

Log("=== SolarApp Starting ===");
Log($"BaseDirectory: {AppContext.BaseDirectory}");
Log($"Environment: {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}");

try
{
    Log("Step 1: Creating builder...");
    var builder = WebApplication.CreateBuilder(args);

    Log("Step 2: Reading connection string...");
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "";
    Log($"ConnectionString: {(string.IsNullOrEmpty(connectionString) ? "EMPTY!" : "Found (" + connectionString.Split(';')[0] + "...)")}");

    Log("Step 3: Configuring core services...");
    SolarApp.Startup.ConfigureServices(builder);
    Log("Core services OK");

    // Hangfire (optional - skip if DB not ready)
    var dbReady = false;
    if (!string.IsNullOrEmpty(connectionString))
    {
        Log("Step 4: Testing database connection...");
        dbReady = SolarApp.Startup.TryCreateDatabase(connectionString, Log);

        if (dbReady)
        {
            Log("Step 5: Configuring Hangfire...");
            try
            {
                SolarApp.Startup.ConfigureHangfire(builder, connectionString);
                Log("Hangfire OK");
            }
            catch (Exception ex)
            {
                Log($"Hangfire FAILED (non-fatal): {ex.Message}");
                dbReady = false;
            }
        }
    }

    Log("Step 6: Building app...");
    var app = builder.Build();
    Log("Build OK");

    // Database schema + seed
    Log("Step 7: Initializing database...");
    try
    {
        await SolarApp.Startup.InitializeDatabaseAsync(app.Services, Log);
    }
    catch (Exception ex)
    {
        var msg = $"DB init FAILED: {ex.Message}";
        Log(msg);
        errors.Add(msg);
        if (ex.InnerException != null) Log($"  Inner: {ex.InnerException.Message}");
    }

    // Middleware
    Log("Step 8: Configuring middleware...");
    app.UseDeveloperExceptionPage();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    if (dbReady)
    {
        try
        {
            SolarApp.Startup.ConfigureHangfireDashboard(app);
            Log("Hangfire dashboard OK");
        }
        catch (Exception ex)
        {
            Log($"Hangfire dashboard FAILED: {ex.Message}");
        }
    }

    app.MapRazorPages();

    // Report export API
    app.MapGet("/api/reports/excel/{installationId:int}", async (
        int installationId, DateTime? from, DateTime? to,
        SolarApp.Services.ReportExport.IReportExportService svc) =>
    {
        var f = from ?? DateTime.UtcNow.AddMonths(-1);
        var t = to ?? DateTime.UtcNow;
        return Results.File(await svc.ExportProductionToExcelAsync(installationId, f, t),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Report_{installationId}.xlsx");
    }).RequireAuthorization();

    app.MapGet("/api/reports/pdf/{installationId:int}", async (
        int installationId, DateTime? from, DateTime? to,
        SolarApp.Services.ReportExport.IReportExportService svc) =>
    {
        var f = from ?? DateTime.UtcNow.AddMonths(-1);
        var t = to ?? DateTime.UtcNow;
        return Results.File(await svc.ExportProductionToPdfAsync(installationId, f, t),
            "application/pdf", $"Report_{installationId}.pdf");
    }).RequireAuthorization();

    // Diagnostic page
    app.MapGet("/startup-log", () =>
    {
        var content = File.Exists(logFile) ? File.ReadAllText(logFile) : "No log";
        var status = errors.Count == 0 ? "ALL OK" : $"{errors.Count} ERROR(S)";
        return Results.Content($@"<html><head><title>Startup Log</title>
<style>body{{font-family:monospace;background:#1a1a2e;color:#eee;padding:20px}}
pre{{background:#16213e;padding:15px;border-radius:8px;white-space:pre-wrap}}
.err{{color:#ff6b6b}} h1{{color:#ffc107}}</style></head>
<body><h1>SolarApp Startup</h1><h2>Status: {status}</h2>
{(errors.Count > 0 ? "<div class='err'><h3>Errors:</h3><ul>" + string.Join("", errors.Select(e => $"<li>{e}</li>")) + "</ul></div>" : "")}
<h3>Log:</h3><pre>{System.Net.WebUtility.HtmlEncode(content)}</pre>
<p>File: {logFile}</p></body></html>", "text/html");
    });

    Log("=== SolarApp Ready ===");
    Log($"Diagnostic: /startup-log");
    Log($"Errors: {errors.Count}");

    app.Run();
}
catch (Exception ex)
{
    Log($"FATAL: {ex}");
    // Keep the error visible
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("\n========================================");
    Console.WriteLine("FATAL ERROR - App cannot start:");
    Console.WriteLine(ex.ToString());
    Console.WriteLine("========================================");
    Console.WriteLine($"\nFull log saved to: {logFile}");
    Console.ResetColor();

    // If running interactively, wait so user can read
    if (Environment.UserInteractive)
    {
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}
