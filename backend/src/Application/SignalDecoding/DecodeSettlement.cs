using VehicleDiagnostics.Application.Abstractions;

namespace VehicleDiagnostics.Application.SignalDecoding;

/// <summary>How the Signal Decoding context's outcomes become settlements for measurement.</summary>
public static class DecodeSettlement
{
    /// <summary>Reason for a raw envelope none of whose frames produced a reading.</summary>
    public const string NoReadings = "no_readings";

    /// <summary>
    /// The settlement of a raw envelope whose fate decoding decided, or <c>null</c> when its
    /// readings were published: the Telemetry context settles it after persisting them.
    /// </summary>
    public static MessageSettlement? From(FramesToDecode input, DecodeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome switch
        {
            DecodeOutcome.Processed { Published: true } => null,
            DecodeOutcome.Processed => Rejected(input, NoReadings),
            DecodeOutcome.Rejected rejected => Rejected(input, SettlementReasons.Join(rejected.Errors.Select(e => e.Code))),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown decode outcome."),
        };
    }

    private static MessageSettlement Rejected(FramesToDecode input, string reason) =>
        new(SettlementOutcome.Rejected, reason, input.DeviceId, input.VehicleId, input.Sequence,
            input.ProcessingMode, input.TimestampDevice, input.Receipt, null, 0);
}
