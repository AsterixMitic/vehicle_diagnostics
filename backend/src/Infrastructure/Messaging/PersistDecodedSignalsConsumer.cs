using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Logging;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts.Internal;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>
/// Bus → Telemetry context: readings decoded from raw frames are persisted through the same
/// ingest use case as P2 telemetry, so deduplication and attribution live in one place.
/// </summary>
public sealed partial class PersistDecodedSignalsConsumer(
    IIngestTelemetry ingestTelemetry,
    ILogger<PersistDecodedSignalsConsumer> logger) : IConsumer<SignalsDecoded>
{
    public async Task Consume(ConsumeContext<SignalsDecoded> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SignalsDecoded message = context.Message;

        var raw = new RawTelemetry(
            message.DeviceId,
            message.VehicleId,
            message.Sequence,
            message.ProcessingMode,
            message.TimestampDevice,
            [.. message.Readings.Select(r => new RawSignalReading(r.Name, r.Value, r.Unit))]);

        IngestOutcome outcome = await ingestTelemetry.IngestAsync(raw, context.CancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case IngestOutcome.Accepted:
                IngestDiagnostics.MessagesPersisted.Add(1);
                break;
            case IngestOutcome.Duplicate:
                IngestDiagnostics.MessagesDeduplicated.Add(1);
                break;
            case IngestOutcome.Rejected rejected:
                foreach (var error in rejected.Errors)
                {
                    IngestDiagnostics.CountRejected(error.Code);
                }

                Activity.Current?.SetStatus(ActivityStatusCode.Error, "readings rejected");
                LogRejected(logger, message.DeviceId, message.Sequence, string.Join("; ", rejected.Errors.Select(e => e.Message)));
                break;
            default:
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected decoded signals from {DeviceId} sequence {Sequence}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string deviceId, long sequence, string reason);
}
