using Microsoft.AspNetCore.Identity;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Data;

public static class DbInitializer
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await context.Database.EnsureCreatedAsync();

        // Seed roles
        string[] roles = { UserRoles.Admin, UserRoles.Partner, UserRoles.Customer };
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        // Seed admin user
        if (await userManager.FindByEmailAsync("admin@solarapp.com") == null)
        {
            var admin = new ApplicationUser
            {
                UserName = "admin@solarapp.com",
                Email = "admin@solarapp.com",
                FullName = "System Administrator",
                CompanyName = "SolarApp",
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(admin, "Admin@123456");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, UserRoles.Admin);
            }
        }
    }
}
