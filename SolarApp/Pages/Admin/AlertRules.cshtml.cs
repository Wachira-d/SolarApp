using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.AlertRules;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AlertRulesModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAlertRulesEngine _engine;

    public AlertRulesModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IAlertRulesEngine engine)
    {
        _context = context;
        _userManager = userManager;
        _engine = engine;
    }

    public List<AlertRule> Rules { get; set; } = new();
    public List<Installation> Installations { get; set; } = new();

    [BindProperty] public AlertRule Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Rules = await _context.AlertRules
            .Include(r => r.Installation)
            .OrderByDescending(r => r.IsEnabled)
            .ThenBy(r => r.Name)
            .ToListAsync();
        Installations = await _context.Installations
            .OrderBy(i => i.SiteName)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ErrorMessage = "Name is required";
            return RedirectToPage();
        }
        if (Input.CooldownMinutes < 1) Input.CooldownMinutes = 60;

        var user = await _userManager.GetUserAsync(User);
        Input.CreatedByUserId = user?.Id;
        Input.CreatedAt = DateTime.UtcNow;
        Input.UpdatedAt = DateTime.UtcNow;

        _context.AlertRules.Add(Input);
        await _context.SaveChangesAsync();

        StatusMessage = $"Rule '{Input.Name}' created";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int ruleId)
    {
        var rule = await _context.AlertRules.FindAsync(ruleId);
        if (rule == null) return NotFound();
        rule.IsEnabled = !rule.IsEnabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        StatusMessage = $"Rule '{rule.Name}' {(rule.IsEnabled ? "enabled" : "disabled")}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int ruleId)
    {
        var rule = await _context.AlertRules.FindAsync(ruleId);
        if (rule == null) return NotFound();
        _context.AlertRules.Remove(rule);
        await _context.SaveChangesAsync();
        StatusMessage = $"Rule '{rule.Name}' deleted";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync()
    {
        var fired = await _engine.EvaluateAllAsync();
        StatusMessage = $"Evaluation complete: {fired} alert(s) fired";
        return RedirectToPage();
    }
}
