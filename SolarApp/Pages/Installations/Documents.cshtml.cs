using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class DocumentsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IInstallationAccessHelper _access;

    public DocumentsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IConfiguration configuration, IInstallationAccessHelper access)
    {
        _context = context;
        _userManager = userManager;
        _configuration = configuration;
        _access = access;
    }

    public Installation Installation { get; set; } = null!;
    public List<InstallationDocument> DocumentList { get; set; } = new();

    [BindProperty]
    public DocumentUploadInput Input { get; set; } = new();

    public class DocumentUploadInput
    {
        public int InstallationId { get; set; }
        public DocumentType DocumentType { get; set; }
        public string Title { get; set; } = string.Empty;
        public IFormFile? File { get; set; }
        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();

        var inst = await _context.Installations
            .Include(i => i.Documents.OrderByDescending(d => d.CreatedAt))
                .ThenInclude(d => d.UploadedByUser)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id);

        if (inst == null) return NotFound();

        Installation = inst;
        DocumentList = inst.Documents.ToList();
        Input.InstallationId = id;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await _access.CanAccessAsync(Input.InstallationId, User)) return Forbid();

        if (Input.File == null || Input.File.Length == 0)
        {
            ModelState.AddModelError("Input.File", "Please select a file");
            return await OnGetAsync(Input.InstallationId);
        }

        var maxSize = _configuration.GetValue("FileStorage:MaxFileSizeMb", 10) * 1024 * 1024;
        if (Input.File.Length > maxSize)
        {
            ModelState.AddModelError("Input.File", $"File size exceeds {maxSize / 1024 / 1024}MB limit");
            return await OnGetAsync(Input.InstallationId);
        }

        var basePath = _configuration.GetValue<string>("FileStorage:BasePath") ?? "./uploads";
        var installationPath = Path.Combine(basePath, Input.InstallationId.ToString());
        Directory.CreateDirectory(installationPath);

        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(Input.File.FileName)}";
        var filePath = Path.Combine(installationPath, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await Input.File.CopyToAsync(stream);
        }

        var user = await _userManager.GetUserAsync(User);

        _context.InstallationDocuments.Add(new InstallationDocument
        {
            InstallationId = Input.InstallationId,
            DocumentType = Input.DocumentType,
            Title = Input.Title,
            FilePath = filePath,
            ContentType = Input.File.ContentType,
            FileSizeBytes = Input.File.Length,
            UploadedByUserId = user!.Id,
            Notes = Input.Notes
        });

        await _context.SaveChangesAsync();
        return RedirectToPage("Documents", new { id = Input.InstallationId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int documentId)
    {
        var doc = await _context.InstallationDocuments.FindAsync(documentId);
        if (doc == null) return NotFound();

        if (!await _access.CanAccessAsync(doc.InstallationId, User)) return Forbid();

        var installationId = doc.InstallationId;

        if (System.IO.File.Exists(doc.FilePath))
            System.IO.File.Delete(doc.FilePath);

        _context.InstallationDocuments.Remove(doc);
        await _context.SaveChangesAsync();

        return RedirectToPage("Documents", new { id = installationId });
    }
}
