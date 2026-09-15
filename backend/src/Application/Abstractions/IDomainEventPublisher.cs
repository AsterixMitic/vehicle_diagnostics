using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Application.Abstractions;

/// <summary>
/// Outbound port for domain events. In this increment the implementation is in-process
/// (log + counter); the internal message bus arrives with the decode increment (design D4)
/// and plugs in behind this same port.
/// </summary>
public interface IDomainEventPublisher
{
    ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}
