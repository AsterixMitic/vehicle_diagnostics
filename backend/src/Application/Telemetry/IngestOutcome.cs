using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>What happened to one inbound telemetry message.</summary>
public abstract record IngestOutcome
{
    private IngestOutcome()
    {
    }

    /// <summary>Readings validated and durably stored; the domain event was published.</summary>
    /// <param name="PersistedAt">Read after the insert returned, so the insert itself counts toward end-to-end latency.</param>
    public sealed record Accepted(TelemetryReceived Event, DateTimeOffset PersistedAt) : IngestOutcome;

    /// <summary>Redelivery of an already-persisted message; nothing stored, not an error.</summary>
    public sealed record Duplicate : IngestOutcome;

    /// <summary>Domain validation failed; nothing stored.</summary>
    public sealed record Rejected(IReadOnlyList<ValidationError> Errors) : IngestOutcome;
}
