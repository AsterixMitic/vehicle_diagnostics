using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Infrastructure.Events;

/// <summary>Hands every domain event to each publisher in order.</summary>
public sealed class CompositeDomainEventPublisher(IReadOnlyList<IDomainEventPublisher> publishers) : IDomainEventPublisher
{
    public async ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        foreach (IDomainEventPublisher publisher in publishers)
        {
            await publisher.PublishAsync(domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
