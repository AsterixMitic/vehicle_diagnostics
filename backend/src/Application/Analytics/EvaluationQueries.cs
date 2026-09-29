using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Application.Analytics;

/// <summary>Three latency percentiles, in milliseconds.</summary>
public sealed record LatencyPercentiles(double P50, double P95, double P99);

/// <summary>
/// Evaluation metrics of one processing mode over a range. <see cref="Mode"/> is <c>P0</c>–<c>P3</c>,
/// or <see cref="UnknownMode"/> for rejections whose mode could not be determined. Latencies are
/// over persisted messages and absent when there are none.
/// </summary>
public sealed record ModeSummary(
    string Mode,
    long Received,
    long Persisted,
    long Duplicates,
    long Rejected,
    long Lost,
    double MessagesPerSecond,
    double BytesPerSecond,
    double? MeanPayloadBytes,
    LatencyPercentiles? EndToEnd,
    LatencyPercentiles? Transport,
    LatencyPercentiles? Backend)
{
    public const string UnknownMode = "unknown";
}

public sealed record RejectionCount(string Mode, string Reason, long Count);

public sealed record EvaluationSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ModeSummary> Modes,
    IReadOnlyList<RejectionCount> Rejections);

/// <summary>Rates and p95 end-to-end latency of one mode in one time bucket.</summary>
public sealed record ModeSeriesPoint(
    DateTimeOffset BucketStart,
    string Mode,
    long Received,
    double MessagesPerSecond,
    double BytesPerSecond,
    double? EndToEndP95);

public sealed record EvaluationSeries(
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Bucket,
    IReadOnlyList<ModeSeriesPoint> Points);

/// <summary>Read port of the Analytics context; evaluated over backend receipt time.</summary>
public interface IEvaluationQueries
{
    Task<EvaluationSummary> SummaryAsync(TimeRange range, CancellationToken cancellationToken);

    Task<EvaluationSeries> SeriesAsync(TimeRange range, BucketWidth bucket, CancellationToken cancellationToken);
}
