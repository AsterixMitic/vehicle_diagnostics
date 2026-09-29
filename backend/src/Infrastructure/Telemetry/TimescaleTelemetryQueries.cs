using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Infrastructure.Telemetry;

/// <summary>
/// Read side of the Telemetry context over <c>telemetry.signal_readings</c>, backed by the
/// <c>(device_id, signal_name, timestamp_device DESC)</c> index. Rows were validated on the way
/// in, so identifiers read back are parsed without re-validating the domain rules.
/// </summary>
public sealed class TimescaleTelemetryQueries(TelemetryDbContext context) : ITelemetryQueries
{
    private const string DevicesSql = """
        SELECT DISTINCT ON (device_id)
               device_id AS "DeviceId", vehicle_id AS "VehicleId",
               timestamp_device AS "LastSeen", processing_mode AS "ProcessingMode"
        FROM telemetry.signal_readings
        WHERE timestamp_device >= @from AND timestamp_device < @to
        ORDER BY device_id, timestamp_device DESC
        """;

    private const string LatestSql = """
        SELECT DISTINCT ON (signal_name)
               signal_name AS "Name", value AS "Value", unit AS "Unit",
               timestamp_device AS "SampledAt", processing_mode AS "ProcessingMode"
        FROM telemetry.signal_readings
        WHERE device_id = @device
        ORDER BY signal_name, timestamp_device DESC
        """;

    private const string SeriesSql = """
        SELECT time_bucket(@bucket, timestamp_device) AS "BucketStart",
               avg(value) AS "Average", min(value) AS "Minimum", max(value) AS "Maximum",
               count(*) AS "Samples"
        FROM telemetry.signal_readings
        WHERE device_id = @device AND signal_name = @signal
          AND timestamp_device >= @from AND timestamp_device < @to
        GROUP BY 1
        ORDER BY 1
        """;

    public async Task<IReadOnlyList<DeviceSeen>> DevicesAsync(TimeRange range, CancellationToken cancellationToken)
    {
        List<DeviceRow> rows = await context.Database
            .SqlQueryRaw<DeviceRow>(DevicesSql, From(range), To(range))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new DeviceSeen(
            DeviceId.Create(r.DeviceId).Value, VehicleId.Create(r.VehicleId).Value, r.LastSeen, Mode(r.ProcessingMode)))];
    }

    public async Task<IReadOnlyList<LatestSignal>> LatestSignalsAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        List<LatestRow> rows = await context.Database
            .SqlQueryRaw<LatestRow>(LatestSql, Text("device", deviceId.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new LatestSignal(
            SignalName.Create(r.Name).Value, r.Value, r.Unit, r.SampledAt, Mode(r.ProcessingMode)))];
    }

    public async Task<SignalSeries> SignalSeriesAsync(
        DeviceId deviceId, SignalName signal, TimeRange range, BucketWidth bucket, CancellationToken cancellationToken)
    {
        List<SignalPoint> points = await context.Database
            .SqlQueryRaw<SignalPoint>(
                SeriesSql,
                new NpgsqlParameter("bucket", NpgsqlDbType.Interval) { Value = bucket.Value },
                Text("device", deviceId.Value),
                Text("signal", signal.Value),
                From(range),
                To(range))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SignalSeries(deviceId, signal, range.From, range.To, bucket.Value, points);
    }

    private static ProcessingMode Mode(string stored) => Enum.Parse<ProcessingMode>(stored);

    private static NpgsqlParameter From(TimeRange range) => new("from", NpgsqlDbType.TimestampTz) { Value = range.From };

    private static NpgsqlParameter To(TimeRange range) => new("to", NpgsqlDbType.TimestampTz) { Value = range.To };

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Varchar) { Value = value };

    private sealed class DeviceRow
    {
        public string DeviceId { get; init; } = string.Empty;

        public string VehicleId { get; init; } = string.Empty;

        public DateTimeOffset LastSeen { get; init; }

        public string ProcessingMode { get; init; } = string.Empty;
    }

    private sealed class LatestRow
    {
        public string Name { get; init; } = string.Empty;

        public double Value { get; init; }

        public string Unit { get; init; } = string.Empty;

        public DateTimeOffset SampledAt { get; init; }

        public string ProcessingMode { get; init; } = string.Empty;
    }
}
