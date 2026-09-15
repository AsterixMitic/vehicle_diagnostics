using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Telemetry;

/// <summary>
/// TimescaleDB adapter of <see cref="ITelemetryRepository"/>. Deduplication happens in the
/// store itself: a single multi-row <c>INSERT … ON CONFLICT DO NOTHING</c> is atomic,
/// restart-safe, and race-free, and its affected-row count distinguishes new data from a
/// redelivery (design D7).
/// </summary>
public sealed class PostgresTelemetryRepository(TelemetryDbContext context) : ITelemetryRepository
{
    private const int ColumnsPerRow = 9;

    public async Task<PersistResult> PersistAsync(TelemetryReceived telemetry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        // The SQL text is assembled only from constant fragments and positional parameter
        // names; every value travels as an NpgsqlParameter.
        var sql = new StringBuilder(
            "INSERT INTO telemetry.signal_readings " +
            "(device_id, vehicle_id, sequence, signal_name, value, unit, timestamp_device, timestamp_ingest, processing_mode) VALUES ");
        var parameters = new List<NpgsqlParameter>(telemetry.Readings.Count * ColumnsPerRow);

        for (int i = 0; i < telemetry.Readings.Count; i++)
        {
            SignalReading reading = telemetry.Readings[i];
            int p = i * ColumnsPerRow;
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(CultureInfo.InvariantCulture, $"(@p{p}, @p{p + 1}, @p{p + 2}, @p{p + 3}, @p{p + 4}, @p{p + 5}, @p{p + 6}, @p{p + 7}, @p{p + 8})");
            parameters.Add(new NpgsqlParameter($"p{p}", telemetry.DeviceId.Value));
            parameters.Add(new NpgsqlParameter($"p{p + 1}", telemetry.VehicleId.Value));
            parameters.Add(new NpgsqlParameter($"p{p + 2}", telemetry.Sequence));
            parameters.Add(new NpgsqlParameter($"p{p + 3}", reading.Name.Value));
            parameters.Add(new NpgsqlParameter($"p{p + 4}", reading.Value));
            parameters.Add(new NpgsqlParameter($"p{p + 5}", reading.Unit));
            parameters.Add(new NpgsqlParameter($"p{p + 6}", reading.SampledAt));
            parameters.Add(new NpgsqlParameter($"p{p + 7}", telemetry.OccurredAt));
            parameters.Add(new NpgsqlParameter($"p{p + 8}", telemetry.ProcessingMode.ToString()));
        }

        sql.Append(" ON CONFLICT DO NOTHING");

        // Deduplication is decided by the store's conflict semantics (design D7): the
        // dedup stage span wraps the persist stage span that executes the single INSERT.
        using Activity? dedupActivity = IngestDiagnostics.ActivitySource.StartActivity("ingest.deduplicate");
        int inserted;
        using (Activity? persistActivity = IngestDiagnostics.ActivitySource.StartActivity("ingest.persist"))
        {
            inserted = await context.Database
                .ExecuteSqlRawAsync(sql.ToString(), parameters, cancellationToken)
                .ConfigureAwait(false);
            persistActivity?.SetTag("db.rows_affected", inserted);
        }

        bool duplicate = inserted == 0;
        dedupActivity?.SetTag("vehiclediagnostics.duplicate", duplicate);
        return duplicate ? PersistResult.Duplicate : PersistResult.Inserted;
    }

    public async Task<IReadOnlyList<StoredReading>> QueryAsync(
        DeviceId deviceId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken cancellationToken)
    {
        List<StoredReading> readings = await context.SignalReadings
            .AsNoTracking()
            .Where(r => r.DeviceId == deviceId.Value
                && r.TimestampDevice >= fromInclusive
                && r.TimestampDevice < toExclusive)
            .OrderBy(r => r.TimestampDevice)
            .ThenBy(r => r.SignalName)
            .Select(r => new StoredReading(r.SignalName, r.Value, r.Unit, r.TimestampDevice, r.TimestampIngest))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return readings;
    }
}
