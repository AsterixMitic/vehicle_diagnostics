using Microsoft.AspNetCore.Http.HttpResults;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Api.Endpoints;

/// <summary>Read access to stored telemetry.</summary>
public static class TelemetryEndpoints
{
    public static RouteGroupBuilder MapTelemetryEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/devices", ListDevicesAsync);
        api.MapGet("/devices/{deviceId}/signals", LatestSignalsAsync);
        api.MapGet("/devices/{deviceId}/signals/{signal}/series", SignalSeriesAsync);
        return api;
    }

    private static async Task<Results<Ok<DeviceDto[]>, ProblemHttpResult>> ListDevicesAsync(
        string? from, string? to, ITelemetryQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        Result<TimeRange, ValidationError> range = QueryParameters.Range(from, to, timeProvider);
        if (range.IsFailure)
        {
            return QueryParameters.Problem(range.Error);
        }

        IReadOnlyList<DeviceSeen> devices = await queries.DevicesAsync(range.Value, cancellationToken);
        return TypedResults.Ok(devices.Select(DeviceDto.Of).ToArray());
    }

    private static async Task<Results<Ok<LatestSignalDto[]>, ProblemHttpResult>> LatestSignalsAsync(
        string deviceId, ITelemetryQueries queries, CancellationToken cancellationToken)
    {
        Result<DeviceId, ValidationError> device = DeviceId.Create(deviceId);
        if (device.IsFailure)
        {
            return QueryParameters.Problem(device.Error);
        }

        IReadOnlyList<LatestSignal> latest = await queries.LatestSignalsAsync(device.Value, cancellationToken);
        return TypedResults.Ok(latest.Select(LatestSignalDto.Of).ToArray());
    }

    private static async Task<Results<Ok<SignalSeriesDto>, ProblemHttpResult>> SignalSeriesAsync(
        string deviceId,
        string signal,
        string? from,
        string? to,
        string? bucketSeconds,
        ITelemetryQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Result<DeviceId, ValidationError> device = DeviceId.Create(deviceId);
        if (device.IsFailure)
        {
            return QueryParameters.Problem(device.Error);
        }

        Result<SignalName, ValidationError> name = SignalName.Create(signal);
        if (name.IsFailure)
        {
            return QueryParameters.Problem(name.Error);
        }

        Result<TimeRange, ValidationError> range = QueryParameters.Range(from, to, timeProvider);
        if (range.IsFailure)
        {
            return QueryParameters.Problem(range.Error);
        }

        Result<BucketWidth, ValidationError> bucket = QueryParameters.Bucket(range.Value, bucketSeconds);
        if (bucket.IsFailure)
        {
            return QueryParameters.Problem(bucket.Error);
        }

        SignalSeries series = await queries.SignalSeriesAsync(device.Value, name.Value, range.Value, bucket.Value, cancellationToken);
        return TypedResults.Ok(SignalSeriesDto.Of(series));
    }
}
