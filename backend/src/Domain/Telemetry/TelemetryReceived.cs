using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.Telemetry;

/// <summary>Raised when a device's telemetry message has been accepted by the backend.</summary>
public sealed record TelemetryReceived : IDomainEvent
{
    private TelemetryReceived(
        DeviceId deviceId,
        VehicleId vehicleId,
        long sequence,
        ProcessingMode processingMode,
        IReadOnlyList<SignalReading> readings,
        DateTimeOffset occurredAt)
    {
        DeviceId = deviceId;
        VehicleId = vehicleId;
        Sequence = sequence;
        ProcessingMode = processingMode;
        Readings = readings;
        OccurredAt = occurredAt;
    }

    public DeviceId DeviceId { get; }

    public VehicleId VehicleId { get; }

    public long Sequence { get; }

    public ProcessingMode ProcessingMode { get; }

    public IReadOnlyList<SignalReading> Readings { get; }

    public DateTimeOffset OccurredAt { get; }

    public static TelemetryReceived Create(
        DeviceId deviceId,
        VehicleId vehicleId,
        long sequence,
        ProcessingMode processingMode,
        IReadOnlyList<SignalReading> readings,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new TelemetryReceived(
            deviceId, vehicleId, sequence, processingMode, readings, timeProvider.GetUtcNow());
    }
}
