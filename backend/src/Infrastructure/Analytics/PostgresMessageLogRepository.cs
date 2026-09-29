using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Domain.Analytics;

namespace VehicleDiagnostics.Infrastructure.Analytics;

/// <summary>
/// TimescaleDB adapter of <see cref="IMessageLogRepository"/>. Idempotency is decided by the
/// store, as in the Telemetry context: <c>ON CONFLICT DO NOTHING</c> on the settlement key, and
/// zero affected rows means a bus redelivery.
/// </summary>
public sealed class PostgresMessageLogRepository(AnalyticsDbContext context, TimeProvider timeProvider) : IMessageLogRepository
{
    private const string Insert = """
        INSERT INTO analytics.message_log
            (settlement_id, received_at, outcome, reject_reason, device_id, vehicle_id, sequence,
             processing_mode, timestamp_device, persisted_at, payload_bytes, reading_count, recorded_at)
        VALUES (@settlement_id, @received_at, @outcome, @reject_reason, @device_id, @vehicle_id, @sequence,
                @processing_mode, @timestamp_device, @persisted_at, @payload_bytes, @reading_count, @recorded_at)
        ON CONFLICT DO NOTHING
        """;

    public async Task<RecordResult> RecordAsync(MessageMeasurement measurement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        NpgsqlParameter[] parameters =
        [
            Parameter("settlement_id", NpgsqlDbType.Uuid, measurement.SettlementId.Value),
            Parameter("received_at", NpgsqlDbType.TimestampTz, measurement.ReceivedAt),
            Parameter("outcome", NpgsqlDbType.Varchar, OutcomeName(measurement.Outcome)),
            Parameter("reject_reason", NpgsqlDbType.Varchar, measurement.RejectReason),
            Parameter("device_id", NpgsqlDbType.Varchar, measurement.DeviceId?.Value),
            Parameter("vehicle_id", NpgsqlDbType.Varchar, measurement.VehicleId?.Value),
            Parameter("sequence", NpgsqlDbType.Bigint, measurement.Sequence),
            Parameter("processing_mode", NpgsqlDbType.Varchar, measurement.ProcessingMode?.ToString()),
            Parameter("timestamp_device", NpgsqlDbType.TimestampTz, measurement.TimestampDevice),
            Parameter("persisted_at", NpgsqlDbType.TimestampTz, measurement.PersistedAt),
            Parameter("payload_bytes", NpgsqlDbType.Integer, measurement.PayloadBytes),
            Parameter("reading_count", NpgsqlDbType.Integer, measurement.ReadingCount),
            Parameter("recorded_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow()),
        ];

        int inserted = await context.Database.ExecuteSqlRawAsync(Insert, parameters, cancellationToken).ConfigureAwait(false);
        return inserted == 0 ? RecordResult.AlreadyRecorded : RecordResult.Recorded;
    }

    /// <summary>Stored outcome names; the evaluation queries filter on them.</summary>
    public static string OutcomeName(MeasurementOutcome outcome) => outcome switch
    {
        MeasurementOutcome.Persisted => "persisted",
        MeasurementOutcome.Duplicate => "duplicate",
        MeasurementOutcome.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown outcome."),
    };

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
