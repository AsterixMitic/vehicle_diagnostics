using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VehicleDiagnostics.Infrastructure.Observability;

/// <summary>Measuring instrument of the decode stage (Signal Decoding context).</summary>
public static class DecodeDiagnostics
{
    public const string ActivitySourceName = "VehicleDiagnostics.Decode";
    public const string MeterName = "VehicleDiagnostics.Decode";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> FramesReceived =
        Meter.CreateCounter<long>("decode.frames.received", description: "CAN frames handed to the decoder");

    public static readonly Counter<long> FramesDecoded =
        Meter.CreateCounter<long>("decode.frames.decoded", description: "Frames that produced a reading");

    public static readonly Counter<long> FramesIgnored =
        Meter.CreateCounter<long>("decode.frames.ignored", description: "Frames ignored, tagged by reason");

    public static readonly Counter<long> EnvelopesEmpty =
        Meter.CreateCounter<long>("decode.envelopes.empty", description: "Raw envelopes that yielded no reading");

    public static void CountIgnored(string reason, long count) =>
        FramesIgnored.Add(count, new KeyValuePair<string, object?>("reason", reason));
}
