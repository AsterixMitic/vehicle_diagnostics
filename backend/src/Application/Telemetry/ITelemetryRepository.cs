using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>Persistence port of the Telemetry context; implemented in Infrastructure.</summary>
public interface ITelemetryRepository
{
    /// <summary>
    /// Durably stores the readings of an accepted telemetry message. Must be idempotent
    /// under redelivery: persisting the same (deviceId, sequence) again reports
    /// <see cref="PersistResult.Duplicate"/> without creating duplicate rows.
    /// </summary>
    Task<PersistResult> PersistAsync(TelemetryReceived telemetry, CancellationToken cancellationToken);

    /// <summary>
    /// Readings of one device whose device-side timestamps fall in
    /// <c>[fromInclusive, toExclusive)</c>, ordered by sample time.
    /// </summary>
    Task<IReadOnlyList<StoredReading>> QueryAsync(
        DeviceId deviceId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken cancellationToken);
}

/// <summary>A persisted reading with both timestamps, so transport latency is derivable.</summary>
public sealed record StoredReading(
    string SignalName,
    double Value,
    string Unit,
    DateTimeOffset SampledAt,
    DateTimeOffset IngestedAt);

public enum PersistResult
{
    Inserted,
    Duplicate,
}
