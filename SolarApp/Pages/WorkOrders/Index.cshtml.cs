using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWorkOrderAccessHelper _access;

    public IndexModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IWorkOrderAccessHelper access)
    {
        _context = context;
        _userManager = userManager;
        _access = access;
    }

    public List<WorkOrder> Orders { get; set; } = new();

    [BindProperty(SupportsGet = true)] public WorkOrderStatus? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public WorkOrderType? TypeFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true), DataType(DataType.Date)] public DateTime? FromDate { get; set; }
    [BindProperty(SupportsGet = true), DataType(DataType.Date)] public DateTime? ToDate { get; set; }

    public int TotalCount { get; set; }
    public int OverdueCount { get; set; }
    public int TodayCount { get; set; }
    public int ThisWeekCount { get; set; }

    public string? TableError { get; set; }

    public async Task OnGetAsync()
    {
        // Guard: table might not exist yet if schema upgrade hasn't run.
        // Try to CREATE it on-demand from this page (idempotent) so the user
        // doesn't need to wait for another app-pool restart.
        bool tableExists = await TryProbeTableAsync();
        if (!tableExists)
        {
            try
            {
                await CreateWorkOrdersTablesAsync();
            }
            catch (Exception ex)
            {
                TableError = "WorkOrders table is missing and on-demand CREATE failed: " + ex.Message +
                    " — Check Admin → Startup Logs and verify the DB user has CREATE TABLE permission.";
                return;
            }

            // Re-probe; if still missing, surface a detailed message
            tableExists = await TryProbeTableAsync();
            if (!tableExists)
            {
                TableError = "WorkOrders table does not exist and on-demand CREATE silently failed. " +
                    "Check Admin → Startup Logs for the schema-upgrade failure reason " +
                    "(usually: DB user lacks CREATE TABLE permission, or a foreign-key target table is missing).";
                return;
            }
        }

        var baseQ = _context.WorkOrders
            .Include(w => w.Installation)
            .Include(w => w.AssignedToUser)
            .Include(w => w.Customer)
            .AsQueryable();

        var q = await _access.ApplyVisibilityFilterAsync(baseQ, User);

        if (StatusFilter.HasValue) q = q.Where(w => w.Status == StatusFilter);
        if (TypeFilter.HasValue) q = q.Where(w => w.Type == TypeFilter);
        if (FromDate.HasValue)
        {
            var from = FromDate.Value.Date;
            q = q.Where(w => w.ScheduledDate >= from);
        }
        if (ToDate.HasValue)
        {
            var to = ToDate.Value.Date.AddDays(1);
            q = q.Where(w => w.ScheduledDate < to);
        }
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim().ToLower();
            q = q.Where(w => w.Title.ToLower().Contains(s)
                          || w.WorkOrderNumber.ToLower().Contains(s)
                          || (w.ExternalSiteName ?? "").ToLower().Contains(s));
        }

        var now = DateTime.UtcNow.Date;
        var weekEnd = now.AddDays(7);

        TotalCount = await q.CountAsync();
        OverdueCount = await q.CountAsync(w => w.Status < WorkOrderStatus.Completed && w.ScheduledDate.Date < now);
        TodayCount = await q.CountAsync(w => w.ScheduledDate.Date == now);
        ThisWeekCount = await q.CountAsync(w => w.ScheduledDate.Date >= now && w.ScheduledDate.Date <= weekEnd);

        Orders = await q.OrderByDescending(w => w.ScheduledDate).Take(200).ToListAsync();
    }

    private async Task<bool> TryProbeTableAsync()
    {
        try
        {
            #pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync("SELECT TOP 0 * FROM [WorkOrders]");
            #pragma warning restore EF1002
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateWorkOrdersTablesAsync()
    {
        // Idempotent CREATE — mirror of the canonical schema in Startup.RunSchemaUpgradesAsync.
        // Kept here so admins who hit this page can self-heal without another app-pool restart.
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrders')
BEGIN
    CREATE TABLE [WorkOrders] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [WorkOrderNumber] nvarchar(20) NOT NULL,
        [Type] int NOT NULL,
        [Status] int NOT NULL DEFAULT 0,
        [InstallationId] int NULL,
        [ExternalSiteName] nvarchar(200) NULL,
        [ExternalSiteAddress] nvarchar(500) NULL,
        [ExternalCustomerName] nvarchar(100) NULL,
        [ExternalCustomerPhone] nvarchar(50) NULL,
        [ExternalLatitude] float NULL,
        [ExternalLongitude] float NULL,
        [CustomerId] int NULL,
        [ScheduledDate] datetime2 NOT NULL,
        [ScheduledTime] time NULL,
        [EstimatedDurationMinutes] int NOT NULL DEFAULT 120,
        [ActualStartTime] datetime2 NULL,
        [ActualEndTime] datetime2 NULL,
        [RecurrenceMonths] int NULL,
        [ParentWorkOrderId] int NULL,
        [AssignedTeam] nvarchar(200) NULL,
        [AssignedToUserId] nvarchar(450) NULL,
        [CreatedByUserId] nvarchar(450) NULL,
        [Title] nvarchar(200) NOT NULL,
        [Description] nvarchar(max) NULL,
        [Scope] nvarchar(max) NULL,
        [SpecialInstructions] nvarchar(max) NULL,
        [LaborCost] decimal(12,2) NOT NULL DEFAULT 0,
        [MaterialCost] decimal(12,2) NOT NULL DEFAULT 0,
        [TravelCost] decimal(12,2) NOT NULL DEFAULT 0,
        [OtherCost] decimal(12,2) NOT NULL DEFAULT 0,
        [CostNotes] nvarchar(max) NULL,
        [BeforePhotos] nvarchar(max) NULL,
        [AfterPhotos] nvarchar(max) NULL,
        [Attachments] nvarchar(max) NULL,
        [CompletionNotes] nvarchar(max) NULL,
        [CustomerSignature] nvarchar(max) NULL,
        [CustomerSigned] bit NOT NULL DEFAULT 0,
        [CustomerRating] int NULL,
        [CustomerFeedback] nvarchar(max) NULL,
        [ServiceTicketId] int NULL,
        [ReminderSentAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [UpdatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX IX_WorkOrders_Number ON [WorkOrders]([WorkOrderNumber]);
    CREATE INDEX IX_WorkOrders_Status ON [WorkOrders]([Status]);
    CREATE INDEX IX_WorkOrders_ScheduledDate ON [WorkOrders]([ScheduledDate]);
    CREATE INDEX IX_WorkOrders_AssignedToUserId ON [WorkOrders]([AssignedToUserId]);
    CREATE INDEX IX_WorkOrders_Type ON [WorkOrders]([Type]);
    CREATE INDEX IX_WorkOrders_InstallationId ON [WorkOrders]([InstallationId]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderItems')
AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrders')
BEGIN
    CREATE TABLE [WorkOrderItems] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [WorkOrderId] int NOT NULL,
        [SortOrder] int NOT NULL DEFAULT 0,
        [Description] nvarchar(300) NOT NULL,
        [IsCompleted] bit NOT NULL DEFAULT 0,
        [CompletedAt] datetime2 NULL,
        [CompletedByUserId] nvarchar(450) NULL,
        [Notes] nvarchar(max) NULL,
        [Photos] nvarchar(max) NULL,
        CONSTRAINT FK_WorkOrderItems_WorkOrders FOREIGN KEY ([WorkOrderId])
            REFERENCES [WorkOrders]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX IX_WorkOrderItems_WorkOrderId_SortOrder ON [WorkOrderItems]([WorkOrderId], [SortOrder]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderPhotos')
AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrders')
BEGIN
    CREATE TABLE [WorkOrderPhotos] (
        [Id] int IDENTITY(1,1) PRIMARY KEY,
        [WorkOrderId] int NOT NULL,
        [Category] int NOT NULL,
        [Timing] int NOT NULL DEFAULT 1,
        [Description] nvarchar(500) NULL,
        [FilePath] nvarchar(500) NOT NULL,
        [ContentType] nvarchar(100) NULL,
        [FileSizeBytes] bigint NOT NULL DEFAULT 0,
        [Latitude] float NULL,
        [Longitude] float NULL,
        [UploadedByUserId] nvarchar(450) NULL,
        [UploadedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [SortOrder] int NOT NULL DEFAULT 0,
        CONSTRAINT FK_WorkOrderPhotos_WorkOrders FOREIGN KEY ([WorkOrderId])
            REFERENCES [WorkOrders]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX IX_WorkOrderPhotos_WO_Cat_Timing ON [WorkOrderPhotos]([WorkOrderId], [Category], [Timing]);
END;";
        #pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync(sql);
        #pragma warning restore EF1002
    }
}
