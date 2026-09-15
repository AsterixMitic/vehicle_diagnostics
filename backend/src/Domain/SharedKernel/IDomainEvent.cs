namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>A fact that happened in the domain, timestamped via <see cref="TimeProvider"/> at creation.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
