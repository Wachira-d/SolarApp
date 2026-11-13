using FluentAssertions;
using SolarApp.Services;
using SolarApp.Services.Notifications;
using Xunit;

namespace SolarApp.Tests.Services.Notifications;

public class CustomerEmailTemplateTests
{
    [Fact]
    public void AlertHtml_ContainsSiteTitleThaiTimeAndLink()
    {
        var utc = new DateTime(2026, 3, 1, 17, 30, 0, DateTimeKind.Utc);
        var html = CustomerEmailTemplate.BuildAlertHtml("บ้านคุณสมชาย", "ระบบขาดการเชื่อมต่อ",
            "บรรทัดแรก\nบรรทัดสอง", utc, "https://solar.example.com/My/Site/5");

        html.Should().Contain("บ้านคุณสมชาย");
        html.Should().Contain("ระบบขาดการเชื่อมต่อ");
        html.Should().Contain(SiteTime.ToSiteTime(utc).ToString("dd/MM/yyyy เวลา HH:mm"));
        html.Should().Contain("บรรทัดแรก<br>บรรทัดสอง");
        html.Should().Contain("href=\"https://solar.example.com/My/Site/5\"");
    }

    [Fact]
    public void AlertHtml_UsesSiteLocalTime_NotUtc()
    {
        // 17:30 UTC is 00:30 the next day in Bangkok — the date must move too.
        var utc = new DateTime(2026, 3, 1, 17, 30, 0, DateTimeKind.Utc);
        var html = CustomerEmailTemplate.BuildAlertHtml("s", "t", "m", utc, null);
        if (SiteTime.TimeZone.BaseUtcOffset == TimeSpan.FromHours(7))
            html.Should().Contain("02/03/2026 เวลา 00:30");
        html.Should().NotContain("ดูข้อมูลไซต์", "no link without a base URL");
    }

    [Fact]
    public void AlertHtml_EscapesHtml()
    {
        var html = CustomerEmailTemplate.BuildAlertHtml("<script>alert(1)</script>", "A & B <b>",
            "x\"><img src=x onerror=alert(1)>", DateTime.UtcNow, "https://e.com/?a=1&b=\"2\"");

        html.Should().NotContain("<script>");
        html.Should().NotContain("<img");
        html.Should().NotContain("<b>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("A &amp; B &lt;b&gt;");
        html.Should().Contain("a=1&amp;b=&quot;2&quot;");
    }

    [Fact]
    public void SplitLineText_TakesFirstLineAsTitle()
    {
        var (title, body) = CustomerEmailTemplate.SplitLineText("\nℹ️ ระบบโซลาร์ขาดการเชื่อมต่อชั่วคราว\n\n📍 ไซต์: A\nข้อความ");
        title.Should().Be("ℹ️ ระบบโซลาร์ขาดการเชื่อมต่อชั่วคราว");
        body.Should().StartWith("📍 ไซต์: A");
    }

    [Fact]
    public void Subject_IsSingleLine()
    {
        CustomerEmailTemplate.Subject("Site\nA", "Down\r\nnow").Should().NotContain("\n").And.Contain("Site A");
    }

    [Theory]
    [InlineData("owner@example.com", true)]
    [InlineData("a.b+c@sub.example.co.th", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("a@b", false)]
    [InlineData("a@@b.com", false)]
    [InlineData("Name <a@b.com>", false)]
    [InlineData("", false)]
    public void IsValidAddress(string address, bool expected) =>
        EmailSettings.IsValidAddress(address).Should().Be(expected);
}
