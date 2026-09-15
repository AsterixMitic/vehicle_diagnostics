using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.Telemetry;

public sealed class TelemetryReceivedTests
{
    [Fact]
    public void Occurrence_time_comes_from_the_injected_TimeProvider()
    {
        var now = new DateTimeOffset(2026, 9, 15, 8, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        SignalReading reading = SignalReading
            .Create(SignalName.Create("engine_rpm").Value, 900, "rpm", now.AddMilliseconds(-5))
            .Value;

        TelemetryReceived @event = TelemetryReceived.Create(
            DeviceId.Create("esp32-01").Value,
            VehicleId.Create("car-01").Value,
            sequence: 17,
            ProcessingMode.P2,
            [reading],
            timeProvider);

        @event.OccurredAt.Should().Be(now);
        @event.DeviceId.Value.Should().Be("esp32-01");
        @event.VehicleId.Value.Should().Be("car-01");
        @event.Sequence.Should().Be(17);
        @event.ProcessingMode.Should().Be(ProcessingMode.P2);
        @event.Readings.Should().ContainSingle().Which.Should().Be(reading);
    }
}
