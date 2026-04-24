using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class StartupLogsModel : PageModel
{
    private readonly IConfiguration _configuration;

    public StartupLogsModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public record LogEntry(long Id, DateTime Timestamp, string MachineName, bool IsError, string LogText);

    public List<LogEntry> Entries { get; set; } = new();
    public string? QueryError { get; set; }

    public async Task OnGetAsync()
    {
        var cs = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs)) return;

        try
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();

            // Verify table exists
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE name='StartupLogs') THEN 1 ELSE 0 END";
                if ((int)(await check.ExecuteScalarAsync() ?? 0) == 0)
                {
                    QueryError = "StartupLogs table doesn't exist yet — no errors have been logged to DB.";
                    return;
                }
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT TOP 50 [Id], [Timestamp], [MachineName], [IsError], [LogText]
                                FROM [StartupLogs]
                                ORDER BY [Timestamp] DESC";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Entries.Add(new LogEntry(
                    reader.GetInt64(0),
                    reader.GetDateTime(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4)));
            }
        }
        catch (Exception ex)
        {
            QueryError = $"Could not read StartupLogs: {ex.Message}";
        }
    }
}
