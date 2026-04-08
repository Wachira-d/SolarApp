namespace SolarApp.Services.ReportExport;

public interface IReportExportService
{
    Task<byte[]> ExportProductionToExcelAsync(int installationId, DateTime from, DateTime to);
    Task<byte[]> ExportProductionToPdfAsync(int installationId, DateTime from, DateTime to);
}
