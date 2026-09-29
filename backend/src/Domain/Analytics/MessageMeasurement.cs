using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.Analytics;

/// <summary>Identity of one settlement; the key Analytics deduplicates bus redeliveries by.</summary>
public readonly record struct SettlementId
{
    private SettlementId(Guid value) => Value = value;

    public Guid Value { get; }

    public static Result<SettlementId, ValidationError> Create(Guid value) =>
        value == Guid.Empty
            ? ValidationError.Required("settlementId")
            : new SettlementId(value);

    public override string ToString() => Value.ToString();
}

public enum MeasurementOutcome
{
    Persisted,
    Duplicate,
    Rejected,
}

/// <summary>
/// The measured fate of one telemetry message the backend received: an immutable fact the
/// evaluation is computed from. Identity is typed where it was valid on the wire; a rejected
/// message whose identifiers failed validation is still measured, attributed to no device.
/// </summary>
public sealed record MessageMeasurement
{
    /// <summary>Longest rejection reason kept.</summary>
    public const int MaxReasonLength = 64;

    private MessageMeasurement()
    {
    }

    public SettlementId SettlementId { get; private init; }

    public MeasurementOutcome Outcome { get; private init; }

    public string? RejectReason { get; private init; }

    public DeviceId? DeviceId { get; private init; }

    public VehicleId? VehicleId { get; private init; }

    public long? Sequence { get; private init; }

    public ProcessingMode? ProcessingMode { get; private init; }

    public DateTimeOffset? TimestampDevice { get; private init; }

    public DateTimeOffset ReceivedAt { get; private init; }

    public DateTimeOffset? PersistedAt { get; private init; }

    public int? PayloadBytes { get; private init; }

    public int ReadingCount { get; private init; }

    /// <summary>
    /// A persisted or duplicate message passed validation on its way in, so its identity,
    /// sequence, mode and device timestamp must be present; a persisted one also has a persist
    /// time, and only a rejected one has a reason.
    /// </summary>
    public static Result<MessageMeasurement, ValidationError> Create(
        SettlementId settlementId,
        MeasurementOutcome outcome,
        string? rejectReason,
        DeviceId? deviceId,
        VehicleId? vehicleId,
        long? sequence,
        ProcessingMode? processingMode,
        DateTimeOffset? timestampDevice,
        DateTimeOffset receivedAt,
        DateTimeOffset? persistedAt,
        int? payloadBytes,
        int readingCount)
    {
        if (!Enum.IsDefined(outcome))
        {
            return ValidationError.Invalid("outcome", $"unknown outcome {(int)outcome}");
        }

        if (readingCount < 0)
        {
            return ValidationError.OutOfRange("readingCount", "must be non-negative");
        }

        if (payloadBytes is < 0)
        {
            return ValidationError.OutOfRange("payloadBytes", "must be non-negative");
        }

        if (sequence is < 0)
        {
            return ValidationError.OutOfRange("sequence", "must be non-negative");
        }

        if (outcome == MeasurementOutcome.Rejected)
        {
            if (string.IsNullOrWhiteSpace(rejectReason))
            {
                return ValidationError.Required("rejectReason");
            }

            if (rejectReason.Length > MaxReasonLength)
            {
                return ValidationError.OutOfRange("rejectReason", $"longer than {MaxReasonLength} characters");
            }
        }
        else
        {
            if (rejectReason is not null)
            {
                return ValidationError.Invalid("rejectReason", "only a rejected message has a reason");
            }

            if (deviceId is null || vehicleId is null || sequence is null || processingMode is null || timestampDevice is null)
            {
                return ValidationError.Required("identity");
            }
        }

        if (outcome == MeasurementOutcome.Persisted && persistedAt is null)
        {
            return ValidationError.Required("persistedAt");
        }

        if (outcome != MeasurementOutcome.Persisted && persistedAt is not null)
        {
            return ValidationError.Invalid("persistedAt", "only a persisted message has a persist time");
        }

        return new MessageMeasurement
        {
            SettlementId = settlementId,
            Outcome = outcome,
            RejectReason = rejectReason,
            DeviceId = deviceId,
            VehicleId = vehicleId,
            Sequence = sequence,
            ProcessingMode = processingMode,
            TimestampDevice = timestampDevice,
            ReceivedAt = receivedAt,
            PersistedAt = persistedAt,
            PayloadBytes = payloadBytes,
            ReadingCount = readingCount,
        };
    }
}
