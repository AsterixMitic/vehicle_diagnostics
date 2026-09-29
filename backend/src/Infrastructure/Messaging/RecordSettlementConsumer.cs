using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Logging;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Contracts.Internal;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>
/// Bus → Analytics context: keep one settlement as a measurement. Unpartitioned, since recording
/// has no ordering requirement. An invalid settlement is counted and dropped rather than
/// retried: retrying cannot make it valid.
/// </summary>
public sealed partial class RecordSettlementConsumer(
    IRecordSettlement recordSettlement,
    ILogger<RecordSettlementConsumer> logger) : IConsumer<TelemetryMessageSettled>
{
    public async Task Consume(ConsumeContext<TelemetryMessageSettled> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TelemetryMessageSettled m = context.Message;
        using Activity? activity = AnalyticsDiagnostics.ActivitySource.StartActivity("analytics.record");
        activity?.SetTag("vehiclediagnostics.device_id", m.DeviceId);
        activity?.SetTag("vehiclediagnostics.sequence", m.Sequence);
        activity?.SetTag("vehiclediagnostics.outcome", m.Outcome);

        var settlement = new SettlementToRecord(
            m.SettlementId, m.Outcome, m.RejectReason, m.DeviceId, m.VehicleId, m.Sequence, m.ProcessingMode,
            m.TimestampDevice, m.ReceivedAt, m.PersistedAt, m.PayloadBytes, m.ReadingCount);

        RecordOutcome outcome = await recordSettlement.RecordAsync(settlement, context.CancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case RecordOutcome.Recorded:
                AnalyticsDiagnostics.SettlementsRecorded.Add(1, new KeyValuePair<string, object?>("outcome", m.Outcome));
                break;
            case RecordOutcome.Redelivered:
                AnalyticsDiagnostics.SettlementsRedelivered.Add(1);
                activity?.SetTag("vehiclediagnostics.redelivered", true);
                break;
            case RecordOutcome.Invalid invalid:
                AnalyticsDiagnostics.SettlementsInvalid.Add(1, new KeyValuePair<string, object?>("reason", invalid.Error.Code));
                activity?.SetStatus(ActivityStatusCode.Error, "invalid settlement");
                LogInvalid(logger, m.SettlementId, invalid.Error.Message);
                break;
            default:
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped invalid settlement {SettlementId}: {Reason}")]
    private static partial void LogInvalid(ILogger logger, Guid settlementId, string reason);
}
