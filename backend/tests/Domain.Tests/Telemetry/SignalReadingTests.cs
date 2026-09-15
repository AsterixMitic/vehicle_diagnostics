using FluentAssertions;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.Telemetry;

public sealed class SignalReadingTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static SignalName Name(string value) => SignalName.Create(value).Value;

    [Fact]
    public void Creates_a_reading_with_trimmed_unit()
    {
        var result = SignalReading.Create(Name("engine_rpm"), 2450.5, " rpm ", SampleTime);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Value.Should().Be("engine_rpm");
        result.Value.Value.Should().Be(2450.5);
        result.Value.Unit.Should().Be("rpm");
        result.Value.SampledAt.Should().Be(SampleTime);
    }

    [Fact]
    public void Null_unit_becomes_dimensionless()
    {
        SignalReading.Create(Name("lambda"), 1.02, null, SampleTime).Value.Unit.Should().BeEmpty();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_values_are_rejected(double value)
    {
        var result = SignalReading.Create(Name("engine_rpm"), value, "rpm", SampleTime);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("invalid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_signal_names_are_rejected(string? value)
    {
        SignalName.Create(value).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Signal_names_are_trimmed()
    {
        SignalName.Create("  coolant_temp  ").Value.Value.Should().Be("coolant_temp");
    }
}
