using VehicleDiagnostics.Domain.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Application.Analytics;

/// <summary>
/// A settlement as it arrived on the bus, before validation. The Analytics context's own copy
/// of the fields it needs, so it depends on no other context's types.
/// </summary>
public sealed record SettlementToRecord(
    Guid SettlementId,
    string Outcome,
    string? RejectReason,
    string? DeviceId,
    string? VehicleId,
    long? Sequence,
    string? ProcessingMode,
    DateTimeOffset? TimestampDevice,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? PersistedAt,
    int? PayloadBytes,
    int ReadingCount);

public enum RecordResult
{
    Recorded,
    AlreadyRecorded,
}

/// <summary>Persistence port of the Analytics context's measurement log.</summary>
public interface IMessageLogRepository
{
    /// <summary>Idempotent by settlement identity: recording the same settlement twice stores it once.</summary>
    Task<RecordResult> RecordAsync(MessageMeasurement measurement, CancellationToken cancellationToken);
}

public abstract record RecordOutcome
{
    private RecordOutcome()
    {
    }

    public sealed record Recorded(MessageMeasurement Measurement) : RecordOutcome;

    /// <summary>A bus redelivery of a settlement already in the log; nothing stored.</summary>
    public sealed record Redelivered : RecordOutcome;

    /// <summary>The settlement violates the measurement's invariants; nothing stored.</summary>
    public sealed record Invalid(ValidationError Error) : RecordOutcome;
}

public interface IRecordSettlement
{
    Task<RecordOutcome> RecordAsync(SettlementToRecord settlement, CancellationToken cancellationToken);
}

public sealed class RecordSettlement(IMessageLogRepository repository) : IRecordSettlement
{
    public async Task<RecordOutcome> RecordAsync(SettlementToRecord settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        Result<MessageMeasurement, ValidationError> measurement = ToMeasurement(settlement);
        if (measurement.IsFailure)
        {
            return new RecordOutcome.Invalid(measurement.Error);
        }

        RecordResult result = await repository.RecordAsync(measurement.Value, cancellationToken).ConfigureAwait(false);
        return result == RecordResult.Recorded
            ? new RecordOutcome.Recorded(measurement.Value)
            : new RecordOutcome.Redelivered();
    }

    /// <summary>
    /// Identifiers that fail validation become unknown rather than failing the measurement:
    /// a rejected message is measured precisely because its content was wrong.
    /// </summary>
    public static Result<MessageMeasurement, ValidationError> ToMeasurement(SettlementToRecord s)
    {
        ArgumentNullException.ThrowIfNull(s);

        Result<SettlementId, ValidationError> id = SettlementId.Create(s.SettlementId);
        if (id.IsFailure)
        {
            return id.Error;
        }

        MeasurementOutcome? outcome = s.Outcome switch
        {
            "persisted" => MeasurementOutcome.Persisted,
            "duplicate" => MeasurementOutcome.Duplicate,
            "rejected" => MeasurementOutcome.Rejected,
            _ => null,
        };
        if (outcome is null)
        {
            return ValidationError.Invalid("outcome", $"unknown outcome '{s.Outcome}'");
        }

        return MessageMeasurement.Create(
            id.Value,
            outcome.Value,
            s.RejectReason,
            OrNull(Domain.SharedKernel.DeviceId.Create(s.DeviceId)),
            OrNull(Domain.SharedKernel.VehicleId.Create(s.VehicleId)),
            s.Sequence,
            Enum.TryParse(s.ProcessingMode, ignoreCase: false, out ProcessingMode mode) && Enum.IsDefined(mode) ? mode : null,
            s.TimestampDevice,
            s.ReceivedAt,
            s.PersistedAt,
            s.PayloadBytes,
            s.ReadingCount);
    }

    private static T? OrNull<T>(Result<T, ValidationError> result)
        where T : struct => result.IsSuccess ? result.Value : null;
}
