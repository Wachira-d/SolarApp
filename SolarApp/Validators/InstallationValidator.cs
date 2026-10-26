using FluentValidation;
using SolarApp.Pages.Installations;

namespace SolarApp.Validators;

public class InstallationValidator : AbstractValidator<CreateModel.InstallationInput>
{
    public InstallationValidator()
    {
        RuleFor(x => x.SiteName)
            .NotEmpty().WithMessage("Site name is required")
            .MaximumLength(200);

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required")
            .MaximumLength(500);

        RuleFor(x => x.CustomerId)
            .NotEmpty().WithMessage("Customer is required");

        RuleFor(x => x.SystemCapacityKw)
            .GreaterThan(0).WithMessage("System capacity must be greater than 0")
            .LessThanOrEqualTo(10000);

        RuleFor(x => x.NumberOfPanels)
            .GreaterThan(0).WithMessage("Number of panels must be greater than 0")
            .LessThanOrEqualTo(10000);

        RuleFor(x => x.PanelWattage)
            .InclusiveBetween(100, 1000).WithMessage("Panel wattage must be between 100-1000W");

        RuleFor(x => x.WarrantyYears)
            .InclusiveBetween(1, 30).WithMessage("Warranty must be between 1-30 years");

        // InstallationDate is the handover date — the day the warranty starts.
        // The column keeps its old name; the messages say what it means.
        RuleFor(x => x.InstallationDate)
            .NotEmpty().WithMessage("ต้องระบุวันที่ส่งมอบงาน")
            .LessThanOrEqualTo(SolarApp.Services.SiteTime.Today.AddDays(1))
            .WithMessage("วันที่ส่งมอบงานต้องไม่เป็นวันในอนาคต");

        // A warranty that ends before it starts is a typo, and it would make
        // every countdown on the site negative.
        RuleFor(x => x.WarrantyExpiryDate)
            .GreaterThan(x => x.InstallationDate)
            .When(x => x.WarrantyExpiryDate.HasValue && x.WarrantyExpiryDate.Value.Year > 1900)
            .WithMessage("วันสิ้นสุดรับประกันต้องอยู่หลังวันที่ส่งมอบงาน");

        RuleFor(x => x.ElectricityRatePerKwh)
            .GreaterThan(0).WithMessage("Electricity rate must be greater than 0");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue);

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue);
    }
}

public class ServiceTicketValidator : AbstractValidator<SolarApp.Models.ServiceTicket>
{
    public ServiceTicketValidator()
    {
        RuleFor(x => x.Subject)
            .NotEmpty().WithMessage("Subject is required")
            .MaximumLength(200);

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required")
            .MaximumLength(4000);

        RuleFor(x => x.InstallationId)
            .GreaterThan(0).WithMessage("Installation is required");
    }
}

public class CustomerValidator : AbstractValidator<SolarApp.Models.Customer>
{
    public CustomerValidator()
    {
        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required")
            .MaximumLength(100);

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required")
            .MaximumLength(100);

        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Phone is required")
            .MaximumLength(20);

        RuleFor(x => x.Email)
            .EmailAddress().When(x => !string.IsNullOrEmpty(x.Email));

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required");
    }
}

public class PartnerValidator : AbstractValidator<SolarApp.Models.Partner>
{
    public PartnerValidator()
    {
        RuleFor(x => x.CompanyName)
            .NotEmpty().WithMessage("Company name is required")
            .MaximumLength(200);

        RuleFor(x => x.ContactEmail)
            .NotEmpty().WithMessage("Contact email is required")
            .EmailAddress();

        RuleFor(x => x.ContactPhone)
            .NotEmpty().WithMessage("Contact phone is required");

        RuleFor(x => x.CommissionRate)
            .InclusiveBetween(0, 100).When(x => x.CommissionRate.HasValue)
            .WithMessage("Commission rate must be between 0-100%");
    }
}
