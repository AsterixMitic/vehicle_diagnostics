namespace VehicleDiagnostics.Contracts.Internal;

/// <summary>
/// Internal bus message (ADR-0004): the Signal Decoding context produced readings from a
/// raw-frames envelope. The Telemetry context persists them through its own ingest path,
/// keyed by the original device identity and sequence number.
/// </summary>
public sealed record SignalsDecoded
{
    public const int Version = 1;

    public required string DeviceId { get; init; }

    public required string VehicleId { get; init; }

    public required long Sequence { get; init; }

    public required DateTimeOffset TimestampDevice { get; init; }

    /// <summary>The originating envelope's mode (P0 for raw frames), so attribution survives decoding.</summary>
    public required string ProcessingMode { get; init; }

    public required IReadOnlyList<SignalReadingDto> Readings { get; init; }
}
