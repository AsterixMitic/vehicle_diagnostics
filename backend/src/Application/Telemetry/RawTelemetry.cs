namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>
/// Transport-agnostic input of the ingest use case: envelope content after structural
/// validation, before domain validation. Produced by an inbound adapter (MQTT today);
/// the use case owns turning it into validated domain types.
/// </summary>
public sealed record RawTelemetry(
    string DeviceId,
    string VehicleId,
    long Sequence,
    string ProcessingMode,
    DateTimeOffset TimestampDevice,
    IReadOnlyList<RawSignalReading> Readings);

/// <summary>One unvalidated signal sample as it arrived on the wire.</summary>
public sealed record RawSignalReading(string Name, double Value, string? Unit);
