using System.ComponentModel.DataAnnotations;
using SolarApp.Models.Enums;

namespace SolarApp.Models;

/// <summary>
/// Individual photo attached to a work order, categorized by type
/// (panel overview, inverter zone, thermal scan, etc.) and timing
/// (before/during/after work).
///
/// Each photo has:
///   - Category (predefined, drives the upload UI flow)
///   - Timing (before/during/after)
///   - Optional description/note from the technician
///   - GPS coordinates if the device/camera provides them
///   - Thumbnail auto-generated on upload
/// </summary>
public class WorkOrderPhoto
{
    public int Id { get; set; }

    public int WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;

    [Required]
    public PhotoCategory Category { get; set; }

    public PhotoTiming Timing { get; set; } = PhotoTiming.Before;

    [StringLength(500)]
    public string? Description { get; set; }

    [Required, StringLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [StringLength(100)]
    public string? ContentType { get; set; }

    public long FileSizeBytes { get; set; }

    // GPS from EXIF or device
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public string? UploadedByUserId { get; set; }
    public ApplicationUser? UploadedByUser { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    // Sorting within a category
    public int SortOrder { get; set; }

    // Display helpers
    public string CategoryDisplayName => Category switch
    {
        PhotoCategory.SiteOverview => "ภาพรวมไซต์",
        PhotoCategory.RoofOverview => "ภาพรวมหลังคา",
        PhotoCategory.PanelArrayOverall => "แผงโซลาร์ภาพรวม",
        PhotoCategory.PanelCloseup => "แผงระยะใกล้",
        PhotoCategory.PanelDamage => "แผงเสียหาย",
        PhotoCategory.PanelStringLabel => "ป้ายสตริง",
        PhotoCategory.InverterZone => "โซน Inverter",
        PhotoCategory.InverterDisplay => "จอ Inverter",
        PhotoCategory.InverterWiring => "สายไฟ Inverter",
        PhotoCategory.InverterNameplate => "ป้ายรุ่น Inverter",
        PhotoCategory.ACBreaker => "ตู้ AC Breaker",
        PhotoCategory.DCIsolator => "DC Isolator",
        PhotoCategory.ElectricalPanel => "ตู้ไฟหลัก",
        PhotoCategory.Meter => "มิเตอร์ไฟฟ้า",
        PhotoCategory.WiringConduit => "ท่อ/รางสาย",
        PhotoCategory.ThermalPanelOverall => "🌡 Thermal แผงภาพรวม",
        PhotoCategory.ThermalHotspot => "🌡 Thermal จุดร้อน",
        PhotoCategory.ThermalInverter => "🌡 Thermal Inverter",
        PhotoCategory.ThermalConnection => "🌡 Thermal ขั้วต่อ",
        PhotoCategory.BatteryZone => "โซน Battery",
        PhotoCategory.BatteryDisplay => "จอ BMS",
        PhotoCategory.MountingStructure => "โครงยึดแผง",
        PhotoCategory.RoofPenetration => "จุดเจาะหลังคา",
        PhotoCategory.PanelBeforeCleaning => "แผงก่อนล้าง",
        PhotoCategory.PanelAfterCleaning => "แผงหลังล้าง",
        PhotoCategory.DrainageGutter => "รางน้ำ",
        PhotoCategory.WorkPermit => "ใบอนุญาตทำงาน",
        PhotoCategory.SafetySign => "ป้ายความปลอดภัย",
        PhotoCategory.MeterReading => "อ่านมิเตอร์",
        PhotoCategory.CustomerSignoff => "ลูกค้าเซ็นรับงาน",
        _ => "อื่นๆ"
    };

    public string TimingBadgeClass => Timing switch
    {
        PhotoTiming.Before => "bg-warning text-dark",
        PhotoTiming.During => "bg-info",
        PhotoTiming.After => "bg-success",
        _ => "bg-secondary"
    };
}
