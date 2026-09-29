using System.Globalization;

namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>
/// Width of the time buckets a series is aggregated into. Bounds the size of every series
/// response: a requested width that would produce more than <see cref="MaxBuckets"/> buckets is
/// refused, and a missing one is chosen from a fixed ladder of readable widths.
/// </summary>
public readonly record struct BucketWidth
{
    /// <summary>Most buckets a single series may have.</summary>
    public const int MaxBuckets = 1000;

    /// <summary>Upper bound on buckets when the width is chosen automatically; enough for a chart.</summary>
    public const int TargetBuckets = 200;

    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromDays(1),
    ];

    private BucketWidth(TimeSpan value) => Value = value;

    public TimeSpan Value { get; }

    public static Result<BucketWidth, ValidationError> Resolve(TimeRange range, TimeSpan? requested)
    {
        if (requested is not { } width)
        {
            // The ladder's widest step divides the longest range (7 d) into 7 buckets, so the
            // search always ends inside the ladder.
            TimeSpan chosen = Ladder.First(step => BucketCount(range, step) <= TargetBuckets);
            return new BucketWidth(chosen);
        }

        if (width < TimeSpan.FromSeconds(1))
        {
            return ValidationError.Invalid("bucket", "must be at least one second");
        }

        long buckets = BucketCount(range, width);
        if (buckets > MaxBuckets)
        {
            return ValidationError.OutOfRange(
                "bucket",
                string.Create(CultureInfo.InvariantCulture, $"{buckets} buckets exceed the limit of {MaxBuckets}"));
        }

        return new BucketWidth(width);
    }

    private static long BucketCount(TimeRange range, TimeSpan width) =>
        (long)Math.Ceiling(range.Span.Ticks / (double)width.Ticks);

    public override string ToString() => Value.ToString();
}
