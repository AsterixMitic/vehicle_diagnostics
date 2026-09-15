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
}

public enum PersistResult
{
    Inserted,
    Duplicate,
}
