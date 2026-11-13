using FluentAssertions;
using SolarApp.Models.Enums;
using SolarApp.Services.Telemetry;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

public class StatusCodeCatalogBrandTests
{
    [Theory]
    [InlineData(0x0000, "initializing", AlertSeverity.Low)]
    [InlineData(0x0200, "On-grid", AlertSeverity.Low)]
    [InlineData(0x0300, "fault", AlertSeverity.High)]
    [InlineData(0xA000, "No irradiation", AlertSeverity.Low)]
    public void Huawei_32089_device_status(long code, string contains, AlertSeverity severity)
    {
        var d = StatusCodeCatalog.Decode(InverterBrand.Huawei, "status", code);
        d.IsKnown.Should().BeTrue();
        d.Text.Should().Contain(contains);
        d.Severity.Should().Be(severity);
    }

    [Fact]
    public void Huawei_alarm_register_is_decoded_bit_by_bit()
    {
        var d = StatusCodeCatalog.Decode(InverterBrand.Huawei, "alarm", (1 << 7) | (1 << 9));
        d.IsKnown.Should().BeTrue();
        d.Text.Should().Contain("Grid loss").And.Contain("Grid overvoltage").And.Contain("ไฟฟ้าดับ");
        d.Severity.Should().Be(AlertSeverity.High);
        StatusCodeCatalog.Decode(InverterBrand.Huawei, "alarm", 1 << 1).Severity.Should().Be(AlertSeverity.Critical);
        StatusCodeCatalog.Decode(InverterBrand.Huawei, "alarm", 0).Text.Should().Be("ไม่มี alarm");
    }

    [Fact]
    public void FoxEss_and_Deye_running_states()
    {
        StatusCodeCatalog.Decode(InverterBrand.FoxESS, "status", 3).Text.Should().Contain("On-grid");
        StatusCodeCatalog.Decode(InverterBrand.FoxESS, "status", 6).Severity.Should().Be(AlertSeverity.Critical);
        StatusCodeCatalog.Decode(InverterBrand.Deye, "status", 2).Text.Should().Contain("ปกติ");
        StatusCodeCatalog.Decode(InverterBrand.Deye, "status", 4).Severity.Should().Be(AlertSeverity.High);
        StatusCodeCatalog.Decode(InverterBrand.Deye, "status", 1).Text.Should().Contain("Self-check");
    }

    [Fact]
    public void Unknown_codes_still_say_unknown()
    {
        var d = StatusCodeCatalog.Decode(InverterBrand.Deye, "status", 99);
        d.IsKnown.Should().BeFalse();
        d.Text.Should().Contain("ไม่ทราบความหมาย");
        StatusCodeCatalog.Decode(InverterBrand.Solis, "status", 3).IsKnown.Should().BeFalse();
    }
}
