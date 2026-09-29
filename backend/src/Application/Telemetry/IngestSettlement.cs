using VehicleDiagnostics.Application.Abstractions;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>How the Telemetry context's outcomes become settlements for measurement.</summary>
public static class IngestSettlement
{
    /// <summary>The settlement of a message that went through the ingest use case.</summary>
    public static MessageSettlement From(RawTelemetry input, IngestOutcome outcome, ReceiptInfo? receipt)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome switch
        {
            IngestOutcome.Accepted accepted => Settle(
                input, SettlementOutcome.Persisted, null, receipt, accepted.PersistedAt, accepted.Event.Readings.Count),
            IngestOutcome.Duplicate => Settle(
                input, SettlementOutcome.Duplicate, null, receipt, null, input.Readings.Count),
            IngestOutcome.Rejected rejected => Settle(
                input, SettlementOutcome.Rejected, SettlementReasons.Join(rejected.Errors.Select(e => e.Code)), receipt, null, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown ingest outcome."),
        };
    }

    /// <summary>
    /// The settlement of a raw envelope the Telemetry context refused to forward. An accepted
    /// raw envelope is not settled here: its fate is decided after decoding.
    /// </summary>
    public static MessageSettlement FromRejectedRaw(RawTelemetryFrames input, AcceptRawOutcome.Rejected rejected)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rejected);

        return new MessageSettlement(
            SettlementOutcome.Rejected,
            SettlementReasons.Join(rejected.Errors.Select(e => e.Code)),
            input.DeviceId,
            input.VehicleId,
            input.Sequence,
            input.ProcessingMode,
            input.TimestampDevice,
            input.Receipt,
            null,
            0);
    }

    private static MessageSettlement Settle(
        RawTelemetry input,
        SettlementOutcome outcome,
        string? reason,
        ReceiptInfo? receipt,
        DateTimeOffset? persistedAt,
        int readingCount) =>
        new(outcome, reason, input.DeviceId, input.VehicleId, input.Sequence, input.ProcessingMode,
            input.TimestampDevice, receipt, persistedAt, readingCount);
}
