namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>
/// Half-open interval <c>[From, To)</c> in UTC, the range of every telemetry and evaluation
/// query. Constructed only through <see cref="Create"/>, so an empty, reversed or unbounded
/// range cannot reach a query.
/// </summary>
public readonly record struct TimeRange
{
    /// <summary>Range used when the caller gives no bounds.</summary>
    public static readonly TimeSpan DefaultSpan = TimeSpan.FromMinutes(15);

    /// <summary>Longest range any query accepts.</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(7);

    private TimeRange(DateTimeOffset from, DateTimeOffset to)
    {
        From = from;
        To = to;
    }

    public DateTimeOffset From { get; }

    public DateTimeOffset To { get; }

    public TimeSpan Span => To - From;

    /// <summary>
    /// A missing <paramref name="to"/> is <paramref name="now"/>; a missing <paramref name="from"/>
    /// is <see cref="DefaultSpan"/> before the end. <paramref name="now"/> is passed in rather than
    /// read, so the domain stays free of the clock.
    /// </summary>
    public static Result<TimeRange, ValidationError> Create(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        DateTimeOffset end = (to ?? now).ToUniversalTime();
        DateTimeOffset start = (from ?? end - DefaultSpan).ToUniversalTime();

        if (start >= end)
        {
            return ValidationError.Invalid("from", "'from' must be earlier than 'to'");
        }

        if (end - start > MaxSpan)
        {
            return ValidationError.OutOfRange("range", $"longer than the maximum of {MaxSpan.TotalDays:0} days");
        }

        return new TimeRange(start, end);
    }

    public override string ToString() => $"[{From:O}, {To:O})";
}
