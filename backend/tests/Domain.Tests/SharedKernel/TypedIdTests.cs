using FluentAssertions;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SharedKernel;

public sealed class TypedIdTests
{
    [Theory]
    [InlineData("esp32-01")]
    [InlineData("gateway_7")]
    [InlineData("A")]
    public void DeviceId_accepts_topic_safe_identifiers(string value)
    {
        var result = DeviceId.Create(value);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(value);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("has space", "invalid")]
    [InlineData("has/slash", "invalid")]
    [InlineData("has+plus", "invalid")]
    [InlineData("has#hash", "invalid")]
    [InlineData("naïve", "invalid")]
    public void DeviceId_rejects_invalid_identifiers(string? value, string expectedCode)
    {
        var result = DeviceId.Create(value);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void DeviceId_rejects_overlong_identifiers()
    {
        var result = DeviceId.Create(new string('x', 65));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("out_of_range");
    }

    [Theory]
    [InlineData("vin-WVWZZZ1JZXW000001")]
    [InlineData("car-01")]
    public void VehicleId_accepts_topic_safe_identifiers(string value)
    {
        VehicleId.Create(value).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("veh/1")]
    public void VehicleId_rejects_invalid_identifiers(string? value)
    {
        VehicleId.Create(value).IsFailure.Should().BeTrue();
    }
}
