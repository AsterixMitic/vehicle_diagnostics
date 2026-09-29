using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>A device with persisted readings in a range, and its latest reading there.</summary>
public sealed record DeviceSeen(DeviceId DeviceId, VehicleId VehicleId, DateTimeOffset LastSeen, ProcessingMode ProcessingMode);

/// <summary>The most recent stored value of one signal of a device.</summary>
public sealed record LatestSignal(SignalName Name, double Value, string Unit, DateTimeOffset SampledAt, ProcessingMode ProcessingMode);

/// <summary>Samples of one signal aggregated over one time bucket.</summary>
public sealed record SignalPoint(DateTimeOffset BucketStart, double Average, double Minimum, double Maximum, long Samples);

public sealed record SignalSeries(
    DeviceId DeviceId,
    SignalName Signal,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Bucket,
    IReadOnlyList<SignalPoint> Points);

/// <summary>Read port of the Telemetry context, over device-side sample time.</summary>
public interface ITelemetryQueries
{
    Task<IReadOnlyList<DeviceSeen>> DevicesAsync(TimeRange range, CancellationToken cancellationToken);

    /// <summary>One entry per signal the device ever reported; empty for an unknown device.</summary>
    Task<IReadOnlyList<LatestSignal>> LatestSignalsAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Only non-empty buckets, ordered by time; empty for an unknown signal.</summary>
    Task<SignalSeries> SignalSeriesAsync(
        DeviceId deviceId, SignalName signal, TimeRange range, BucketWidth bucket, CancellationToken cancellationToken);
}
