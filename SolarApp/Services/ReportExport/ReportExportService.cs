using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SolarApp.Data;
using SolarApp.Models.Enums;

namespace SolarApp.Services.ReportExport;

public class ReportExportService : IReportExportService
{
    private readonly ApplicationDbContext _context;

    public ReportExportService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<byte[]> ExportProductionToExcelAsync(int installationId, DateTime from, DateTime to)
    {
        var installation = await _context.Installations
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == installationId)
            ?? throw new ArgumentException("Installation not found");

        var data = await _context.EnergyData
            .Where(e => e.InstallationId == installationId
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        using var workbook = new XLWorkbook();

        // Summary sheet
        var summarySheet = workbook.Worksheets.Add("Summary");
        summarySheet.Cell("A1").Value = "Solar Production Report";
        summarySheet.Cell("A1").Style.Font.Bold = true;
        summarySheet.Cell("A1").Style.Font.FontSize = 16;

        summarySheet.Cell("A3").Value = "Site:";
        summarySheet.Cell("B3").Value = installation.SiteName;
        summarySheet.Cell("A4").Value = "Customer:";
        summarySheet.Cell("B4").Value = installation.Customer.FullName;
        summarySheet.Cell("A5").Value = "Period:";
        summarySheet.Cell("B5").Value = $"{from:dd/MM/yyyy} - {to:dd/MM/yyyy}";
        summarySheet.Cell("A6").Value = "Capacity:";
        summarySheet.Cell("B6").Value = $"{installation.SystemCapacityKw} kWp";

        var totalGen = data.Sum(e => e.EnergyGenerationKwh);
        var totalExport = data.Sum(e => e.GridExportKwh);
        var totalImport = data.Sum(e => e.GridImportKwh);
        var totalSelf = data.Sum(e => e.SelfConsumptionKwh);
        var totalSavings = totalGen * installation.ElectricityRatePerKwh;
        var days = Math.Max(1, (to - from).Days);

        summarySheet.Cell("A8").Value = "Total Generation (kWh):";
        summarySheet.Cell("B8").Value = totalGen;
        summarySheet.Cell("A9").Value = "Total Export (kWh):";
        summarySheet.Cell("B9").Value = totalExport;
        summarySheet.Cell("A10").Value = "Total Import (kWh):";
        summarySheet.Cell("B10").Value = totalImport;
        summarySheet.Cell("A11").Value = "Self Consumption (kWh):";
        summarySheet.Cell("B11").Value = totalSelf;
        summarySheet.Cell("A12").Value = "Average Daily (kWh):";
        summarySheet.Cell("B12").Value = totalGen / days;
        summarySheet.Cell("A13").Value = "Total Savings (THB):";
        summarySheet.Cell("B13").Value = totalSavings;
        summarySheet.Cell("B13").Style.NumberFormat.Format = "#,##0.00";

        summarySheet.Column("A").Width = 25;
        summarySheet.Column("B").Width = 20;

        // Daily data sheet
        var dailySheet = workbook.Worksheets.Add("Daily Data");
        var headers = new[] { "Date", "Generation (kWh)", "Export (kWh)", "Import (kWh)",
                              "Self Consumption (kWh)", "Battery SOC (%)", "Savings (THB)" };

        for (var i = 0; i < headers.Length; i++)
        {
            dailySheet.Cell(1, i + 1).Value = headers[i];
            dailySheet.Cell(1, i + 1).Style.Font.Bold = true;
            dailySheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        for (var row = 0; row < data.Count; row++)
        {
            var d = data[row];
            dailySheet.Cell(row + 2, 1).Value = d.Timestamp.ToString("dd/MM/yyyy");
            dailySheet.Cell(row + 2, 2).Value = (double)d.EnergyGenerationKwh;
            dailySheet.Cell(row + 2, 3).Value = (double)d.GridExportKwh;
            dailySheet.Cell(row + 2, 4).Value = (double)d.GridImportKwh;
            dailySheet.Cell(row + 2, 5).Value = (double)d.SelfConsumptionKwh;
            dailySheet.Cell(row + 2, 6).Value = d.BatterySocPercent.HasValue ? (double)d.BatterySocPercent : 0;
            dailySheet.Cell(row + 2, 7).Value = (double)(d.EnergyGenerationKwh * installation.ElectricityRatePerKwh);
        }

        dailySheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportProductionToPdfAsync(int installationId, DateTime from, DateTime to)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var installation = await _context.Installations
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == installationId)
            ?? throw new ArgumentException("Installation not found");

        var data = await _context.EnergyData
            .Where(e => e.InstallationId == installationId
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        var totalGen = data.Sum(e => e.EnergyGenerationKwh);
        var totalSavings = totalGen * installation.ElectricityRatePerKwh;
        var days = Math.Max(1, (to - from).Days);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);

                page.Header().Column(col =>
                {
                    col.Item().Text("Solar Production Report").FontSize(20).Bold()
                        .FontColor(Colors.Orange.Darken2);
                    col.Item().PaddingTop(5).Text($"Generated: {DateTime.Now:dd/MM/yyyy HH:mm}")
                        .FontSize(9).FontColor(Colors.Grey.Medium);
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    // Site info
                    col.Item().Background(Colors.Grey.Lighten4).Padding(15).Column(info =>
                    {
                        info.Item().Text($"Site: {installation.SiteName}").FontSize(14).Bold();
                        info.Item().Text($"Customer: {installation.Customer.FullName}");
                        info.Item().Text($"Period: {from:dd/MM/yyyy} - {to:dd/MM/yyyy}");
                        info.Item().Text($"System: {installation.SystemCapacityKw} kWp | {installation.InverterBrand} | {installation.NumberOfPanels} panels");
                    });

                    col.Item().PaddingTop(15);

                    // Summary
                    col.Item().Text("Production Summary").FontSize(14).Bold();
                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2);
                            c.RelativeColumn(1);
                        });

                        AddSummaryRow(table, "Total Generation", $"{totalGen:N1} kWh");
                        AddSummaryRow(table, "Grid Export", $"{data.Sum(e => e.GridExportKwh):N1} kWh");
                        AddSummaryRow(table, "Grid Import", $"{data.Sum(e => e.GridImportKwh):N1} kWh");
                        AddSummaryRow(table, "Self Consumption", $"{data.Sum(e => e.SelfConsumptionKwh):N1} kWh");
                        AddSummaryRow(table, "Average Daily", $"{totalGen / days:N1} kWh/day");
                        AddSummaryRow(table, "Total Savings", $"{totalSavings:N0} THB");
                    });

                    col.Item().PaddingTop(15);

                    // Daily table
                    col.Item().Text("Daily Breakdown").FontSize(14).Bold();
                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2);
                            c.RelativeColumn(1.5f);
                            c.RelativeColumn(1.5f);
                            c.RelativeColumn(1.5f);
                            c.RelativeColumn(1.5f);
                        });

                        // Header
                        table.Header(header =>
                        {
                            header.Cell().Background(Colors.Orange.Darken2).Padding(5)
                                .Text("Date").FontColor(Colors.White).FontSize(9).Bold();
                            header.Cell().Background(Colors.Orange.Darken2).Padding(5)
                                .Text("Generation").FontColor(Colors.White).FontSize(9).Bold();
                            header.Cell().Background(Colors.Orange.Darken2).Padding(5)
                                .Text("Export").FontColor(Colors.White).FontSize(9).Bold();
                            header.Cell().Background(Colors.Orange.Darken2).Padding(5)
                                .Text("Import").FontColor(Colors.White).FontSize(9).Bold();
                            header.Cell().Background(Colors.Orange.Darken2).Padding(5)
                                .Text("Savings").FontColor(Colors.White).FontSize(9).Bold();
                        });

                        foreach (var d in data)
                        {
                            var bg = data.IndexOf(d) % 2 == 0 ? Colors.White : Colors.Grey.Lighten4;
                            table.Cell().Background(bg).Padding(4).Text(d.Timestamp.ToString("dd/MM/yyyy")).FontSize(9);
                            table.Cell().Background(bg).Padding(4).Text($"{d.EnergyGenerationKwh:N1}").FontSize(9);
                            table.Cell().Background(bg).Padding(4).Text($"{d.GridExportKwh:N1}").FontSize(9);
                            table.Cell().Background(bg).Padding(4).Text($"{d.GridImportKwh:N1}").FontSize(9);
                            table.Cell().Background(bg).Padding(4)
                                .Text($"{d.EnergyGenerationKwh * installation.ElectricityRatePerKwh:N0}").FontSize(9);
                        }
                    });
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("SolarApp - Solar Management System | Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });

        using var stream = new MemoryStream();
        document.GeneratePdf(stream);
        return stream.ToArray();
    }

    private static void AddSummaryRow(TableDescriptor table, string label, string value)
    {
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(label).FontSize(10);
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
            .AlignRight().Text(value).FontSize(10).Bold();
    }
}
