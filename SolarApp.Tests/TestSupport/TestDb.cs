using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Tests.TestSupport;

/// <summary>A fresh in-memory ApplicationDbContext per test, with one customer and one site.</summary>
public static class TestDb
{
    public static ApplicationDbContext Create(string? name = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    public static (Customer Customer, Installation Site) SeedSite(ApplicationDbContext db,
        InverterBrand brand = InverterBrand.FoxESS, bool hasBattery = true)
    {
        var customer = new Customer { FirstName = "Test", LastName = "Owner", Phone = "0800000000", Address = "x" };
        db.Customers.Add(customer);
        db.SaveChanges();
        var site = new Installation
        {
            SiteName = "Test Site",
            SiteCode = "TS-001",
            Address = "x",
            CustomerId = customer.Id,
            InverterBrand = brand,
            SystemCapacityKw = 10,
            HasBattery = hasBattery,
            BatteryCapacityKwh = hasBattery ? 10.24m : null,
            BatteryCount = hasBattery ? 3 : null,
            TelemetrySource = TelemetrySource.LocalGateway,
            InstallationDate = new DateTime(2025, 1, 1),
            Status = InstallationStatus.Active,
        };
        db.Installations.Add(site);
        db.SaveChanges();
        return (customer, site);
    }
}
