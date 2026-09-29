using FluentAssertions;
using VehicleDiagnostics.Domain.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.Analytics;

public sealed class MessageMeasurementTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly SettlementId Id = SettlementId.Create(Guid.NewGuid()).Value;
    private static readonly DeviceId Device = DeviceId.Create("esp32-01").Value;
    private static readonly VehicleId Vehicle = VehicleId.Create("car-01").Value;

    private static Result<MessageMeasurement, ValidationError> Persisted(
        DeviceId? device = null,
        DateTimeOffset? persistedAt = null,
        string? reason = null,
        int readings = 3) =>
        MessageMeasurement.Create(
            Id, MeasurementOutcome.Persisted, reason, device ?? Device, Vehicle, 7, ProcessingMode.P2,
            Received.AddMilliseconds(-40), Received, persistedAt ?? Received.AddMilliseconds(6), 280, readings);

    private static Result<MessageMeasurement, ValidationError> Rejected(string? reason) =>
        MessageMeasurement.Create(
            Id, MeasurementOutcome.Rejected, reason, null, null, null, null, null, Received, null, 40, 0);

    [Fact]
    public void Complete_persisted_measurement_is_created()
    {
        MessageMeasurement m = Persisted().Value;

        m.Outcome.Should().Be(MeasurementOutcome.Persisted);
        m.DeviceId.Should().Be(Device);
        m.PersistedAt.Should().Be(Received.AddMilliseconds(6));
        m.PayloadBytes.Should().Be(280);
    }

    [Fact]
    public void Anonymous_rejection_is_created()
    {
        MessageMeasurement m = Rejected("unknown_topic").Value;

        m.DeviceId.Should().BeNull();
        m.RejectReason.Should().Be("unknown_topic");
    }

    [Fact]
    public void Empty_settlement_identifier_is_refused()
    {
        SettlementId.Create(Guid.Empty).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Persisted_without_persist_time_is_refused()
    {
        var result = MessageMeasurement.Create(
            Id, MeasurementOutcome.Persisted, null, Device, Vehicle, 7, ProcessingMode.P2,
            Received, Received, null, 280, 3);

        result.Error.Message.Should().Contain("persistedAt");
    }

    [Fact]
    public void Persisted_without_identity_is_refused()
    {
        var result = MessageMeasurement.Create(
            Id, MeasurementOutcome.Duplicate, null, null, Vehicle, 7, ProcessingMode.P2, Received, Received, null, 280, 3);

        result.Error.Message.Should().Contain("identity");
    }

    [Fact]
    public void Persist_time_on_a_duplicate_is_refused()
    {
        var result = MessageMeasurement.Create(
            Id, MeasurementOutcome.Duplicate, null, Device, Vehicle, 7, ProcessingMode.P2,
            Received, Received, Received, 280, 3);

        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Rejection_without_reason_is_refused(string? reason)
    {
        Rejected(reason).Error.Message.Should().Contain("rejectReason");
    }

    [Fact]
    public void Overlong_reason_is_refused()
    {
        Rejected(new string('x', MessageMeasurement.MaxReasonLength + 1)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Reason_on_a_persisted_message_is_refused()
    {
        Persisted(reason: "malformed").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Negative_counts_are_refused()
    {
        Persisted(readings: -1).IsFailure.Should().BeTrue();
        MessageMeasurement.Create(
            Id, MeasurementOutcome.Rejected, "malformed", null, null, null, null, null, Received, null, -3, 0)
            .IsFailure.Should().BeTrue();
    }
}
