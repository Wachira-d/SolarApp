namespace SolarApp.Models.Enums;

/// <summary>
/// Thai labels and badge colours for every status and type enum, in one
/// place. Before this, each page carried its own switch — nine copies for
/// InstallationStatus alone, with different words for the same value — and
/// anything without a copy printed the raw C# name ("PanelCleaning",
/// "InProgress") to the technician.
/// </summary>
public static class UiLabels
{
    // ── Work orders ──────────────────────────────────────────────────────

    public static string Label(WorkOrderType t) => t switch
    {
        WorkOrderType.PanelCleaning => "ล้างแผง",
        WorkOrderType.PreventiveMaintenance => "บำรุงรักษาตามรอบ (PM)",
        WorkOrderType.CorrectiveRepair => "ซ่อมแก้ไข",
        WorkOrderType.Inspection => "ตรวจสอบ",
        WorkOrderType.NewInstallation => "ติดตั้งใหม่",
        WorkOrderType.Commissioning => "เปิดใช้งานระบบ",
        WorkOrderType.Decommission => "ถอนการติดตั้ง",
        WorkOrderType.Other => "อื่นๆ",
        _ => t.ToString()
    };

    /// <summary>What the visit was, including a cleaning done as part of it.</summary>
    public static string Label(WorkOrderType t, bool includesCleaning) =>
        includesCleaning && t != WorkOrderType.PanelCleaning ? $"{Label(t)} + ล้างแผง" : Label(t);

    public static string Icon(WorkOrderType t) => t switch
    {
        WorkOrderType.PanelCleaning => "droplet",
        WorkOrderType.PreventiveMaintenance => "clipboard-check",
        WorkOrderType.CorrectiveRepair => "tools",
        WorkOrderType.Inspection => "search",
        WorkOrderType.NewInstallation => "box-seam",
        WorkOrderType.Commissioning => "power",
        WorkOrderType.Decommission => "x-octagon",
        _ => "three-dots"
    };

    public static string Label(WorkOrderStatus s) => s switch
    {
        WorkOrderStatus.Draft => "ร่าง",
        WorkOrderStatus.Scheduled => "นัดหมายแล้ว",
        WorkOrderStatus.Assigned => "มอบหมายแล้ว",
        WorkOrderStatus.Accepted => "รับงานแล้ว",
        WorkOrderStatus.InProgress => "กำลังทำ",
        WorkOrderStatus.Completed => "เสร็จแล้ว",
        WorkOrderStatus.Verified => "ตรวจรับแล้ว",
        WorkOrderStatus.Invoiced => "ออกบิลแล้ว",
        WorkOrderStatus.Closed => "ปิดงาน",
        WorkOrderStatus.Cancelled => "ยกเลิก",
        _ => s.ToString()
    };

    public static string Badge(WorkOrderStatus s) => s switch
    {
        WorkOrderStatus.Draft => "bg-secondary",
        WorkOrderStatus.Scheduled => "bg-info text-dark",
        WorkOrderStatus.Assigned => "bg-primary",
        WorkOrderStatus.Accepted => "bg-primary",
        WorkOrderStatus.InProgress => "bg-warning text-dark",
        WorkOrderStatus.Completed => "bg-success",
        WorkOrderStatus.Verified => "bg-success",
        WorkOrderStatus.Invoiced => "bg-dark",
        WorkOrderStatus.Closed => "bg-secondary",
        WorkOrderStatus.Cancelled => "bg-danger",
        _ => "bg-secondary"
    };

    // ── Legacy maintenance records ───────────────────────────────────────

    public static string Label(MaintenanceType t) => t switch
    {
        MaintenanceType.PanelCleaning => "ล้างแผง",
        MaintenanceType.Inspection => "ตรวจสอบ",
        MaintenanceType.Repair => "ซ่อมแก้ไข",
        MaintenanceType.Replacement => "เปลี่ยนอุปกรณ์",
        MaintenanceType.GeneralMaintenance => "บำรุงรักษาทั่วไป",
        _ => t.ToString()
    };

    public static string Label(MaintenanceStatus s) => s switch
    {
        MaintenanceStatus.Scheduled => "นัดหมายแล้ว",
        MaintenanceStatus.InProgress => "กำลังทำ",
        MaintenanceStatus.Completed => "เสร็จแล้ว",
        MaintenanceStatus.Cancelled => "ยกเลิก",
        _ => s.ToString()
    };

    public static string Badge(MaintenanceStatus s) => s switch
    {
        MaintenanceStatus.Scheduled => "bg-info text-dark",
        MaintenanceStatus.InProgress => "bg-warning text-dark",
        MaintenanceStatus.Completed => "bg-success",
        MaintenanceStatus.Cancelled => "bg-secondary",
        _ => "bg-secondary"
    };

    /// <summary>The work-order type a legacy maintenance type maps onto.</summary>
    public static WorkOrderType ToWorkOrderType(MaintenanceType t) => t switch
    {
        MaintenanceType.PanelCleaning => WorkOrderType.PanelCleaning,
        MaintenanceType.Inspection => WorkOrderType.Inspection,
        MaintenanceType.Repair => WorkOrderType.CorrectiveRepair,
        MaintenanceType.Replacement => WorkOrderType.CorrectiveRepair,
        _ => WorkOrderType.PreventiveMaintenance
    };

    // ── Cleaning ─────────────────────────────────────────────────────────

    public static string Label(CleaningStatus s) => s switch
    {
        CleaningStatus.Scheduled => "นัดหมายแล้ว",
        CleaningStatus.Completed => "ล้างแล้ว",
        CleaningStatus.Missed => "ไม่ได้ล้าง",
        CleaningStatus.Cancelled => "ยกเลิก",
        _ => s.ToString()
    };

    public static string Badge(CleaningStatus s) => s switch
    {
        CleaningStatus.Scheduled => "bg-info text-dark",
        CleaningStatus.Completed => "bg-success",
        CleaningStatus.Missed => "bg-danger",
        CleaningStatus.Cancelled => "bg-secondary",
        _ => "bg-secondary"
    };

    // ── Service tickets ──────────────────────────────────────────────────

    public static string Label(TicketStatus s) => s switch
    {
        TicketStatus.Open => "รับเรื่องแล้ว",
        TicketStatus.Assigned => "มอบหมายแล้ว",
        TicketStatus.InProgress => "กำลังดำเนินการ",
        TicketStatus.Resolved => "แก้ไขแล้ว",
        TicketStatus.Closed => "ปิดเรื่อง",
        _ => s.ToString()
    };

    public static string Badge(TicketStatus s) => s switch
    {
        TicketStatus.Open => "bg-primary",
        TicketStatus.Assigned => "bg-primary",
        TicketStatus.InProgress => "bg-warning text-dark",
        TicketStatus.Resolved => "bg-success",
        TicketStatus.Closed => "bg-secondary",
        _ => "bg-secondary"
    };

    public static string Label(TicketPriority p) => p switch
    {
        TicketPriority.Urgent => "เร่งด่วน",
        TicketPriority.High => "สูง",
        TicketPriority.Normal => "ปกติ",
        TicketPriority.Low => "ต่ำ",
        _ => p.ToString()
    };

    public static string Badge(TicketPriority p) => p switch
    {
        TicketPriority.Urgent => "bg-danger",
        TicketPriority.High => "bg-warning text-dark",
        TicketPriority.Normal => "bg-info text-dark",
        _ => "bg-secondary"
    };

    // ── Sites and alerts ─────────────────────────────────────────────────

    public static string Label(InstallationStatus s) => s switch
    {
        InstallationStatus.Active => "ทำงานปกติ",
        InstallationStatus.Offline => "ออฟไลน์",
        InstallationStatus.Maintenance => "ต้องตรวจสอบ",
        InstallationStatus.Inactive => "ปิดใช้งาน",
        _ => s.ToString()
    };

    public static string Badge(InstallationStatus s) => s switch
    {
        InstallationStatus.Active => "bg-success",
        InstallationStatus.Offline => "bg-danger",
        InstallationStatus.Maintenance => "bg-warning text-dark",
        InstallationStatus.Inactive => "bg-dark",
        _ => "bg-secondary"
    };

    public static string Label(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "วิกฤต",
        AlertSeverity.High => "สูง",
        AlertSeverity.Medium => "ปานกลาง",
        AlertSeverity.Low => "ต่ำ",
        _ => s.ToString()
    };

    public static string Badge(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "bg-danger",
        AlertSeverity.High => "bg-warning text-dark",
        AlertSeverity.Medium => "bg-info text-dark",
        _ => "bg-secondary"
    };
}
