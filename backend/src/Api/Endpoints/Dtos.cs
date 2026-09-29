using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Api.Endpoints;

// HTTP shapes. Typed identifiers become plain strings and time spans become seconds here, at the
// edge, so the JSON the dashboard reads is flat and stable.

public sealed record DeviceDto(string DeviceId, string VehicleId, DateTimeOffset LastSeen, string ProcessingMode)
{
    public static DeviceDto Of(DeviceSeen d) =>
        new(d.DeviceId.Value, d.VehicleId.Value, d.LastSeen, d.ProcessingMode.ToString());
}

public sealed record LatestSignalDto(string Name, double Value, string Unit, DateTimeOffset SampledAt, string ProcessingMode)
{
    public static LatestSignalDto Of(LatestSignal s) =>
        new(s.Name.Value, s.Value, s.Unit, s.SampledAt, s.ProcessingMode.ToString());
}

public sealed record SignalSeriesDto(
    string DeviceId,
    string Signal,
    DateTimeOffset From,
    DateTimeOffset To,
    double BucketSeconds,
    IReadOnlyList<SignalPoint> Points)
{
    public static SignalSeriesDto Of(SignalSeries s) =>
        new(s.DeviceId.Value, s.Signal.Value, s.From, s.To, s.Bucket.TotalSeconds, s.Points);
}

public sealed record EvaluationSeriesDto(
    DateTimeOffset From,
    DateTimeOffset To,
    double BucketSeconds,
    IReadOnlyList<ModeSeriesPoint> Points)
{
    public static EvaluationSeriesDto Of(EvaluationSeries s) => new(s.From, s.To, s.Bucket.TotalSeconds, s.Points);
}

public sealed record LiveSignalDto(string Name, double Value, string Unit);

public sealed record LiveReadingDto(
    string DeviceId,
    string VehicleId,
    long Sequence,
    DateTimeOffset TimestampDevice,
    string ProcessingMode,
    IReadOnlyList<LiveSignalDto> Readings)
{
    public static LiveReadingDto Of(TelemetryReceived t) =>
        new(t.DeviceId.Value, t.VehicleId.Value, t.Sequence, t.Readings.Count > 0 ? t.Readings[0].SampledAt : t.OccurredAt,
            t.ProcessingMode.ToString(), [.. t.Readings.Select(r => new LiveSignalDto(r.Name.Value, r.Value, r.Unit))]);
}
