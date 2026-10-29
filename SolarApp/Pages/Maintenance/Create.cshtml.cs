using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Maintenance;

/// <summary>
/// New field work is a work order. This page used to create a third kind of
/// record that the dashboard, reports, LINE and the customer portal mostly
/// ignored, and that offered a second way to log the same visit. Existing
/// records are still viewable and editable on Maintenance/Edit.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class CreateModel : PageModel
{
    public IActionResult OnGet(int? installationId = null, MaintenanceType? type = null)
    {
        var woType = type.HasValue ? UiLabels.ToWorkOrderType(type.Value) : WorkOrderType.PreventiveMaintenance;
        return RedirectToPagePermanent("/WorkOrders/Create", new { installationId, type = woType.ToString() });
    }
}
