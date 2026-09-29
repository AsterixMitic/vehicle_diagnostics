using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Api.Endpoints;

/// <summary>
/// Query parameters are bound as text and parsed here, so every invalid value becomes a
/// <c>ProblemDetails</c> 400 that names the parameter, rather than a bare binding failure.
/// </summary>
internal static class QueryParameters
{
    public static Result<TimeRange, ValidationError> Range(string? from, string? to, TimeProvider timeProvider)
    {
        Result<DateTimeOffset?, ValidationError> start = Instant("from", from);
        if (start.IsFailure)
        {
            return start.Error;
        }

        Result<DateTimeOffset?, ValidationError> end = Instant("to", to);
        if (end.IsFailure)
        {
            return end.Error;
        }

        return TimeRange.Create(start.Value, end.Value, timeProvider.GetUtcNow());
    }

    public static Result<BucketWidth, ValidationError> Bucket(TimeRange range, string? bucketSeconds)
    {
        if (string.IsNullOrWhiteSpace(bucketSeconds))
        {
            return BucketWidth.Resolve(range, null);
        }

        if (!int.TryParse(bucketSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
        {
            return ValidationError.Invalid("bucketSeconds", "must be a whole number of seconds");
        }

        return BucketWidth.Resolve(range, TimeSpan.FromSeconds(seconds));
    }

    public static ProblemHttpResult Problem(ValidationError error) =>
        TypedResults.Problem(
            title: "Invalid request",
            detail: error.Message,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    private static Result<DateTimeOffset?, ValidationError> Instant(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<DateTimeOffset?, ValidationError>.Ok(null);
        }

        // An unencoded '+' of a UTC offset arrives as a space; no valid instant contains one.
        string text = value.Trim().Replace(' ', '+');
        return DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? Result<DateTimeOffset?, ValidationError>.Ok(parsed)
            : ValidationError.Invalid(name, $"'{value}' is not an ISO 8601 instant");
    }
}
