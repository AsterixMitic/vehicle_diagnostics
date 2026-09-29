using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Infrastructure.Analytics;

/// <summary>
/// Evaluation queries over <c>analytics.message_log</c>, one SQL statement each (design D6):
/// counts by outcome, continuous percentiles of the three latencies over persisted messages,
/// sequence gaps per device, and <c>time_bucket</c> series. Rates count the intervals between a
/// mode's first and last receipt, so N messages over their active span give (N − 1) / span and a
/// device sending at exactly 1 Hz reports 1 per second; a mode with one message, or an active span
/// under a second, is divided by the whole range instead.
/// </summary>
public sealed class TimescaleEvaluationQueries(AnalyticsDbContext context) : IEvaluationQueries
{
    // Latencies in milliseconds; NULL outside persisted messages, which percentile_cont skips.
    private const string EndToEnd = "CASE WHEN outcome = 'persisted' THEN extract(epoch FROM persisted_at - timestamp_device) * 1000 END";
    private const string Transport = "CASE WHEN outcome = 'persisted' THEN extract(epoch FROM received_at - timestamp_device) * 1000 END";
    private const string Backend = "CASE WHEN outcome = 'persisted' THEN extract(epoch FROM persisted_at - received_at) * 1000 END";

    private const string SummarySql = $"""
        WITH m AS (
            SELECT * FROM analytics.message_log WHERE received_at >= @from AND received_at < @to
        ),
        gaps AS (
            SELECT mode, sum(lost)::bigint AS lost
            FROM (
                SELECT coalesce(processing_mode, '{ModeSummary.UnknownMode}') AS mode,
                       max(sequence) - min(sequence) + 1 - count(DISTINCT sequence) AS lost
                FROM m
                WHERE sequence IS NOT NULL AND device_id IS NOT NULL
                GROUP BY processing_mode, device_id
            ) per_device
            GROUP BY mode
        )
        SELECT coalesce(m.processing_mode, '{ModeSummary.UnknownMode}') AS "Mode",
               count(*) AS "Received",
               count(*) FILTER (WHERE outcome = 'persisted') AS "Persisted",
               count(*) FILTER (WHERE outcome = 'duplicate') AS "Duplicates",
               count(*) FILTER (WHERE outcome = 'rejected') AS "Rejected",
               coalesce(max(g.lost), 0)::bigint AS "Lost",
               extract(epoch FROM max(received_at) - min(received_at))::double precision AS "ActiveSeconds",
               coalesce(sum(payload_bytes), 0)::bigint AS "Bytes",
               avg(payload_bytes)::double precision AS "MeanPayloadBytes",
               percentile_cont(0.50) WITHIN GROUP (ORDER BY {EndToEnd}) AS "EndToEndP50",
               percentile_cont(0.95) WITHIN GROUP (ORDER BY {EndToEnd}) AS "EndToEndP95",
               percentile_cont(0.99) WITHIN GROUP (ORDER BY {EndToEnd}) AS "EndToEndP99",
               percentile_cont(0.50) WITHIN GROUP (ORDER BY {Transport}) AS "TransportP50",
               percentile_cont(0.95) WITHIN GROUP (ORDER BY {Transport}) AS "TransportP95",
               percentile_cont(0.99) WITHIN GROUP (ORDER BY {Transport}) AS "TransportP99",
               percentile_cont(0.50) WITHIN GROUP (ORDER BY {Backend}) AS "BackendP50",
               percentile_cont(0.95) WITHIN GROUP (ORDER BY {Backend}) AS "BackendP95",
               percentile_cont(0.99) WITHIN GROUP (ORDER BY {Backend}) AS "BackendP99"
        FROM m
        LEFT JOIN gaps g ON g.mode = coalesce(m.processing_mode, '{ModeSummary.UnknownMode}')
        GROUP BY 1
        ORDER BY 1
        """;

    private const string RejectionsSql = $"""
        SELECT coalesce(processing_mode, '{ModeSummary.UnknownMode}') AS "Mode",
               reject_reason AS "Reason",
               count(*) AS "Count"
        FROM analytics.message_log
        WHERE received_at >= @from AND received_at < @to AND outcome = 'rejected'
        GROUP BY 1, 2
        ORDER BY 1, 3 DESC, 2
        """;

    private const string SeriesSql = $"""
        SELECT time_bucket(@bucket, received_at) AS "BucketStart",
               coalesce(processing_mode, '{ModeSummary.UnknownMode}') AS "Mode",
               count(*) AS "Received",
               coalesce(sum(payload_bytes), 0)::bigint AS "Bytes",
               percentile_cont(0.95) WITHIN GROUP (ORDER BY {EndToEnd}) AS "EndToEndP95"
        FROM analytics.message_log
        WHERE received_at >= @from AND received_at < @to
        GROUP BY 1, 2
        ORDER BY 1, 2
        """;

    public async Task<EvaluationSummary> SummaryAsync(TimeRange range, CancellationToken cancellationToken)
    {
        List<SummaryRow> rows = await context.Database
            .SqlQueryRaw<SummaryRow>(SummarySql, RangeParameters(range))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<RejectionCount> rejections = await context.Database
            .SqlQueryRaw<RejectionCount>(RejectionsSql, RangeParameters(range))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        double rangeSeconds = range.Span.TotalSeconds;
        List<ModeSummary> modes = [.. rows.Select(r =>
        {
            (double messagesPerSecond, double bytesPerSecond) = r.Received >= 2 && r.ActiveSeconds >= 1
                ? ((r.Received - 1) / r.ActiveSeconds, (r.MeanPayloadBytes ?? 0) * (r.Received - 1) / r.ActiveSeconds)
                : (r.Received / rangeSeconds, r.Bytes / rangeSeconds);
            return new ModeSummary(
                r.Mode, r.Received, r.Persisted, r.Duplicates, r.Rejected, r.Lost,
                messagesPerSecond, bytesPerSecond, r.MeanPayloadBytes,
                Percentiles(r.EndToEndP50, r.EndToEndP95, r.EndToEndP99),
                Percentiles(r.TransportP50, r.TransportP95, r.TransportP99),
                Percentiles(r.BackendP50, r.BackendP95, r.BackendP99));
        })];

        return new EvaluationSummary(range.From, range.To, modes, rejections);
    }

    public async Task<EvaluationSeries> SeriesAsync(TimeRange range, BucketWidth bucket, CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters = [.. RangeParameters(range), new NpgsqlParameter("bucket", NpgsqlDbType.Interval) { Value = bucket.Value }];
        List<SeriesRow> rows = await context.Database
            .SqlQueryRaw<SeriesRow>(SeriesSql, parameters)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ModeSeriesPoint> points = [.. rows.Select(r =>
        {
            double seconds = SecondsInside(range, r.BucketStart, bucket.Value);
            return new ModeSeriesPoint(r.BucketStart, r.Mode, r.Received, r.Received / seconds, r.Bytes / seconds, r.EndToEndP95);
        })];

        return new EvaluationSeries(range.From, range.To, bucket.Value, points);
    }

    /// <summary>
    /// The part of a bucket that lies inside the range. Buckets are aligned to a fixed origin, so
    /// the first and last usually stick out of the range; dividing their counts by the full width
    /// would draw a false drop in throughput at both ends of every chart.
    /// </summary>
    private static double SecondsInside(TimeRange range, DateTimeOffset bucketStart, TimeSpan width)
    {
        DateTimeOffset start = bucketStart > range.From ? bucketStart : range.From;
        DateTimeOffset end = bucketStart + width < range.To ? bucketStart + width : range.To;
        return Math.Max((end - start).TotalSeconds, 1e-3);
    }

    private static NpgsqlParameter[] RangeParameters(TimeRange range) =>
    [
        new("from", NpgsqlDbType.TimestampTz) { Value = range.From },
        new("to", NpgsqlDbType.TimestampTz) { Value = range.To },
    ];

    private static LatencyPercentiles? Percentiles(double? p50, double? p95, double? p99) =>
        p50 is { } a && p95 is { } b && p99 is { } c ? new LatencyPercentiles(a, b, c) : null;

    private sealed class SummaryRow
    {
        public string Mode { get; init; } = string.Empty;

        public long Received { get; init; }

        public long Persisted { get; init; }

        public long Duplicates { get; init; }

        public long Rejected { get; init; }

        public long Lost { get; init; }

        public double ActiveSeconds { get; init; }

        public long Bytes { get; init; }

        public double? MeanPayloadBytes { get; init; }

        public double? EndToEndP50 { get; init; }

        public double? EndToEndP95 { get; init; }

        public double? EndToEndP99 { get; init; }

        public double? TransportP50 { get; init; }

        public double? TransportP95 { get; init; }

        public double? TransportP99 { get; init; }

        public double? BackendP50 { get; init; }

        public double? BackendP95 { get; init; }

        public double? BackendP99 { get; init; }
    }

    private sealed class SeriesRow
    {
        public DateTimeOffset BucketStart { get; init; }

        public string Mode { get; init; } = string.Empty;

        public long Received { get; init; }

        public long Bytes { get; init; }

        public double? EndToEndP95 { get; init; }
    }
}
