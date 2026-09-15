namespace VehicleDiagnostics.Infrastructure.Telemetry;

/// <summary>
/// Persistence shape of one reading in <c>telemetry.signal_readings</c>. The composite key
/// (device, sequence, signal, device timestamp) is the deduplication contract: a redelivered
/// batch collides on every row. The time column is in the key because TimescaleDB requires
/// the partitioning column in any unique constraint.
/// </summary>
public sealed class SignalReadingRow
{
    public required string DeviceId { get; init; }

    public required string VehicleId { get; init; }

    public required long Sequence { get; init; }

    public required string SignalName { get; init; }

    public required double Value { get; init; }

    public required string Unit { get; init; }

    public required DateTimeOffset TimestampDevice { get; init; }

    public required DateTimeOffset TimestampIngest { get; init; }

    public required string ProcessingMode { get; init; }
}
