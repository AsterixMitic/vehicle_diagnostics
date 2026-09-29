using MassTransit;
using Microsoft.Extensions.Logging;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Contracts.Internal;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>
/// Publishes settlements to the Analytics context. Never throws: a failed publish is counted and
/// logged, because a lost measurement must not turn into a retried, re-counted ingestion.
/// </summary>
public sealed partial class MassTransitSettlementReporter(
    IPublishEndpoint publishEndpoint,
    TimeProvider timeProvider,
    ILogger<MassTransitSettlementReporter> logger) : IMessageSettlementReporter
{
    public async Task ReportAsync(MessageSettlement settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        TelemetryMessageSettled message = ToMessage(settlement, Guid.NewGuid(), timeProvider.GetUtcNow());
        try
        {
            await publishEndpoint.Publish(message, cancellationToken).ConfigureAwait(false);
            IngestDiagnostics.SettlementsReported.Add(1, new KeyValuePair<string, object?>("outcome", message.Outcome));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IngestDiagnostics.SettlementsFailed.Add(1);
            LogPublishFailed(logger, settlement.DeviceId, settlement.Sequence, exception);
        }
    }

    /// <summary>
    /// A settlement whose receipt is unknown (a version-1 bus message) is placed at
    /// <paramref name="now"/>, the settlement time, with an unknown size.
    /// </summary>
    public static TelemetryMessageSettled ToMessage(MessageSettlement settlement, Guid settlementId, DateTimeOffset now) => new()
    {
        SettlementId = settlementId,
        Outcome = settlement.Outcome switch
        {
            SettlementOutcome.Persisted => SettlementOutcomes.Persisted,
            SettlementOutcome.Duplicate => SettlementOutcomes.Duplicate,
            SettlementOutcome.Rejected => SettlementOutcomes.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(settlement), settlement.Outcome, "Unknown settlement outcome."),
        },
        RejectReason = settlement.RejectReason,
        DeviceId = settlement.DeviceId,
        VehicleId = settlement.VehicleId,
        Sequence = settlement.Sequence,
        ProcessingMode = settlement.ProcessingMode,
        TimestampDevice = settlement.TimestampDevice,
        ReceivedAt = settlement.Receipt?.ReceivedAt ?? now,
        PersistedAt = settlement.PersistedAt,
        PayloadBytes = settlement.Receipt?.PayloadBytes,
        ReadingCount = settlement.ReadingCount,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish the settlement of {DeviceId} sequence {Sequence}; the measurement is lost")]
    private static partial void LogPublishFailed(ILogger logger, string? deviceId, long? sequence, Exception exception);
}
