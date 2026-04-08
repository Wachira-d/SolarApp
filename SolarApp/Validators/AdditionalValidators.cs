using FluentValidation;
using SolarApp.Models;

namespace SolarApp.Validators;

public class MaintenanceRecordValidator : AbstractValidator<MaintenanceRecord>
{
    public MaintenanceRecordValidator()
    {
        RuleFor(x => x.InstallationId).GreaterThan(0);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.ScheduledDate).NotEmpty();
        RuleFor(x => x.CompletedDate)
            .GreaterThanOrEqualTo(x => x.ScheduledDate)
            .When(x => x.CompletedDate.HasValue)
            .WithMessage("Completed date cannot be before scheduled date");
        RuleFor(x => x.Cost).GreaterThanOrEqualTo(0).When(x => x.Cost.HasValue);
    }
}

public class CleaningRecordValidator : AbstractValidator<CleaningRecord>
{
    public CleaningRecordValidator()
    {
        RuleFor(x => x.InstallationId).GreaterThan(0);
        RuleFor(x => x.ScheduledDate).NotEmpty();
        RuleFor(x => x.ActualDate)
            .LessThanOrEqualTo(DateTime.UtcNow.AddDays(1))
            .When(x => x.ActualDate.HasValue)
            .WithMessage("Actual date cannot be in the future");
    }
}

public class EquipmentValidator : AbstractValidator<Equipment>
{
    public EquipmentValidator()
    {
        RuleFor(x => x.Brand).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Model).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.WarrantyYears).InclusiveBetween(0, 30);
        RuleFor(x => x.SerialNumber).MaximumLength(200);
    }
}

public class WarrantyRecordValidator : AbstractValidator<WarrantyRecord>
{
    public WarrantyRecordValidator()
    {
        RuleFor(x => x.ProviderName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.WarrantyStartDate).NotEmpty();
        RuleFor(x => x.WarrantyEndDate).GreaterThan(x => x.WarrantyStartDate)
            .WithMessage("End date must be after start date");
    }
}
