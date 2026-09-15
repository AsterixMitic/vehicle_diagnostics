using Microsoft.Extensions.Logging;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Infrastructure.Events;

/// <summary>
/// In-process stand-in for the internal message bus (design D4): domain events are
/// logged with the active trace id. The decode increment replaces this with a real
/// bus adapter behind the same port.
/// </summary>
public sealed partial class LoggingDomainEventPublisher(ILogger<LoggingDomainEventPublisher> logger)
    : IDomainEventPublisher
{
    public ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        LogDomainEvent(logger, domainEvent.GetType().Name, domainEvent.OccurredAt);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Domain event {EventType} occurred at {OccurredAt}")]
    private static partial void LogDomainEvent(ILogger logger, string eventType, DateTimeOffset occurredAt);
}
